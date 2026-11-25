"""Portable Linux archives for SSH Manager, with authenticated encryption for passwords."""

import hashlib
import io
import os
import threading
import uuid
import zipfile
from pathlib import Path, PurePosixPath

from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.exceptions import InvalidTag
from gi.repository import GLib, Gtk

from .auth import account_for
from .config import Document, parse_settings, read, write
from .rdp import DesktopProfile
from .widgets import chooser, confirm, menu_at, message, prompt


MAGIC = b"SSHMLNX1"
MARKER = "format.txt"
FORMAT = b"SSH Manager Linux backup 1\n"
STORE = "app/settings.json"
CONFIG = "ssh/config"
KEY_PREFIX = "ssh/keys/"
PASS_PREFIX = "app/passwords/"
RDP_PREFIX = "rdp-"  # RDP passwords: app/passwords/rdp-<profile id>, the same naming as the Windows and macOS archives
MAX_TOTAL = 64 * 1024 * 1024
MAX_ENTRY = 8 * 1024 * 1024


def crypt_key(password, salt):
    return hashlib.pbkdf2_hmac("sha256", password.encode(), salt, 600_000, 32)


def entry_name(name):
    if not name or name in (".", "..") or "/" in name or "\0" in name or "\\" in name:
        raise ValueError("Недопустимое имя в резервной копии.")
    return name


def accounts(document, settings=None):
    """Secret Service accounts by backup name: SSH profiles from the config, RDP profiles from the settings."""
    result = {account.key: account for profile in document.profiles if (account := account_for(profile))}
    for entry in (settings or {}).get("desktops", []):
        profile = DesktopProfile(**entry)
        result[RDP_PREFIX + profile.id] = profile.secret
    return result


def export(config, vault, destination, password=None):
    with config.locked():
        document = config.load()
        settings = read(config.settings_path) or b"{}"
        parsed = parse_settings(settings)
        entries = {MARKER: FORMAT, CONFIG: document.raw, STORE: settings}
        for key in config.keys():
            path = Path(key)
            entries[KEY_PREFIX + entry_name(path.name)] = read(path)
            public = path.with_name(path.name + ".pub")
            if public.is_file() and not public.is_symlink():
                entries[KEY_PREFIX + entry_name(public.name)] = read(public)
        if password:
            for identifier, account in accounts(document, parsed).items():
                saved = vault.lookup(account)
                if saved:
                    entries[PASS_PREFIX + identifier] = saved.encode()
    if len(entries) > 2048 or any(len(value) > MAX_ENTRY for value in entries.values()) or sum(map(len, entries.values())) > MAX_TOTAL:
        raise ValueError("Превышен допустимый размер резервной копии.")
    with io.BytesIO() as output:
        with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, value in sorted(entries.items()):
                archive.writestr(name, value)
        data = output.getvalue()
    if password:
        salt, nonce = os.urandom(16), os.urandom(12)
        header = MAGIC + salt + nonce
        data = header + AESGCM(crypt_key(password, salt)).encrypt(nonce, data, header)
    write(destination, data)
    return sum(name.startswith(PASS_PREFIX) for name in entries)


def protected(path):
    with Path(path).open("rb") as source:
        return source.read(len(MAGIC)) == MAGIC


def decode(path, password):
    path = Path(path)
    if path.stat().st_size > MAX_TOTAL + 1024 * 1024:
        raise ValueError("Архив слишком большой.")
    data = path.read_bytes()
    encrypted = data.startswith(MAGIC)
    if encrypted:
        if not password or len(data) < 52:
            raise ValueError("Нужен пароль резервной копии.")
        salt, nonce = data[8:24], data[24:36]
        try:
            data = AESGCM(crypt_key(password, salt)).decrypt(nonce, data[36:], data[:36])
        except InvalidTag as error:
            raise ValueError("Пароль неверный или архив повреждён.") from error
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        entries = archive.infolist()
        if len(entries) > 2048 or any(info.file_size > MAX_ENTRY for info in entries) or sum(info.file_size for info in entries) > MAX_TOTAL:
            raise ValueError("Архив превышает допустимый размер.")
        names = [info.filename for info in entries]
        if len(set(names)) != len(names) or any(PurePosixPath(name).is_absolute() for name in names):
            raise ValueError("Недопустимые имена файлов в архиве.")
        if any(name.startswith(PASS_PREFIX) for name in names) and not encrypted:
            raise ValueError("Для переноса сохранённых паролей нужен зашифрованный архив.")
        contents = {name: archive.read(name) for name in names}
    if contents.pop(MARKER, None) != FORMAT or CONFIG not in contents or STORE not in contents:
        raise ValueError("Этот файл не является резервной копией Linux-версии SSH Manager.")
    document = Document(contents[CONFIG])
    valid = accounts(document, parse_settings(contents[STORE]))
    for name, value in contents.items():
        if name.startswith(KEY_PREFIX):
            entry_name(name[len(KEY_PREFIX):])
        elif name.startswith(PASS_PREFIX):
            if name[len(PASS_PREFIX):] not in valid or not 1 <= len(value) <= 1022 or any(char in value for char in (0, 10, 13)):
                raise ValueError("Архив содержит пароль без соответствующего профиля.")
            value.decode("utf-8")
        elif name not in (CONFIG, STORE):
            raise ValueError("Неизвестный файл в архиве.")
    return contents, valid


