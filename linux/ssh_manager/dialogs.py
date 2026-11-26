"""Dialogs with the Windows layout and wording (docs/ui-spec.md §4)."""

import uuid
from dataclasses import replace
from pathlib import Path

from gi.repository import Gdk, Gio, GLib, Gtk, Pango

import os
import re
import socket
import subprocess
import threading

from .auth import account_for
from .config import FORWARD_KINDS, Profile
from . import known_hosts
from .extras import generate_key
from .rdp import DesktopProfile
from .vnc import VncProfile
from .theme import mark
from .widgets import action_button, chooser, dialog_buttons, themed

IMPORT_RESPONSE = 7


def _label(text, muted=False, bold=False, wrap=False):
    label = Gtk.Label(label=text, xalign=0, wrap=wrap)
    if wrap:
        label.set_line_wrap_mode(Pango.WrapMode.WORD_CHAR)
    if muted:
        mark(label, "sshm-muted")
    if bold:
        mark(label, "sshm-emphasis")
    return label


def _grid(dialog, label_width):
    grid = Gtk.Grid(column_spacing=12, row_spacing=8, margin_start=20, margin_end=20, margin_top=14, margin_bottom=10)
    grid.label_width = label_width
    dialog.get_content_area().add(grid)
    return grid


def _field(grid, row, caption, value="", hidden=False, limit=0):
    label = _label(caption)
    label.set_size_request(grid.label_width, -1)
    grid.attach(label, 0, row, 1, 1)
    entry = Gtk.Entry(text=str(value or ""), visibility=not hidden, activates_default=True, hexpand=True,
                      max_length=limit)
    if hidden:
        entry.set_input_purpose(Gtk.InputPurpose.PASSWORD)
    grid.attach(entry, 1, row, 2, 1)
    return entry


