"""Use OpenSSH for authentication and Secret Service for the saved password."""

import hashlib
import os
import re
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import threading
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Account:
    alias: str
    host: str
    user: str
    port: str

    @property
    def key(self):
        value = "\n".join((self.alias.lower(), self.host.lower(), self.user, self.port))
        return hashlib.sha256(value.encode()).hexdigest().upper()

    @property
    def prompts(self):
        account = f"{self.user}@{self.host}"
        return {f"{account}'s password:", f"({account}) password:", "password:"}


def account_for(profile):
    if not profile or not profile.user or not profile.host or "%" in profile.host:
        return None
    if not re.fullmatch(r"[A-Za-z0-9_.:-]+", profile.host) or profile.host.startswith("-"):
        return None
    try:
        port = int(profile.port or "22")
    except ValueError:
        return None
    return Account(profile.alias, profile.host.lower(), profile.user, str(port)) if 1 <= port <= 65535 else None


class Vault:
    @staticmethod
    def command(action, account, password=None):
        tool = shutil.which("secret-tool")
        if not tool or not os.environ.get("DBUS_SESSION_BUS_ADDRESS"):
            if action == "lookup":
                return None
            raise RuntimeError("Хранилище Secret Service недоступно в этом сеансе рабочего стола.")
        arguments = [tool, action]
        if action == "store":
            if not password or any(char in password for char in "\0\r\n") or len(password.encode()) > 1022:
                raise ValueError("Пароль должен быть одной строкой длиной до 1022 байт.")
            arguments += ["--label", f"SSH Manager · {account.user}@{account.host}:{account.port}"]
        result = subprocess.run(arguments + ["application", "io.github.clarkufa.SSHManager", "account", account.key],
                                input=password or "", text=True, encoding="utf-8", capture_output=True, timeout=15)
        if action == "lookup":
            return result.stdout.removesuffix("\n") if result.returncode == 0 else None
        if result.returncode and not (action == "clear" and result.returncode == 1 and not result.stderr):
            raise RuntimeError(result.stderr.strip() or "Операция с хранилищем Secret Service завершилась ошибкой.")

    def lookup(self, account):
        return self.command("lookup", account) if account else None

    def save(self, account, password):
        self.command("store" if password else "clear", account, password)


def ssh_args(profile, config, account=None, files=False):
    executable = shutil.which("ssh")
    if not executable:
        raise RuntimeError("Системная команда ssh недоступна.")
    arguments = [executable]
    if config.path != Path.home() / ".ssh/config":
        arguments += ["-F", str(config.path)]
    if account:
        arguments += ["-o", "HostName=" + account.host, "-l", account.user, "-p", account.port,
                      "-o", "BatchMode=no", "-o", "PasswordAuthentication=yes", "-o",
                      "PreferredAuthentications=password,publickey,keyboard-interactive"]
    if files:
        arguments += ["-T", "-o", "ClearAllForwardings=yes", "-o", "RemoteCommand=none",
                      "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", "-s", profile.alias, "sftp"]
    else:
        arguments.append(profile.alias)
    return arguments


class AskPass:
    """A per-process, single-use socket keeps the password out of argv and the environment."""

    def __init__(self, account=None, password=None):
        self.account = account
        self.password = password
        self.pid = None
        self.ready = threading.Event()
        self.closed = threading.Event()
        self.launcher = str(Path(sys.argv[0]).resolve())
        self.temporary = tempfile.TemporaryDirectory(prefix="ssh-manager-")
        self.address = str(Path(self.temporary.name) / "askpass")
        self.listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.listener.bind(self.address)
        self.listener.listen(4)
        self.listener.settimeout(.3)
        if account and password:
            threading.Thread(target=self.serve, daemon=True).start()

    def environment(self):
        result = os.environ.copy()
        result.update(SSH_ASKPASS=self.launcher, SSH_ASKPASS_REQUIRE="force", SSH_MANAGER_ASKPASS=self.address,
                      SSH_MANAGER_ACCOUNT=f"{self.account.user}@{self.account.host}" if self.account else "")
        return result

    def attach(self, pid):
        self.pid = pid
        self.ready.set()

    def is_helper(self, client):
        pid, uid, _ = struct.unpack("3i", client.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
        if uid != os.getuid() or not self.ready.wait(3) or not self.pid:
            return False
        try:
            status = Path(f"/proc/{pid}/status").read_text()
            parent = re.search(r"^PPid:\s*(\d+)$", status, re.MULTILINE)
            args = Path(f"/proc/{pid}/cmdline").read_bytes().split(b"\0")
            return parent is not None and int(parent[1]) == self.pid and os.fsencode(self.launcher) in args[:2]
        except OSError:
            return False

    def serve(self):
        while not self.closed.is_set():
            try:
                client, _ = self.listener.accept()
            except TimeoutError:
                continue
            except OSError:
                return
            with client:
                if not self.password or not self.is_helper(client):
                    continue
                try:
                    client.sendall(self.password.encode() + b"\n")
                except OSError:
                    pass
                finally:
                    self.password = None
                return

    def close(self):
        self.closed.set()
        self.ready.set()
        self.password = None
        self.listener.close()
        self.temporary.cleanup()


def askpass(prompt):
    account = os.environ.get("SSH_MANAGER_ACCOUNT", "").casefold()
    address = os.environ.get("SSH_MANAGER_ASKPASS")
    if account and prompt.strip().casefold() in {f"{account}'s password:", f"({account}) password:", "password:"}:
        try:
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
                client.settimeout(.7)
                client.connect(address)
                answer = client.makefile("rb").read(1024)
            if answer:
                sys.stdout.buffer.write(answer)
                return 0
        except OSError:
            pass
    import gi

    gi.require_version("Gtk", "3.0")
    from gi.repository import Gtk

    if prompt.strip().endswith(("(yes/no/[fingerprint])?", "(yes/no)?")):
        dialog = Gtk.MessageDialog(message_type=Gtk.MessageType.QUESTION, buttons=Gtk.ButtonsType.YES_NO,
                                   text="Подтверждение ключа SSH-сервера")
        dialog.format_secondary_text(prompt.strip())
        dialog.set_default_response(Gtk.ResponseType.NO)
        response = dialog.run()
        dialog.destroy()
        if response == Gtk.ResponseType.YES:
            print("yes")
            return 0
        return 1
    dialog = Gtk.Dialog(title="Вход по SSH", modal=True)
    dialog.add_button("Отмена", Gtk.ResponseType.CANCEL)
    dialog.add_button("Продолжить", Gtk.ResponseType.OK)
    dialog.set_default_response(Gtk.ResponseType.OK)
    entry = Gtk.Entry(visibility=False, activates_default=True, max_length=1022)
    label = Gtk.Label(label=prompt.strip(), wrap=True, xalign=0)
    label.set_max_width_chars(60)
    box = dialog.get_content_area()
    box.set_border_width(14)
    box.set_spacing(9)
    box.pack_start(label, False, False, 0)
    box.pack_start(entry, False, False, 0)
    dialog.show_all()
    response = dialog.run()
    answer = entry.get_text()
    dialog.destroy()
    if response == Gtk.ResponseType.OK and len(answer.encode()) <= 1022:
        print(answer)
        return 0
    return 1