def restore(config, vault, contents, valid):
    changes = {config.path.resolve(): contents[CONFIG], config.settings_path: contents[STORE]}
    passwords = []
    for name, value in contents.items():
        if name.startswith(KEY_PREFIX):
            file = config.path.parent / name[len(KEY_PREFIX):]
            if file.is_symlink() or file.resolve() in {target.resolve() for target in changes}:
                raise ValueError("Небезопасный путь SSH-ключа.")
            changes[file] = value
        if name.startswith(PASS_PREFIX):
            passwords.append((valid[name[len(PASS_PREFIX):]], value.decode("utf-8")))
    recovery = config.state / ("restore-" + uuid.uuid4().hex[:12])
    with config.locked():
        previous = {file: read(file) if file.exists() else None for file in changes}
        for index, (file, value) in enumerate(previous.items()):
            if value is not None:
                write(recovery / (f"{index:03d}-" + file.name), value)
        installed, vault_changes = [], []
        try:
            for account, value in passwords:
                prior = vault.lookup(account)
                vault.save(account, value)
                vault_changes.append((account, prior))
            for file, data in changes.items():
                write(file, data)
                installed.append(file)
        except Exception as error:
            failed = False
            for file in reversed(installed):
                try:
                    if previous[file] is None:
                        file.unlink(missing_ok=True)
                    else:
                        write(file, previous[file])
                except OSError:
                    failed = True
            for account, password in reversed(vault_changes):
                try:
                    vault.save(account, password)
                except Exception:
                    failed = True
            if failed:
                raise OSError(f"Импорт прерван. Предыдущие файлы сохранены в {recovery}.") from error
            raise
    return recovery if recovery.exists() else None


def backup_menu(owner):
    menu_at(owner.get_focus() or owner, None, [
        ("Экспорт защищённой копии…", lambda: choose_export(owner, True)),
        ("Экспорт ZIP без паролей…", lambda: choose_export(owner, False)),
        ("Импорт копии…", lambda: choose_import(owner)),
    ])


def background(owner, work, done):
    def deliver(result, error):
        if owner.get_realized():
            if error:
                message(owner, error, Gtk.MessageType.ERROR)
            else:
                done(result)
        return False

    def run():
        try:
            result = work()
        except Exception as error:
            GLib.idle_add(deliver, None, error)
        else:
            GLib.idle_add(deliver, result, None)

    threading.Thread(target=run, daemon=True).start()


def choose_export(owner, encrypted):
    suffix = ".sshbackup" if encrypted else ".zip"
    path = chooser(owner, "Экспорт резервной копии", Gtk.FileChooserAction.SAVE, "ssh-manager" + suffix)
    if not path:
        return
    password = None
    if encrypted:
        password = prompt(owner, "Пароль резервной копии", hidden=True)
        if not password:
            return
        if password != prompt(owner, "Повторите пароль резервной копии", hidden=True):
            message(owner, "Пароли не совпадают.", Gtk.MessageType.ERROR)
            return
    background(owner, lambda: export(owner.config, owner.vault, path, password),
               lambda count: message(owner, f"Копия сохранена. Паролей SSH и RDP в архиве: {count}."))


def choose_import(owner):
    path = chooser(owner, "Импорт резервной копии", Gtk.FileChooserAction.OPEN)
    if not path:
        return
    try:
        encrypted = protected(path)
    except OSError as error:
        message(owner, error, Gtk.MessageType.ERROR)
        return
    password = prompt(owner, "Пароль резервной копии", hidden=True) if encrypted else None
    if encrypted and password is None:
        return

    def preview(result):
        contents, valid = result
        secrets = [name for name in contents if name.startswith(PASS_PREFIX)]
        desktop = sum(name.startswith(PASS_PREFIX + RDP_PREFIX) for name in secrets)
        details = f"Файлов: {len(contents) - len(secrets)}, паролей SSH: {len(secrets) - desktop}, RDP: {desktop}"
        if not confirm(owner, f"Восстановить настройки SSH, RDP и файлы из архива?\n{details}"):
            return

        def done(recovery):
            owner.settings = owner.config.settings()
            owner.active_group = owner.settings["default"]
            owner.apply_theme()
            owner.reload()
            message(owner, "Резервная копия восстановлена." + (f"\nПрежние файлы: {recovery}" if recovery else ""))

        background(owner, lambda: restore(owner.config, owner.vault, contents, valid), done)

    background(owner, lambda: decode(path, password), preview)
