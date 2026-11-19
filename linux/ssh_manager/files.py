import datetime
import posixpath
import threading
import time
from pathlib import Path

from gi.repository import Gdk, Gio, GLib, Gtk

from .transfer import Cancelled, Copies, Remote, local_items, remote_items
from .widgets import button, chooser, confirm, menu_at, message, prompt


class Pane(Gtk.Box):
    def __init__(self, title, browse):
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        head = Gtk.Box(spacing=6)
        name = Gtk.Label(label=title, xalign=0)
        name.get_style_context().add_class("section-title")
        self.path = ""
        self.entry = Gtk.Entry(placeholder_text="Путь к папке")
        self.entry.connect("activate", lambda widget: browse(widget.get_text()))
        head.pack_start(name, True, True, 0)
        head.pack_start(button("Вверх", lambda: browse("..")), False, False, 0)
        head.pack_start(button("Обновить", lambda: browse(self.path)), False, False, 0)
        self.pack_start(head, False, False, 0)
        self.pack_start(self.entry, False, False, 0)
        self.model = Gtk.ListStore(str, str, str, str, object)
        self.view = Gtk.TreeView(model=self.model, enable_search=True, search_column=1)
        self.view.get_selection().set_mode(Gtk.SelectionMode.MULTIPLE)
        name_cell = Gtk.CellRendererText(ellipsize=3)
        icon = Gtk.CellRendererPixbuf()
        column = Gtk.TreeViewColumn("Имя")
        column.pack_start(icon, False)
        column.add_attribute(icon, "icon-name", 0)
        column.pack_start(name_cell, True)
        column.add_attribute(name_cell, "text", 1)
        column.set_expand(True)
        column.set_min_width(115)
        self.view.append_column(column)
        self.view.append_column(Gtk.TreeViewColumn("Размер", Gtk.CellRendererText(xalign=1), text=2))
        self.view.append_column(Gtk.TreeViewColumn("Изменён", Gtk.CellRendererText(), text=3))
        self.view.connect("row-activated", lambda view, index, column: self.open(index, browse))
        scroll = Gtk.ScrolledWindow(shadow_type=Gtk.ShadowType.IN)
        scroll.add(self.view)
        self.pack_start(scroll, True, True, 0)

    def open(self, index, browse):
        item = self.model[index][4]
        if item.directory or item.kind == "link":
            browse(item.name)

    def display(self, path, items):
        self.path = str(path)
        self.entry.set_text(self.path)
        self.model.clear()
        icons = {"folder": "folder-symbolic", "link": "emblem-symbolic-link-symbolic",
                 "file": "text-x-generic-symbolic", "other": "dialog-information-symbolic"}
        for item in sorted(items, key=lambda entry: (not entry.directory, entry.name.casefold())):
            date = ""
            if item.modified:
                try:
                    date = datetime.datetime.fromtimestamp(item.modified).strftime("%d.%m.%y %H:%M")
                except (ValueError, OverflowError, OSError):
                    pass
            self.model.append((icons[item.kind], item.name, GLib.format_size(item.size) if item.kind == "file" else "—",
                               date, item))

    def selected(self):
        _, paths = self.view.get_selection().get_selected_rows()
        return [self.model[path][4] for path in paths]


