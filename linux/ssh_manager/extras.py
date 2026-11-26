"""Stage 2 logic without GTK: quick connect, imports from other programs, key helpers (docs/ui-spec.md §7)."""

import re
import shlex
import subprocess
import urllib.parse
import xml.etree.ElementTree as ElementTree
from dataclasses import dataclass
from pathlib import Path

HOST = r"(?:\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9_.-]+)"
DEFAULT_PORTS = {"ssh": 22, "rdp": 3389, "vnc": 5900}


@dataclass(frozen=True)
class Found:
    """A connection found in another program."""
    protocol: str  # "ssh", "rdp" or "vnc"
    name: str
    host: str
    port: int
    user: str = ""
    domain: str = ""
    source: str = ""


def parse_quick(text):
    """«user@host:port» → ("ssh", user, host, port); «rdp://[user@]host[:port]» → ("rdp", …); «vnc://…» → ("vnc", …)."""
    text = text.strip()
    protocol = "ssh"
    for prefix, kind in (("rdp://", "rdp"), ("vnc://", "vnc"), ("ssh://", "ssh")):
        if text.lower().startswith(prefix):
            protocol, text = kind, text[len(prefix):].rstrip("/")
    match = re.fullmatch(rf"(?:([^@\s:]+)@)?({HOST})(?::([0-9]{{1,5}}))?", text)
    if not match:
        raise ValueError("Введите адрес как user@host:port, rdp://host:port или vnc://host:port.")
    user, host, port = match.groups()
    port = int(port) if port else DEFAULT_PORTS[protocol]
    if not 1 <= port <= 65535:
        raise ValueError("Порт должен быть числом от 1 до 65535.")
    return protocol, user or "", host.strip("[]"), port


def quick_alias(host, existing):
    base = re.sub(r"[^A-Za-z0-9_.-]", "-", host).strip("-.") or "server"
    names = {name.casefold() for name in existing}
    alias, number = base, 2
    while alias.casefold() in names:
        alias, number = f"{base}-{number}", number + 1
    return alias


# --- PuTTY --------------------------------------------------------------------------------------
def putty_sessions(folder=Path.home() / ".putty" / "sessions"):
    found = []
    folder = Path(folder)
    if not folder.is_dir():
        return found
    for path in sorted(folder.iterdir()):
        if not path.is_file():
            continue
        values = {}
        for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
            key, _, value = line.partition("=")
            values[key.strip()] = value.strip()
        if values.get("Protocol", "ssh") != "ssh" or not values.get("HostName"):
            continue
        host = values["HostName"]
        user = values.get("UserName", "")
        if "@" in host and not user:
            user, host = host.split("@", 1)
        port = int(values.get("PortNumber", "22") or 22)
        found.append(Found("ssh", urllib.parse.unquote(path.name), host, port, user, source="PuTTY"))
    return found


# --- mRemoteNG -------------------------------------------------------------------------------------
def mremoteng(path):
    """Connections from confCons.xml. Passwords are encrypted there and are not imported."""
    root = ElementTree.parse(path).getroot()
    found = []
    for node in root.iter("Node"):
        if node.get("Type") != "Connection":
            continue
        protocol = {"SSH2": "ssh", "SSH1": "ssh", "RDP": "rdp", "VNC": "vnc"}.get(node.get("Protocol", ""))
        host = node.get("Hostname", "")
        if not protocol or not host:
            continue
        try:
            port = int(node.get("Port") or DEFAULT_PORTS[protocol])
        except ValueError:
            port = DEFAULT_PORTS[protocol]
        found.append(Found(protocol, node.get("Name") or host, host, port, node.get("Username", ""),
                           node.get("Domain", "") if protocol == "rdp" else "", "mRemoteNG"))
    return found


# --- MobaXterm --------------------------------------------------------------------------------------
def mobaxterm(path):
    """Sessions from a .mxtsessions / MobaXterm.ini export: Name=#icon#type%host%port%user%…"""
    found = []
    for line in Path(path).read_text(encoding="utf-8", errors="replace").splitlines():
        if "=" not in line or line.startswith(("[", ";", "SubRep", "ImgNum")):
            continue
        name, _, value = line.partition("=")
        match = re.match(r"#\d+#(\d+)%([^%]*)%([^%]*)%([^%]*)", value)
        if not match:
            continue
        kind, host, port, user = match.groups()
        protocol = {"0": "ssh", "4": "rdp", "5": "vnc"}.get(kind)
        if not protocol or not host:
            continue
        try:
            port = int(port) if port else DEFAULT_PORTS[protocol]
        except ValueError:
            port = DEFAULT_PORTS[protocol]
        found.append(Found(protocol, name.strip(), host, port, user, source="MobaXterm"))
    return found


def read_import(path):
    path = Path(path)
    if path.suffix.lower() == ".xml":
        return mremoteng(path)
    return mobaxterm(path)


# --- keys ---------------------------------------------------------------------------------------------
def generate_key(path, comment, run=subprocess.run):
    """Creates an ed25519 key without a passphrase; returns the public key path."""
    path = Path(path).expanduser()
    if path.exists():
        raise ValueError(f"Файл {path} уже существует.")
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    result = run(["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-C", comment, "-f", str(path)],
                 capture_output=True, text=True, timeout=30)
    if result.returncode:
        raise ValueError(result.stderr.strip() or "ssh-keygen завершился с ошибкой.")
    return path.with_name(path.name + ".pub")


def copy_key_command(public_key):
    """Remote shell command that appends the key once to ~/.ssh/authorized_keys."""
    key = public_key.strip()
    if not re.fullmatch(r"(ssh|ecdsa|sk)-[A-Za-z0-9@.-]+ [A-Za-z0-9+/=]+( [^'\r\n]*)?", key):
        raise ValueError("Файл не похож на публичный ключ OpenSSH.")
    quoted = shlex.quote(key)
    return ("umask 077; mkdir -p ~/.ssh && touch ~/.ssh/authorized_keys && "
            f"(grep -qxF {quoted} ~/.ssh/authorized_keys || echo {quoted} >> ~/.ssh/authorized_keys) && "
            "echo 'Ключ добавлен в ~/.ssh/authorized_keys.'")
