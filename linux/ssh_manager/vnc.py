"""VNC profiles and the SSH tunnel for «Через SSH-сервер» (docs/ui-spec.md §8)."""

import hashlib
import ipaddress
import re
import socket
import subprocess
import threading
import time
import unicodedata
from dataclasses import dataclass

from .auth import AskPass, account_for, ssh_args
from .rdp import DesktopAccount

DEFAULT_PORT = 5900
# The same rule as config.ALIAS (config imports this module, so it is repeated here).
ALIAS = re.compile(r"[A-Za-z0-9_.][A-Za-z0-9_.-]*\Z")
SCALES = ("fit", "native")
MISSING_CLIENT = "Для VNC установите пакет gir1.2-gtk-vnc-2.0 (Debian/Ubuntu) или gtk-vnc2 (Fedora)."


@dataclass(frozen=True)
class VncProfile:
    id: str
    name: str
    host: str
    port: int = DEFAULT_PORT
    user: str = ""
    group: str = ""
    scale: str = "fit"
    view_only: bool = False
    clipboard: bool = True
    fullscreen: bool = False
    via: str = ""

    @property
    def alias(self):
        return self.name

    @property
    def account(self):
        return self.user

    @property
    def secret(self):
        key = hashlib.sha256(("vnc\n" + self.id).encode()).hexdigest().upper()
        return DesktopAccount(key, self.user or "-", self.host, str(self.port))

    @property
    def address(self):
        return f"[{self.host}]:{self.port}" if ":" in self.host else f"{self.host}:{self.port}"

    def validate(self):
        for value, limit in ((self.name, 80), (self.host, 253), (self.user, 128), (self.via, 253)):
            if not isinstance(value, str) or len(value) > limit or any(unicodedata.category(c) == "Cc" for c in value):
                raise ValueError("Некорректный текст в настройках VNC-подключения.")
        if not self.name.strip() or not self.host or not isinstance(self.id, str) or not re.fullmatch("[0-9a-f]{32}", self.id):
            raise ValueError("Укажите название и адрес VNC-подключения.")
        try:
            if not re.fullmatch(r"[0-9A-Fa-f:.]+", self.host):
                raise ValueError
            ipaddress.ip_address(self.host)
        except ValueError:
            labels = self.host.removesuffix(".").split(".")
            if not all(re.fullmatch(r"[a-zA-Z0-9_](?:[a-zA-Z0-9_-]{0,61}[a-zA-Z0-9_])?", label) for label in labels):
                raise ValueError("Укажите DNS-имя, IPv4 или IPv6 без порта.") from None
        if type(self.port) is not int or not 1 <= self.port <= 65535:
            raise ValueError("Укажите порт от 1 до 65535.")
        if self.scale not in SCALES:
            raise ValueError("Некорректный масштаб VNC-подключения.")
        if self.via and not ALIAS.fullmatch(self.via):
            raise ValueError("Укажите SSH-сервер из каталога или «Напрямую».")
        if (not isinstance(self.group, str) or not isinstance(self.view_only, bool) or
                not isinstance(self.clipboard, bool) or not isinstance(self.fullscreen, bool)):
            raise ValueError("Некорректные параметры VNC-подключения.")
        return self


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


class TunnelError(Exception):
    pass


class SshTunnel:
    """`ssh -N -L 127.0.0.1:<free port>:host:port alias`; the VNC client connects to 127.0.0.1:<free port>.

    The saved SSH password goes through AskPass as for SFTP; otherwise ssh asks in its own dialog."""

    def __init__(self, profile, config, vault, host, port, timeout=20):
        account = account_for(profile)
        try:
            password = vault.lookup(account)
        except (OSError, RuntimeError, subprocess.TimeoutExpired):
            password = None
        self.port = free_port()
        self.messages = bytearray()
        self.broker = AskPass(account if password else None, password)
        target = f"[{host}]" if ":" in host else host
        arguments = ssh_args(profile, config, account if password else None)
        arguments[-1:-1] = ["-N", "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=15",
                            "-o", "ServerAliveCountMax=3", "-L", f"127.0.0.1:{self.port}:{target}:{port}"]
        try:
            self.process = subprocess.Popen(arguments, env=self.broker.environment(), stdin=subprocess.DEVNULL,
                                            stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        except OSError as error:
            self.broker.close()
            raise TunnelError(f"Не удалось открыть SSH-туннель: {error}") from error
        self.broker.attach(self.process.pid)
        threading.Thread(target=self._drain, daemon=True).start()
        try:
            self._wait(timeout)
        except BaseException:
            self.close()
            raise

    def _drain(self):
        try:
            while True:
                block = self.process.stderr.read(2048)
                if not block:
                    return
                self.messages.extend(block)
                del self.messages[:-4096]
        except (OSError, ValueError):
            pass

    def _wait(self, timeout):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            if self.process.poll() is not None:
                time.sleep(.2)
                lines = [line.strip() for line in self.messages.decode(errors="replace").splitlines() if line.strip()]
                raise TunnelError("Не удалось открыть SSH-туннель: " +
                                  ("\n".join(lines[-3:]) or f"ssh завершился с кодом {self.process.returncode}."))
            try:
                with socket.create_connection(("127.0.0.1", self.port), timeout=.5):
                    return
            except OSError:
                time.sleep(.2)
        raise TunnelError("Не удалось открыть SSH-туннель: SSH-сервер не ответил за отведённое время.")

    @property
    def alive(self):
        return self.process.poll() is None

    def close(self):
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(3)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
        self.broker.close()
        if self.process.stderr:
            self.process.stderr.close()