class FilesWindow(Gtk.ApplicationWindow):
    def __init__(self, owner, profile, config, vault):
        super().__init__(application=owner.get_application(), transient_for=owner,
                         title=f"Файлы · {profile.alias} · {profile.address}")
        self.profile, self.config, self.vault = profile, config, vault
        self.set_default_size(1050, 635)
        self.set_destroy_with_parent(True)
        self.remote = None
        self.operation = None
        self.destroyed = False
        self.connect("destroy", self.closed)
        self.connect("delete-event", self.can_close)

        outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12, margin=16)
        self.add(outer)
        self.left = Pane("Компьютер", self.browse_local)
        self.right = Pane(f"{profile.alias} · {profile.address}", self.browse_remote)
        panels = Gtk.Paned.new(Gtk.Orientation.HORIZONTAL)
        panels.pack1(self.left, True, False)
        panels.pack2(self.right, True, False)
        panels.set_position(500)
        outer.pack_start(panels, True, True, 0)
        actions = Gtk.Box(spacing=7)
        self.upload = button("На сервер →", self.upload_selected, suggested=True)
        self.download = button("← На компьютер", self.download_selected)
        self.cancel = button("Отменить", self.cancel_transfer)
        self.new_folder = button("Новая папка", self.mkdir)
        self.reconnect = button("Переподключить", self.connect_server)
        for widget in (self.upload, self.download, self.cancel, self.new_folder, self.reconnect):
            actions.pack_start(widget, False, False, 0)
        actions.pack_end(button("Локальная папка…", self.choose_local), False, False, 0)
        outer.pack_start(actions, False, False, 0)
        self.progress = Gtk.ProgressBar(show_text=True, no_show_all=True)
        self.status = Gtk.Label(xalign=0, ellipsize=3)
        outer.pack_start(self.progress, False, False, 0)
        outer.pack_start(self.status, False, False, 0)
        for pane in (self.left, self.right):
            pane.view.get_selection().connect("changed", lambda *_: self.update_actions())
        self.right.view.connect("button-press-event", self.remote_menu)
        self.right.view.drag_dest_set(Gtk.DestDefaults.ALL,
                                      [Gtk.TargetEntry.new("text/uri-list", 0, 0)], Gdk.DragAction.COPY)
        self.right.view.connect("drag-data-received", self.dropped)
        self.show_all()
        self.browse_local(str(Path.home()))
        self.connect_server()

    def can_close(self, *_):
        return self.operation is not None and not confirm(self, "Прервать операцию и закрыть окно файлов?")

    def closed(self, *_):
        self.destroyed = True
        if self.operation:
            self.operation.set()
        if self.remote:
            remote, self.remote = self.remote, None
            threading.Thread(target=remote.close, daemon=True).start()

    def update_actions(self):
        available = self.operation is None
        ready = available and self.remote is not None and bool(self.right.path)
        self.left.set_sensitive(available)
        self.right.set_sensitive(ready)
        self.upload.set_sensitive(ready and bool(self.left.selected()))
        self.download.set_sensitive(ready and bool(self.right.selected()) and bool(self.left.path))
        self.cancel.set_sensitive(not available and not self.operation.is_set())
        self.new_folder.set_sensitive(ready)
        self.reconnect.set_sensitive(available)

    def run(self, caption, work, complete, transferring=False):
        if self.operation is not None:
            return
        cancelled = threading.Event()
        self.operation = cancelled
        self.status.set_text(caption)
        if transferring:
            self.progress.set_fraction(0)
            self.progress.set_text("")
            self.progress.show()
        self.update_actions()

        def worker():
            try:
                result = work(cancelled)
            except Exception as exc:
                GLib.idle_add(finished, None, exc)
            else:
                GLib.idle_add(finished, result, None)

        def finished(result, error):
            if self.destroyed:
                return False
            self.operation = None
            self.progress.hide()
            if cancelled.is_set() or isinstance(error, Cancelled):
                self.status.set_text("Операция отменена.")
            elif error:
                self.status.set_text(str(error))
                message(self, error, Gtk.MessageType.ERROR)
            else:
                complete(result)
            self.update_actions()
            return False

        threading.Thread(target=worker, daemon=True).start()

    def connect_server(self):
        previous, self.remote = self.remote, None
        self.right.display("", [])

        def connect(cancelled):
            if previous:
                previous.close()
            session = Remote(self.profile, self.config, self.vault)
            try:
                if cancelled.is_set():
                    raise Cancelled()
                self.remote = session
                path = session.open()
                data = remote_items(session.client, path)
                if cancelled.is_set():
                    raise Cancelled()
                return path, data
            except BaseException:
                self.remote = None
                session.close()
                raise

        self.run("Подключение SFTP…", connect, self.show_remote)

    def cancel_transfer(self):
        if not self.operation:
            return
        active = self.operation
        active.set()
        self.status.set_text("Отмена…")
        self.update_actions()

        def interrupt():
            if self.operation is active and self.remote and not self.destroyed:
                remote, self.remote = self.remote, None
                self.right.display("", [])
                threading.Thread(target=remote.close, daemon=True).start()
            return False

        GLib.timeout_add(1500, interrupt)

    def choose_local(self):
        path = chooser(self, "Локальная папка", Gtk.FileChooserAction.SELECT_FOLDER, directory=self.left.path)
        if path:
            self.browse_local(path)

    def browse_local(self, value):
        if not value:
            return
        path = Path(value).expanduser()
        if not path.is_absolute():
            path = Path(self.left.path) / path
        try:
            path = path.resolve(strict=True)
            self.left.display(path, local_items(path))
        except OSError as error:
            message(self, error, Gtk.MessageType.ERROR)
            self.left.entry.set_text(self.left.path)
        self.update_actions()

    def show_remote(self, result):
        path, data = result
        self.right.display(path, data)
        self.status.set_text("SFTP подключён · " + self.profile.alias)

    def browse_remote(self, value):
        if self.remote is None or not value or self.operation:
            return
        path = value if value.startswith("/") else posixpath.join(self.right.path, value)
        client = self.remote.client

        def load(cancelled):
            target = client.normalize(path)
            return target, remote_items(client, target)

        self.run("Чтение папки…", load, self.show_remote)

    def remote_menu(self, view, event):
        if event.button != 3 or self.operation:
            return False
        position = view.get_path_at_pos(int(event.x), int(event.y))
        if not position:
            return False
        selection = view.get_selection()
        if not selection.path_is_selected(position[0]):
            selection.unselect_all()
            selection.select_path(position[0])
        return menu_at(view, event, [
            ("Скачать", self.download_selected), ("Переименовать", self.rename if len(self.right.selected()) == 1 else None),
            ("Удалить", self.remove),
        ])

    @staticmethod
    def check_name(name):
        if not name or name in (".", "..") or "/" in name or "\0" in name:
            raise ValueError("Укажите имя без разделителя пути.")

    def modify(self, action):
        if not self.remote or self.operation:
            return
        client, path = self.remote.client, self.right.path

        def work(cancelled):
            action(client, path)
            return path, remote_items(client, path)

        self.run("Обновление папки…", work, self.show_remote)

    def mkdir(self):
        name = prompt(self, "Новая папка на сервере")
        if name is not None:
            def create(client, path):
                self.check_name(name)
                client.mkdir(posixpath.join(path, name))
            self.modify(create)

    def rename(self):
        [selected] = self.right.selected()
        name = prompt(self, "Переименовать на сервере", selected.name)
        if name is not None and selected.name != name:
            def move(client, path):
                self.check_name(name)
                client.rename(posixpath.join(path, selected.name), posixpath.join(path, name))
            self.modify(move)

    def remove(self):
        selected = self.right.selected()
        if not selected or not confirm(self, f"Удалить выбранные элементы ({len(selected)})?\n"
                                             f"{self.profile.alias} · {self.right.path}"):
            return

        def remove_items(client, path):
            for selected_item in selected:
                full = posixpath.join(path, selected_item.name)
                if selected_item.directory:
                    client.rmdir(full)
                else:
                    client.remove(full)

        self.modify(remove_items)

    def dropped(self, widget, context, x, y, selection, info, when):
        files = [Gio.File.new_for_uri(uri).get_path() for uri in (selection.get_uris() or [])]
        paths = [path for path in files if path]
        if paths and self.remote and not self.operation:
            self.transfer(paths, True)
            Gtk.drag_finish(context, True, False, when)
        else:
            Gtk.drag_finish(context, False, False, when)

    def upload_selected(self):
        self.transfer([str(Path(self.left.path) / item.name) for item in self.left.selected()], True)

    def download_selected(self):
        self.transfer([item.name for item in self.right.selected()], False)

    def transfer(self, names, upload):
        if not names or not self.remote or self.operation or not self.right.path:
            return
        client, directory, destination = self.remote.client, self.right.path, self.left.path
        last = 0.0

        def work(cancelled):
            def progress(name, done, size):
                nonlocal last
                now = time.monotonic()
                if now - last > .09 or done == size:
                    last = now
                    GLib.idle_add(self.progress_at, cancelled, name, done, size)

            copies = Copies(client, cancelled, lambda path, replace: self.choose_conflict(cancelled, path, replace), progress)
            for name in names:
                if upload:
                    copies.upload(name, directory)
                else:
                    copies.download(directory, name, destination)
            return copies.completed, copies.skipped, remote_items(client, directory) if upload else local_items(destination)

        def complete(result):
            count, skipped, items = result
            (self.right if upload else self.left).display(directory if upload else destination, items)
            self.status.set_text(f"Передано файлов: {count}" + (f" · пропущено: {skipped}" if skipped else ""))

        self.run("Загрузка на сервер…" if upload else "Скачивание на компьютер…", work, complete, True)

    def progress_at(self, active, name, done, size):
        if self.operation is active and not active.is_set() and not self.destroyed:
            if size:
                self.progress.set_fraction(min(1, done / size))
            else:
                self.progress.pulse()
            self.progress.set_text(f"{name} · {GLib.format_size(done)}")
        return False

    def choose_conflict(self, cancelled, path, replace):
        ready = threading.Event()
        decision = ["cancel"]

        def show():
            if not cancelled.is_set() and not self.destroyed:
                dialog = Gtk.MessageDialog(transient_for=self, modal=True, message_type=Gtk.MessageType.QUESTION,
                                           text="Файл или папка с таким именем уже существует")
                dialog.format_secondary_text(path)
                dialog.add_button("Отмена", 0)
                dialog.add_button("Пропустить", 1)
                dialog.add_button("Сохранить оба", 2)
                if replace:
                    dialog.add_button("Заменить", 3)
                dialog.set_default_response(2)
                answer = dialog.run()
                dialog.destroy()
                decision[0] = {1: "skip", 2: "both", 3: "replace"}.get(answer, "cancel")
            ready.set()
            return False

        GLib.idle_add(show)
        while not ready.wait(.2):
            if cancelled.is_set():
                break
        return decision[0]