class ProfileDialog(Gtk.Dialog):
    """SSH connection: same fields, hints and key controls as src/ConnectionDialog.cs."""

    def __init__(self, owner, profile, keys, has_password=False, key_error=None, defaults=None):
        super().__init__(title="Изменить подключение" if profile else "Новое подключение", transient_for=owner, modal=True)
        themed(self)
        self.set_default_size(724, 700)
        dialog_buttons(self, "Сохранить")
        self.original = profile
        self.has_password = has_password
        if profile is None:
            defaults = defaults or {}
            profile = Profile("", user=defaults.get("default_user", ""), port=defaults.get("default_port", ""))
        grid = _grid(self, 138)
        self.entries = {}
        for row, (name, caption) in enumerate((("alias", "Псевдоним"), ("host", "Хост"), ("user", "Пользователь"),
                                               ("port", "Порт"), ("password", "Пароль"))):
            self.entries[name] = _field(grid, row, caption, "" if name == "password" else getattr(profile, name, ""),
                                        hidden=name == "password")
        options = Gtk.Box(spacing=12)
        self.password_hint = _label("", muted=True)
        self.forget = Gtk.CheckButton(label="Удалить пароль")
        self.forget.connect("toggled", lambda check: self.entries["password"].set_sensitive(not check.get_active()))
        options.pack_start(self.password_hint, False, False, 0)
        options.pack_start(self.forget, False, False, 0)
        grid.attach(options, 1, 5, 2, 1)
        grid.attach(_label("Пустые поля используют настройки OpenSSH.", muted=True), 1, 6, 2, 1)

        grid.attach(_label("Доступные ключи"), 0, 7, 1, 1)
        self.available = Gtk.ComboBoxText(hexpand=True)
        for key in keys:
            self.available.append_text(key)
        if keys:
            self.available.set_active(0)
        grid.attach(self.available, 1, 7, 1, 1)
        grid.attach(action_button("Обзор…", self.browse, icon="browse", width=100), 2, 7, 1, 1)
        hint = ("Не удалось прочитать папку .ssh. Можно выбрать файл вручную." if key_error else
                "Ключи найдены в папке .ssh." if keys else
                "В папке .ssh ключи не найдены. Можно указать файл через «Обзор».")
        grid.attach(_label(hint, muted=True, wrap=True), 1, 8, 2, 1)

        grid.attach(_label("Выбранные ключи", ), 0, 9, 1, 1)
        self.selected = Gtk.ListStore(str)
        for key in profile.keys:
            self.selected.append((key,))
        self.view = Gtk.TreeView(model=self.selected, headers_visible=False)
        self.view.append_column(Gtk.TreeViewColumn("", Gtk.CellRendererText(ellipsize=Pango.EllipsizeMode.MIDDLE), text=0))
        scroll = Gtk.ScrolledWindow(min_content_height=86, vexpand=True)
        scroll.add(self.view)
        mark(scroll, "sshm-frame")
        grid.attach(scroll, 1, 9, 2, 1)
        keys_row = Gtk.Box(spacing=6)
        keys_row.pack_start(action_button("Добавить ключ", self.add_selected, icon="key", width=142), False, False, 0)
        keys_row.pack_start(action_button("Заменить", self.replace_selected, icon="edit", width=106), False, False, 0)
        keys_row.pack_start(action_button("Убрать", self.remove_selected, icon="delete", width=88), False, False, 0)
        keys_row.pack_start(action_button("Создать ключ…", self.create_key, icon="plus"), False, False, 0)
        grid.attach(keys_row, 1, 10, 2, 1)

        grid.attach(_label("Через сервер (ProxyJump)"), 0, 11, 1, 1)
        self.proxy_jump = Gtk.Entry(text=profile.proxy_jump, hexpand=True, placeholder_text="bastion или user@host:port")
        grid.attach(self.proxy_jump, 1, 11, 2, 1)
        grid.attach(_label("Файл known_hosts"), 0, 12, 1, 1)
        self.known_hosts = Gtk.Entry(text=profile.known_hosts, hexpand=True, placeholder_text="~/.ssh/known_hosts (по умолчанию)")
        self.known_hosts.set_tooltip_text("UserKnownHostsFile: полный путь или путь от ~")
        grid.attach(self.known_hosts, 1, 12, 2, 1)
        grid.attach(_label("Пробросы портов"), 0, 13, 1, 1)
        self.forwards = Gtk.ListStore(str, str)
        names = {name: kind for kind, name in FORWARD_KINDS.items()}
        for name, value in profile.forwards:
            self.forwards.append((names[name], value))
        forward_view = Gtk.TreeView(model=self.forwards, headers_visible=False)
        forward_view.append_column(Gtk.TreeViewColumn("", Gtk.CellRendererText(), text=0))
        forward_view.append_column(Gtk.TreeViewColumn("", Gtk.CellRendererText(), text=1))
        self.forward_view = forward_view
        forward_scroll = Gtk.ScrolledWindow(min_content_height=60)
        forward_scroll.add(forward_view)
        mark(forward_scroll, "sshm-frame")
        grid.attach(forward_scroll, 1, 13, 2, 1)
        forward_row = Gtk.Box(spacing=6)
        self.forward_kind = Gtk.ComboBoxText()
        for kind, title in (("L", "L — локальный"), ("R", "R — удалённый"), ("D", "D — SOCKS")):
            self.forward_kind.append(kind, title)
        self.forward_kind.set_active_id("L")
        self.forward_value = Gtk.Entry(hexpand=True, placeholder_text="8080 localhost:80")
        forward_row.pack_start(self.forward_kind, False, False, 0)
        forward_row.pack_start(self.forward_value, True, True, 0)
        forward_row.pack_start(action_button("Добавить", self.add_forward, icon="plus"), False, False, 0)
        forward_row.pack_start(action_button("Убрать", self.remove_forward, icon="delete"), False, False, 0)
        grid.attach(forward_row, 1, 14, 2, 1)
        self.hint = _label("", wrap=True)
        mark(self.hint, "sshm-danger")
        grid.attach(self.hint, 1, 15, 2, 1)
        for name in ("alias", "host", "user", "port"):
            self.entries[name].connect("changed", lambda *_: self.update_password_hint())
        self.show_all()
        self.update_password_hint()

    def update_password_hint(self):
        current = account_for(Profile(*(self.entries[name].get_text().strip() for name in ("alias", "host", "user", "port"))))
        before = account_for(self.original) if self.original else None
        same = before is not None and current is not None and (before.host, before.user, before.port) == (
            current.host, current.user, current.port)
        if not self.has_password:
            text = "Пароль необязателен. Хранится в системном хранилище."
        elif same:
            text = "Пароль сохранён. Пустое поле оставит прежний."
        else:
            text = "Для нового адреса, порта или пользователя введите пароль заново."
        self.password_hint.set_text(text)
        can_forget = self.has_password and same
        if not can_forget:
            self.forget.set_active(False)
        self.forget.set_visible(can_forget)

    def keys(self):
        return tuple(row[0] for row in self.selected)

    def _key_choice(self):
        return self.available.get_active_text()

    def add_selected(self):
        key = self._key_choice()
        if key and key not in self.keys():
            self.selected.append((key,))

    def replace_selected(self):
        key = self._key_choice()
        model, item = self.view.get_selection().get_selected()
        if key and item is not None and key not in self.keys():
            model.set_value(item, 0, key)

    def remove_selected(self):
        model, item = self.view.get_selection().get_selected()
        if item is not None:
            model.remove(item)

    def browse(self):
        path = chooser(self, "Выбрать SSH-ключ", Gtk.FileChooserAction.OPEN, directory=Path.home() / ".ssh")
        if path:
            self.available.prepend_text(path)
            self.available.set_active(0)

    def add_forward(self):
        value = " ".join(self.forward_value.get_text().split())
        if value:
            self.forwards.append((self.forward_kind.get_active_id(), value))
            self.forward_value.set_text("")

    def remove_forward(self):
        model, item = self.forward_view.get_selection().get_selected()
        if item is not None:
            model.remove(item)

    def create_key(self):
        from .widgets import message, prompt
        alias = self.entries["alias"].get_text().strip()
        name = prompt(self, "Создать ключ", f"id_ed25519_{alias}" if alias else "id_ed25519",
                      label="Имя файла в ~/.ssh", commit="Создать")
        if not name:
            return
        if "/" in name or name.startswith("."):
            message(self, "Укажите имя файла без папок.", Gtk.MessageType.ERROR)
            return
        try:
            public = generate_key(Path.home() / ".ssh" / name, f"{os.environ.get('USER', 'user')}@{socket.gethostname()}")
        except (OSError, ValueError) as error:
            message(self, error, Gtk.MessageType.ERROR)
            return
        private = str(public.with_suffix(""))
        self.available.prepend_text(private)
        self.available.set_active(0)
        if private not in self.keys():
            self.selected.append((private,))
        message(self, f"Ключ создан: {private}\nПубличную часть можно скопировать на сервер через меню строки «Скопировать ключ на сервер».")

    def values(self):
        forwards = tuple((FORWARD_KINDS[kind], value) for kind, value in self.forwards)
        profile = Profile(*(self.entries[name].get_text().strip() for name in ("alias", "host", "user", "port")), self.keys(),
                          proxy_jump=self.proxy_jump.get_text().strip(), forwards=forwards,
                          known_hosts=self.known_hosts.get_text().strip())
        return profile, self.entries["password"].get_text(), self.forget.get_active()


