import argparse
import os
import subprocess
import sys
import time
import uuid
from dataclasses import asdict, replace
from pathlib import Path

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
gi.require_version("Vte", "2.91")
gi.require_version("PangoCairo", "1.0")
try:
    gi.require_version("GdkX11", "3.0")
except ValueError:
    pass
from gi.repository import Gdk, GdkPixbuf, Gio, GLib, Gtk, Pango, Vte  # noqa: F401 - Vte is used by smoke tests

from . import VERSION
from .auth import Vault, account_for, ssh_args
from .config import Config, Profile, parse_settings
from .dialogs import IMPORT_RESPONSE, DesktopDialog, ImportDialog, ProfileDialog, ServerPicker, SettingsDialog, VncDialog
from .extras import copy_key_command, parse_quick, putty_sessions, quick_alias
from .files import FilesPanel
from .launch import ssh_command_text, terminal_command
from .rdp import DesktopProfile, ordered
from .sessions import (ALL_NAME, UNGROUPED_NAME, RdpSession, SessionsPage, SshSession, VncSession, VncWindow,
                       ask_credential, embedding_supported,
                       prepare_terminal_paste, terminal_font)  # noqa: F401 - re-exported for tests
from .theme import TAB_COLORS, Theme, mark
from .vnc import VncProfile
from .tray import Tray
from .update_ui import UpdateController
from .widgets import Item, action_button, chooser, confirm, menu_at, message, prompt, set_button_text

__all__ = ["Application", "Config", "Window", "main", "prepare_terminal_paste", "terminal_font", "Gtk", "Vte"]


def app_icon():
    for path in (Path(sys.argv[0]).resolve().parent / "icon.png",
                 Path(__file__).resolve().parents[2] / "assets" / "AppMark.png"):
        if path.exists():
            return path
    return None


