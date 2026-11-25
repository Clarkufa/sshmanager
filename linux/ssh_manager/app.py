import argparse
import os
import re
import signal
import subprocess
import sys
import threading
import uuid
from dataclasses import asdict, replace
from pathlib import Path

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
gi.require_version("Vte", "2.91")
from gi.repository import Gdk, Gio, GLib, GObject, Gtk, Pango, Vte

from . import VERSION
from .auth import AskPass, Vault, account_for, ssh_args
from .config import Config, Profile, parse_settings
from .files import FilesWindow
from .rdp import DesktopProfile, ORDERS, ordered
from .update_ui import UpdateController
from .widgets import button, chooser, confirm, menu_at, message, prompt


STYLE = b"""
.page-title { font-size: 20px; font-weight: bold; }
.section-title, .host-title { font-weight: bold; }
.host-row { padding: 8px 12px; }
.muted { opacity: 0.75; }
.group-strip button { border-radius: 6px; }
vte-terminal { padding: 9px; }
"""

def prepare_terminal_paste(text):
    text = re.sub(r"(?:\x1b\[|\x9b)[0-?]*[ -/]*[@-~]|\^\[\[20[01]~", "", text)
    return re.sub(r"[\x00-\x08\x0a-\x0c\x0e-\x1f\x7f-\x9f]", "", text.replace("\r\n", "\r").replace("\n", "\r"))


def terminal_font():
    source = Gio.SettingsSchemaSource.get_default()
    schema = source.lookup("org.gnome.desktop.interface", True) if source else None
    if schema and schema.has_key("monospace-font-name"):
        font = Gio.Settings.new_full(schema, None, None).get_string("monospace-font-name").strip()
        if font:
            return Pango.FontDescription.from_string(font)
    return Pango.FontDescription.from_string("Monospace 11")


class CatalogRow(Gtk.ListBoxRow):
    def __init__(self, owner, profile):
        super().__init__()
        self.profile = profile
        self.get_style_context().add_class("host-row")
        bar = Gtk.Box(spacing=9)
        desktop = isinstance(profile, DesktopProfile)
        icon = Gtk.Image.new_from_icon_name("video-display-symbolic" if desktop else "network-server-symbolic", Gtk.IconSize.BUTTON)
        bar.pack_start(icon, False, False, 4)
        labels = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2)
        name = Gtk.Label(label=profile.alias, xalign=0, ellipsize=Pango.EllipsizeMode.END)
        name.get_style_context().add_class("host-title")
        address = Gtk.Label(label=profile.address, xalign=0, ellipsize=Pango.EllipsizeMode.END)
        address.get_style_context().add_class("muted")
        labels.pack_start(name, False, False, 0)
        labels.pack_start(address, False, False, 0)
        bar.pack_start(labels, True, True, 3)
        if not desktop:
            bar.pack_start(button("Файлы", lambda: owner.open_files(profile)), False, False, 0)
        bar.pack_start(button("Подключиться", lambda: owner.connect_profile(profile), suggested=True), False, False, 0)
        more = button("⋯", lambda: owner.profile_menu(self, None))
        more.set_tooltip_text("Действия с сервером")
        bar.pack_start(more, False, False, 0)
        self.add(bar)
        self.connect("button-press-event", lambda _, event: owner.profile_menu(self, event) if event.button == 3 else False)


class ProfileDialog(Gtk.Dialog):
    def __init__(self, owner, profile, keys):
        super().__init__(title="Изменить сервер" if profile else "Новый сервер", transient_for=owner, modal=True)
        self.set_default_size(515, 415)
        self.add_button("Отмена", Gtk.ResponseType.CANCEL)
        self.add_button("Сохранить", Gtk.ResponseType.OK)
        self.get_widget_for_response(Gtk.ResponseType.OK).get_style_context().add_class("suggested-action")
        self.set_default_response(Gtk.ResponseType.OK)
        grid = Gtk.Grid(column_spacing=12, row_spacing=8, margin=15)
        self.get_content_area().add(grid)
        self.entries = {}
        updating = profile is not None
        profile = profile or Profile("")
        for i, (name, caption) in enumerate((("alias", "Псевдоним"), ("host", "Адрес"), ("user", "Пользователь"),
                                             ("port", "Порт"), ("password", "Пароль SSH"))):
            entry = Gtk.Entry(text=getattr(profile, name, ""), visibility=name != "password",
                              activates_default=True, hexpand=True)
            if name == "password":
                entry.set_placeholder_text("Пустое поле сохраняет прежний пароль" if updating else "Необязательно")
            self.entries[name] = entry
            grid.attach(Gtk.Label(label=caption, xalign=0), 0, i, 1, 1)
            grid.attach(entry, 1, i, 2, 1)
        self.forget = Gtk.CheckButton(label="Удалить сохранённый пароль")
        self.forget.connect("toggled", lambda widget: self.entries["password"].set_sensitive(not widget.get_active()))
        grid.attach(self.forget, 1, 5, 2, 1)
        self.forget.set_no_show_all(not updating)
        self.key_view = Gtk.TextView(monospace=True)
        self.key_view.get_buffer().set_text("\n".join(profile.keys))
        scroll = Gtk.ScrolledWindow(shadow_type=Gtk.ShadowType.IN, min_content_height=77)
        scroll.add(self.key_view)
        grid.attach(Gtk.Label(label="Ключи SSH", xalign=0, yalign=0), 0, 6, 1, 1)
        grid.attach(scroll, 1, 6, 2, 1)
        available = Gtk.ComboBoxText(hexpand=True)
        available.append_text("Добавить из ~/.ssh…")
        for key in keys:
            available.append_text(key)
        available.set_active(0)

        def choose_available(combo):
            text = combo.get_active_text()
            if combo.get_active() > 0:
                self.add_key(text)
                combo.set_active(0)

        available.connect("changed", choose_available)
        grid.attach(available, 1, 7, 1, 1)
        grid.attach(button("Обзор…", self.browse), 2, 7, 1, 1)
        self.hint = Gtk.Label(xalign=0, wrap=True)
        self.hint.get_style_context().add_class("error")
        grid.attach(self.hint, 1, 8, 2, 1)
        self.show_all()

    def keys(self):
        buffer = self.key_view.get_buffer()
        return tuple(line.strip() for line in buffer.get_text(*buffer.get_bounds(), False).splitlines() if line.strip())

    def add_key(self, key):
        if key and key not in self.keys():
            self.key_view.get_buffer().set_text("\n".join((*self.keys(), key)))

    def browse(self):
        self.add_key(chooser(self, "Выбрать SSH-ключ", Gtk.FileChooserAction.OPEN, directory=Path.home() / ".ssh"))

    def values(self):
        profile = Profile(*(self.entries[name].get_text().strip() for name in ("alias", "host", "user", "port")), self.keys())
        return profile, self.entries["password"].get_text(), self.forget.get_active()


