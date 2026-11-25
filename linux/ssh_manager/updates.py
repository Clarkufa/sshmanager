import ast
import ctypes
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tarfile
import tempfile
from dataclasses import dataclass
from urllib.error import URLError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener

from . import VERSION

REPOSITORY = "https://github.com/Clarkufa/sshmanager"
LATEST = "https://api.github.com/repos/Clarkufa/sshmanager/releases/latest"
HOSTS = {"github.com", "api.github.com", "objects.githubusercontent.com",
         "release-assets.githubusercontent.com", "github-releases.githubusercontent.com"}
DEB_ROOT = Path("/usr/lib/ssh-manager")
RPM_ROOT = Path("/usr/share/ssh-manager")
DEB = "SSH-Manager-Linux.deb"
RPM = "SSH-Manager-Linux.rpm"
ARCHIVE = "SSH-Manager-Linux.tar.gz"
PACKAGE_ASSETS = {DEB_ROOT: DEB, RPM_ROOT: RPM}
MAX_ASSET = 64 * 1024 * 1024
MAX_TAR = 16 * 1024 * 1024
MANIFEST = ".ssh-manager-checksums.json"
TOP_FILES = {"ssh-manager", "icon.png", "README.md", "LICENSE", MANIFEST}


class UpdateError(RuntimeError):
    pass


class UpdateRedirects(HTTPRedirectHandler):
    max_redirections = 5

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if not trusted_url(newurl):
            raise URLError("Недопустимая ссылка на обновление SSH Manager.")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def trusted_url(address):
    uri = urlsplit(address)
    return (uri.scheme == "https" and uri.hostname in HOSTS and uri.port in (None, 443)
            and uri.username is None and uri.password is None)


def request(address, *, metadata=False):
    headers = {"User-Agent": "SSHManager/" + VERSION}
    if metadata:
        headers["Accept"] = "application/vnd.github+json"
    response = build_opener(UpdateRedirects()).open(Request(address, headers=headers), timeout=20)
    if response.status != 200 or not trusted_url(response.url):
        response.close()
        raise UpdateError("GitHub не вернул ожидаемый файл обновления.")
    return response


def version_number(text):
    if not isinstance(text, str) or not re.fullmatch(r"(?:0|[1-9][0-9]{0,7})(?:\.(?:0|[1-9][0-9]{0,7})){2}", text):
        raise UpdateError("Не удалось определить версию выпуска.")
    return tuple(map(int, text.split(".")))


@dataclass(frozen=True)
class Update:
    version: str
    name: str
    size: int
    digest: str

    @classmethod
    def from_release(cls, data, *, current, name):
        release = json.loads(data)
        if not isinstance(release, dict) or release.get("draft") is not False or release.get("prerelease") is not False:
            raise UpdateError("GitHub не вернул стабильный выпуск SSH Manager.")
        tag = release.get("tag_name")
        if not isinstance(tag, str) or not tag.startswith("v"):
            raise UpdateError("Не удалось определить версию выпуска.")
        version = tag[1:]
        if version_number(version) <= version_number(current):
            return None
        assets = release.get("assets")
        if not isinstance(assets, list):
            raise UpdateError("GitHub не вернул файлы выпуска.")
        matches = [asset for asset in assets if isinstance(asset, dict) and asset.get("name") == name]
        if len(matches) != 1:
            raise UpdateError("В выпуске нет файла обновления для Linux.")
        asset = matches[0]
        size, digest = asset.get("size"), asset.get("digest")
        if (asset.get("state") != "uploaded" or type(size) is not int or not 0 < size <= MAX_ASSET
                or not isinstance(digest, str) or not re.fullmatch(r"sha256:[0-9a-fA-F]{64}", digest)):
            raise UpdateError("В выпуске нет проверяемого файла для Linux.")
        return cls(version, name, size, digest[7:].lower())

    @classmethod
    def check(cls, root):
        limit = 512 * 1024
        with request(LATEST, metadata=True) as response:
            data = response.read(limit + 1)
        if len(data) > limit:
            raise UpdateError("Описание выпуска превышает допустимый размер.")
        return cls.from_release(data, current=VERSION, name=PACKAGE_ASSETS.get(root, ARCHIVE))

    def download(self):
        folder = Path(tempfile.mkdtemp(prefix="ssh-manager-update-"))
        target = folder / self.name
        try:
            address = f"{REPOSITORY}/releases/download/v{self.version}/{self.name}"
            digest = hashlib.sha256()
            size = 0
            with request(address) as response, target.open("xb") as output:
                for chunk in iter(lambda: response.read(32768), b""):
                    size += len(chunk)
                    if size > self.size:
                        raise UpdateError("Файл обновления превышает ожидаемый размер.")
                    output.write(chunk)
                    digest.update(chunk)
            if size != self.size or digest.hexdigest() != self.digest:
                raise UpdateError("Контрольная сумма скачанного файла не совпала с опубликованной.")
            return target
        except Exception:
            shutil.rmtree(folder, ignore_errors=True)
            raise

    def install(self, package, root):
        if self.name == DEB:
            install_deb(package, self.version)
            return None
        if self.name == RPM:
            install_rpm(package, self.version)
            return None
        return BundleReplacement.install(package, root, self.version)