class HostKeyDialog(Gtk.Dialog):
    """«Добавить ключ хоста»: host and port for ssh-keyscan."""

    def __init__(self, owner, suggested=""):
        super().__init__(title="Добавить ключ хоста", transient_for=owner, modal=True)
        themed(self)
        self.set_default_size(440, -1)
        dialog_buttons(self, "Получить ключи")
        grid = _grid(self, 90)
        name, number = suggested or "", "22"
        match = re.fullmatch(r"\[([^\]]+)\]:(\d{1,5})", name)
        if match:
            name, number = match.groups()
        self.host = _field(grid, 0, "Хост", name, limit=253)
        self.host.set_placeholder_text("server.example.com или 10.0.0.5")
        self.port = _field(grid, 1, "Порт", number, limit=5)
        self.show_all()

    def values(self):
        host = self.host.get_text().strip().strip("[]")
        port = self.port.get_text().strip()
        if not host:
            raise ValueError("Укажите хост.")
        if not port.isdigit() or not 1 <= int(port) <= 65535:
            raise ValueError("Порт должен быть числом от 1 до 65535.")
        return host, int(port)


class KnownHostsDialog(Gtk.Dialog):
    """«Известные хосты»: view, search, delete and add known_hosts entries (docs/ui-spec.md §9.2)."""

    def __init__(self, owner, default, profile_files=(), selected=None, query=""):
        """profile_files: [(UserKnownHostsFile value, [aliases])]; selected: a path to open first."""
        super().__init__(title="Известные хосты", transient_for=owner, modal=True)
        themed(self)
        self.set_default_size(900, 540)
        self.owner = owner
        self.current = None
        self.busy = False
        close = self.add_button("Закрыть", Gtk.ResponseType.CLOSE)
        mark(close, "sshm-button", "outline")
        area = self.get_content_area()
        area.set_border_width(14)
        area.set_spacing(8)
        home = str(Path.home())

        def short(path):
            text = str(path)
            return "~" + text[len(home):] if text.startswith(home + "/") else text
        row = Gtk.Box(spacing=8)
        row.pack_start(_label("Файл"), False, False, 0)
        self.files = Gtk.ComboBoxText(hexpand=True)
        self.files.append(str(default), short(default) + " (по умолчанию)")
        for value, aliases in profile_files:
            path = str(known_hosts.resolve(value, default))
            if not any(path == item[1] for item in self.files.get_model()):
                self.files.append(path, short(path) + " · " + ", ".join(aliases))
        if selected and not any(str(selected) == item[1] for item in self.files.get_model()):
            self.files.append(str(selected), short(selected))
        self.files.set_active_id(str(selected) if selected else str(default))
        self.files.connect("changed", lambda *_: self.load())
        row.pack_start(self.files, True, True, 0)
        row.pack_start(action_button("Другой файл…", self.choose_file, icon="browse"), False, False, 0)
        row.pack_start(action_button("Открыть в редакторе", self.open_editor, "quiet", icon="edit"), False, False, 0)
        area.pack_start(row, False, False, 0)

        tools = Gtk.Box(spacing=6)
        self.search = Gtk.SearchEntry(placeholder_text="Найти хост", text=query or "")
        self.search.set_size_request(265, -1)
        self.search.connect("search-changed", lambda *_: self.fill())
        tools.pack_start(self.search, False, False, 0)
        self.remove_button = action_button("Удалить", self.remove_selected, "quiet", icon="delete", danger=True)
        self.refresh_button = action_button("Обновить", self.load, "quiet", icon="refresh")
        self.add_key_button = action_button("Добавить…", self.add_keys, "quiet", icon="plus")
        for widget in (self.remove_button, self.refresh_button, self.add_key_button):
            tools.pack_end(widget, False, False, 0)
        area.pack_start(tools, False, False, 0)

        self.model = Gtk.ListStore(str, str, str, str, object)
        self.view = Gtk.TreeView(model=self.model)
        self.view.get_selection().set_mode(Gtk.SelectionMode.MULTIPLE)
        self.view.get_selection().connect("changed", lambda *_: self.update_actions())
        self.view.connect("key-press-event", self.keys)
        for index, (title, width) in enumerate((("Хост", 280), ("Тип ключа", 170), ("Отпечаток", 300), ("Метка", 110))):
            column = Gtk.TreeViewColumn(title, Gtk.CellRendererText(ellipsize=Pango.EllipsizeMode.END), text=index)
            column.set_resizable(True)
            column.set_min_width(80)
            column.set_fixed_width(width)
            column.set_sizing(Gtk.TreeViewColumnSizing.FIXED)
            self.view.append_column(column)
        scroll = Gtk.ScrolledWindow(vexpand=True)
        scroll.add(self.view)
        mark(scroll, "sshm-frame")
        area.pack_start(scroll, True, True, 0)
        self.status = _label("", muted=True)
        area.pack_start(self.status, False, False, 0)
        self.show_all()
        self.load()
        self.search.grab_focus()

    @property
    def path(self):
        return Path(self.files.get_active_id())

    def load(self):
        try:
            self.current = known_hosts.KnownHosts(self.path)
        except (OSError, UnicodeError) as error:
            self.current = None
            self.model.clear()
            self.status.set_text(f"Не удалось прочитать файл. {error}")
            self.update_actions()
            return
        self.fill()

    def fill(self):
        if self.current is None:
            return
        shown = self.current.search(self.search.get_text())
        self.model.clear()
        for entry in shown:
            self.model.append((entry.display, entry.key_type, entry.fingerprint, entry.marker, entry))
        total = len(self.current.entries)
        if not total:
            self.status.set_text("Записей нет." if self.current.path.exists() else "Файла пока нет: он появится после первой записи.")
        else:
            self.status.set_text(f"Найдено: {len(shown)} из {total}" if self.search.get_text().strip() else f"Записей: {total}")
        self.update_actions()

    def chosen(self):
        model, paths = self.view.get_selection().get_selected_rows()
        return [model[path][4] for path in paths]

    def update_actions(self):
        self.remove_button.set_sensitive(not self.busy and self.current is not None and bool(self.chosen()))
        for widget in (self.add_key_button, self.refresh_button, self.files):
            widget.set_sensitive(not self.busy)

    def keys(self, view, event):
        if event.keyval == Gdk.KEY_Delete:
            self.remove_selected()
            return True
        return False

    def remove_selected(self):
        from .widgets import confirm, message
        chosen = self.chosen()
        if self.busy or self.current is None or not chosen:
            return
        if not confirm(self, f"Удалить записи ({len(chosen)}) из known_hosts?", str(self.current.path)):
            return
        try:
            self.current.remove(chosen)
        except OSError as error:
            message(self, error, Gtk.MessageType.ERROR)
            return
        self.load()
        self.status.set_text(f"Удалено записей: {len(chosen)}. Прежняя версия: {self.current.path.name}.old")

    def add_keys(self):
        from .widgets import confirm
        if self.busy or self.current is None:
            return
        dialog = HostKeyDialog(self, self.search.get_text().strip())
        try:
            while True:
                if dialog.run() != Gtk.ResponseType.OK:
                    return
                try:
                    host, port = dialog.values()
                    break
                except ValueError as error:
                    from .widgets import message
                    message(dialog, error, Gtk.MessageType.ERROR)
        finally:
            dialog.destroy()
        name = known_hosts.host_name(host, port)
        self.busy = True
        self.update_actions()
        self.status.set_text(f"Получение ключей {name}…")

        def work():
            try:
                keys = known_hosts.scan(host, port)
            except (OSError, ValueError, subprocess.SubprocessError) as error:
                GLib.idle_add(done, None, error)
            else:
                GLib.idle_add(done, keys, None)

        def done(keys, error):
            self.busy = False
            self.update_actions()
            if error is not None:
                self.status.set_text(f"Не удалось получить ключи. {error}")
                return False
            summary = "\n".join(f"{entry.key_type} · {entry.fingerprint}" for entry in map(known_hosts.parse, keys) if entry)
            if not confirm(self, "Добавить ключи хоста в known_hosts?",
                           f"Прежние записи этого хоста будут заменены.\n\n{name}\n{summary}"):
                self.status.set_text("Ключи не добавлены.")
                return False
            try:
                self.current.replace(name, keys)
            except OSError as error:
                self.status.set_text(str(error))
                return False
            self.load()
            self.search.set_text(name)
            self.status.set_text(f"Добавлено ключей: {len(keys)}.")
            return False
        threading.Thread(target=work, daemon=True).start()

    def choose_file(self):
        path = chooser(self, "Файл known_hosts", Gtk.FileChooserAction.OPEN, directory=self.path.parent)
        if path:
            if not any(path == item[1] for item in self.files.get_model()):
                self.files.append(path, path)
            self.files.set_active_id(path)

    def open_editor(self):
        from .widgets import message
        if not self.path.exists():
            message(self, "Файла пока нет: он появится после первой записи.")
            return
        try:
            Gio.AppInfo.launch_default_for_uri(self.path.as_uri(), None)
        except GLib.Error as error:
            message(self, error.message, Gtk.MessageType.ERROR)