class DesktopDialog(Gtk.Dialog):
    def __init__(self, owner, profile):
        super().__init__(title="RDP-подключение", transient_for=owner, modal=True)
        self.set_default_size(500, 370)
        self.add_button("Отмена", Gtk.ResponseType.CANCEL)
        self.add_button("Сохранить", Gtk.ResponseType.OK)
        self.set_default_response(Gtk.ResponseType.OK)
        self.profile = profile or DesktopProfile(uuid.uuid4().hex, "", "", group=owner.active_group)
        grid = Gtk.Grid(column_spacing=12, row_spacing=8, margin=15)
        self.get_content_area().add(grid)
        self.entries = {}
        fields = (("name", "Название"), ("host", "Адрес"), ("port", "Порт"), ("user", "Пользователь"),
                  ("domain", "Домен"), ("width", "Ширина экрана"), ("height", "Высота экрана"))
        for row, (key, label) in enumerate(fields):
            value = getattr(self.profile, key)
            entry = Gtk.Entry(text=str(value or ""), activates_default=True, hexpand=True)
            self.entries[key] = entry
            grid.attach(Gtk.Label(label=label, xalign=0), 0, row, 1, 1)
            grid.attach(entry, 1, row, 1, 1)
        self.clipboard = Gtk.CheckButton(label="Общий буфер обмена", active=self.profile.clipboard)
        self.fullscreen = Gtk.CheckButton(label="Полноэкранный режим", active=self.profile.fullscreen)
        self.error = Gtk.Label(xalign=0, wrap=True)
        grid.attach(self.clipboard, 1, 7, 1, 1)
        grid.attach(self.fullscreen, 1, 8, 1, 1)
        grid.attach(self.error, 1, 9, 1, 1)
        self.show_all()

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
        return replace(self.profile, **fields, clipboard=self.clipboard.get_active(),
                       fullscreen=self.fullscreen.get_active()).validate()