def install_deb(package, version):
    apt = Path("/usr/bin/apt-get")
    pkexec = Path("/usr/bin/pkexec")
    if not (apt.is_file() and pkexec.is_file()):
        raise UpdateError("Для установки из приложения нужны apt и системный запрос прав администратора.")
    fields = subprocess.run(
        ["/usr/bin/dpkg-deb", "--showformat=${Package}\n${Version}", "--show", str(package)],
        check=True, text=True, capture_output=True, timeout=10,
    ).stdout.splitlines()
    if fields != ["ssh-manager-desktop", version]:
        raise UpdateError("Загружен пакет другой программы или версии.")
    install_system_package(apt, package)


def install_rpm(package, version):
    dnf = Path("/usr/bin/dnf")
    if not (dnf.is_file() and Path("/usr/bin/pkexec").is_file()):
        raise UpdateError("Для установки из приложения нужны DNF и системный запрос прав администратора.")
    fields = subprocess.run(
        ["/usr/bin/rpm", "--query", "--package", "--queryformat", "%{NAME}\n%{VERSION}\n%{ARCH}", str(package)],
        check=True, text=True, capture_output=True, timeout=10,
    ).stdout.splitlines()
    if fields != ["ssh-manager-desktop", version, "noarch"]:
        raise UpdateError("Загружен пакет другой программы, версии или архитектуры.")
    install_system_package(dnf, package)


def install_system_package(manager, package):
    result = subprocess.run(
        ["/usr/bin/pkexec", "--disable-internal-agent", str(manager), "install", "-y", str(package)],
        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=300,
    )
    if result.returncode:
        raise UpdateError("Системная установка не завершилась. Пакет можно установить вручную.")


def exchange(first, second):
    libc = ctypes.CDLL(None, use_errno=True)
    try:
        rename = libc.renameat2
    except AttributeError:
        raise UpdateError("Система не поддерживает безопасную замену папки приложения.") from None
    rename.argtypes = (ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint)
    rename.restype = ctypes.c_int
    at_fdcwd, rename_exchange = -100, 2
    if rename(at_fdcwd, os.fsencode(first), at_fdcwd, os.fsencode(second), rename_exchange):
        error = ctypes.get_errno()
        raise OSError(error, os.strerror(error))


