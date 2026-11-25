"""Dialogs with the Windows layout and wording (docs/ui-spec.md §4)."""

import uuid
from dataclasses import replace
from pathlib import Path

from gi.repository import Gdk, Gtk, Pango

from .auth import account_for
from .config import Profile
from .rdp import DesktopProfile
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

    def __init__(self, owner, profile, keys, has_password=False, key_error=None):
        super().__init__(title="Изменить подключение" if profile else "Новое подключение", transient_for=owner, modal=True)
        themed(self)
        self.set_default_size(724, 540)
        dialog_buttons(self, "Сохранить")
        self.original = profile
        self.has_password = has_password
        profile = profile or Profile("")
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
        grid.attach(keys_row, 1, 10, 2, 1)
        self.hint = _label("", wrap=True)
        mark(self.hint, "sshm-danger")
        grid.attach(self.hint, 1, 11, 2, 1)
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

    def values(self):
        profile = Profile(*(self.entries[name].get_text().strip() for name in ("alias", "host", "user", "port")), self.keys())
        return profile, self.entries["password"].get_text(), self.forget.get_active()


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
        self.error = _label("", wrap=True)
        mark(self.error, "sshm-danger")
        grid.attach(self.error, 1, 11, 2, 1)
        self.show_all()
        self.forget.set_visible(has_password)

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
                          fullscreen=self.fullscreen.get_active()).validate()
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


class ServerPicker(Gtk.Dialog):
    """«Новый сеанс»: protocol switch, search, list with group dots and a status line (src/ServerPickerDialog.cs)."""

    def __init__(self, owner, protocol, entries):
        """entries(protocol) -> [(profile, group_name, group_color)] sorted by name."""
        super().__init__(title="Новый сеанс", transient_for=owner, modal=True)
        themed(self)
        self.set_default_size(495, 389)
        dialog_buttons(self, "Подключиться")
        self.entries = entries
        self.protocol = protocol
        area = self.get_content_area()
        area.set_spacing(8)
        area.set_border_width(14)
        switch = Gtk.Box(spacing=4)
        self.tabs = {}
        for kind, caption, icon in (("ssh", "SSH", "terminal"), ("rdp", "RDP", "server")):
            tab = action_button(caption, lambda kind=kind: self.select(kind), "tab", icon=icon, width=86)
            self.tabs[kind] = tab
            switch.pack_start(tab, False, False, 0)
        area.pack_start(switch, False, False, 0)
        self.search = Gtk.SearchEntry(placeholder_text="Найти сервер")
        self.search.connect("search-changed", lambda *_: self.render())
        self.search.connect("key-press-event", self.navigate)
        self.search.connect("activate", lambda *_: self.response(Gtk.ResponseType.OK))
        area.pack_start(self.search, False, False, 0)
        self.listing = Gtk.ListBox(activate_on_single_click=False)
        self.listing.connect("row-activated", lambda *_: self.response(Gtk.ResponseType.OK))
        scroll = Gtk.ScrolledWindow(vexpand=True)
        scroll.add(self.listing)
        mark(scroll, "sshm-frame")
        area.pack_start(scroll, True, True, 0)
        self.status = _label("", muted=True)
        area.pack_start(self.status, False, False, 0)
        self.show_all()
        self.select(protocol)

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
        for profile, group, color in shown:
            row = Gtk.ListBoxRow()
            row.profile = profile
            line = Gtk.Box(spacing=9, margin=7)
            dot = Gtk.Label()
            dot.set_markup(f'<span foreground="{color}">●</span>')
            line.pack_start(dot, False, False, 0)
            texts = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
            texts.pack_start(_label(profile.alias, bold=True), False, False, 0)
            texts.pack_start(_label(profile.address + (f"  ·  {group}" if group else ""), muted=True), False, False, 0)
            line.pack_start(texts, True, True, 0)
            row.add(line)
            self.listing.add(row)
        self.listing.show_all()
        self.listing.select_row(self.listing.get_row_at_index(0))
        self.status.set_text(f"Серверов: {len(shown)}" if shown else
                             "Серверы не найдены." if everything else "Сохранённых серверов пока нет.")
        self.set_response_sensitive(Gtk.ResponseType.OK, bool(shown))

    def navigate(self, entry, event):
        if event.keyval not in (Gdk.KEY_Up, Gdk.KEY_Down):
            return False
        row = self.listing.get_selected_row()
        index = row.get_index() if row else -1
        target = self.listing.get_row_at_index(max(0, index + (1 if event.keyval == Gdk.KEY_Down else -1)))
        if target is not None:
            self.listing.select_row(target)
        return True

    def chosen(self):
        row = self.listing.get_selected_row()
        return row.profile if row else None