class Shell(Gtk.Box):
    def __init__(self, owner, profile, forwarding=True):
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        self.owner, self.profile = owner, profile
        self.forwarding = forwarding
        self.pid = None
        self.live = True
        self.broker = None
        self.closed = False
        self.shift_paste_held = False
        bar = Gtk.Box(spacing=8)
        title = Gtk.Label(label=profile.address, xalign=0, ellipsize=Pango.EllipsizeMode.END)
        title.get_style_context().add_class("section-title")
        bar.pack_start(title, True, True, 0)
        bar.pack_start(button("Файлы", lambda: owner.open_files(profile)), False, False, 0)
        bar.pack_start(button("Без проброса портов", lambda: owner.open_session(profile, True, False)), False, False, 0)
        bar.pack_start(button("Закрыть сеанс", lambda: owner.close_session(self)), False, False, 0)
        self.pack_start(bar, False, False, 0)
        self.terminal = Vte.Terminal()
        self.terminal.set_scrollback_lines(7000)
        self.terminal.set_mouse_autohide(True)
        self.terminal.set_font(terminal_font())
        foreground, background = Gdk.RGBA(), Gdk.RGBA()
        foreground.parse("#e5edf6")
        background.parse("#111a27")
        self.terminal.set_colors(foreground, background, [])
        self.terminal.connect("child-exited", self.exited)
        self.terminal.connect("key-press-event", self.shortcut)
        self.terminal.connect("paste-clipboard", self.on_paste_clipboard)
        self.terminal.connect("button-press-event", self.force_right_click)
        self.terminal.connect("button-release-event", self.force_right_click)
        # VTE handles mouse reporting first. Paste only if it leaves the click unhandled.
        self.terminal.connect_after("button-press-event", self.right_click)
        scroll = Gtk.ScrolledWindow()
        scroll.add(self.terminal)
        self.pack_start(scroll, True, True, 0)
        self.connect("destroy", self.stop)
        self.show_all()
        self.terminal.grab_focus()
        threading.Thread(target=self.prepare, daemon=True).start()

    def force_right_click(self, terminal, event):
        if event.button != 3:
            return False
        if event.type == Gdk.EventType.BUTTON_RELEASE:
            handled = self.shift_paste_held
            self.shift_paste_held = False
            return handled
        if event.state & Gdk.ModifierType.SHIFT_MASK and self.right_click(terminal, event):
            self.shift_paste_held = True
            return True
        return False

    def right_click(self, terminal, event):
        if event.type != Gdk.EventType.BUTTON_PRESS or event.button != 3 or not self.live or self.closed:
            return False
        terminal.grab_focus()
        self.paste_clipboard(terminal)
        return True

    def on_paste_clipboard(self, terminal):
        GObject.signal_stop_emission_by_name(terminal, "paste-clipboard")
        self.paste_clipboard(terminal)

    def paste_clipboard(self, terminal):
        if self.live and not self.closed:
            clipboard = Gtk.Clipboard.get_for_display(terminal.get_display(), Gdk.SELECTION_CLIPBOARD)
            clipboard.request_text(self.paste_text, terminal)

    def paste_text(self, clipboard, text, terminal):
        if not text or not self.live or self.closed:
            return
        text = prepare_terminal_paste(text)
        if "\r" in text and not confirm(self.owner, "Текст содержит переносы строк. Вставка может сразу выполнить команды. Продолжить?"):
            return
        if text and self.live and not self.closed:
            terminal.feed_child(text.encode("utf-8"))

    def shortcut(self, terminal, event):
        control, shift = Gdk.ModifierType.CONTROL_MASK, Gdk.ModifierType.SHIFT_MASK
        modifiers = event.state & Gtk.accelerator_get_default_mod_mask()
        key = Gdk.keyval_to_lower(event.keyval)
        letters = {Gdk.KEY_a, Gdk.KEY_c, Gdk.KEY_v}
        if modifiers in (control, control | shift) and not Gdk.KEY_a <= key <= Gdk.KEY_z and key != Gdk.KEY_Insert:
            keymap = Gdk.Keymap.get_for_display(terminal.get_display())
            found, _, values = keymap.get_entries_for_keycode(event.hardware_keycode)
            if found:
                key = next((value for value in map(Gdk.keyval_to_lower, values) if value in letters), key)
            key = {Gdk.KEY_Cyrillic_ef: Gdk.KEY_a, Gdk.KEY_Cyrillic_es: Gdk.KEY_c,
                   Gdk.KEY_Cyrillic_em: Gdk.KEY_v}.get(key, key)
        copy = (key == Gdk.KEY_c and (modifiers == control | shift or
                modifiers == control and terminal.get_has_selection()) or key == Gdk.KEY_Insert and modifiers == control)
        paste = (key == Gdk.KEY_v and modifiers in (control, control | shift) or
                 key == Gdk.KEY_Insert and modifiers == shift)
        if copy:
            terminal.copy_clipboard_format(Vte.Format.TEXT)
        elif paste:
            self.paste_clipboard(terminal)
        elif key == Gdk.KEY_a and modifiers == control | shift:
            terminal.select_all()
        else:
            return False
        return True

    def prepare(self):
        account = account_for(self.profile)
        try:
            password = self.owner.vault.lookup(account)
        except (RuntimeError, OSError, subprocess.TimeoutExpired):
            password = None
        GLib.idle_add(self.start, account if password else None, password)

    def start(self, account, password):
        if self.closed:
            return False
        try:
            arguments = ssh_args(self.profile, self.owner.config, account)
            if not self.forwarding:
                arguments[1:1] = ["-o", "ClearAllForwardings=yes"]
            environment = None
            if password:
                self.broker = AskPass(account, password)
                environment = [key + "=" + value for key, value in self.broker.environment().items()]
            # PyGObject expects child_setup_data even though it omits it in the generated signature.
            self.terminal.spawn_async(Vte.PtyFlags.DEFAULT, str(Path.home()), arguments, environment,
                                      GLib.SpawnFlags.DEFAULT, None, None, -1, None, self.spawned, None)
        except (OSError, RuntimeError, TypeError, GLib.Error) as error:
            self.exited(self.terminal, 0)
            message(self.owner, error, Gtk.MessageType.ERROR)
        return False

    def spawned(self, terminal, pid, error, *_):
        if error:
            self.exited(terminal, 0)
            if not self.closed:
                message(self.owner, error, Gtk.MessageType.ERROR)
            return
        if self.closed:
            try:
                os.kill(pid, signal.SIGHUP)
            except ProcessLookupError:
                pass
            return
        self.pid = pid
        if self.broker:
            self.broker.attach(pid)

    def exited(self, terminal, status):
        self.pid = None
        self.live = False
        if self.broker:
            self.broker.close()
            self.broker = None
        terminal.set_input_enabled(False)
        if not self.closed:
            self.tab.set_text(self.tab.get_text().removesuffix(" · завершён") + " · завершён")

    def stop(self, *_):
        self.closed = True
        if self.pid:
            try:
                os.kill(self.pid, signal.SIGHUP)
            except ProcessLookupError:
                pass
        if self.broker:
            self.broker.close()
            self.broker = None