class DesktopDialog(Gtk.Dialog):
    """RDP connection: fields and texts of src/RdpDialog.cs. Response IMPORT_RESPONSE asks to import a .rdp file."""

    def __init__(self, owner, profile, has_password=False, group=""):
        super().__init__(title="Изменить RDP-подключение" if profile else "Новое RDP-подключение",
                         transient_for=owner, modal=True)
        themed(self)
        self.set_default_size(545, 470)
        if profile is None:
            importer = self.add_button("Импорт .rdp…", IMPORT_RESPONSE)
            mark(importer, "sshm-button", "outline")
        dialog_buttons(self, "Сохранить")
        self.profile = profile or DesktopProfile(uuid.uuid4().hex, "", "", group=group)
        grid = _grid(self, 132)
        self.entries = {}
        rows = (("name", "Название", 80), ("host", "Адрес", 253), ("port", "Порт", 5), ("user", "Пользователь", 128),
                ("domain", "Домен", 128))
        for row, (key, caption, limit) in enumerate(rows):
            value = self.profile.port if key == "port" else getattr(self.profile, key)
            self.entries[key] = _field(grid, row, caption, value, limit=limit)
        self.entries["domain"].set_tooltip_text(
            "Пусто — учётная запись на самом сервере. Для доменной учётной записи укажите имя домена, например CORP.")
        self.password = _field(grid, 5, "Пароль", hidden=True)
        options = Gtk.Box(spacing=12)
        options.pack_start(_label("Сохранён. Пустое поле — не менять." if has_password else
                                  "Необязательно. Хранится в системном хранилище.", muted=True), False, False, 0)
        self.forget = Gtk.CheckButton(label="Удалить пароль")
        self.forget.connect("toggled", lambda check: self.password.set_sensitive(not check.get_active()))
        options.pack_start(self.forget, False, False, 0)
        grid.attach(options, 1, 6, 2, 1)
        for row, (key, caption) in enumerate((("width", "Ширина экрана"), ("height", "Высота экрана")), start=7):
            value = getattr(self.profile, key)
            self.entries[key] = _field(grid, row, caption, value or "", limit=4)
            self.entries[key].set_placeholder_text("По размеру окна")
        self.clipboard = Gtk.CheckButton(label="Общий буфер обмена", active=self.profile.clipboard)
        self.fullscreen = Gtk.CheckButton(label="Открывать на весь экран", active=self.profile.fullscreen)
        grid.attach(self.clipboard, 1, 9, 2, 1)
        grid.attach(self.fullscreen, 1, 10, 2, 1)
        extra = Gtk.Expander(label="Дополнительно")
        extra.set_expanded(bool(self.profile.drives or self.profile.printers or self.profile.multimon or self.profile.gateway))
        inner = Gtk.Grid(column_spacing=12, row_spacing=8, margin_top=8)
        inner.label_width = 120
        inner.attach(_label("Подключать папки"), 0, 0, 1, 1)
        self.drives = Gtk.ListStore(str)
        for path in self.profile.drives:
            self.drives.append((path,))
        self.drive_view = Gtk.TreeView(model=self.drives, headers_visible=False)
        self.drive_view.append_column(Gtk.TreeViewColumn("", Gtk.CellRendererText(ellipsize=Pango.EllipsizeMode.MIDDLE), text=0))
        drive_scroll = Gtk.ScrolledWindow(min_content_height=52, hexpand=True)
        drive_scroll.add(self.drive_view)
        mark(drive_scroll, "sshm-frame")
        inner.attach(drive_scroll, 1, 0, 1, 1)
        drive_buttons = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        drive_buttons.pack_start(action_button("Добавить…", self.add_drive, icon="plus"), False, False, 0)
        drive_buttons.pack_start(action_button("Убрать", self.remove_drive, icon="delete"), False, False, 0)
        inner.attach(drive_buttons, 2, 0, 1, 1)
        self.printers = Gtk.CheckButton(label="Принтеры", active=self.profile.printers)
        self.multimon = Gtk.CheckButton(label="Все мониторы (в отдельном окне)", active=self.profile.multimon)
        inner.attach(self.printers, 1, 1, 2, 1)
        inner.attach(self.multimon, 1, 2, 2, 1)
        self.gateway = _field(inner, 3, "Шлюз RD Gateway", self.profile.gateway, limit=253)
        self.gateway_user = _field(inner, 4, "Пользователь шлюза", self.profile.gateway_user, limit=128)
        extra.add(inner)
        grid.attach(extra, 0, 11, 3, 1)
        self.error = _label("", wrap=True)
        mark(self.error, "sshm-danger")
        grid.attach(self.error, 1, 12, 2, 1)
        self.show_all()
        self.forget.set_visible(has_password)

    def add_drive(self):
        path = chooser(self, "Папка для удалённого рабочего стола", Gtk.FileChooserAction.SELECT_FOLDER, directory=Path.home())
        if path and path not in (row[0] for row in self.drives):
            self.drives.append((path,))

    def remove_drive(self):
        model, item = self.drive_view.get_selection().get_selected()
        if item is not None:
            model.remove(item)

    def values(self):
        fields = {key: entry.get_text().strip() for key, entry in self.entries.items()}
        try:
            fields["port"] = int(fields["port"] or "3389")
            fields["width"] = int(fields["width"] or "0")
            fields["height"] = int(fields["height"] or "0")
        except ValueError:
            raise ValueError("Укажите числовой порт и размеры экрана.") from None
        host = fields["host"].strip("[]")
        if not host.isascii():
            host = host.encode("idna").decode("ascii")
        fields["host"] = host
        profile = replace(self.profile, **fields, clipboard=self.clipboard.get_active(),
                          fullscreen=self.fullscreen.get_active(), drives=tuple(row[0] for row in self.drives),
                          printers=self.printers.get_active(), multimon=self.multimon.get_active(),
                          gateway=self.gateway.get_text().strip(), gateway_user=self.gateway_user.get_text().strip()).validate()
        password = "" if self.forget.get_active() else self.password.get_text()
        if password and not profile.user:
            raise ValueError("Укажите пользователя, чтобы сохранить пароль RDP.")
        return profile, password, self.forget.get_active()


