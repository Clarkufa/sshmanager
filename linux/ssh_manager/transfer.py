"""SFTP file operations over a subsystem started by the system OpenSSH client."""

import errno
import os
import posixpath
import socket
import stat
import subprocess
import tempfile
import threading
import uuid
from dataclasses import dataclass
from pathlib import Path

import paramiko

from .auth import AskPass, account_for, ssh_args


class Cancelled(Exception):
    pass


class Pipe:
    # Paramiko's SFTP protocol accepts a socket-like, already authenticated stream.
    def __init__(self, arguments, environment):
        self.channel, child = socket.socketpair()
        try:
            self.process = subprocess.Popen(arguments, env=environment, stdin=child, stdout=child,
                                            stderr=subprocess.PIPE, bufsize=0)
        except BaseException:
            self.channel.close()
            raise
        finally:
            child.close()
        self.channel.settimeout(120)
        self.messages = bytearray()
        threading.Thread(target=self.drain_errors, daemon=True).start()

    def drain_errors(self):
        try:
            while True:
                block = self.process.stderr.read(2048)
                if not block:
                    return
                self.messages.extend(block)
                del self.messages[:-4096]
        except (OSError, ValueError):
            pass

    def send(self, data):
        return self.channel.send(data)

    def get_name(self):
        return "OpenSSH SFTP"

    def recv(self, size):
        return self.channel.recv(size)

    def close(self):
        try:
            self.channel.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self.channel.close()
        if self.process.poll() is None:
            self.process.terminate()
        try:
            self.process.wait(timeout=1)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        self.process.stderr.close()


@dataclass
class Item:
    name: str
    kind: str
    size: int
    modified: float

    @property
    def directory(self):
        return self.kind == "folder"


def item(name, mode, size, modified):
    kind = "folder" if stat.S_ISDIR(mode) else "link" if stat.S_ISLNK(mode) else "file" if not mode or stat.S_ISREG(mode) else "other"
    return Item(name, kind, size or 0, modified or 0)


def local_items(path):
    result = []
    for child in Path(path).iterdir():
        try:
            info = child.lstat()
        except FileNotFoundError:
            continue
        result.append(item(child.name, info.st_mode, info.st_size, info.st_mtime))
    return result


def remote_items(client, path):
    result = []
    for attrs in client.listdir_attr(path):
        name = attrs.filename
        if not name or name in (".", "..") or "/" in name or "\0" in name:
            continue
        result.append(item(name, attrs.st_mode or 0, attrs.st_size, attrs.st_mtime))
    return result


class Remote:
    def __init__(self, profile, config, vault):
        account = account_for(profile)
        try:
            password = vault.lookup(account)
        except (OSError, RuntimeError, subprocess.TimeoutExpired):
            password = None
        self.broker = AskPass(account if password else None, password)
        self.pipe = None
        self.client = None
        try:
            self.pipe = Pipe(ssh_args(profile, config, account if password else None, files=True), self.broker.environment())
            self.broker.attach(self.pipe.process.pid)
        except BaseException:
            self.close()
            raise

    def open(self):
        try:
            self.client = paramiko.SFTPClient(self.pipe)
            return self.client.normalize(".")
        except (OSError, EOFError, paramiko.SSHException) as error:
            detail = self.pipe.messages.decode(errors="replace").strip()
            raise OSError(detail or f"Не удалось открыть SFTP: {error}") from error

    def close(self):
        self.broker.close()
        if self.pipe:
            self.pipe.close()


def absent(call, path):
    try:
        return call(path)
    except (FileNotFoundError, OSError) as error:
        if error.errno == errno.ENOENT:
            return None
        raise


