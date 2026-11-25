"""Read and edit individual OpenSSH profiles without rewriting unrelated directives."""

import fcntl
import json
import os
import re
import shlex
import tempfile
from contextlib import contextmanager
from dataclasses import asdict, dataclass
from pathlib import Path

from .rdp import DesktopProfile, ORDERS

ALIAS = re.compile(r"[A-Za-z0-9_.][A-Za-z0-9_.-]*\Z")
DIRECTIVE = re.compile(r"(?P<indent>[ \t]*)(?P<key>[A-Za-z][A-Za-z0-9]*)(?:[ \t]*=[ \t]*|[ \t]+)(?P<value>.*)")


def tokens(value):
    lexer = shlex.shlex(value, posix=True)
    lexer.whitespace_split = True
    try:
        return tuple(lexer)
    except ValueError:
        return ()


def option(line):
    match = DIRECTIVE.fullmatch(line.rstrip("\r\n"))
    return (match["key"].lower(), tokens(match["value"])) if match else ("", ())


def quote(value):
    if not value or any(ord(char) < 32 or ord(char) == 127 for char in value):
        raise ValueError("Укажите непустое значение без управляющих символов.")
    if not any(char.isspace() or char in "#\"'\\" for char in value):
        return value
    return '"' + value.replace('\\', '\\\\').replace('"', '\\"') + '"'


def inline_comment(line):
    in_quotes = False
    escaped = False
    for index, char in enumerate(line):
        if escaped:
            escaped = False
        elif char == "\\":
            escaped = True
        elif char == '"':
            in_quotes = not in_quotes
        elif char == "#" and not in_quotes:
            return line[index:].rstrip("\r\n")
    return ""


@dataclass(frozen=True)
class Profile:
    alias: str
    host: str = ""
    user: str = ""
    port: str = ""
    keys: tuple = ()
    line: int = -1
    end: int = -1

    @property
    def address(self):
        host = self.host or self.alias
        if self.port and ":" in host:
            host = f"[{host}]"
        return (f"{self.user}@" if self.user else "") + host + (f":{self.port}" if self.port else "")

    def validate(self):
        if not ALIAS.fullmatch(self.alias):
            raise ValueError("Псевдоним может содержать латинские буквы, цифры, точки, дефисы и подчёркивания.")
        if self.host and not re.fullmatch(r"[A-Za-z0-9_.:%-]+", self.host):
            raise ValueError("Укажите имя сервера или IP-адрес без пробелов.")
        if any(char.isspace() or char in '#"\'' or ord(char) < 32 for char in self.user):
            raise ValueError("Укажите имя пользователя без пробелов и кавычек.")
        if self.port and (not re.fullmatch(r"[0-9]{1,5}", self.port) or not 1 <= int(self.port) <= 65535):
            raise ValueError("Порт должен быть числом от 1 до 65535.")
        for path in self.keys:
            quote(path)


class Document:
    def __init__(self, raw=b""):
        self.raw = raw
        self.bom = raw.startswith(b"\xef\xbb\xbf")
        body = raw.decode("utf-8-sig")
        if "\x00" in body:
            raise ValueError("SSH config должен быть текстовым файлом UTF‑8.")
        self.lines = body.splitlines(keepends=True)
        self.newline = "\r\n" if self.lines and self.lines[0].endswith("\r\n") else "\n"
        self.directives = tuple(map(option, self.lines))
        self.blocks = [i for i, (key, _) in enumerate(self.directives) if key in ("host", "match")]
        self.profiles = []
        for begin, end in zip(self.blocks, self.blocks[1:] + [len(self.lines)]):
            key, names = self.directives[begin]
            if key != "host" or len(names) != 1 or not ALIAS.fullmatch(names[0]):
                continue
            settings = {}
            for key, values in self.directives[begin + 1:end]:
                if len(values) == 1:
                    settings.setdefault(key, []).append(values[0])
            self.profiles.append(Profile(
                names[0], *(settings.get(key, [""])[0] for key in ("hostname", "user", "port")),
                tuple(settings.get("identityfile", ())), begin, end,
            ))

    def update(self, before, after):
        if after:
            after.validate()
            if not before or before.alias.casefold() != after.alias.casefold():
                if any(key == "host" and after.alias.casefold() in (name.casefold() for name in names)
                       for key, names in self.directives):
                    raise ValueError("Такой псевдоним уже существует в SSH config.")
        lines = self.lines.copy()
        if before is None:
            if lines and not lines[-1].endswith(("\n", "\r")):
                lines[-1] += self.newline
            if lines and lines[-1].strip():
                lines.append(self.newline)
            lines.append(f"Host {after.alias}{self.newline}")
            lines.extend(self.format_fields(after))
        elif after is None:
            # Keep standalone comments and whitespace in their original position.
            lines[before.line:before.end] = [line for line in lines[before.line + 1:before.end]
                                            if not line.strip() or line.lstrip().startswith("#")]
        else:
            fields = (
                ("HostName", (before.host,) if before.host else (), (after.host,) if after.host else ()),
                ("User", (before.user,) if before.user else (), (after.user,) if after.user else ()),
                ("Port", (before.port,) if before.port else (), (after.port,) if after.port else ()),
                ("IdentityFile", before.keys, after.keys),
            )
            changes = {key.lower(): (key, new) for key, old, new in fields if old != new}
            replacements = []
            used = set()
            for index in range(before.line + 1, before.end):
                line = lines[index]
                key, values = self.directives[index]
                if key not in changes or len(values) != 1:
                    replacements.append(line)
                    continue
                name, new_values = changes[key]
                if key not in used:
                    indent = line[:len(line) - len(line.lstrip())]
                    comment = inline_comment(line)
                    for position, value in enumerate(new_values):
                        suffix = ("  " + comment) if comment and position == 0 else ""
                        replacements.append(f"{indent}{name} {quote(value)}{suffix}{self.newline}")
                    used.add(key)
                    if new_values:
                        continue
                comment = inline_comment(line)
                if comment:
                    replacements.append("    " + comment + self.newline)
            missing = [f"    {name} {quote(value)}{self.newline}" for key, (name, values) in changes.items()
                       if key not in used for value in values]
            lines[before.line + 1:before.end] = missing + replacements
            if before.alias != after.alias or (missing and not lines[before.line].endswith(("\n", "\r"))):
                suffix = inline_comment(lines[before.line])
                lines[before.line] = f"Host {after.alias}{'  ' + suffix if suffix else ''}{self.newline}"
        return Document((b"\xef\xbb\xbf" if self.bom else b"") + "".join(lines).encode("utf-8"))

    def format_fields(self, profile):
        for name, value in (("HostName", profile.host), ("User", profile.user), ("Port", profile.port),
                            *(("IdentityFile", key) for key in profile.keys)):
            if value:
                yield f"    {name} {quote(value)}{self.newline}"