class DesktopSession(Gtk.Box):
    def __init__(self, owner, profile, command, label):
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        self.owner = owner
        self.profile = profile
        self.command = command
        self.live = True
        self.closed = False
        self.pid = None
        self.tab = Gtk.Label(label=label)
        self.tab.set_tooltip_text(profile.address)
        bar = Gtk.Box(spacing=8)
        bar.pack_start(Gtk.Label(label=profile.address, xalign=0), True, True, 0)
        bar.pack_start(button("Новый сеанс", lambda: owner.connect_profile(profile, True)), False, False, 0)
        bar.pack_start(button("Закрыть сеанс", lambda: owner.close_session(self)), False, False, 0)
        self.pack_start(bar, False, False, 0)
        self.pack_start(Gtk.Label(label="Рабочий стол откроется в окне FreeRDP. Здесь отображаются запросы и сообщения клиента.",
                                  xalign=0, wrap=True), False, False, 0)
        self.terminal = Vte.Terminal()
        self.terminal.set_font(terminal_font())
        self.terminal.connect("child-exited", self.exited)
        self.terminal.connect("key-press-event", self.paste_key)
        scroll = Gtk.ScrolledWindow()
        scroll.add(self.terminal)
        self.pack_start(scroll, True, True, 0)
        self.connect("destroy", self.stop)
        self.show_all()

    def start(self):
        try:
            self.terminal.spawn_async(Vte.PtyFlags.DEFAULT, str(Path.home()), self.command, None,
                                      GLib.SpawnFlags.DEFAULT, None, None, -1, None, self.spawned, None)
        except (OSError, RuntimeError, TypeError, GLib.Error) as error:
            self.spawned(self.terminal, -1, error)

    def paste_key(self, terminal, event):
        keys = event.state & Gtk.accelerator_get_default_mod_mask()
        if keys == Gdk.ModifierType.CONTROL_MASK and Gdk.keyval_to_lower(event.keyval) == Gdk.KEY_v:
            terminal.paste_clipboard()
            return True
        return False

    def spawned(self, terminal, pid, error, *_):
        if error:
            self.exited(terminal, 0)
            if not self.closed:
                message(self.owner, error, Gtk.MessageType.ERROR)
        elif self.closed:
            self.terminate(pid)
        else:
            self.pid = pid

    def exited(self, terminal, status):
        self.pid = None
        self.live = False
        terminal.set_input_enabled(False)
        if not self.closed:
            self.tab.set_text(self.tab.get_text().removesuffix(" · завершён") + " · завершён")

    @staticmethod
    def terminate(pid):
        try:
            os.kill(pid, signal.SIGHUP)
        except ProcessLookupError:
            pass

    def stop(self, *_):
        self.closed = True
        if self.pid is not None:
            self.terminate(self.pid)


