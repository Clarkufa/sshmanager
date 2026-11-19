import os
from pathlib import Path
import shutil
import subprocess
import sys
import threading

from gi.repository import Gio, GLib, Gtk

from . import VERSION
from .updates import Update
from .widgets import button, confirm, message


class UpdateController:
    def __init__(self, owner):
        self.owner = owner
        self.root = Path(__file__).resolve().parent.parent
        self.control = button("Обновления", lambda: self.check(manual=True))
        self.checking = False
        self.installing = False

    def check(self, manual=False):
        if self.checking or self.installing:
            return
        self.checking = True
        self.control.set_sensitive(False)
        if manual:
            self.control.set_label("Проверяем…")
        self._run(lambda: Update.check(self.root), lambda update, error: self._checked(update, error, manual))

    def _checked(self, update, error, manual):
        self.checking = False
        self.control.set_sensitive(True)
        self.control.set_label(f"Обновление {update.version}" if update else "Обновления")
        if not manual:
            return
        if error:
            message(self.owner, f"Не удалось проверить обновления. {error}", Gtk.MessageType.ERROR)
        elif not update:
            message(self.owner, f"Установлена актуальная версия {VERSION}.")
        elif confirm(self.owner, f"Доступна версия {update.version}. Установить?",
                     "После установки приложение перезапустится и закроет активные SSH-сеансы и передачи файлов. "
                     "При установке DEB система может запросить права администратора."):
            self._install(update)

    def _install(self, update):
        self.installing = True
        self.control.set_sensitive(False)
        self.control.set_label("Устанавливаем…")

        def job():
            package = update.download()
            try:
                return package, update.install(package, self.root), None
            except Exception as error:
                return package, None, error

        self._run(job, self._installed)

    def _installed(self, result, download_error):
        self.installing = False
        self.control.set_sensitive(True)
        self.control.set_label("Обновления")
        if download_error:
            message(self.owner, f"Не удалось скачать обновление. {download_error}", Gtk.MessageType.ERROR)
            return
        package, replacement, install_error = result
        if install_error:
            self._manual(package, install_error)
            return
        try:
            self._restart()
        except OSError as error:
            if replacement:
                try:
                    replacement.rollback()
                except (OSError, RuntimeError):
                    error = f"{error}\nПредыдущая копия сохранена в {replacement.previous}."
            self._manual(package, error)
            return
        if replacement:
            replacement.clean()
        shutil.rmtree(package.parent, ignore_errors=True)

    def _manual(self, package, error):
        if confirm(self.owner, "Открыть папку со скачанным обновлением?",
                   f"Файл прошёл проверку. {error}\n\n{package}"):
            try:
                Gio.AppInfo.launch_default_for_uri(package.parent.as_uri(), None)
            except GLib.Error as failure:
                message(self.owner, f"Не удалось открыть папку.\n{package}\n{failure}", Gtk.MessageType.ERROR)

    def _restart(self):
        # The GTK application owns a D-Bus name. Its replacement starts only after this process exits.
        reader, writer = os.pipe()
        program = "import os,sys; fd=int(sys.argv[1]); os.read(fd,1); os.close(fd); os.execv(sys.argv[2],sys.argv[2:])"
        try:
            subprocess.Popen(
                [sys.executable, "-c", program, str(reader), str(self.root / "ssh-manager"), *sys.argv[1:]],
                pass_fds=(reader,), start_new_session=True,
            )
        except OSError:
            os.close(writer)
            raise
        finally:
            os.close(reader)
        # Keep the write end until exit. The child does not inherit it.
        self.restart_pipe = writer
        for shell in self.owner.shells[:]:
            shell.destroy()
        for window in list(self.owner.files.values()):
            window.destroy()
        self.owner.get_application().quit()

    @staticmethod
    def _run(work, finish):
        def execute():
            try:
                result, error = work(), None
            except Exception as failure:
                result, error = None, failure
            GLib.idle_add(finish, result, error)

        threading.Thread(target=execute, daemon=True).start()