class LogonDialog(Gtk.Dialog):
    """«Вход: имя» before an RDP connection without a saved password (src/RdpDialog.cs RdpLogonDialog)."""

    def __init__(self, owner, profile):
        super().__init__(title="Вход: " + profile.name, transient_for=owner, modal=True)
        themed(self)
        self.set_default_size(460, -1)
        dialog_buttons(self, "Подключиться")
        grid = _grid(self, 120)
        grid.attach(_label(profile.address, muted=True), 0, 0, 3, 1)
        self.user = _field(grid, 1, "Пользователь", profile.account, limit=257)
        self.password = _field(grid, 2, "Пароль", hidden=True)
        self.remember = Gtk.CheckButton(label="Сохранить пароль в системном хранилище")
        grid.attach(self.remember, 1, 3, 2, 1)
        self.show_all()
        (self.password if profile.user else self.user).grab_focus()

    def values(self, profile):
        """(user, domain, password, remember). DOMAIN\\user in the user field sets the domain."""
        account = self.user.get_text().strip()
        user, domain = account, profile.domain
        if "\\" in account:
            domain, user = account.split("\\", 1)
        return user, domain, self.password.get_text(), self.remember.get_active()


DIRECT = "Напрямую"


class VncDialog(Gtk.Dialog):
    """VNC connection: fields and texts of src/VncDialog.cs (docs/ui-spec.md §8)."""

    def __init__(self, owner, profile, has_password=False, group="", ssh_aliases=()):
        super().__init__(title="Изменить VNC-подключение" if profile else "Новое VNC-подключение",
                         transient_for=owner, modal=True)
        themed(self)
        self.set_default_size(545, -1)
        dialog_buttons(self, "Сохранить")
        self.profile = profile or VncProfile(uuid.uuid4().hex, "", "", group=group)
        grid = _grid(self, 150)
        self.entries = {}
        rows = (("name", "Название", 80), ("host", "Адрес", 253), ("port", "Порт", 5), ("user", "Пользователь", 128))
        for row, (key, caption, limit) in enumerate(rows):
            self.entries[key] = _field(grid, row, caption, getattr(self.profile, key), limit=limit)
        self.entries["user"].set_placeholder_text("необязательно")
        self.password = _field(grid, 4, "Пароль", hidden=True)
        options = Gtk.Box(spacing=12)
        options.pack_start(_label("Сохранён. Пустое поле — не менять." if has_password else
                                  "Необязательно. Хранится в системном хранилище.", muted=True), False, False, 0)
        self.forget = Gtk.CheckButton(label="Удалить пароль")
        self.forget.connect("toggled", lambda check: self.password.set_sensitive(not check.get_active()))
        options.pack_start(self.forget, False, False, 0)
        grid.attach(options, 1, 5, 2, 1)
        grid.attach(_label("VNC-пароль: учитываются первые 8 символов.", muted=True), 1, 6, 2, 1)
        grid.attach(_label("Через SSH-сервер"), 0, 7, 1, 1)
        self.via = Gtk.ComboBoxText(hexpand=True)
        aliases = sorted(ssh_aliases, key=str.casefold)
        if self.profile.via and self.profile.via not in aliases:
            aliases.append(self.profile.via)
        for value in [DIRECT] + aliases:
            self.via.append(value, value)
        self.via.set_active_id(self.profile.via or DIRECT)
        grid.attach(self.via, 1, 7, 2, 1)
        grid.attach(_label("SSH-профиль для файлов"), 0, 8, 1, 1)
        self.files_via = Gtk.ComboBoxText(hexpand=True)
        self.files_via.append("", "Не выбран")
        file_aliases = set(ssh_aliases)
        if self.profile.files_via:
            file_aliases.add(self.profile.files_via)
        for alias in sorted(file_aliases, key=str.casefold):
            self.files_via.append(alias, alias)
        self.files_via.set_active_id(self.profile.files_via)
        grid.attach(self.files_via, 1, 8, 2, 1)
        grid.attach(_label("Масштаб"), 0, 9, 1, 1)
        self.scale = Gtk.ComboBoxText(hexpand=True)
        self.scale.append("fit", "По размеру окна")
        self.scale.append("native", "Исходный размер")
        self.scale.set_active_id(self.profile.scale)
        grid.attach(self.scale, 1, 9, 2, 1)
        self.view_only = Gtk.CheckButton(label="Только просмотр", active=self.profile.view_only)
        self.clipboard = Gtk.CheckButton(label="Общий буфер обмена", active=self.profile.clipboard)
        self.fullscreen = Gtk.CheckButton(label="Открывать на весь экран", active=self.profile.fullscreen)
        for row, check in enumerate((self.view_only, self.clipboard, self.fullscreen), start=10):
            grid.attach(check, 1, row, 2, 1)
        self.error = _label("", wrap=True)
        mark(self.error, "sshm-danger")
        grid.attach(self.error, 1, 13, 2, 1)
        self.show_all()
        self.forget.set_visible(has_password)

    def values(self):
        fields = {key: entry.get_text().strip() for key, entry in self.entries.items()}
        try:
            fields["port"] = int(fields["port"] or "5900")
        except ValueError:
            raise ValueError("Укажите порт от 1 до 65535.") from None
        host = fields["host"].strip("[]")
        if not host.isascii():
            host = host.encode("idna").decode("ascii")
        fields["host"] = host
        via = self.via.get_active_id() or DIRECT
        profile = replace(self.profile, **fields, scale=self.scale.get_active_id() or "fit",
                          view_only=self.view_only.get_active(), clipboard=self.clipboard.get_active(),
                          fullscreen=self.fullscreen.get_active(), via="" if via == DIRECT else via,
                          files_via=self.files_via.get_active_id() or "").validate()
        password = "" if self.forget.get_active() else self.password.get_text()
        if any(char in password for char in "\0\r\n"):
            raise ValueError("Пароль должен быть одной строкой.")
        return profile, password, self.forget.get_active()


