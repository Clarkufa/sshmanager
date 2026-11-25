import ipaddress
import os
import re
import shutil
import unicodedata
import uuid
from dataclasses import dataclass
from pathlib import Path


ORDERS = (
    ("original", "Исходный порядок"),
    ("name", "По названию ↑"),
    ("name_desc", "По названию ↓"),
    ("address", "По адресу ↑"),
    ("address_desc", "По адресу ↓"),
)


def ordered(profiles, order):
    if order == "original":
        return profiles
    key = lambda profile: profile.alias.casefold()
    if order.startswith("address"):
        def key(profile):
            host = (profile.host or profile.alias).strip("[]")
            try:
                address = ipaddress.ip_address(host)
                return address.version, address.packed
            except ValueError:
                return 7, host.casefold()
    return sorted(profiles, key=key, reverse=order.endswith("_desc"))


@dataclass(frozen=True)
class DesktopProfile:
    id: str
    name: str
    host: str
    port: int = 3389
    user: str = ""
    domain: str = ""
    group: str = ""
    width: int = 0
    height: int = 0
    clipboard: bool = False
    fullscreen: bool = False

    @property
    def alias(self):
        return self.name

    @property
    def address(self):
        return f"[{self.host}]:{self.port}" if ":" in self.host else f"{self.host}:{self.port}"

    def validate(self):
        for value, limit in ((self.name, 80), (self.host, 253), (self.user, 128), (self.domain, 128)):
            if not isinstance(value, str) or len(value) > limit or any(unicodedata.category(c) == "Cc" for c in value):
                raise ValueError("Некорректный текст в настройках RDP-подключения.")
        if not self.name.strip() or not self.host or not isinstance(self.id, str) or not re.fullmatch("[0-9a-f]{32}", self.id):
            raise ValueError("Укажите название и адрес RDP-подключения.")
        host = self.host
        try:
            if not re.fullmatch(r"[0-9A-Fa-f:.]+", host):
                raise ValueError
            ipaddress.ip_address(host)
        except ValueError:
            labels = host.removesuffix(".").split(".")
            if not all(re.fullmatch(r"[a-zA-Z0-9_](?:[a-zA-Z0-9_-]{0,61}[a-zA-Z0-9_])?", label) for label in labels):
                raise ValueError("Укажите DNS-имя, IPv4 или IPv6 без порта.") from None
        if type(self.port) is not int or not 1 <= self.port <= 65535:
            raise ValueError("Укажите порт от 1 до 65535.")
        if (type(self.width) is not int or type(self.height) is not int or
                ((self.width, self.height) != (0, 0) and not (640 <= self.width <= 8192 and 480 <= self.height <= 8192))):
            raise ValueError("Укажите разрешение от 640 × 480 до 8192 × 8192 или оставьте оба поля пустыми.")
        if not isinstance(self.group, str) or not isinstance(self.clipboard, bool) or not isinstance(self.fullscreen, bool):
            raise ValueError("Некорректные параметры RDP-подключения.")
        return self

    def command(self):
        x11 = ("xfreerdp3", "xfreerdp")
        wayland = ("sdl-freerdp3", "sdl-freerdp", "wlfreerdp3", "wlfreerdp")
        names = x11 + wayland if os.environ.get("DISPLAY") else wayland + x11
        client = next((path for name in names if (path := shutil.which(name))), None)
        if not client:
            raise ValueError("Установите клиент FreeRDP из репозитория вашего дистрибутива.")
        args = [client, "/v:" + self.address, "/t:" + self.name, "+clipboard" if self.clipboard else "-clipboard"]
        if self.user:
            args.append("/u:" + self.user)
        if self.domain:
            args.append("/d:" + self.domain)
        if self.width:
            args.append(f"/size:{self.width}x{self.height}")
        if self.fullscreen:
            args.append("/f")
        return args

    @classmethod
    def import_file(cls, filename, group):
        with open(filename, "rb") as stream:
            contents = stream.read(256 * 1024 + 1)
        if len(contents) > 256 * 1024:
            raise ValueError("RDP-файл слишком большой.")
        encoding = "utf-16" if contents.startswith((b"\xff\xfe", b"\xfe\xff")) else "utf-8-sig"
        fields = {}
        for line in contents.decode(encoding).splitlines():
            parts = line.split(":", 2)
            if len(parts) == 3:
                fields[parts[0].strip().casefold()] = parts[2].strip()
        host = fields.get("full address", "")
        port = fields.get("server port", "3389")
        endpoint = re.fullmatch(r"(\[[^\]]+\]|[^:]+):([0-9]+)", host)
        if endpoint:
            host, port = endpoint.groups()
        user, domain = fields.get("username", ""), fields.get("domain", "")
        if not domain and "\\" in user:
            domain, user = user.split("\\", 1)
        width, height = int(fields.get("desktopwidth", "0")), int(fields.get("desktopheight", "0"))
        if not width or not height:
            width = height = 0
        return cls(uuid.uuid4().hex, Path(filename).stem, host.strip("[]"), int(port), user, domain,
                   group, width, height, fields.get("redirectclipboard") == "1", fields.get("screen mode id") == "2").validate()

    def export_file(self, filename):
        account = self.domain + "\\" + self.user if self.domain and self.user else self.user
        lines = [f"full address:s:{self.address}", f"username:s:{account}",
                 f"screen mode id:i:{2 if self.fullscreen else 1}", f"redirectclipboard:i:{int(self.clipboard)}",
                 "enablecredsspsupport:i:1", "authentication level:i:2", "drivestoredirect:s:", "redirectprinters:i:0"]
        if self.width:
            lines.extend((f"desktopwidth:i:{self.width}", f"desktopheight:i:{self.height}"))
        Path(filename).write_bytes(("\r\n".join(lines) + "\r\n").encode("utf-16"))
