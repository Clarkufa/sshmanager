"""known_hosts files: entries, SHA256 fingerprints, hashed names, removal and ssh-keyscan (docs/ui-spec.md §9)."""

import base64
import getpass
import hashlib
import hmac
import os
import re
import shutil
import subprocess
import tempfile
from dataclasses import dataclass, field
from pathlib import Path


@dataclass
class Entry:
    line: int
    hosts: str
    key_type: str
    key: str
    marker: str = ""
    revealed: str = field(default=None, compare=False)

    @property
    def hashed(self):
        return self.hosts.startswith("|1|")

    @property
    def display(self):
        return (self.revealed or "скрытое имя") if self.hashed else self.hosts

    @property
    def fingerprint(self):
        try:
            digest = hashlib.sha256(base64.b64decode(self.key, validate=True)).digest()
        except ValueError:
            return "повреждённый ключ"
        return "SHA256:" + base64.b64encode(digest).decode().rstrip("=")

    def is_for(self, name):
        """True when the entry is for exactly `name` (`host` or `[host]:port`)."""
        if self.hashed:
            parts = self.hosts.split("|")
            if len(parts) != 4:
                return False
            try:
                salt, expected = base64.b64decode(parts[2]), base64.b64decode(parts[3])
            except ValueError:
                return False
            return hmac.compare_digest(hmac.new(salt, name.encode(), hashlib.sha1).digest(), expected)
        return any(not pattern.startswith("!") and wildcard(pattern).fullmatch(name)
                   for pattern in self.hosts.split(","))


def wildcard(pattern):
    """An OpenSSH host pattern: only `*` and `?` are special (`[host]:port` is a literal name)."""
    return re.compile(re.escape(pattern).replace(r"\*", ".*").replace(r"\?", "."), re.IGNORECASE)


def parse(line):
    fields = line.split()
    if not fields or fields[0].startswith("#"):
        return None
    marker = fields.pop(0) if fields[0].startswith("@") else ""
    if len(fields) < 3:
        return None
    return Entry(-1, fields[0], fields[1], fields[2], marker)


def host_name(host, port):
    """The name ssh writes: `host`, or `[host]:port` for another port."""
    host = host.strip().strip("[]")
    return host if int(port) == 22 else f"[{host}]:{int(port)}"


def resolve(configured, default):
    """UserKnownHostsFile as OpenSSH reads it: empty — `default`; `~` and `%d` — home, `%u` — the user."""
    value = (configured or "").strip()
    if not value:
        return Path(default)
    home = str(Path.home())
    if value == "~" or value.startswith("~/"):
        value = home + value[1:]
    value = value.replace("%d", home).replace("%u", getpass.getuser()).replace("%%", "%")
    return Path(value)


def valid_setting(value):
    """One file: absolute or from ~, no control characters or quotes."""
    return (bool(value) and len(value) <= 1024 and '"' not in value and not any(ord(char) < 32 for char in value)
            and (value.startswith("/") or value.startswith("~/")))


class KnownHosts:
    def __init__(self, path):
        self.path = Path(path)
        self.lines = []
        self.entries = []
        if self.path.exists():
            if self.path.stat().st_size > 16 * 1024 * 1024:
                raise OSError("Файл known_hosts слишком большой.")
            self.lines = self.path.read_text(encoding="utf-8", errors="replace").splitlines()
        for index, line in enumerate(self.lines):
            entry = parse(line)
            if entry is not None:
                entry.line = index
                self.entries.append(entry)

    def search(self, query):
        """Plain names by substring, hashed names only by the exact host (and then they show it)."""
        query = (query or "").strip()
        for entry in self.entries:
            entry.revealed = None
        if not query:
            return list(self.entries)
        names = [query]
        match = re.fullmatch(r"\[?([^\[\]]+?)\]?:(\d{1,5})", query)
        if match:
            names.append(host_name(match.group(1), match.group(2)))
        found = []
        for entry in self.entries:
            if entry.hashed:
                name = next((name for name in names if entry.is_for(name)), None)
                if name:
                    entry.revealed = name
                    found.append(entry)
            elif query.lower() in entry.hosts.lower() or query.lower() in entry.key_type.lower():
                found.append(entry)
        return found

    def remove(self, entries):
        drop = {entry.line for entry in entries}
        self.write([line for index, line in enumerate(self.lines) if index not in drop])

    def replace(self, name, key_lines):
        drop = {entry.line for entry in self.entries if not entry.marker and entry.is_for(name)}
        self.write([line for index, line in enumerate(self.lines) if index not in drop] + list(key_lines))

    def write(self, lines):
        self.path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        if self.path.exists():
            shutil.copy2(self.path, self.path.with_name(self.path.name + ".old"))
        fd, temp = tempfile.mkstemp(dir=self.path.parent, prefix=self.path.name + ".")
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as stream:
                os.fchmod(stream.fileno(), 0o600)
                stream.write("".join(line + "\n" for line in lines))
            os.replace(temp, self.path)
        finally:
            Path(temp).unlink(missing_ok=True)


def scan(host, port, run=subprocess.run):
    """`ssh-keyscan -T 10`: the key lines for host:port with the name ssh would write."""
    host = host.strip().strip("[]")
    if not re.fullmatch(r"[A-Za-z0-9_.:-]+", host) or host.startswith("-"):
        raise ValueError("Укажите DNS-имя, IPv4 или IPv6 без пробелов.")
    port = int(port)
    if not 1 <= port <= 65535:
        raise ValueError("Порт должен быть числом от 1 до 65535.")
    scanner = shutil.which("ssh-keyscan")
    if not scanner:
        raise OSError("Не найдена команда ssh-keyscan (пакет openssh-client).")
    result = run([scanner, "-T", "10", "-p", str(port), host], capture_output=True, text=True, timeout=40)
    keys = [line.strip() for line in result.stdout.splitlines()
            if line.strip() and not line.startswith("#") and parse(line.strip())]
    if not keys:
        detail = [line.strip() for line in result.stderr.splitlines() if line.strip() and not line.startswith("#")]
        raise OSError("Сервер не прислал ключи." + (" " + detail[-1] if detail else ""))
    return keys