class VncLogonDialog(Gtk.Dialog):
    """«Вход: имя» when the VNC server asks for a password that is not saved (or was rejected)."""

    def __init__(self, owner, profile, wrong=False):
        super().__init__(title="Вход: " + profile.name, transient_for=owner, modal=True)
        themed(self)
        self.set_default_size(460, -1)
        dialog_buttons(self, "Подключиться")
        grid = _grid(self, 120)
        note = _label("Неверный пароль VNC. Попробуйте ещё раз." if wrong else profile.address, muted=not wrong)
        if wrong:
            mark(note, "sshm-danger")
        grid.attach(note, 0, 0, 3, 1)
        self.password = _field(grid, 1, "Пароль", hidden=True)
        self.password.set_activates_default(True)
        self.set_default_response(Gtk.ResponseType.OK)
        self.remember = Gtk.CheckButton(label="Сохранить пароль в системном хранилище")
        grid.attach(self.remember, 1, 2, 2, 1)
        self.show_all()
        self.password.grab_focus()

    def values(self):
        return self.password.get_text(), self.remember.get_active()


class ServerPicker(Gtk.Dialog):
    def __init__(self, owner, protocol, entries, recent=()):
        super().__init__(title="Новый сеанс", transient_for=owner, modal=True)
        themed(self)
        self.set_default_size(495, 389)
        dialog_buttons(self, "Подключиться")
        self.panel = ServerPickerPanel(protocol, entries, lambda: recent,
                                       lambda _profile: self.response(Gtk.ResponseType.OK))
        self.panel.set_border_width(14)
        self.get_content_area().pack_start(self.panel, True, True, 0)
        self.panel.listing.connect("row-selected", lambda *_: self.update_action())
        self.update_action()
        self.show_all()
        self.panel.search.grab_focus()

    def update_action(self):
        self.set_response_sensitive(Gtk.ResponseType.OK, self.chosen() is not None)

    def chosen(self):
        return self.panel.chosen()