class Window(Gtk.ApplicationWindow):
    def __init__(self, application, config):
        super().__init__(application=application, title="SSH Manager " + VERSION)
        self.config = config
        self.vault = Vault()
        self.files = {}
        self.shells = []
        self.document = None
        try:
            self.settings = config.settings()
        except (ValueError, OSError):
            self.settings = parse_settings(b"")
        self.active_group = self.settings["default"]
        self.protocol = "ssh"
        self.set_default_size(940, 595)
        self.set_size_request(660, 400)
        self.connect("delete-event", self.can_close)
        self.connect("key-press-event", self.navigate_sessions)

        header = Gtk.HeaderBar(show_close_button=True)
        header.set_title("SSH Manager")
        self.set_titlebar(header)
        self.stack = Gtk.Stack(transition_type=Gtk.StackTransitionType.CROSSFADE, transition_duration=130)
        switch = Gtk.StackSwitcher(stack=self.stack)
        header.set_custom_title(switch)
        version = Gtk.Label(label="SSH Manager " + VERSION)
        version.get_style_context().add_class("section-title")
        header.pack_start(version)
        self.theme = button("", self.switch_theme)
        header.pack_end(self.theme)
        refresh = button("Обновить", self.reload)
        refresh.set_tooltip_text("Перечитать список серверов")
        header.pack_end(refresh)
        self.updates = UpdateController(self)
        header.pack_end(self.updates.control)

        panel = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=11, margin=17)
        heading = Gtk.Box(spacing=8)
        self.title = Gtk.Label(label="SSH-подключения", xalign=0)
        self.title.get_style_context().add_class("page-title")
        heading.pack_start(self.title, True, True, 0)
        heading.pack_end(button("Добавить сервер", self.add_profile, suggested=True), False, False, 0)
        heading.pack_end(button("Резервные копии", self.backup_menu), False, False, 0)
        panel.pack_start(heading, False, False, 0)
        navigation = Gtk.Box(spacing=8)
        protocols = Gtk.Box()
        protocols.get_style_context().add_class("linked")
        ssh = Gtk.RadioButton.new_with_label_from_widget(None, "SSH")
        rdp = Gtk.RadioButton.new_with_label_from_widget(ssh, "RDP")
        for tab, kind in ((ssh, "ssh"), (rdp, "rdp")):
            tab.set_mode(False)
            tab.connect("toggled", lambda widget, value=kind:
                        self.select_protocol(value) if widget.get_active() else None)
            protocols.pack_start(tab, False, False, 0)
        navigation.pack_start(protocols, False, False, 0)
        self.import_desktop_button = button("Импорт .rdp…", lambda: self.transfer_desktop(None))
        self.import_desktop_button.set_no_show_all(True)
        navigation.pack_end(self.import_desktop_button, False, False, 0)
        panel.pack_start(navigation, False, False, 0)
        tabs = Gtk.Box(spacing=6)
        self.group_strip = Gtk.Box(spacing=5)
        self.group_strip.get_style_context().add_class("group-strip")
        group_scroll = Gtk.ScrolledWindow(hscrollbar_policy=Gtk.PolicyType.AUTOMATIC,
                                          vscrollbar_policy=Gtk.PolicyType.NEVER)
        group_scroll.add(self.group_strip)
        tabs.pack_start(group_scroll, True, True, 0)
        tabs.pack_end(button("Новая вкладка", self.new_group), False, False, 0)
        panel.pack_start(tabs, False, False, 0)
        self.search = Gtk.SearchEntry(placeholder_text="Найти сервер")
        self.search.connect("search-changed", lambda *_: self.render_profiles())
        filters = Gtk.Box(spacing=8)
        filters.pack_start(self.search, True, True, 0)
        self.sort = Gtk.ComboBoxText()
        self.sort.set_tooltip_text("Сортировка в текущей вкладке")
        for order, caption in ORDERS:
            self.sort.append(order, caption)
        self.sort_signal = self.sort.connect("changed", self.change_sort)
        filters.pack_end(self.sort, False, False, 0)
        panel.pack_start(filters, False, False, 0)
        self.list = Gtk.ListBox(selection_mode=Gtk.SelectionMode.NONE, activate_on_single_click=False)
        self.list.connect("row-activated", lambda _, row: self.edit_catalog_entry(row.profile))
        placeholder = Gtk.Label(label="Подключений нет. Добавьте сервер или измените поиск.", margin=22)
        placeholder.show()
        self.list.set_placeholder(placeholder)
        scroll = Gtk.ScrolledWindow(shadow_type=Gtk.ShadowType.IN)
        scroll.add(self.list)
        panel.pack_start(scroll, True, True, 0)
        self.count = Gtk.Label(xalign=1)
        self.count.get_style_context().add_class("muted")
        panel.pack_start(self.count, False, False, 0)
        self.stack.add_titled(panel, "catalog", "Серверы")
        terminal_page = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=9, margin=17)
        terminal_head = Gtk.Box(spacing=8)
        label = Gtk.Label(label="Сеансы", xalign=0)
        label.get_style_context().add_class("page-title")
        terminal_head.pack_start(label, True, True, 0)
        terminal_head.pack_end(button("Новый сеанс", self.pick_server, suggested=True), False, False, 0)
        terminal_page.pack_start(terminal_head, False, False, 0)
        self.sessions = Gtk.Notebook(scrollable=True)
        terminal_page.pack_start(self.sessions, True, True, 0)
        self.stack.add_titled(terminal_page, "sessions", "Сеансы")
        self.add(self.stack)
        self.apply_theme()
        self.reload(show_error=False)
        self.updates.check()

    def navigate_sessions(self, window, event):
        modifiers = event.state & Gtk.accelerator_get_default_mod_mask()
        if (self.stack.get_visible_child_name() == "sessions" and event.keyval == Gdk.KEY_Escape
                and modifiers == Gdk.ModifierType.SHIFT_MASK):
            self.stack.set_visible_child_name("catalog")
            self.search.grab_focus()
            return True
        return False

    def apply_theme(self):
        Gtk.Settings.get_default().set_property("gtk-application-prefer-dark-theme", self.settings["dark"])
        self.theme.set_label("Светлая тема" if self.settings["dark"] else "Тёмная тема")

    def switch_theme(self):
        self.settings["dark"] = not self.settings["dark"]
        self.apply_theme()
        self.save_settings()

    def can_close(self, *_):
        if self.updates.installing:
            message(self, "Дождитесь завершения установки обновления.")
            return True
        return (any(shell.live for shell in self.shells) or bool(self.files)) and not confirm(self, "Закрыть активные сеансы и окна SFTP?")

    def reload(self, show_error=True):
        try:
            self.document = self.config.load()
        except (OSError, ValueError) as error:
            self.document = None
            if show_error:
                message(self, error, Gtk.MessageType.ERROR)
        self.render_groups()
        self.render_profiles()

    def render_profiles(self):
        self.sort.handler_block(self.sort_signal)
        try:
            order = self.settings["sort"].get(self.sort_key, "original")
            self.sort.set_active_id(order)
        finally:
            self.sort.handler_unblock(self.sort_signal)
        for row in self.list.get_children():
            self.list.remove(row)
        if self.protocol == "ssh" and self.document is None:
            self.count.set_text("Не удалось прочитать SSH config.")
            return
        query = self.search.get_text().strip().casefold()
        if self.protocol == "rdp":
            candidates = [profile for profile in self.desktop_profiles() if not self.active_group or profile.group == self.active_group]
        else:
            candidates = [profile for profile in self.document.profiles if not self.active_group or
                          self.settings["members"].get(profile.alias.lower()) == self.active_group]
        matches = [profile for profile in candidates if query in (profile.alias + " " + profile.address).casefold()]
        for profile in ordered(matches, order):
            self.list.add(CatalogRow(self, profile))
        self.count.set_text(f"Подключений: {len(matches)}")
        self.list.show_all()

    @property
    def sort_key(self):
        return self.protocol + ":" + self.active_group

    def change_sort(self, combo):
        choice = combo.get_active_id()
        if choice:
            changes = dict(self.settings["sort"])
            changes[self.sort_key] = choice
            try:
                self.update_catalog_settings(sort=changes)
            except OSError as error:
                message(self, error, Gtk.MessageType.ERROR)
            self.render_profiles()

    def select_protocol(self, protocol):
        if self.protocol == protocol:
            return
        self.protocol = protocol
        self.title.set_text("SSH-подключения" if protocol == "ssh" else "RDP-подключения")
        self.import_desktop_button.set_visible(protocol == "rdp")
        self.search.set_text("")
        self.render_profiles()

    def desktop_profiles(self):
        return [DesktopProfile(**profile) for profile in self.settings["desktops"]]

    def update_catalog_settings(self, **changes):
        settings = {**self.settings, **changes}
        self.config.save_settings(settings)
        self.settings = settings

    def render_groups(self):
        for widget in self.group_strip.get_children():
            self.group_strip.remove(widget)
        for group in [{"id": "", "name": "Все серверы", "color": ""}, *self.settings["groups"]]:
            toggle = Gtk.ToggleButton()
            item = Gtk.Label()
            dot = f'<span foreground="{group["color"]}">●</span> ' if group["color"] else ""
            item.set_markup(dot + GLib.markup_escape_text(group["name"]))
            toggle.add(item)
            toggle.set_active(group["id"] == self.active_group)
            toggle.connect("clicked", lambda _, g=group: self.select_group(g["id"]))
            toggle.connect("button-press-event", lambda widget, event, g=group:
                           self.group_menu(widget, event, g) if event.button == 3 else False)
            if group["id"]:
                self.enable_group_drag(toggle, group)
            self.group_strip.pack_start(toggle, False, False, 0)
        self.group_strip.show_all()

    def enable_group_drag(self, widget, group):
        types = [Gtk.TargetEntry.new("application/x-ssh-manager-tab", Gtk.TargetFlags.SAME_APP, 0)]
        widget.drag_source_set(Gdk.ModifierType.BUTTON1_MASK, types, Gdk.DragAction.MOVE)
        widget.drag_dest_set(Gtk.DestDefaults.ALL, types, Gdk.DragAction.MOVE)
        widget.connect("drag-data-get", lambda _, context, data, code, time:
                       data.set(data.get_target(), 8, group["id"].encode()))

        def drop(target, context, x, y, data, code, time):
            groups = self.settings["groups"]
            moved = next((item for item in groups if item["id"].encode() == data.get_data()), None)
            if not moved or moved is group:
                Gtk.drag_finish(context, False, False, time)
                return
            groups.remove(moved)
            groups.insert(groups.index(group) + (x >= target.get_allocated_width() / 2), moved)
            Gtk.drag_finish(context, True, False, time)
            self.save_settings()
            self.render_groups()

        widget.connect("drag-data-received", drop)

    def select_group(self, selected):
        self.active_group = selected
        self.render_groups()
        self.render_profiles()

    def save_settings(self):
        try:
            self.config.save_settings(self.settings)
        except OSError as error:
            message(self, error, Gtk.MessageType.ERROR)

    def new_group(self):
        name = prompt(self, "Название вкладки")
        if name and 0 < len(name.strip()) <= 120:
            group = {"id": uuid.uuid4().hex, "name": name.strip(), "color": ""}
            self.settings["groups"].append(group)
            self.save_settings()
            self.select_group(group["id"])

    def group_menu(self, widget, event, group):
        actions = [("Открывать при запуске", lambda: self.default_group(group["id"]))]
        if group["id"]:
            actions.extend([("Переименовать", lambda: self.rename_group(group)),
                            ("Цветовая метка", lambda: self.mark_group(group)),
                            ("Переместить влево", lambda: self.shift_group(group, -1)),
                            ("Переместить вправо", lambda: self.shift_group(group, 1)),
                            ("Удалить вкладку", lambda: self.delete_group(group))])
        return menu_at(widget, event, actions)

    def default_group(self, identifier):
        self.settings["default"] = identifier
        self.save_settings()

    def rename_group(self, group):
        name = prompt(self, "Переименовать вкладку", group["name"])
        if name and 0 < len(name.strip()) <= 120:
            group["name"] = name.strip()
            self.save_settings()
            self.render_groups()

    def mark_group(self, group):
        dialog = Gtk.ColorChooserDialog(title="Цвет вкладки", transient_for=self)
        dialog.set_use_alpha(False)
        dialog.add_button("Убрать метку", 1)
        response = dialog.run()
        if response == Gtk.ResponseType.OK:
            color = dialog.get_rgba()
            group["color"] = "#{:02x}{:02x}{:02x}".format(*(round(channel * 255) for channel in (color.red, color.green, color.blue)))
        elif response == 1:
            group["color"] = ""
        dialog.destroy()
        self.save_settings()
        self.render_groups()

    def shift_group(self, group, offset):
        groups = self.settings["groups"]
        index = groups.index(group)
        target = index + offset
        if 0 <= target < len(groups):
            groups[index], groups[target] = groups[target], groups[index]
            self.save_settings()
            self.render_groups()

    def delete_group(self, group):
        if confirm(self, "Удалить вкладку «" + group["name"] + "»? Серверы останутся в общем списке."):
            self.settings["groups"].remove(group)
            self.settings["members"] = {key: value for key, value in self.settings["members"].items() if value != group["id"]}
            self.settings["desktops"] = [dict(item, group="") if item["group"] == group["id"] else item
                                         for item in self.settings["desktops"]]
            self.settings["sort"] = {key: value for key, value in self.settings["sort"].items()
                                     if not key.endswith(":" + group["id"])}
            if self.settings["default"] == group["id"]:
                self.settings["default"] = ""
            self.save_settings()
            self.select_group("")

    def profile_menu(self, row, event):
        profile = row.profile
        if isinstance(profile, DesktopProfile):
            return menu_at(row, event, [
                ("Подключиться", lambda: self.connect_profile(profile)),
                ("Новый сеанс", lambda: self.connect_profile(profile, True)),
                ("Изменить", lambda: self.edit_desktop(profile)),
                ("Сменить вкладку", lambda: self.move_profile(profile)),
                ("Экспорт .rdp…", lambda: self.transfer_desktop(profile)),
                ("Удалить подключение", lambda: self.delete_desktop(profile)),
            ])
        actions = [("Подключиться", lambda: self.open_session(profile)),
                   ("Новый сеанс", lambda: self.open_session(profile, True)),
                   ("Файлы (SFTP)", lambda: self.open_files(profile)),
                   ("Изменить", lambda: self.edit_profile(profile)),
                   ("Сменить вкладку", lambda: self.move_profile(profile)),
                   ("Удалить сервер", lambda: self.delete_profile(profile))]
        return menu_at(row, event, actions)

    def move_profile(self, profile):
        dialog = Gtk.Dialog(title="Переместить сервер", transient_for=self, modal=True)
        dialog.add_button("Отмена", Gtk.ResponseType.CANCEL)
        dialog.add_button("Переместить", Gtk.ResponseType.OK)
        groups = self.settings["groups"]
        combo = Gtk.ComboBoxText(margin=14)
        combo.append("", "Без вкладки")
        for group in groups:
            combo.append(group["id"], group["name"])
        desktop = isinstance(profile, DesktopProfile)
        combo.set_active_id(profile.group if desktop else self.settings["members"].get(profile.alias.lower(), ""))
        dialog.get_content_area().add(combo)
        dialog.show_all()
        if dialog.run() == Gtk.ResponseType.OK:
            destination = combo.get_active_id() or ""
            try:
                if desktop:
                    self.save_desktop(replace(profile, group=destination), editing=True)
                else:
                    self.update_catalog_settings(members={**self.settings["members"], profile.alias.lower(): destination})
            except (OSError, ValueError) as error:
                message(self, error, Gtk.MessageType.ERROR)
            else:
                self.render_profiles()
        dialog.destroy()

    def add_profile(self):
        if self.protocol == "rdp":
            self.edit_desktop(None)
        else:
            self.edit_profile(None)

    def edit_catalog_entry(self, profile):
        if isinstance(profile, DesktopProfile):
            self.edit_desktop(profile)
        else:
            self.edit_profile(profile)

    def edit_desktop(self, before):
        dialog = DesktopDialog(self, before)
        while dialog.run() == Gtk.ResponseType.OK:
            try:
                self.save_desktop(dialog.values(), editing=before is not None)
            except (OSError, ValueError) as error:
                dialog.error.set_text(str(error))
            else:
                self.search.set_text("")
                self.render_profiles()
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
            self.render_profiles()

    def transfer_desktop(self, profile):
        if profile is None:
            path = chooser(self, "Импорт профиля RDP", Gtk.FileChooserAction.OPEN)
        else:
            path = chooser(self, "Экспорт профиля RDP", Gtk.FileChooserAction.SAVE, proposed="connection.rdp")
        if path:
            try:
                if profile is None:
                    self.save_desktop(DesktopProfile.import_file(path, self.active_group))
                    self.search.set_text("")
                    self.render_profiles()
                else:
                    profile.export_file(path)
            except (OSError, ValueError) as error:
                message(self, error, Gtk.MessageType.ERROR)

    def edit_profile(self, before):
        if self.document is None:
            return
        dialog = ProfileDialog(self, before, self.config.keys())
        while dialog.run() == Gtk.ResponseType.OK:
            after, password, forget = dialog.values()
            try:
                updated = self.document.update(before, after)
                previous, account = account_for(before), account_for(after)
                if password and not forget and not account:
                    raise ValueError("Для сохранения пароля явно укажите адрес, пользователя и, при необходимости, порт.")
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
                elif not before and self.active_group:
                    self.settings["members"][after.alias.lower()] = self.active_group
                self.save_settings()
                self.reload()
                break
            except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
                dialog.hint.set_text(str(error))
        dialog.destroy()

    def delete_profile(self, profile):
        if not confirm(self, "Удалить подключение «" + profile.alias + "»?"):
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
        key = (profile.alias, profile.address)
        if key not in self.files:
            window = FilesWindow(self, profile, self.config, self.vault)
            self.files[key] = window
            window.connect("destroy", lambda unused: self.files.pop(key, None))
        self.files[key].present()

    def connect_profile(self, profile, new=False):
        if not isinstance(profile, DesktopProfile):
            self.open_session(profile, new=new)
            return
        related = [session for session in self.shells if isinstance(session, DesktopSession) and session.profile.id == profile.id]
        existing = next((session for session in reversed(related) if session.live), None)
        if existing is not None and not new:
            self.show_session(existing)
            return
        try:
            command = profile.command()
        except ValueError as error:
            message(self, error, Gtk.MessageType.ERROR)
            return
        label = "RDP · " + profile.name
        used = {session.tab.get_text().removesuffix(" · завершён") for session in related}
        numbered = label
        number = 1
        while numbered in used:
            number += 1
            numbered = f"{label} · {number}"
        session = DesktopSession(self, profile, command, numbered)
        self.add_session(session)
        self.show_session(session)
        session.start()

    def open_session(self, profile, new=False, forwarding=True):
        existing = next((shell for shell in reversed(self.shells) if isinstance(shell, Shell) and shell.live and
                         (shell.profile.alias, shell.profile.address) == (profile.alias, profile.address)), None)
        if existing and not new:
            shell = existing
        else:
            shell = Shell(self, profile, forwarding)
            count = 1 + sum(isinstance(other, Shell) and other.profile.alias == profile.alias for other in self.shells)
            shell.tab = Gtk.Label(label=profile.alias + (f" · {count}" if count > 1 else ""))
            shell.tab.set_tooltip_text(profile.address)
            self.add_session(shell)
        self.show_session(shell)

    def add_session(self, session):
        self.sessions.append_page(session, session.tab)
        self.sessions.set_tab_reorderable(session, True)
        self.shells.append(session)

    def show_session(self, shell):
        self.stack.set_visible_child_name("sessions")
        self.sessions.set_current_page(self.sessions.page_num(shell))
        shell.terminal.grab_focus()

    def close_session(self, shell):
        if shell not in self.shells or not confirm(self, "Вы уверены?", f"Закрыть сеанс «{shell.tab.get_text()}»?"):
            return
        self.shells.remove(shell)
        shell.destroy()
        if not self.shells:
            self.stack.set_visible_child_name("catalog")

    def pick_server(self):
        dialog = Gtk.Dialog(title="Новый сеанс", transient_for=self, modal=True)
        dialog.set_default_size(470, 380)
        dialog.add_button("Отмена", Gtk.ResponseType.CANCEL)
        dialog.add_button("Новый сеанс", Gtk.ResponseType.OK)
        dialog.set_default_response(Gtk.ResponseType.OK)
        area = dialog.get_content_area()
        area.set_border_width(13)
        area.set_spacing(8)
        protocols = Gtk.ComboBoxText()
        protocols.append("ssh", "SSH")
        protocols.append("rdp", "RDP")
        protocols.set_active_id(self.protocol)
        area.pack_start(protocols, False, False, 0)
        search = Gtk.SearchEntry(placeholder_text="Найти сервер", activates_default=True)
        area.pack_start(search, False, False, 0)
        listing = Gtk.ListBox(activate_on_single_click=False)
        scroll = Gtk.ScrolledWindow(shadow_type=Gtk.ShadowType.IN)
        scroll.add(listing)
        area.pack_start(scroll, True, True, 0)

        def filter_profiles(*_):
            listing.foreach(listing.remove)
            query = search.get_text().strip().casefold()
            profiles = self.desktop_profiles() if protocols.get_active_id() == "rdp" else (
                self.document.profiles if self.document else [])
            for profile in ordered(profiles, "name"):
                if query in (profile.alias + " " + profile.address).casefold():
                    row = Gtk.ListBoxRow()
                    row.profile = profile
                    row.add(Gtk.Label(label=f"{profile.alias}   ·   {profile.address}", xalign=0, margin=8))
                    listing.add(row)
            listing.show_all()
            listing.select_row(listing.get_row_at_index(0))

        search.connect("search-changed", filter_profiles)
        protocols.connect("changed", filter_profiles)
        listing.connect("row-activated", lambda *_: dialog.response(Gtk.ResponseType.OK))
        filter_profiles()
        dialog.show_all()
        profile = listing.get_selected_row().profile if dialog.run() == Gtk.ResponseType.OK and listing.get_selected_row() else None
        dialog.destroy()
        if profile is not None:
            self.connect_profile(profile, True)

    def backup_menu(self):
        from .backup import backup_menu

        backup_menu(self)