class Copies:
    def __init__(self, client, cancelled, conflict, progress):
        self.client = client
        self.cancelled = cancelled
        self.conflict = conflict
        self.progress = progress
        self.completed = self.skipped = 0

    def check(self, depth=0):
        if self.cancelled.is_set():
            raise Cancelled()
        if depth > 64:
            raise OSError("Слишком много уровней вложенных папок.")

    def choose(self, destination, expected_directory, inspect, join):
        existing = absent(inspect, destination)
        if existing is None:
            return destination, False
        mode = existing.st_mode or 0
        if expected_directory and stat.S_ISDIR(mode):
            return destination, False
        allows_replace = not expected_directory and stat.S_ISREG(mode)
        action = self.conflict(str(destination), allows_replace)
        self.check()
        if action == "cancel":
            raise Cancelled()
        if action == "skip":
            self.skipped += 1
            return None, False
        if action == "replace" and allows_replace:
            return destination, True
        name = posixpath.basename(str(destination))
        stem, extension = posixpath.splitext(name)
        for number in range(2, 10000):
            suggested = join(f"{stem} ({number}){extension}")
            if absent(inspect, suggested) is None:
                return suggested, False
        raise OSError("Не удалось подобрать свободное имя.")

    def upload(self, path, directory, depth=0):
        self.check(depth)
        path = Path(path)
        source = path.lstat()
        folder = stat.S_ISDIR(source.st_mode)
        if not folder and not stat.S_ISREG(source.st_mode):
            self.skipped += 1
            return
        if not path.name:
            raise ValueError("Выберите файл или папку внутри диска.")
        remote = self.client
        join = lambda name: posixpath.join(directory, name)
        destination, replace = self.choose(join(path.name), folder, remote.lstat, join)
        if destination is None:
            return
        if folder:
            if absent(remote.lstat, destination) is None:
                remote.mkdir(destination)
            for child in path.iterdir():
                self.upload(child, destination, depth + 1)
            return
        temporary = join(f".ssh-manager-{uuid.uuid4().hex}.part")
        owned = False
        try:
            with path.open("rb") as input_file, remote.open(temporary, "wx") as output:
                owned = True
                output.set_pipelined(True)
                self.copy_file(input_file, output, path.name, source.st_size)
            self.check()
            if replace:
                previous = remote.lstat(destination)
                if not stat.S_ISREG(previous.st_mode or 0):
                    raise OSError("Тип файла на сервере изменился.")
                remote.chmod(temporary, stat.S_IMODE(previous.st_mode))
                self.replace_remote(temporary, destination, join)
            else:
                remote.rename(temporary, destination)
            owned = False
            self.completed += 1
        finally:
            if owned:
                try:
                    remote.remove(temporary)
                except (OSError, EOFError, paramiko.SSHException):
                    pass

    def replace_remote(self, source, destination, join):
        try:
            self.client.posix_rename(source, destination)
        except OSError:
            if absent(self.client.lstat, source) is None or absent(self.client.lstat, destination) is None:
                raise
            recovery = join(f".ssh-manager-{uuid.uuid4().hex}.previous")
            self.client.rename(destination, recovery)
            try:
                self.client.rename(source, destination)
            except OSError:
                try:
                    self.client.rename(recovery, destination)
                except OSError as error:
                    raise OSError(f"Прежний файл остался на сервере как {recovery}.") from error
                raise
            self.client.remove(recovery)

    def download(self, directory, name, destination_dir, depth=0):
        self.check(depth)
        if not name or name in (".", "..") or "/" in name or "\0" in name:
            raise OSError("Сервер вернул недопустимое имя.")
        destination_dir = Path(destination_dir)
        remote = self.client
        source = posixpath.join(directory, name)
        info = remote.lstat(source)
        mode = info.st_mode or 0
        folder = stat.S_ISDIR(mode)
        if mode and not folder and not stat.S_ISREG(mode):
            self.skipped += 1
            return
        join = lambda child: destination_dir / child
        destination, replace = self.choose(join(name), folder, Path.lstat, join)
        if destination is None:
            return
        if folder:
            destination.mkdir(exist_ok=True)
            for child in remote_items(remote, source):
                self.download(source, child.name, destination, depth + 1)
            return
        fd, temporary = tempfile.mkstemp(prefix=".ssh-manager-", suffix=".part", dir=destination_dir)
        try:
            with os.fdopen(fd, "wb") as output, remote.open(source, "rb") as input_file:
                if info.st_size is None:
                    self.copy_file(input_file, output, name, None)
                else:
                    self.copy_remote(input_file, output, name, info.st_size)
            self.check()
            if replace:
                os.replace(temporary, destination)
            else:
                os.link(temporary, destination)
            self.completed += 1
        finally:
            Path(temporary).unlink(missing_ok=True)

    def copy_file(self, source, destination, name, size):
        total = 0
        self.progress(name, total, size)
        while True:
            self.check()
            block = source.read(128 * 1024)
            if not block:
                break
            destination.write(block)
            total += len(block)
            self.progress(name, total, size)
        if size is not None and total != size:
            raise OSError("Размер исходного файла изменился во время передачи.")

    def copy_remote(self, source, destination, name, size):
        total = 0
        self.progress(name, 0, size)
        while total < size:
            block_size = 32 * 1024
            requests = [(offset, min(block_size, size - offset))
                        for offset in range(total, min(total + 16 * block_size, size), block_size)]
            for block, (_, expected) in zip(source.readv(requests), requests):
                self.check()
                if len(block) != expected:
                    raise OSError("Размер исходного файла изменился во время передачи.")
                destination.write(block)
                total += len(block)
        self.progress(name, total, size)