class ServerPickerPanel(Gtk.Box):
    def __init__(self, protocol, entries, recent, submit, compact=False):
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=8, vexpand=True)
        self.recent = recent
        self.entries = entries
        self.protocol = protocol
        self.submit = submit
        area = self
        switch = Gtk.Box(spacing=4)
        self.tabs = {}
        for kind, caption, icon in (("ssh", "SSH", "terminal"), ("rdp", "RDP", "server"), ("vnc", "VNC", "server")):
            tab = action_button(caption, lambda kind=kind: self.select(kind), "tab", icon=None if compact else icon, width=62 if compact else 86)
            self.tabs[kind] = tab
            switch.pack_start(tab, False, False, 0)
        area.pack_start(switch, False, False, 0)
        self.search = Gtk.SearchEntry(placeholder_text="Найти сервер")
        self.search.connect("search-changed", lambda *_: self.render())
        self.search.connect("key-press-event", self.navigate)
        self.search.connect("activate", lambda *_: self.activate())
        area.pack_start(self.search, False, False, 0)
        self.listing = Gtk.ListBox(activate_on_single_click=False)
        self.listing.connect("row-activated", lambda _listing, row: self.activate(row))
        scroll = Gtk.ScrolledWindow(vexpand=True, hscrollbar_policy=Gtk.PolicyType.NEVER if compact else Gtk.PolicyType.AUTOMATIC)
        scroll.add(self.listing)
        mark(scroll, "sshm-frame")
        area.pack_start(scroll, True, True, 0)
        self.status = _label("", muted=True)
        area.pack_start(self.status, False, False, 0)
        if compact:
            self.connect_button = action_button("Подключиться", self.activate, "primary", width=128)
            area.pack_end(self.connect_button, False, False, 0)
            self.listing.connect("row-selected", lambda *_: self.connect_button.set_sensitive(self.chosen() is not None))
        self.show_all()
        self.select(protocol)

    def activate(self, row=None):
        profile = row.profile if row is not None else self.chosen()
        if profile is not None:
            self.submit(profile)

    def select(self, protocol):
        self.protocol = protocol
        for kind, tab in self.tabs.items():
            context = tab.get_style_context()
            (context.add_class if kind == protocol else context.remove_class)("selected")
        self.render()

    def render(self):
        for row in self.listing.get_children():
            self.listing.remove(row)
        everything = self.entries(self.protocol)
        query = self.search.get_text().strip().casefold()
        shown = [entry for entry in everything if query in (entry[0].alias + " " + entry[0].address + " " + entry[1]).casefold()]
        recent = []
        if not query:
            keys = [key for key in self.recent() if key.startswith(self.protocol + ":")][:5]
            by_key = {self.key(entry[0]): entry for entry in shown}
            recent = [by_key[key] for key in keys if key in by_key]
        sections = [("Недавние", recent), ("Все серверы", shown)] if recent else [(None, shown)]
        for title, items in sections:
            if title:
                header = Gtk.ListBoxRow(selectable=False, activatable=False)
                header.profile = None
                caption = _label(title, muted=True)
                caption.set_margin_start(8)
                caption.set_margin_top(4)
                header.add(caption)
                self.listing.add(header)
            for profile, group, color in items:
                self.listing.add(self.row(profile, group, color))
        self.listing.show_all()
        first = next((row for row in self.listing.get_children() if row.get_selectable()), None)
        if first is not None:
            self.listing.select_row(first)
        self.status.set_text(f"Серверов: {len(shown)}" if shown else
                             "Серверы не найдены." if everything else "Сохранённых серверов пока нет.")

    @staticmethod
    def key(profile):
        if isinstance(profile, VncProfile):
            return "vnc:" + profile.id
        return ("rdp:" + profile.id) if isinstance(profile, DesktopProfile) else "ssh:" + profile.alias.lower()

    def row(self, profile, group, color):
        row = Gtk.ListBoxRow()
        row.profile = profile
        line = Gtk.Box(spacing=9, margin=7)
        dot = Gtk.Label()
        dot.set_markup(f'<span foreground="{color}">●</span>')
        line.pack_start(dot, False, False, 0)
        texts = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        for caption in (_label(profile.alias, bold=True),
                        _label(profile.address + (f"  ·  {group}" if group else ""), muted=True)):
            caption.set_ellipsize(Pango.EllipsizeMode.END)
            texts.pack_start(caption, False, False, 0)
        line.pack_start(texts, True, True, 0)
        row.add(line)
        return row

    def navigate(self, entry, event):
        if event.keyval not in (Gdk.KEY_Up, Gdk.KEY_Down):
            return False
        row = self.listing.get_selected_row()
        index = row.get_index() if row else -1
        step = 1 if event.keyval == Gdk.KEY_Down else -1
        target = self.listing.get_row_at_index(max(0, index + step))
        while target is not None and not target.get_selectable():
            index += step
            target = self.listing.get_row_at_index(index + step) if index + step >= 0 else None
        if target is not None:
            self.listing.select_row(target)
        return True

    def chosen(self):
        row = self.listing.get_selected_row()
        return row.profile if row else None