@dataclass(frozen=True)
class BundleReplacement:
    live: Path
    previous: Path

    @classmethod
    def install(cls, archive, root, version):
        verify_portable_folder(root)
        current = installed_version(root)
        if version_number(current) >= version_number(version):
            raise UpdateError("Эта версия уже установлена или устарела.")
        stage = Path(tempfile.mkdtemp(prefix=".ssh-manager-update-", dir=root.parent))
        candidate = stage / "app"
        try:
            unpack_application(archive, candidate, version)
            # Check again before replacing a directory that may have gained local files during extraction.
            verify_portable_folder(root)
            if installed_version(root) != current:
                raise UpdateError("Версия приложения изменилась во время установки.")
            exchange(root, candidate)
        except Exception:
            shutil.rmtree(stage, ignore_errors=True)
            raise
        return cls(root, candidate)

    def rollback(self):
        exchange(self.live, self.previous)
        self.clean()

    def clean(self):
        shutil.rmtree(self.previous.parent, ignore_errors=True)


def verify_portable_folder(root):
    modules = root / "ssh_manager"
    custom = UpdateError("В папке приложения есть локальные изменения. Распакуйте проверенный архив вручную.")
    if (root.is_symlink() or root.stat().st_uid != os.getuid()
            or not modules.is_dir() or modules.is_symlink()
            or {path.name for path in root.iterdir()} != TOP_FILES | {"ssh_manager"}
            or any(path.is_symlink() or not path.is_file() for path in (root / name for name in TOP_FILES))
            or any(path.is_symlink() or (path.name != "__pycache__" and
                   (path.suffix != ".py" or not path.is_file())) for path in modules.iterdir())):
        raise custom
    cache = modules / "__pycache__"
    if cache.exists() and (not cache.is_dir() or any(path.is_symlink() or path.suffix != ".pyc" or not path.is_file()
                                                   for path in cache.iterdir())):
        raise custom
    manifest = root / MANIFEST
    paths = [root / name for name in TOP_FILES - {MANIFEST}] + list(modules.glob("*.py"))
    if len(paths) >= 128 or manifest.stat().st_size > 512 * 1024 or sum(path.stat().st_size for path in paths) > MAX_TAR:
        raise custom
    expected = json.loads(manifest.read_text(encoding="utf-8"))
    actual = {path.relative_to(root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest() for path in paths}
    if actual != expected:
        raise custom


def unpack_application(archive, destination, version):
    destination.mkdir()
    prefix = "ssh-manager-" + version
    names = set()
    total = 0
    with tarfile.open(archive, mode="r:gz") as contents:
        for member in contents:
            path = PurePosixPath(member.name)
            parts = path.parts
            if (not parts or parts[0] != prefix or ".." in parts or path in names or len(names) >= 128
                    or not (member.isdir() or member.isfile())):
                raise UpdateError("Архив содержит недопустимые файлы.")
            names.add(path)
            output = destination.joinpath(*parts[1:])
            if member.isdir() and parts[1:] in ((), ("ssh_manager",)):
                output.mkdir(exist_ok=True)
                continue
            if (not member.isfile() or not (len(parts) == 2 and parts[1] in TOP_FILES
                    or len(parts) == 3 and parts[1] == "ssh_manager" and path.suffix == ".py")):
                raise UpdateError("Архив содержит недопустимые файлы.")
            total += member.size
            if total > MAX_TAR:
                raise UpdateError("Распакованный архив превышает допустимый размер.")
            output.parent.mkdir(exist_ok=True)
            with contents.extractfile(member) as source, output.open("xb") as file:
                shutil.copyfileobj(source, file)
            output.chmod(0o755 if output.name == "ssh-manager" else 0o644)
    verify_portable_folder(destination)
    if installed_version(destination) != version or not (destination / "ssh_manager" / "app.py").is_file():
        raise UpdateError("Архив не содержит приложение нужной версии.")


def installed_version(destination):
    module = ast.parse((destination / "ssh_manager" / "__init__.py").read_text(encoding="utf-8"))
    assignment = module.body[0] if module.body else None
    if not (isinstance(assignment, ast.Assign) and len(assignment.targets) == 1
            and isinstance(assignment.targets[0], ast.Name) and assignment.targets[0].id == "VERSION"
            and isinstance(assignment.value, ast.Constant) and isinstance(assignment.value.value, str)):
        raise UpdateError("Не удалось определить установленную версию.")
    return assignment.value.value