def read(path):
    try:
        return Path(path).read_bytes()
    except FileNotFoundError:
        return b""


def write(path, contents, mode=0o600):
    path = Path(path)
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix=".ssh-manager-", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as stream:
            os.fchmod(stream.fileno(), mode)
            stream.write(contents)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(name, path)
    finally:
        Path(name).unlink(missing_ok=True)


def parse_settings(contents):
    initial = {"groups": [], "members": {}, "default": "", "dark": True, "desktops": [], "sort": {}}
    if not contents:
        return initial
    data = json.loads(contents)
    if not isinstance(data, dict):
        raise ValueError("Некорректный файл настроек.")
    initial.update({name: data[name] for name in initial if name in data})
    groups = initial["groups"]
    if not isinstance(groups, list) or len(groups) > 500 or not isinstance(initial["members"], dict):
        raise ValueError("Некорректный список вкладок.")
    ids = {""}
    for group in groups:
        if (not isinstance(group, dict) or any(not isinstance(group.get(key), str) for key in ("id", "name", "color")) or
                not re.fullmatch("[0-9a-f]{32}", group["id"]) or group["id"] in ids or
                not group["name"] or len(group["name"]) > 120 or
                (group["color"] and not re.fullmatch("#[0-9a-fA-F]{6}", group["color"]))):
            raise ValueError("Некорректная вкладка.")
        ids.add(group["id"])
    if not isinstance(initial["dark"], bool) or not isinstance(initial["default"], str) or initial["default"] not in ids:
        raise ValueError("Некорректная тема или вкладка по умолчанию.")
    if any(not ALIAS.fullmatch(alias) or not isinstance(group, str) or group not in ids
           for alias, group in initial["members"].items()):
        raise ValueError("Некорректное распределение серверов по вкладкам.")
    sort_keys = {protocol + ":" + group for protocol in ("ssh", "rdp") for group in ids}
    orders = dict(ORDERS)
    if not isinstance(initial["sort"], dict) or any(
            key not in sort_keys or not isinstance(order, str) or order not in orders
            for key, order in initial["sort"].items()):
        raise ValueError("Некорректная сортировка серверов.")
    desktop_ids = set()
    desktops = []
    if not isinstance(initial["desktops"], list) or len(initial["desktops"]) > 10000:
        raise ValueError("Некорректный список RDP-подключений.")
    for entry in initial["desktops"]:
        try:
            profile = DesktopProfile(**entry).validate()
        except TypeError as error:
            raise ValueError("Некорректное RDP-подключение.") from error
        if profile.id in desktop_ids or profile.group not in ids:
            raise ValueError("Некорректная вкладка или повторяющееся RDP-подключение.")
        desktop_ids.add(profile.id)
        desktops.append(asdict(profile))
    initial["desktops"] = desktops
    return initial


class Config:
    def __init__(self, path, state):
        self.path = Path(path).expanduser().absolute()
        self.state = Path(state).expanduser().absolute()
        self.settings_path = self.state / "settings.json"

    def load(self):
        return Document(read(self.path))

    @contextmanager
    def locked(self):
        self.state.mkdir(mode=0o700, parents=True, exist_ok=True)
        with os.fdopen(os.open(self.state / "write.lock", os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600), "wb") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            yield

    def save(self, before, after):
        with self.locked():
            current = read(self.path)
            if current != before.raw:
                raise ValueError("SSH config изменился. Обновите список перед сохранением.")
            if current != after.raw:
                path = self.path.resolve()
                if path.exists():
                    write(path.with_name(path.name + ".bak"), current)
                write(path, after.raw)

    def settings(self):
        return parse_settings(read(self.settings_path))

    def save_settings(self, settings):
        with self.locked():
            write(self.settings_path, json.dumps(settings, ensure_ascii=False, indent=2).encode())

    def keys(self):
        try:
            items = list(self.path.parent.iterdir())
        except FileNotFoundError:
            return []
        result = []
        for path in items:
            if path.is_symlink() or not path.is_file():
                continue
            try:
                with path.open("rb") as file:
                    header = file.read(180).split(b"\n", 1)[0]
                if header.startswith(b"-----BEGIN ") and header.rstrip().endswith(b"PRIVATE KEY-----"):
                    result.append(str(path))
            except OSError:
                continue
        return sorted(result)