class SettingsDialog(Gtk.Dialog):
    """«Настройки» (docs/ui-spec.md §7)."""

    def __init__(self, owner, preferences):
        super().__init__(title="Настройки", transient_for=owner, modal=True)
        themed(self)
        self.set_default_size(520, -1)
        dialog_buttons(self, "Сохранить")
        grid = _grid(self, 190)
        grid.attach(_label("Шрифт терминала"), 0, 0, 1, 1)
        self.font = Gtk.FontButton(use_font=True, hexpand=True)
        self.font.set_filter_func(lambda family, face: family.is_monospace())
        self.font_chosen = bool(preferences["terminal_font"])
        self.font.set_font(f"{preferences['terminal_font'] or 'Monospace'} {preferences['terminal_size']}")
        self.font.connect("font-set", lambda *_: setattr(self, "font_chosen", True))
        grid.attach(self.font, 1, 0, 2, 1)
        grid.attach(_label("Строк прокрутки"), 0, 1, 1, 1)
        self.scrollback = Gtk.SpinButton.new_with_range(100, 100000, 500)
        self.scrollback.set_value(preferences["scrollback"])
        grid.attach(self.scrollback, 1, 1, 2, 1)
        self.user = _field(grid, 2, "Пользователь по умолчанию", preferences["default_user"], limit=128)
        self.port = _field(grid, 3, "Порт по умолчанию", preferences["default_port"], limit=5)
        self.reconnect = Gtk.CheckButton(label="Переподключать SSH автоматически", active=preferences["auto_reconnect"])
        self.tray = Gtk.CheckButton(label="Сворачивать в трей при закрытии окна", active=preferences["close_to_tray"])
        self.updates = Gtk.CheckButton(label="Проверять обновления при запуске", active=preferences["check_updates"])
        for row, check in enumerate((self.reconnect, self.tray, self.updates), start=4):
            grid.attach(check, 1, row, 2, 1)
        if hasattr(owner, "open_known_hosts"):
            known = action_button("Известные хосты…", lambda: owner.open_known_hosts(None), icon="key")
            known.set_halign(Gtk.Align.START)
            grid.attach(known, 1, 7, 2, 1)
        self.error = _label("", wrap=True)
        mark(self.error, "sshm-danger")
        grid.attach(self.error, 1, 8, 2, 1)
        self.show_all()

    def values(self, preferences):
        port = self.port.get_text().strip()
        if port and (not port.isdigit() or not 1 <= int(port) <= 65535):
            raise ValueError("Порт должен быть числом от 1 до 65535.")
        user = self.user.get_text().strip()
        if any(char.isspace() or char in "#'\"" for char in user):
            raise ValueError("Укажите имя пользователя без пробелов и кавычек.")
        font = Pango.FontDescription.from_string(self.font.get_font() or "")
        size = font.get_size() // Pango.SCALE if font.get_size() else preferences["terminal_size"]
        family = (font.get_family() or "") if self.font_chosen else preferences["terminal_font"]
        return {**preferences, "terminal_font": family, "terminal_size": max(6, min(48, size)),
                "scrollback": int(self.scrollback.get_value()), "default_user": user, "default_port": port,
                "auto_reconnect": self.reconnect.get_active(), "close_to_tray": self.tray.get_active(),
                "check_updates": self.updates.get_active()}


class ImportDialog(Gtk.Dialog):
    """«Импорт из других программ»: PuTTY sessions or a mRemoteNG / MobaXterm file, with checkboxes."""

    def __init__(self, owner, putty):
        super().__init__(title="Импорт из других программ", transient_for=owner, modal=True)
        themed(self)
        self.set_default_size(620, 440)
        dialog_buttons(self, "Импортировать")
        area = self.get_content_area()
        area.set_border_width(14)
        area.set_spacing(8)
        sources = Gtk.Box(spacing=6)
        sources.pack_start(action_button("PuTTY", lambda: self.show(putty()), icon="terminal"), False, False, 0)
        sources.pack_start(action_button("Файл mRemoteNG или MobaXterm…", self.choose_file, icon="folder"), False, False, 0)
        area.pack_start(sources, False, False, 0)
        self.model = Gtk.ListStore(bool, str, str, str, str, object)
        view = Gtk.TreeView(model=self.model)
        toggle = Gtk.CellRendererToggle()
        toggle.connect("toggled", lambda _, path: self.model.set_value(self.model.get_iter(path), 0, not self.model[path][0]))
        view.append_column(Gtk.TreeViewColumn("", toggle, active=0))
        for index, title in enumerate(("Протокол", "Название", "Адрес", "Источник"), start=1):
            view.append_column(Gtk.TreeViewColumn(title, Gtk.CellRendererText(ellipsize=Pango.EllipsizeMode.END), text=index))
        scroll = Gtk.ScrolledWindow(vexpand=True)
        scroll.add(view)
        mark(scroll, "sshm-frame")
        area.pack_start(scroll, True, True, 0)
        self.status = _label("Выберите источник. Пароли из других программ не переносятся.", muted=True, wrap=True)
        area.pack_start(self.status, False, False, 0)
        self.show_all()

    def choose_file(self):
        from .extras import read_import
        from .widgets import message
        path = chooser(self, "Файл подключений", Gtk.FileChooserAction.OPEN,
                       filters=(("mRemoteNG (*.xml)", "*.xml"), ("MobaXterm (*.mxtsessions, *.ini)", "*.mxtsessions"),
                                ("MobaXterm.ini", "*.ini")))
        if path:
            try:
                self.show(read_import(path))
            except (OSError, ValueError, SyntaxError) as error:
                message(self, f"Не удалось прочитать файл. {error}", Gtk.MessageType.ERROR)

    def show(self, found):
        self.model.clear()
        for item in found:
            address = (item.user + "@" if item.user else "") + item.host + f":{item.port}"
            self.model.append((True, item.protocol.upper(), item.name, address, item.source, item))
        self.status.set_text(f"Найдено подключений: {len(found)}" if found else "Подключения не найдены.")

    def chosen(self):
        return [row[5] for row in self.model if row[0]]