class Application(Gtk.Application):
    def __init__(self, config, isolated=False):
        super().__init__(application_id="io.github.clarkufa.SSHManager",
                         flags=Gio.ApplicationFlags.NON_UNIQUE if isolated else Gio.ApplicationFlags.FLAGS_NONE)
        self.config = config

    def do_activate(self):
        windows = self.get_windows()
        window = next((window for window in windows if isinstance(window, Window)), None)
        if not window:
            style = Gtk.CssProvider()
            style.load_from_data(STYLE)
            Gtk.StyleContext.add_provider_for_screen(Gdk.Screen.get_default(), style, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
            icon = Path(sys.argv[0]).resolve().parent / "icon.png"
            if icon.exists():
                Gtk.Window.set_default_icon_from_file(str(icon))
            window = Window(self, self.config)
            window.show_all()
        window.present()


def main():
    parser = argparse.ArgumentParser(prog="ssh-manager")
    parser.add_argument("--version", action="version", version=VERSION)
    parser.add_argument("--config", type=Path, help="OpenSSH config")
    parser.add_argument("--settings-dir", type=Path, help="Application settings directory")
    options = parser.parse_args()
    state = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "ssh-manager"
    config = Config(options.config or Path.home() / ".ssh" / "config", options.settings_dir or state)
    return Application(config, bool(options.config or options.settings_dir)).run([sys.argv[0]])