class Window(Gtk.ApplicationWindow):
    def __init__(self, application, config):
        super().__init__(application=application, title="SSH Manager " + VERSION)
        mark(self, "sshm")
        self.config = config
        self.vault = Vault()
        self.files = {}
        self.sessions = []
        self.document = None
        self.full_session = None
        self.quitting = False
        self.transient = {}  # quick-connect aliases → (user, host, port), not saved in the SSH config
        try:
            self.settings = config.settings()
        except (ValueError, OSError):
            self.settings = parse_settings(b"")
        self.theme = Theme()
        self.theme.apply(self.settings["dark"])
        self.protocol = "ssh"
        self.set_default_size(1280, 720)
        self.set_size_request(800, 480)
        self.set_position(Gtk.WindowPosition.CENTER)
        self.connect("delete-event", self.on_delete)
        self.connect("key-press-event", self.shortcuts)

        self.theme_buttons = []
        outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        mark(outer, "sshm-page")
        self.add(outer)
        # One window: header, then «Серверы» | «Сеансы» | workspace (docs/ui-spec.md §2).
        self.servers_button = action_button("Серверы", lambda: self.sessions_page.toggle_servers(), icon="server", width=104,
                                            tooltip="Показать или скрыть колонку «Серверы»")
        self.sessions_button = action_button("Сеансы", lambda: self.sessions_page.toggle_sessions(), icon="terminal",
                                             width=116, tooltip="Показать или скрыть колонку «Сеансы»")
        self.settings_button = action_button("Настройки", self.open_settings, "quiet", icon="edit", tooltip="Настройки")
        self.add_button = action_button("Добавить сервер", self.add_profile, "primary", icon="plus", width=158)
        self.quick = Gtk.Entry(placeholder_text="Быстрое подключение: user@host, rdp:// или vnc://", width_chars=18,
                               max_width_chars=52, halign=Gtk.Align.CENTER, valign=Gtk.Align.CENTER, hexpand=True)
        self.quick.set_tooltip_text("Подключиться без сохранения профиля. Enter — подключиться.")
        self.quick.connect("activate", lambda entry: self.quick_connect(entry.get_text()) and entry.set_text(""))
        # The header never sets the window's minimum width: on a narrow window its buttons shrink to icons instead.
        self.header_row = Gtk.ScrolledWindow(hscrollbar_policy=Gtk.PolicyType.EXTERNAL,
                                             vscrollbar_policy=Gtk.PolicyType.NEVER)
        self.header_row.add(self.header())
        self.header_row.get_child().set_shadow_type(Gtk.ShadowType.NONE)
        mark(self.header_row.get_child(), "sshm-page")
        self.header_row.connect("size-allocate", lambda widget, allocation: self.fit_header(allocation.width))
        outer.pack_start(self.header_row, False, False, 0)
        self.sessions_page = SessionsPage(self, self.start_page())
        # The columns fold on a narrow window: the body never asks for more width than the window has.
        self.body = Gtk.ScrolledWindow(hscrollbar_policy=Gtk.PolicyType.EXTERNAL, vscrollbar_policy=Gtk.PolicyType.NEVER,
                                       margin_start=12, margin_end=12)
        self.body.add(self.sessions_page)
        viewport = self.body.get_child()
        viewport.set_shadow_type(Gtk.ShadowType.NONE)
        mark(viewport, "sshm-page")
        self.body.connect("size-allocate", lambda widget, allocation: self.sessions_page.fit_columns(allocation.width))
        outer.pack_start(self.body, True, True, 0)

        self.footer = Gtk.Box(spacing=12, margin_start=16, margin_end=16, margin_top=6, margin_bottom=8)
        self.source = Gtk.Label(xalign=0, ellipsize=Pango.EllipsizeMode.MIDDLE)
        self.status = Gtk.Label(xalign=0)
        for label in (self.source, self.status):
            mark(label, "sshm-muted")
        self.footer.pack_start(self.source, True, True, 0)
        self.footer.pack_start(self.status, False, False, 0)
        self.updates = UpdateController(self)
        self.updates.control.set_size_request(170, -1)
        self.footer.pack_end(self.updates.control, False, False, 0)
        outer.pack_end(self.footer, False, False, 0)

        icon = app_icon()
        self.tray = Tray(self, str(icon) if icon else None)
        self.apply_theme()
        self.reload(show_error=False)
        if self.settings["preferences"]["check_updates"]:
            self.updates.check()

    # --- layout helpers ---------------------------------------------------------------------
    def header(self):
        """Logo, «SSH Manager», quick connect and the window buttons (docs/ui-spec.md §2.1)."""
        row = Gtk.Box(spacing=8, margin_start=16, margin_end=16, margin_top=8, margin_bottom=8)
        row.set_size_request(-1, 54)
        icon = app_icon()
        if icon:
            try:
                row.pack_start(Gtk.Image.new_from_pixbuf(GdkPixbuf.Pixbuf.new_from_file_at_scale(str(icon), 32, 32, True)),
                               False, False, 0)
            except GLib.Error:
                pass
        title = Gtk.Label(label="SSH Manager", xalign=0)
        mark(title, "sshm-emphasis")
        row.pack_start(title, False, False, 4)
        row.pack_start(self.quick, True, True, 6)
        for widget in (self.servers_button, self.sessions_button, self.settings_button, self.theme_button(), self.add_button):
            widget.set_valign(Gtk.Align.CENTER)
            row.pack_start(widget, False, False, 0)
        return row

    def fit_header(self, width):
        """Narrow windows keep only the icons of «Настройки» and the theme, then of «Серверы» and «Сеансы»."""
        compact = {self.settings_button: width < 1100, self.theme_buttons[0]: width < 1100,
                   self.servers_button: width < 960, self.sessions_button: width < 960}
        widths = {self.settings_button: -1, self.theme_buttons[0]: 130, self.servers_button: 104, self.sessions_button: 116}
        for button, small in compact.items():
            if button.label.get_visible() == small:
                GLib.idle_add(self.compact_button, button, small, widths[button])

    @staticmethod
    def compact_button(button, small, width):
        button.label.set_visible(not small)
        button.set_size_request(38 if small else width, -1)
        return False

    def theme_button(self):
        widget = action_button("", self.switch_theme, icon="moon", width=130)
        self.theme_buttons.append(widget)
        return widget

    def apply_theme(self):
        dark = self.settings["dark"]
        self.theme.apply(dark)
        for widget in self.theme_buttons:
            widget.glyph.set_name_glyph("sun" if dark else "moon")
            set_button_text(widget, "Светлая тема" if dark else "Тёмная тема")
        if hasattr(self, "sessions_page"):
            self.sessions_page.refresh()
            self.sessions_page.server_panel.render()

    def switch_theme(self):
        self.settings["dark"] = not self.settings["dark"]
        self.apply_theme()
        self.save_settings()

    def update_status(self):
        """Footer: the file of the chosen protocol and its servers; the start page text (docs/ui-spec.md §2.3, §2.4)."""
        self.status.set_text(f"Подключений: {len(self.server_entries(self.protocol))}")
        home = str(Path.home())
        path = str(self.config.path if self.protocol == "ssh" else self.config.settings_path)
        self.source.set_text("~" + path[len(home):] if path.startswith(home + "/") else path)
        self.add_button.set_sensitive(self.protocol != "ssh" or self.document is not None)
        if not hasattr(self, "start_title"):
            return
        empty = self.document is not None and not self.document.profiles and not self.settings["desktops"] and not self.settings["vnc"]
        if self.document is None:
            title, detail = "Не удалось прочитать SSH config", f"Проверьте файл {self.config.path} и нажмите «Обновить»."
        elif empty:
            title = "Добавьте первый сервер"
            detail = ("SSH-серверы сохраняются в ~/.ssh/config, RDP и VNC — в настройках приложения. "
                      "Можно перенести подключения из PuTTY, mRemoteNG или MobaXterm.")
        else:
            title = "Выберите сервер слева"
            detail = ("Двойной щелчок или Enter открывает сеанс. Правая кнопка — изменить, открыть файлы, "
                      "перенести во вкладку. Быстрое подключение — Ctrl+K.")
        self.start_title.set_text(title)
        self.start_detail.set_text(detail)
        self.start_retry.set_visible(self.document is None)

    def start_page(self):
        """The workspace without a session or files (docs/ui-spec.md §2.3)."""
        page = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8, valign=Gtk.Align.CENTER, halign=Gtk.Align.CENTER)
        self.start_title = Gtk.Label(justify=Gtk.Justification.CENTER)
        mark(self.start_title, "sshm-title")
        self.start_detail = Gtk.Label(wrap=True, max_width_chars=64, justify=Gtk.Justification.CENTER)
        mark(self.start_detail, "sshm-muted")
        buttons = Gtk.Box(spacing=8, halign=Gtk.Align.CENTER, margin_top=10)
        buttons.pack_start(action_button("Добавить сервер", self.add_profile, "primary", icon="plus", width=170), False, False, 0)
        buttons.pack_start(action_button("Импорт из других программ…", self.import_other, icon="folder"), False, False, 0)
        self.start_retry = action_button("Обновить", self.reload, icon="refresh", width=120)
        self.start_retry.set_no_show_all(True)
        buttons.pack_start(self.start_retry, False, False, 0)
        for widget in (self.start_title, self.start_detail, buttons):
            page.pack_start(widget, False, False, 0)
        return page

    def refresh_servers(self, select=None):
        """Refills the «Серверы» column (selecting a saved server) and the footer."""
        self.sessions_page.server_panel.render(select=select)
        self.update_status()

    # --- navigation -------------------------------------------------------------------------
    def show_sessions(self):
        self.sessions_page.show_session()
        if self.sessions_page.active:
            GLib.idle_add(self.sessions_page.active.focus)

    def show_start(self):
        if self.full_session:
            self.leave_full_screen()
        self.sessions_page.show_start()

    def workspace_changed(self):
        self.update_status()

    def shortcuts(self, window, event):
        modifiers = event.state & Gtk.accelerator_get_default_mod_mask()
        control, shift = Gdk.ModifierType.CONTROL_MASK, Gdk.ModifierType.SHIFT_MASK
        key = Gdk.keyval_to_lower(event.keyval)
        # Ctrl+F, Ctrl+N, Ctrl+L, Ctrl+K and F5 belong to the terminal or the remote desktop while it has the keyboard.
        focus = self.get_focus()
        in_session = isinstance(focus, Vte.Terminal) or type(focus).__name__ == "Display"
        if event.keyval in (Gdk.KEY_Tab, Gdk.KEY_ISO_Left_Tab) and modifiers == control:
            self.sessions_page.cycle()
        elif key == Gdk.KEY_p and modifiers == control | shift:
            self.sessions_page.focus_search()
        elif in_session:
            return False
        elif key == Gdk.KEY_f and modifiers == control:
            self.sessions_page.focus_server_search()
        elif key == Gdk.KEY_n and modifiers == control:
            self.add_profile()
        elif key in (Gdk.KEY_l, Gdk.KEY_k) and modifiers == control:
            self.quick.grab_focus()
        elif event.keyval == Gdk.KEY_F5 and not modifiers:
            self.reload()
        else:
            return False
        return True

    def select_protocol(self, protocol):
        """The protocol of the «Серверы» column: «Добавить сервер» creates one of this kind."""
        self.protocol = protocol
        if self.sessions_page.server_panel.protocol != protocol:
            self.sessions_page.server_panel.select(protocol)
        self.update_status()

    # --- data ---------------------------------------------------------------------------------
    def reload(self, show_error=True):
        try:
            self.document = self.config.load()
        except (OSError, ValueError) as error:
            self.document = None
            if show_error:
                message(self, error, Gtk.MessageType.ERROR)
        self.refresh_servers()

    def desktop_profiles(self):
        fields = DesktopProfile.__dataclass_fields__
        return [DesktopProfile(**{key: value for key, value in entry.items() if key in fields})
                for entry in self.settings["desktops"]]

    def vnc_profiles(self):
        fields = VncProfile.__dataclass_fields__
        return [VncProfile(**{key: value for key, value in entry.items() if key in fields}) for entry in self.settings["vnc"]]

    def ssh_profile(self, alias):
        """The SSH profile «Через SSH-сервер» of a VNC connection names."""
        profiles = self.document.profiles if self.document else []
        return next((profile for profile in profiles if profile.alias.lower() == alias.lower()), None)

    @property
    def target_group(self):
        """A new server goes to the tab of the server or group header chosen in the «Серверы» column."""
        page = getattr(self, "sessions_page", None)
        group = page.server_panel.selected_group() if page else ""
        return group if self.group_record(group) else ""

    @staticmethod
    def profile_key(profile):
        if isinstance(profile, VncProfile):
            return "vnc:" + profile.id
        return ("rdp:" + profile.id) if isinstance(profile, DesktopProfile) else "ssh:" + profile.alias.lower()

    def is_favorite(self, profile):
        return self.profile_key(profile) in self.settings["favorites"]

    def toggle_favorite(self, profile):
        key = self.profile_key(profile)
        favorites = [item for item in self.settings["favorites"] if item != key]
        if len(favorites) == len(self.settings["favorites"]):
            favorites.append(key)
        try:
            self.update_catalog_settings(favorites=favorites)
        except OSError as error:
            message(self, error, Gtk.MessageType.ERROR)
        self.refresh_servers()

    def record_recent(self, profile):
        key = self.profile_key(profile)
        recent = [{"key": key, "at": int(time.time())}] + [item for item in self.settings["recent"] if item["key"] != key]
        try:
            self.update_catalog_settings(recent=recent[:10])
        except OSError:
            pass

    def group_of(self, profile):
        if isinstance(profile, (DesktopProfile, VncProfile)):
            return profile.group
        return self.settings["members"].get(profile.alias.lower(), "")

    def group_record(self, identifier):
        return next((group for group in self.settings["groups"] if group["id"] == identifier), None) if identifier else None

    def set_sort(self, order):
        """The order of the «Серверы» column for the chosen protocol ("sort": {"ssh:": "name", …})."""
        try:
            self.update_catalog_settings(sort={**self.settings["sort"], self.protocol + ":": order})
        except OSError as error:
            message(self, error, Gtk.MessageType.ERROR)
        self.refresh_servers()

    def update_catalog_settings(self, **changes):
        settings = {**self.settings, **changes}
        self.config.save_settings(settings)
        self.settings = settings

    def save_settings(self):
        try:
            self.config.save_settings(self.settings)
        except OSError as error:
            message(self, error, Gtk.MessageType.ERROR)

    # --- groups -------------------------------------------------------------------------------
    def ask_group_name(self, title, commit, initial="", current=None):
        name = initial
        while True:
            name = prompt(self, title, name, label="Название вкладки", commit=commit, max_length=32)
            if name is None:
                return None
            name = name.strip()
            taken = {group["name"].casefold() for group in self.settings["groups"] if group is not current}
            if not 1 <= len(name) <= 32 or any(ord(char) < 32 for char in name):
                message(self, "Введите название вкладки длиной от 1 до 32 символов.", Gtk.MessageType.ERROR)
            elif name.casefold() in {ALL_NAME.casefold(), UNGROUPED_NAME.casefold()} | taken:
                message(self, "Это название уже используется. Выберите другое.", Gtk.MessageType.ERROR)
            else:
                return name

    def new_group(self):
        name = self.ask_group_name("Новая вкладка", "Создать")
        if not name:
            return None
        group = {"id": uuid.uuid4().hex, "name": name, "color": ""}
        self.settings["groups"].append(group)
        self.save_settings()
        self.refresh_servers()
        return group

    def rename_group(self, group):
        name = self.ask_group_name("Переименовать вкладку", "Сохранить", group["name"], group)
        if name:
            group["name"] = name
            self.save_settings()
            self.refresh_servers()

    def mark_group(self, group, color):
        group["color"] = color
        self.save_settings()
        self.refresh_servers()
        self.sessions_page.refresh()

    def shift_group(self, group, offset):
        groups = self.settings["groups"]
        index = groups.index(group)
        target = index + offset
        if 0 <= target < len(groups):
            groups[index], groups[target] = groups[target], groups[index]
            self.save_settings()
            self.refresh_servers()

    def delete_group(self, group):
        if confirm(self, f"Удалить вкладку «{group['name']}»? Её подключения перейдут в «Без вкладки»."):
            self.settings["groups"].remove(group)
            self.settings["members"] = {key: value for key, value in self.settings["members"].items() if value != group["id"]}
            self.settings["desktops"] = [dict(item, group="") if item["group"] == group["id"] else item
                                         for item in self.settings["desktops"]]
            self.settings["vnc"] = [dict(item, group="") if item["group"] == group["id"] else item for item in self.settings["vnc"]]
            self.settings["collapsed"] = [key for key in self.settings.get("collapsed", []) if key != group["id"]]
            self.settings["sort"] = {key: value for key, value in self.settings["sort"].items()
                                     if not key.endswith(":" + group["id"])}
            if self.settings["default"] == group["id"]:
                self.settings["default"] = ""
            self.save_settings()
            self.refresh_servers()

    def column_menu(self, widget):
        """«⋯» of the «Серверы» column (docs/ui-spec.md §2.2)."""
        current = self.settings["sort"].get(self.protocol + ":", "original")
        orders = (("original", "Исходный порядок"), ("name", "По названию ↑"), ("name_desc", "По названию ↓"),
                  ("address", "По адресу ↑"), ("address_desc", "По адресу ↓"))
        return menu_at(widget, None, [
            Item("Новая вкладка…", self.new_group, icon="plus"),
            Item("Сортировка", icon="move", children=[Item(title, lambda value=value: self.set_sort(value), checked=current == value)
                                                    for value, title in orders]),
            None,
            Item("Обновить список", self.reload, icon="refresh"),
            Item("Экспорт архива…", self.export_backup, icon="copy"),
            Item("Импорт архива…", self.import_backup, icon="folder"),
            Item("Импорт из других программ…", self.import_other, icon="folder"),
            Item("Импорт .rdp…", lambda: self.transfer_desktop(None), icon="server"),
            None,
            Item("Открыть SSH config", self.open_config, icon="edit"),
            Item("Известные хосты…", lambda: self.open_known_hosts(None), icon="key"),
        ])

    def section_menu(self, widget, event, key):
        """A group header: fold; for a tab also rename, color, order and delete."""
        panel = self.sessions_page.server_panel
        folded = key in panel.collapsed()
        fold = Item("Развернуть" if folded else "Свернуть", lambda: panel.toggle(key), icon="move")
        group = self.group_record(key)
        if group is None:
            return menu_at(widget, event, [fold, None, Item("Новая вкладка…", self.new_group, icon="plus")])
        groups = self.settings["groups"]
        index = groups.index(group)
        colors = [Item("Без метки", lambda: self.mark_group(group, ""), checked=not group["color"])]
        colors += [Item(name, lambda value=value: self.mark_group(group, value), checked=group["color"].lower() == value.lower())
                   for name, value in TAB_COLORS]
        return menu_at(widget, event, [
            fold,
            Item("Переименовать вкладку", lambda: self.rename_group(group), icon="edit"),
            Item("Цветовая метка", icon="dot", children=colors),
            Item("Переместить выше", lambda: self.shift_group(group, -1), icon="move_left", enabled=index > 0),
            Item("Переместить ниже", lambda: self.shift_group(group, 1), icon="move", enabled=index < len(groups) - 1),
            None,
            Item("Новая вкладка…", self.new_group, icon="plus"),
            Item("Удалить вкладку", lambda: self.delete_group(group), icon="delete", danger=True),
        ])

    def open_config(self):
        if not self.config.path.exists():
            message(self, "Файл ~/.ssh/config появится после добавления первого сервера.")
            return
        try:
            Gio.AppInfo.launch_default_for_uri(self.config.path.as_uri(), None)
        except GLib.Error as error:
            message(self, error.message, Gtk.MessageType.ERROR)

    # --- catalog actions ------------------------------------------------------------------------
    def profile_menu(self, widget, event, profile):
        current = self.group_of(profile)
        destinations = [Item(UNGROUPED_NAME, lambda: self.move_profile(profile, ""), icon="folder", checked=current == "")]
        destinations += [Item(group["name"], lambda g=group: self.move_profile(profile, g["id"]), checked=current == group["id"])
                         for group in self.settings["groups"]]
        if isinstance(profile, VncProfile):
            return menu_at(widget, event, [
                Item("Подключиться", lambda: self.connect_profile(profile), icon="server"),
                Item("Новый сеанс", lambda: self.connect_profile(profile, True), icon="plus"),
                Item("Открыть в отдельном окне", lambda: self.connect_profile(profile, True, embedded=False), icon="move"),
                Item("Изменить", lambda: self.edit_vnc(profile), icon="edit"),
                Item("Убрать из избранного" if self.is_favorite(profile) else "В избранное",
                     lambda: self.toggle_favorite(profile), icon="plus"),
                Item("Переместить во вкладку", icon="move", children=destinations),
                None,
                Item("Удалить", lambda: self.delete_vnc(profile), icon="delete", danger=True),
            ])
        if isinstance(profile, DesktopProfile):
            return menu_at(widget, event, [
                Item("Подключиться", lambda: self.connect_profile(profile), icon="server"),
                Item("Новый сеанс", lambda: self.connect_profile(profile, True), icon="plus"),
                Item("Открыть в отдельном окне", lambda: self.connect_profile(profile, True, embedded=False), icon="move"),
                Item("Изменить", lambda: self.edit_desktop(profile), icon="edit"),
                Item("Переместить во вкладку", icon="move", children=destinations),
                Item("Экспорт .rdp…", lambda: self.transfer_desktop(profile), icon="copy"),
                Item("Убрать из избранного" if self.is_favorite(profile) else "В избранное",
                     lambda: self.toggle_favorite(profile), icon="plus"),
                None,
                Item("Удалить", lambda: self.delete_desktop(profile), icon="delete", danger=True),
            ])
        destinations += [None, Item("Новая вкладка…", lambda: self.move_to_new_group(profile), icon="plus")]
        favorite = Item("Убрать из избранного" if self.is_favorite(profile) else "В избранное",
                        lambda: self.toggle_favorite(profile), icon="plus")
        return menu_at(widget, event, [
            Item("Подключиться", lambda: self.connect_profile(profile), icon="terminal"),
            Item("Новый сеанс", lambda: self.connect_profile(profile, True), icon="plus"),
            Item("Файлы (SFTP)", lambda: self.open_files(profile), icon="folder"),
            Item("Открыть в терминале", lambda: self.open_external_terminal(profile), icon="terminal"),
            Item("Изменить", lambda: self.edit_profile(profile), icon="edit"),
            Item("Переместить во вкладку", icon="move", children=destinations),
            Item("Копировать команду SSH", lambda: self.copy_command(profile), icon="copy"),
            Item("Скопировать ключ на сервер", lambda: self.copy_key(profile), icon="key"),
            Item("Известные хосты…", lambda: self.open_known_hosts(profile), icon="key"),
            favorite,
            None,
            Item("Удалить", lambda: self.delete_profile(profile), icon="delete", danger=True),
        ])

    def move_profile(self, profile, destination):
        try:
            if isinstance(profile, VncProfile):
                self.save_vnc(replace(profile, group=destination), editing=True)
            elif isinstance(profile, DesktopProfile):
                self.save_desktop(replace(profile, group=destination), editing=True)
            else:
                self.update_catalog_settings(members={**self.settings["members"], profile.alias.lower(): destination})
        except (OSError, ValueError) as error:
            message(self, error, Gtk.MessageType.ERROR)
        self.refresh_servers()

    def move_to_new_group(self, profile):
        group = self.new_group()
        if group:
            self.move_profile(profile, group["id"])

    def copy_command(self, profile):
        Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD).set_text(ssh_command_text(profile.alias), -1)

    def open_external_terminal(self, profile):
        command = terminal_command(ssh_args(profile, self.config))
        if command is None:
            message(self, "Не найден эмулятор терминала. Установите, например, gnome-terminal или xterm.", Gtk.MessageType.ERROR)
            return
        try:
            subprocess.Popen(command, start_new_session=True, stdin=subprocess.DEVNULL,
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        except OSError as error:
            message(self, error, Gtk.MessageType.ERROR)

    def add_profile(self):
        if self.protocol == "rdp":
            self.edit_desktop(None)
        elif self.protocol == "vnc":
            self.edit_vnc(None)
        else:
            self.edit_profile(None)

    def edit_catalog_entry(self, profile):
        if profile is None:
            return
        if isinstance(profile, VncProfile):
            self.edit_vnc(profile)
        elif isinstance(profile, DesktopProfile):
            self.edit_desktop(profile)
        else:
            self.edit_profile(profile)

    def delete_entry(self, profile):
        if profile is None:
            return
        if isinstance(profile, VncProfile):
            self.delete_vnc(profile)
        elif isinstance(profile, DesktopProfile):
            self.delete_desktop(profile)
        else:
            self.delete_profile(profile)

    def edit_desktop(self, before):
        try:
            has_password = before is not None and self.vault.lookup(before.secret) is not None
        except (OSError, RuntimeError, subprocess.TimeoutExpired):
            has_password = False
        dialog = DesktopDialog(self, before, has_password, group=self.target_group)
        while True:
            response = dialog.run()
            if response == IMPORT_RESPONSE:
                dialog.destroy()
                self.transfer_desktop(None)
                return
            if response != Gtk.ResponseType.OK:
                break
            try:
                profile, password, forget = dialog.values()
                secret = profile.secret
                previous = self.vault.lookup(secret) if (password or forget or has_password) else None
                changed = False
                if forget and previous is not None:
                    self.vault.save(secret, None)
                    changed = True
                elif password or (previous is not None and before is not None and before.account != profile.account):
                    self.vault.save(secret, password or previous)
                    changed = True
                try:
                    self.save_desktop(profile, editing=before is not None)
                except Exception:
                    if changed:
                        self.vault.save(secret, previous)
                    raise
            except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
                dialog.error.set_text(str(error))
            else:
                self.refresh_servers(profile)
                break
        dialog.destroy()

    def save_desktop(self, profile, editing=False):
        entries = list(self.settings["desktops"])
        if editing:
            index = next((i for i, entry in enumerate(entries) if entry["id"] == profile.id), None)
            if index is None:
                raise ValueError("RDP-подключение уже удалено.")
            entries[index] = asdict(profile)
        else:
            entries.append(asdict(profile))
        self.update_catalog_settings(desktops=entries)

    def delete_desktop(self, profile):
        if not confirm(self, f"Удалить RDP-подключение «{profile.name}»?"):
            return
        try:
            self.update_catalog_settings(desktops=[item for item in self.settings["desktops"] if item["id"] != profile.id])
        except OSError as error:
            message(self, error, Gtk.MessageType.ERROR)
        else:
            try:
                if self.vault.lookup(profile.secret) is not None:
                    self.vault.save(profile.secret, None)
            except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
                message(self, error, Gtk.MessageType.ERROR)
            self.refresh_servers()

    def edit_vnc(self, before):
        try:
            has_password = before is not None and self.vault.lookup(before.secret) is not None
        except (OSError, RuntimeError, subprocess.TimeoutExpired):
            has_password = False
        aliases = [profile.alias for profile in self.document.profiles] if self.document else []
        dialog = VncDialog(self, before, has_password, group=self.target_group, ssh_aliases=aliases)
        while dialog.run() == Gtk.ResponseType.OK:
            try:
                profile, password, forget = dialog.values()
                secret = profile.secret
                previous = self.vault.lookup(secret) if (password or forget) else None
                changed = False
                if forget and previous is not None:
                    self.vault.save(secret, None)
                    changed = True
                elif password:
                    self.vault.save(secret, password)
                    changed = True
                try:
                    self.save_vnc(profile, editing=before is not None)
                except Exception:
                    if changed:
                        self.vault.save(secret, previous)
                    raise
            except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
                dialog.error.set_text(str(error))
            else:
                self.refresh_servers(profile)
                break
        dialog.destroy()

    def save_vnc(self, profile, editing=False):
        entries = list(self.settings["vnc"])
        if editing:
            index = next((i for i, entry in enumerate(entries) if entry["id"] == profile.id), None)
            if index is None:
                raise ValueError("VNC-подключение уже удалено.")
            entries[index] = asdict(profile)
        else:
            entries.append(asdict(profile))
        self.update_catalog_settings(vnc=entries)

    def delete_vnc(self, profile):
        if not confirm(self, f"Удалить VNC-подключение «{profile.name}»?"):
            return
        try:
            self.update_catalog_settings(vnc=[item for item in self.settings["vnc"] if item["id"] != profile.id])
        except OSError as error:
            message(self, error, Gtk.MessageType.ERROR)
        else:
            try:
                if self.vault.lookup(profile.secret) is not None:
                    self.vault.save(profile.secret, None)
            except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
                message(self, error, Gtk.MessageType.ERROR)
            self.refresh_servers()

    def transfer_desktop(self, profile):
        rdp_filter = [("Профиль удалённого рабочего стола (*.rdp)", "*.rdp")]
        if profile is None:
            path = chooser(self, "Импорт профиля RDP", Gtk.FileChooserAction.OPEN, filters=rdp_filter)
        else:
            path = chooser(self, "Экспорт профиля RDP", Gtk.FileChooserAction.SAVE, proposed="connection.rdp", filters=rdp_filter)
        if path:
            try:
                if profile is None:
                    imported = DesktopProfile.import_file(path, self.target_group)
                    self.save_desktop(imported)
                    self.refresh_servers(imported)
                else:
                    profile.export_file(path)
            except (OSError, ValueError) as error:
                message(self, error, Gtk.MessageType.ERROR)

    def edit_profile(self, before):
        if self.document is None:
            return
        try:
            keys, key_error = self.config.keys(), None
        except OSError as error:
            keys, key_error = [], error
        try:
            has_password = before is not None and self.vault.lookup(account_for(before)) is not None
        except (OSError, RuntimeError, subprocess.TimeoutExpired):
            has_password = False
        dialog = ProfileDialog(self, before, keys, has_password, key_error, self.settings["preferences"])
        while dialog.run() == Gtk.ResponseType.OK:
            after, password, forget = dialog.values()
            try:
                updated = self.document.update(before, after)
                previous, account = account_for(before), account_for(after)
                if password and not forget and not account:
                    raise ValueError("Для входа по сохранённому паролю укажите SSH-пользователя и обычный DNS-адрес или IP без подстановок.")
                migrated = None
                if previous and account and previous.key != account.key and not password and not forget and (
                        previous.host, previous.user, previous.port) == (account.host, account.user, account.port):
                    migrated = self.vault.lookup(previous)
                changed = False
                previous_password = None
                if account and (password or forget or migrated):
                    previous_password = self.vault.lookup(account)
                    if not forget or previous_password is not None:
                        self.vault.save(account, None if forget else password or migrated)
                        changed = True
                try:
                    self.config.save(self.document, updated)
                except Exception:
                    if changed:
                        self.vault.save(account, previous_password)
                    raise
                if previous and previous.key != (account.key if account else ""):
                    self.drop_password(previous, updated)
                if before and before.alias.lower() != after.alias.lower():
                    self.settings["members"][after.alias.lower()] = self.settings["members"].pop(before.alias.lower(), "")
                elif not before and self.target_group:
                    self.settings["members"][after.alias.lower()] = self.target_group
                self.save_settings()
                self.reload()
                self.refresh_servers(after)
                break
            except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
                dialog.hint.set_text(str(error))
        dialog.destroy()

    def delete_profile(self, profile):
        if not confirm(self, f"Удалить подключение «{profile.alias}» из SSH config?"):
            return
        try:
            updated = self.document.update(profile, None)
            self.config.save(self.document, updated)
            self.drop_password(account_for(profile), updated)
            self.settings["members"].pop(profile.alias.lower(), None)
            self.save_settings()
            self.reload()
        except (OSError, ValueError) as error:
            message(self, error, Gtk.MessageType.ERROR)

    def drop_password(self, account, document):
        if not account or any(old and account.key == old.key for old in map(account_for, document.profiles)):
            return
        try:
            if self.vault.lookup(account) is not None:
                self.vault.save(account, None)
        except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
            message(self, error, Gtk.MessageType.ERROR)

    def open_files(self, profile):
        if isinstance(profile, VncProfile):
            current = next((item for item in self.vnc_profiles() if item.id == profile.id), profile)
            profile = self.ssh_profile(current.files_via) if current.files_via else None
            if profile is None:
                message(self, "Выберите SSH-профиль для файлов в настройках VNC-подключения.", Gtk.MessageType.ERROR)
                return
        key = (profile.alias, profile.address)
        if key in self.files:
            self.sessions_page.show_files(self.files[key])
            return
        panel = FilesPanel(self, profile, self.config, self.vault, self.sessions_page.remove_files)
        self.files[key] = panel
        panel.connect("destroy", lambda unused: self.files.pop(key, None))
        self.sessions_page.add_files(panel)

    def export_backup(self):
        from .backup import choose_export
        choose_export(self)

    def import_backup(self):
        from .backup import choose_import
        choose_import(self)

    # --- sessions -----------------------------------------------------------------------------
    def ssh_arguments(self, profile, account):
        quick = self.transient.get(profile.alias)
        if quick is not None:
            user, host, port = quick
            base = ssh_args(Profile(host), self.config, None)
            base[-1:-1] = ["-p", str(port)] + (["-l", user] if user else [])
            return base
        return ssh_args(profile, self.config, account)

    # --- stage 2 actions ------------------------------------------------------------------------
    def quick_connect(self, text):
        try:
            protocol, user, host, port = parse_quick(text)
        except ValueError as error:
            message(self, error, Gtk.MessageType.ERROR)
            return False
        if protocol in ("rdp", "vnc"):
            kind = DesktopProfile if protocol == "rdp" else VncProfile
            profile = kind(uuid.uuid4().hex, host, host, port, user).validate()
            self.connect_profile(profile)
            if confirm(self, f"Сохранить подключение «{host}»?"):
                try:
                    (self.save_desktop if protocol == "rdp" else self.save_vnc)(replace(profile, group=self.target_group))
                except (OSError, ValueError) as error:
                    message(self, error, Gtk.MessageType.ERROR)
                self.refresh_servers()
            return True
        existing = [item.alias for item in self.document.profiles] if self.document else []
        alias = quick_alias(host, existing + list(self.transient))
        self.transient[alias] = (user, host, port)
        profile = Profile(alias, host, user, "" if port == 22 else str(port))
        self.connect_profile(profile, True)
        if self.document is not None and confirm(self, f"Сохранить подключение «{alias}»?"):
            try:
                updated = self.document.update(None, profile)
                self.config.save(self.document, updated)
                if self.target_group:
                    self.settings["members"][alias.lower()] = self.target_group
                    self.save_settings()
                self.transient.pop(alias, None)
                self.reload()
            except (OSError, ValueError) as error:
                message(self, error, Gtk.MessageType.ERROR)
        return True

    def copy_key(self, profile):
        ssh_folder = Path.home() / ".ssh"
        keys = sorted(ssh_folder.glob("*.pub")) if ssh_folder.is_dir() else []
        path = chooser(self, "Публичный ключ для сервера", Gtk.FileChooserAction.OPEN, directory=ssh_folder,
                       filters=(("Публичные ключи (*.pub)", "*.pub"),)) if len(keys) != 1 else str(keys[0])
        if not path:
            return
        try:
            command = copy_key_command(Path(path).read_text(encoding="utf-8", errors="replace"))
        except (OSError, ValueError) as error:
            message(self, error, Gtk.MessageType.ERROR)
            return
        number = max((session.number for session in self.related(profile)), default=0) + 1
        session = SshSession(self, profile, number, command=command)
        session.name = "ключ"
        self.sessions.append(session)
        self.show_sessions()
        self.sessions_page.add(session)
        self.sessions_changed()

    def add_snippet(self):
        name = prompt(self, "Новая команда", label="Название", commit="Далее", max_length=80)
        if not name or not name.strip():
            return
        command = prompt(self, "Новая команда", label="Команда", commit="Сохранить", max_length=4000)
        if not command or not command.strip():
            return
        try:
            self.update_catalog_settings(snippets=self.settings["snippets"] + [{"name": name.strip(), "command": command}])
        except OSError as error:
            message(self, error, Gtk.MessageType.ERROR)

    def remove_snippet(self, index):
        snippets = list(self.settings["snippets"])
        if 0 <= index < len(snippets) and confirm(self, f"Удалить команду «{snippets[index]['name']}»?"):
            del snippets[index]
            try:
                self.update_catalog_settings(snippets=snippets)
            except OSError as error:
                message(self, error, Gtk.MessageType.ERROR)

    def open_known_hosts(self, focus=None):
        """«Известные хосты» (docs/ui-spec.md §9.2): for a server — its file and its host in the search."""
        from .dialogs import KnownHostsDialog
        from .known_hosts import host_name, resolve
        default = self.config.path.parent / "known_hosts"
        files = {}
        for profile in (self.document.profiles if self.document else []):
            if profile.known_hosts:
                files.setdefault(profile.known_hosts, []).append(profile.alias)
        selected, query = None, ""
        if focus is not None:
            selected = resolve(focus.known_hosts, default)
            port = int(focus.port) if focus.port.isdigit() and 1 <= int(focus.port) <= 65535 else 22
            query = host_name(focus.host or focus.alias, port)
        dialog = KnownHostsDialog(self, default, sorted(files.items()), selected, query)
        dialog.run()
        dialog.destroy()

    def open_settings(self):
        dialog = SettingsDialog(self, self.settings["preferences"])
        while dialog.run() == Gtk.ResponseType.OK:
            try:
                preferences = dialog.values(self.settings["preferences"])
                self.update_catalog_settings(preferences=preferences)
            except (OSError, ValueError) as error:
                dialog.error.set_text(str(error))
                continue
            for session in self.sessions:
                session.apply_preferences(preferences)
            break
        dialog.destroy()

    def import_other(self):
        dialog = ImportDialog(self, putty_sessions)
        chosen = dialog.chosen() if dialog.run() == Gtk.ResponseType.OK else []
        dialog.destroy()
        if not chosen:
            return
        added = 0
        try:
            desktops = list(self.settings["desktops"])
            vnc = list(self.settings["vnc"])
            document = self.document
            names = [item.alias for item in document.profiles] if document else []
            for item in chosen:
                if item.protocol == "rdp":
                    desktops.append(asdict(DesktopProfile(uuid.uuid4().hex, item.name[:80] or item.host, item.host, item.port,
                                                          item.user, item.domain, self.target_group).validate()))
                elif item.protocol == "vnc":
                    vnc.append(asdict(VncProfile(uuid.uuid4().hex, item.name[:80] or item.host, item.host, item.port,
                                                 item.user, self.target_group).validate()))
                elif document is not None:
                    alias = quick_alias(item.name, names)
                    names.append(alias)
                    document = document.update(None, Profile(alias, item.host, item.user,
                                                             "" if item.port == 22 else str(item.port)))
                    if self.target_group:
                        self.settings["members"][alias.lower()] = self.target_group
                added += 1
            if document is not self.document:
                self.config.save(self.document, document)
            self.update_catalog_settings(desktops=desktops, vnc=vnc)
        except (OSError, ValueError) as error:
            message(self, error, Gtk.MessageType.ERROR)
        self.reload()
        message(self, f"Импортировано подключений: {added}.")

    @staticmethod
    def server_key(profile):
        if isinstance(profile, VncProfile):
            return "vnc", profile.id
        return ("rdp", profile.id) if isinstance(profile, DesktopProfile) else ("ssh", profile.alias.lower())

    def related(self, profile):
        key = self.server_key(profile)
        return [session for session in self.sessions if self.server_key(session.profile) == key]

    def session_count(self, profile):
        return sum(session.live for session in self.related(profile))

    def connect_profile(self, profile, new=False, embedded=None, forwarding=True, credential=None):
        if isinstance(profile, VncProfile) and embedded is False:
            self.record_recent(profile)
            VncWindow(self, profile, credential if credential is not None else self.saved_vnc_password(profile))
            return
        related = self.related(profile)
        if not new:
            existing = max((session for session in related if session.live), key=lambda item: item.last_used, default=None)
            if existing is not None:
                self.show_sessions()
                self.sessions_page.activate(existing, focus=True)
                return
        number = max((session.number for session in related), default=0) + 1
        if not isinstance(profile, (DesktopProfile, VncProfile)) and profile.alias in self.transient:
            pass
        else:
            self.record_recent(profile)
        if isinstance(profile, VncProfile):
            session = VncSession(self, profile, number, credential if credential is not None else self.saved_vnc_password(profile))
        elif isinstance(profile, DesktopProfile):
            if credential is None:
                try:
                    saved = self.vault.lookup(profile.secret) if profile.user else None
                except (OSError, RuntimeError, subprocess.TimeoutExpired):
                    saved = None
                credential = ask_credential(self, profile, saved)
                if credential is None:
                    return
            embedded = embedding_supported() if embedded is None else embedded and embedding_supported()
            session = RdpSession(self, profile, number, credential, embedded)
        else:
            session = SshSession(self, profile, number, forwarding)
        self.sessions.append(session)
        self.show_sessions()
        self.sessions_page.add(session)
        if isinstance(session, (RdpSession, VncSession)):
            session.start()
        self.sessions_changed()

    def saved_vnc_password(self, profile):
        try:
            return self.vault.lookup(profile.secret)
        except (OSError, RuntimeError, subprocess.TimeoutExpired):
            return None

    def duplicate_session(self, session, forwarding=True):
        if isinstance(session, VncSession):
            self.connect_profile(session.profile, True, credential=session.credential)
        elif isinstance(session, RdpSession):
            self.connect_profile(session.profile, True, embedded=session.embedded, credential=session.credential)
        else:
            self.connect_profile(session.profile, True, forwarding=forwarding)

    def detach_session(self, session):
        credential = session.credential
        self.close_session(session, confirm_close=False)
        self.connect_profile(session.profile, True, embedded=False, credential=credential)

    def session_changed(self, session):
        self.sessions_page.refresh()
        self.sessions_changed()

    def sessions_changed(self):
        live = sum(session.live for session in self.sessions)
        set_button_text(self.sessions_button, f"Сеансы · {live}" if live else "Сеансы")
        self.sessions_page.server_panel.render()

    def close_session(self, session, confirm_close=True):
        if session not in self.sessions:
            return
        if confirm_close and session.live and not confirm(
                self, f"Закрыть {session.protocol}-сеанс «{session.title}»?", title="Вы уверены?"):
            return
        if session is self.full_session:
            self.leave_full_screen()
        self.sessions.remove(session)
        session.stop()
        self.sessions_page.remove(session)
        session.destroy()
        self.sessions_changed()

    def rename_session(self, session):
        while True:
            name = prompt(self, "Переименовать сеанс", session.name, label="Метка сеанса", commit="Сохранить", max_length=80)
            if name is None:
                return
            name = name.strip()
            clash = any(other is not session and other.name.casefold() == name.casefold() for other in self.related(session.profile))
            if any(ord(char) < 32 for char in name) or (name and clash):
                message(self, "Укажите уникальную метку для сеанса этого сервера.", Gtk.MessageType.ERROR)
                continue
            session.name = name
            self.sessions_page.refresh()
            return

    def enter_full_screen(self, session):
        if not isinstance(session, (RdpSession, VncSession)) or not session.embedded:
            return
        self.full_session = session
        self.sessions_page.activate(session)
        self.sessions_page.set_chrome(False)
        self.header_row.hide()
        self.footer.hide()
        self.body.set_margin_start(0)
        self.body.set_margin_end(0)
        session.set_full_screen(True)
        self.fullscreen()

    def leave_full_screen(self):
        session, self.full_session = self.full_session, None
        self.unfullscreen()
        self.sessions_page.set_chrome(True)
        self.header_row.show()
        self.footer.show()
        self.body.set_margin_start(12)
        self.body.set_margin_end(12)
        if session is not None:
            session.set_full_screen(False)

    def server_entries(self, protocol, order="name"):
        names = {group["id"]: group for group in self.settings["groups"]}
        profiles = (self.desktop_profiles() if protocol == "rdp" else self.vnc_profiles() if protocol == "vnc" else
                    list(self.document.profiles) if self.document else [])
        result = []
        for profile in ordered(profiles, order):
            group = names.get(self.group_of(profile))
            result.append((profile, group["name"] if group else "",
                           group["color"] if group and group["color"] else self.theme.color("teal")))
        return result

    def pick_server(self):
        dialog = ServerPicker(self, self.protocol, self.server_entries, [item["key"] for item in self.settings["recent"]])
        profile = dialog.chosen() if dialog.run() == Gtk.ResponseType.OK else None
        dialog.destroy()
        if profile is not None:
            self.connect_profile(profile, True)

    # --- window -------------------------------------------------------------------------------
    def restore(self):
        self.show()
        self.deiconify()
        self.present()

    def on_delete(self, *_):
        if self.quitting:
            return False
        if self.tray.available and self.settings["preferences"]["close_to_tray"]:
            self.hide()
            return True
        if self.updates.installing:
            message(self, "Дождитесь завершения установки обновления.")
            return True
        busy = any(session.live for session in self.sessions) or bool(self.files)
        return busy and not confirm(self, "Закрыть активные сеансы и окна SFTP?")

    def quit_app(self):
        if self.updates.installing:
            message(self, "Дождитесь завершения установки обновления.")
            return
        busy = any(session.live for session in self.sessions) or bool(self.files)
        if busy:
            self.restore()
            if not confirm(self, "Закрыть активные сеансы и окна SFTP?"):
                return
        self.quitting = True
        for session in self.sessions[:]:
            session.stop()
        self.get_application().quit()


class Application(Gtk.Application):
    def __init__(self, config, isolated=False):
        super().__init__(application_id="io.github.clarkufa.SSHManager",
                         flags=Gio.ApplicationFlags.NON_UNIQUE if isolated else Gio.ApplicationFlags.FLAGS_NONE)
        self.config = config

    def do_shutdown(self):
        for window in self.get_windows():
            if isinstance(window, VncWindow):
                window.session.stop()
            elif isinstance(window, Window):
                for session in window.sessions:
                    if isinstance(session, VncSession):
                        session.stop()
        Gtk.Application.do_shutdown(self)

    def do_activate(self):
        window = next((window for window in self.get_windows() if isinstance(window, Window)), None)
        if not window:
            icon = app_icon()
            if icon:
                Gtk.Window.set_default_icon_from_file(str(icon))
            window = Window(self, self.config)
            window.show_all()
            window.sessions_page.refresh_details()
        window.restore()


def main():
    parser = argparse.ArgumentParser(prog="ssh-manager")
    parser.add_argument("--version", action="version", version=VERSION)
    parser.add_argument("--config", type=Path, help="OpenSSH config")
    parser.add_argument("--settings-dir", type=Path, help="Application settings directory")
    options = parser.parse_args()
    state = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "ssh-manager"
    config = Config(options.config or Path.home() / ".ssh" / "config", options.settings_dir or state)
    return Application(config, bool(options.config or options.settings_dir)).run([sys.argv[0]])
