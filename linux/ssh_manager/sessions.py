"""Sessions page: sidebar list, info bar, SSH terminals, RDP and VNC desktops (docs/ui-spec.md §3, §8)."""

import os
import re
import signal
import subprocess
import threading
from pathlib import Path

import gi
from gi.repository import Gdk, Gio, GLib, GObject, Gtk, Pango, Vte

from .auth import AskPass, account_for, ssh_args
from .dialogs import LogonDialog, VncLogonDialog
from .theme import TERMINAL, mark, rgba
from .widgets import Item, action_button, confirm, menu_at, message
from .vnc import MISSING_CLIENT, SshTunnel, TunnelError
from .x11 import Display

FORWARDING_ERROR = re.compile(r"cannot\s*listen\s*to\s*port\s*:|could\s*not\s*request\s*local\s*forwarding", re.IGNORECASE)
CONSOLE_SEQUENCE = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
# FreeRDP exit codes for an ordinary end of the session: success, disconnect, logoff, idle, user request.
NORMAL_RDP_EXIT = {0, 1, 2, 3, 12}


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


def exit_code(status):
    try:
        return os.waitstatus_to_exitcode(status)
    except (ValueError, AttributeError):
        return status


def terminal_text(terminal):
    """Visible text of a VTE terminal across VTE versions."""
    try:
        text = terminal.get_text_format(Vte.Format.TEXT)
    except (AttributeError, TypeError):
        try:
            text = terminal.get_text(None, None)
        except TypeError:
            text = terminal.get_text()
    if isinstance(text, tuple):
        text = text[0]
    return text or ""


def forwarding_failed(text):
    return bool(FORWARDING_ERROR.search(CONSOLE_SEQUENCE.sub("", text)))


def style_terminal(terminal, preferences=None):
    preferences = preferences or {}
    terminal.set_scrollback_lines(preferences.get("scrollback", 3000))
    terminal.set_mouse_autohide(True)
    font = terminal_font()
    if preferences.get("terminal_font"):
        font = Pango.FontDescription.from_string(f"{preferences['terminal_font']} {preferences.get('terminal_size', 11)}")
    terminal.set_font(font)
    terminal.set_colors(rgba(TERMINAL["foreground"]), rgba(TERMINAL["background"]), [])
    terminal.set_color_cursor(rgba(TERMINAL["cursor"]))
    terminal.set_color_highlight(rgba(TERMINAL["selection"]))
    terminal.set_color_highlight_foreground(rgba(TERMINAL["foreground"]))
    try:
        terminal.set_cursor_shape(Vte.CursorShape.UNDERLINE)
    except AttributeError:
        pass


def kill(pid, sig=signal.SIGHUP):
    try:
        os.kill(pid, sig)
    except (ProcessLookupError, PermissionError):
        pass


class Session(Gtk.Box):
    protocol = "SSH"

    def __init__(self, owner, profile, number):
        super().__init__(orientation=Gtk.Orientation.VERTICAL)
        self.owner, self.profile, self.number = owner, profile, number
        self.name = ""
        self.live = True
        self.closed = False
        self.status = ""
        self.last_used = 0
        self.forwarding_failed = False
        self.without_forwarding = False
        self.pid = None
        mark(self, "sshm-terminal-area")

    @property
    def alias(self):
        return self.profile.alias

    @property
    def title(self):
        if self.name:
            return f"{self.alias} · {self.name}"
        return self.alias if self.number <= 1 else f"{self.alias} · {self.number}"

    @property
    def address(self):
        return self.profile.address

    def changed(self):
        if not self.closed:
            self.owner.session_changed(self)

    def focus(self):
        pass

    def apply_preferences(self, preferences):
        pass

    def stop(self, *_):
        self.closed = True
        if self.pid:
            kill(self.pid)


class SshSession(Session):
    def __init__(self, owner, profile, number, forwarding=True, command=None):
        super().__init__(owner, profile, number)
        self.forwarding = forwarding
        self.without_forwarding = not forwarding
        self.command = command  # a remote command, e.g. copying a public key
        self.broker = None
        self.inspected = 0
        self.attempts = 0
        self.shift_paste_held = False
        self.terminal = Vte.Terminal()
        style_terminal(self.terminal, owner.settings.get("preferences"))
        # Shown when SSH ends: the terminal output stays visible above it (docs/ui-spec.md §7).
        self.ended_bar = Gtk.Box(spacing=8, margin=6)
        self.ended_text = Gtk.Label(xalign=0, hexpand=True, ellipsize=Pango.EllipsizeMode.END)
        mark(self.ended_text, "sshm-terminal-text")
        self.ended_bar.pack_start(self.ended_text, True, True, 0)
        self.ended_bar.pack_start(action_button("Переподключить", self.reconnect, "primary", icon="refresh"), False, False, 0)
        self.ended_bar.pack_start(action_button("Закрыть сеанс", lambda: owner.close_session(self, confirm_close=False)),
                                  False, False, 0)
        self.pack_start(self.ended_bar, False, False, 0)
        self.terminal.connect("child-exited", self.exited)
        self.terminal.connect("key-press-event", self.shortcut)
        self.terminal.connect("paste-clipboard", self.on_paste_clipboard)
        self.terminal.connect("button-press-event", self.force_right_click)
        self.terminal.connect("button-release-event", self.force_right_click)
        # VTE handles mouse reporting first. Paste only if it leaves the click unhandled.
        self.terminal.connect_after("button-press-event", self.right_click)
        self.terminal.connect("contents-changed", self.inspect_output)
        scroll = Gtk.ScrolledWindow()
        scroll.add(self.terminal)
        self.pack_start(scroll, True, True, 0)
        self.connect("destroy", self.stop)
        self.show_all()
        self.ended_bar.hide()
        threading.Thread(target=self.prepare, daemon=True).start()

    def apply_preferences(self, preferences):
        style_terminal(self.terminal, preferences)

    def reconnect(self, automatic=False):
        if self.live or self.closed:
            return False
        if not automatic:
            self.attempts = 0
        self.ended_bar.hide()
        self.live = True
        self.status = ""
        self.terminal.set_input_enabled(True)
        self.terminal.feed(b"\r\n")
        self.changed()
        threading.Thread(target=self.prepare, daemon=True).start()
        return False

    def send(self, text):
        if self.live and not self.closed:
            self.terminal.feed_child((text.replace("\r\n", "\r").replace("\n", "\r") + "\r").encode("utf-8"))

    @property
    def address(self):
        host = self.profile.host or self.profile.alias
        if self.profile.port and ":" in host:
            host = f"[{host}]"
        return (self.profile.user + "@" if self.profile.user else "") + host + (":" + self.profile.port if self.profile.port else "")

    def focus(self):
        self.terminal.grab_focus()

    # --- clipboard -----------------------------------------------------------------------------
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
        if "\r" in text and not confirm(self.owner, "Текст содержит переносы строк. Вставка может сразу выполнить команды. Продолжить?",
                                        title="Вставка в терминал"):
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

    # --- process -----------------------------------------------------------------------------
    def inspect_output(self, terminal):
        if self.forwarding_failed or not self.forwarding or self.inspected > 400:
            return
        self.inspected += 1
        if forwarding_failed(terminal_text(terminal)):
            self.forwarding_failed = True
            self.changed()

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
            arguments = self.owner.ssh_arguments(self.profile, account)
            if not self.forwarding:
                arguments[1:1] = ["-o", "ClearAllForwardings=yes"]
            if self.command:
                arguments += [self.command]
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
            kill(pid)
            return
        self.pid = pid
        if self.broker:
            self.broker.attach(pid)

    def exited(self, terminal, status):
        self.pid = None
        self.live = False
        self.status = "завершён"
        if self.broker:
            self.broker.close()
            self.broker = None
        code = exit_code(status)
        if not self.closed:
            terminal.feed(f"\r\n\x1b[0mSSH завершился (код {code}).\r\n".encode())
        terminal.set_input_enabled(False)
        self.ended_text.set_text(f"Сеанс SSH завершён · {self.address} · код {code}")
        self.ended_bar.show()
        preferences = self.owner.settings.get("preferences", {})
        if (not self.closed and code not in (0, 130) and not self.command and preferences.get("auto_reconnect")
                and self.attempts < 3):
            self.attempts += 1
            self.ended_text.set_text(f"Соединение прервано · переподключение через 5 с (попытка {self.attempts} из 3)")
            GLib.timeout_add_seconds(5, self.reconnect, True)
        self.changed()

    def stop(self, *_):
        super().stop()
        if self.broker:
            self.broker.close()
            self.broker = None


class RdpSession(Session):
    """FreeRDP inside the window on X11 (`/parent-window`), or in its own window elsewhere."""
    protocol = "RDP"

    def __init__(self, owner, profile, number, credential, embedded):
        super().__init__(owner, profile, number)
        self.credential = credential  # (user, domain, password)
        self.embedded = embedded
        self.display = None
        self.xid = None
        self.fit_timer = 0
        self.poll_timer = 0
        self.started_at = 0
        self.full_screen = False
        self.pages = Gtk.Stack()
        self.pack_start(self.pages, True, True, 0)

        desktop = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        self.bar = Gtk.Box(spacing=8, margin=6)
        bar_title = Gtk.Label(label=profile.name + "  ·  " + profile.address, xalign=0)
        mark(bar_title, "sshm-terminal-text")
        self.bar.pack_start(bar_title, True, True, 0)
        self.bar.pack_end(action_button("Выйти из полноэкранного режима", lambda: owner.leave_full_screen(), "outline",
                                        icon="move_left"), False, False, 0)
        desktop.pack_start(self.bar, False, False, 0)
        self.area = Gtk.DrawingArea(hexpand=True, vexpand=True, can_focus=True)
        self.area.add_events(Gdk.EventMask.BUTTON_PRESS_MASK)
        self.area.connect("size-allocate", lambda *_: self.schedule_fit())
        self.area.connect("button-press-event", lambda *_: self.focus())
        desktop.pack_start(self.area, True, True, 0)
        self.pages.add_named(desktop, "desktop")

        external = Gtk.Label(label="Рабочий стол открыт в отдельном окне FreeRDP.", wrap=True,
                             justify=Gtk.Justification.CENTER)
        mark(external, "sshm-terminal-text")
        self.pages.add_named(external, "external")

        ended = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6, valign=Gtk.Align.CENTER, halign=Gtk.Align.CENTER)
        self.ended_title = Gtk.Label()
        mark(self.ended_title, "sshm-overlay-title")
        self.ended_detail = Gtk.Label(wrap=True, justify=Gtk.Justification.CENTER, max_width_chars=70, selectable=True)
        mark(self.ended_detail, "sshm-overlay-detail")
        actions = Gtk.Box(spacing=8, halign=Gtk.Align.CENTER, margin_top=12)
        actions.pack_start(action_button("Переподключить", self.restart, "primary", icon="refresh", width=160), False, False, 0)
        actions.pack_start(action_button("Закрыть сеанс", lambda: owner.close_session(self, confirm_close=False), "outline",
                                         width=140), False, False, 0)
        for widget in (self.ended_title, self.ended_detail, actions):
            ended.pack_start(widget, False, False, 0)
        self.pages.add_named(ended, "ended")

        # FreeRDP runs in a hidden terminal: its output explains a failed connection.
        self.log = Vte.Terminal()
        self.log.set_size(160, 40)
        self.log.connect("child-exited", self.exited)
        self.pages.add_named(self.log, "log")
        self.connect("destroy", self.stop)
        self.show_all()
        self.bar.hide()
        self.pages.set_visible_child_name("desktop" if embedded else "external")

    def focus(self):
        if self.display and self.xid:
            self.display.focus_child(self.xid)
        return False

    # --- lifecycle ---------------------------------------------------------------------------
    def start(self):
        self.live = True
        self.status = "подключение"
        self.changed()
        if self.embedded:
            self.pages.set_visible_child_name("desktop")
            if not self.area.get_realized() or self.area.get_allocated_width() < 50:
                GLib.timeout_add(100, self._start_when_ready)
                return
        else:
            self.pages.set_visible_child_name("external")
        self._spawn()

    def _start_when_ready(self):
        if self.closed:
            return False
        if self.area.get_realized() and self.area.get_allocated_width() >= 50:
            self._spawn()
            return False
        return True

    def _spawn(self):
        parent, size = None, None
        if self.embedded:
            from gi.repository import GdkX11
            window = self.area.get_window()
            window.ensure_native()
            self.xid = GdkX11.X11Window.get_xid(window)
            size = (self.area.get_allocated_width(), self.area.get_allocated_height())
            try:
                self.display = self.display or Display()
            except OSError:
                self.display = None
            parent = self.xid
        user, domain, password = self.credential
        try:
            command, extra = self.profile.command(password, parent=parent, size=size, user=user, domain=domain)
        except ValueError as error:
            self.ended(str(error), failed=True)
            return
        environment = [key + "=" + value for key, value in dict(os.environ, **extra).items()]
        self.log.reset(True, True)
        self.log.set_input_enabled(True)
        try:
            self.log.spawn_async(Vte.PtyFlags.DEFAULT, str(Path.home()), command, environment,
                                 GLib.SpawnFlags.DEFAULT, None, None, -1, None, self.spawned, None)
        except (OSError, RuntimeError, TypeError, GLib.Error) as error:
            self.ended(str(error), failed=True)

    def spawned(self, terminal, pid, error, *_):
        if error:
            self.ended(str(error), failed=True)
            return
        if self.closed:
            kill(pid, signal.SIGTERM)
            return
        self.pid = pid
        self.started_at = GLib.get_monotonic_time()
        self.poll_timer = GLib.timeout_add(500, self.poll)

    def poll(self):
        if self.closed or not self.live or self.pid is None:
            self.poll_timer = 0
            return False
        connected = False
        if self.embedded and self.display and self.xid:
            connected = self.fit() > 0
        elif GLib.get_monotonic_time() - self.started_at > 2_500_000:
            connected = True
        if connected and self.status != "подключён":
            self.status = "подключён"
            self.changed()
            if self.profile.fullscreen and self.embedded and not self.full_screen:
                self.owner.enter_full_screen(self)
        return True

    def schedule_fit(self):
        if self.fit_timer:
            GLib.source_remove(self.fit_timer)
        self.fit_timer = GLib.timeout_add(250, self._fit_later)

    def _fit_later(self):
        self.fit_timer = 0
        self.fit()
        return False

    def fit(self):
        if not (self.display and self.xid and self.live):
            return 0
        return self.display.fit_children(self.xid, self.area.get_allocated_width(), self.area.get_allocated_height())

    def exited(self, terminal, status):
        code = exit_code(status)
        self.pid = None
        if self.poll_timer:
            GLib.source_remove(self.poll_timer)
            self.poll_timer = 0
        if self.closed:
            return
        normal = code in NORMAL_RDP_EXIT
        lines = [line.strip() for line in CONSOLE_SEQUENCE.sub("", terminal_text(self.log)).splitlines() if line.strip()]
        detail = "\n".join(lines[-4:]) if not normal else "Удалённый сеанс закрыт."
        self.ended(detail + ("" if normal else f"\nКод: {code}"), failed=not normal, code=code)

    def ended(self, detail, failed, code=None):
        self.live = False
        if self.full_screen:
            self.owner.leave_full_screen()
        self.status = ("отключён" if not failed else f"отключён (код {code})" if code is not None else "ошибка подключения")
        self.ended_title.set_text("Подключение прервано" if failed and code is not None else
                                  "Не удалось подключиться" if failed else "Сеанс завершён")
        self.ended_detail.set_text(f"{self.profile.name} · {self.profile.address}\n{detail}".strip())
        self.pages.set_visible_child_name("ended")
        self.changed()

    def restart(self):
        if self.pid:
            pid, self.pid = self.pid, None
            self.closed = True
            kill(pid, signal.SIGTERM)
            self.closed = False
        self.start()

    def set_full_screen(self, value):
        self.full_screen = value
        self.bar.set_visible(value)
        self.schedule_fit()

    def stop(self, *_):
        super().stop()
        if self.pid:
            kill(self.pid, signal.SIGTERM)
        for timer in (self.poll_timer, self.fit_timer):
            if timer:
                GLib.source_remove(timer)
        self.poll_timer = self.fit_timer = 0
        if self.display:
            self.display.close()
            self.display = None


def load_gtk_vnc():
    """The GtkVnc module, or None when gir1.2-gtk-vnc-2.0 / gtk-vnc2 is not installed."""
    try:
        gi.require_version("GtkVnc", "2.0")
        from gi.repository import GtkVnc
    except (ImportError, ValueError):
        return None
    return GtkVnc


def credential_kinds(values):
    """The credential types of a vnc-auth-credential signal (a GValueArray or a list, depending on PyGObject)."""
    if hasattr(values, "n_values"):
        values = [values.get_nth(index) for index in range(values.n_values)]
    return [int(value) for value in values]


class VncSession(Session):
    """A gtk-vnc display inside the window (X11 and Wayland), or inside a VncWindow of its own (docs/ui-spec.md §8)."""
    protocol = "VNC"
    embedded = True

    def __init__(self, owner, profile, number, credential=None, window=None):
        super().__init__(owner, profile, number)
        self.credential = credential  # the VNC password, or None to ask when the server wants one
        self.window = window
        self.display = None
        self.tunnel = None
        self.tunnel_cancel = None
        self.tunnel_lock = threading.Lock()
        self.generation = 0
        self.initialized = False
        self.failure = ""
        self.wrong_password = False
        self.full_screen = False
        self.received = None
        self.sent = None
        self.clipboard = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
        self.clipboard_handler = self.clipboard.connect("owner-change", self.clipboard_changed)
        self.pages = Gtk.Stack()
        self.pack_start(self.pages, True, True, 0)

        desktop = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        self.bar = Gtk.Box(spacing=8, margin=6)
        bar_title = Gtk.Label(label=profile.name + "  ·  " + profile.address, xalign=0)
        mark(bar_title, "sshm-terminal-text")
        self.bar.pack_start(bar_title, True, True, 0)
        self.bar.pack_end(action_button("Выйти из полноэкранного режима", self.leave_full_screen, "outline",
                                        icon="move_left"), False, False, 0)
        desktop.pack_start(self.bar, False, False, 0)
        self.holder = Gtk.Box(hexpand=True, vexpand=True)
        desktop.pack_start(self.holder, True, True, 0)
        self.pages.add_named(desktop, "desktop")

        ended = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6, valign=Gtk.Align.CENTER, halign=Gtk.Align.CENTER)
        self.ended_title = Gtk.Label()
        mark(self.ended_title, "sshm-overlay-title")
        self.ended_detail = Gtk.Label(wrap=True, justify=Gtk.Justification.CENTER, max_width_chars=70, selectable=True)
        mark(self.ended_detail, "sshm-overlay-detail")
        actions = Gtk.Box(spacing=8, halign=Gtk.Align.CENTER, margin_top=12)
        actions.pack_start(action_button("Переподключить", self.restart, "primary", icon="refresh", width=160), False, False, 0)
        actions.pack_start(action_button("Закрыть сеанс", self.close_request, "outline", width=140), False, False, 0)
        for widget in (self.ended_title, self.ended_detail, actions):
            ended.pack_start(widget, False, False, 0)
        self.pages.add_named(ended, "ended")
        self.connect("destroy", self.stop)
        self.show_all()
        self.bar.hide()

    @property
    def address(self):
        return self.profile.address + (f"  ·  через {self.profile.via}" if self.profile.via else "")

    def focus(self):
        if self.display is not None and self.live:
            self.display.grab_focus()
        return False

    # --- lifecycle ---------------------------------------------------------------------------
    def start(self):
        self.generation += 1
        generation = self.generation
        self.close_connection()
        self.live = True
        self.initialized = False
        self.failure = ""
        self.wrong_password = False
        self.status = "подключение"
        self.pages.set_visible_child_name("desktop")
        self.changed()
        GtkVnc = load_gtk_vnc()
        if GtkVnc is None:
            self.ended(MISSING_CLIENT, failed=True)
            return
        self.display = self.create_display(GtkVnc, generation)
        if not self.profile.via:
            self.open(generation, self.profile.host, self.profile.port)
            return
        jump = self.owner.ssh_profile(self.profile.via)
        if jump is None:
            self.ended(f"Не удалось открыть SSH-туннель: сервер «{self.profile.via}» не найден в каталоге SSH.", failed=True)
            return
        config, vault = self.owner.config, self.owner.vault
        host, port = self.profile.host, self.profile.port

        cancelled = self.tunnel_cancel = threading.Event()

        def tunnel():
            try:
                opened = SshTunnel(jump, config, vault, host, port, cancelled=cancelled)
            except (TunnelError, OSError, RuntimeError) as error:
                if not cancelled.is_set():
                    GLib.idle_add(self.tunnel_failed, generation, str(error))
                return
            with self.tunnel_lock:
                if not cancelled.is_set():
                    self.tunnel = opened
                    GLib.idle_add(self.tunnel_ready, generation)
                    return
            opened.close()

        # Let tunnel cleanup finish even when GTK quits while OpenSSH is starting.
        threading.Thread(target=tunnel, daemon=False).start()

    def tunnel_ready(self, generation):
        if self.current(generation) and self.tunnel is not None:
            self.open(generation, "127.0.0.1", self.tunnel.port)
        return False

    def tunnel_failed(self, generation, detail):
        if not self.closed and generation == self.generation:
            self.ended(detail if detail.startswith("Не удалось") else "Не удалось открыть SSH-туннель: " + detail, failed=True)
        return False

    def create_display(self, GtkVnc, generation):
        display = GtkVnc.Display(can_focus=True, hexpand=True, vexpand=True)
        fit = self.profile.scale == "fit"
        display.set_scaling(fit)
        for method, value in (("set_keep_aspect_ratio", True), ("set_allow_resize", fit), ("set_smoothing", True)):
            if hasattr(display, method):
                getattr(display, method)(value)
        display.set_read_only(self.profile.view_only)
        display.set_shared_flag(True)
        display.set_pointer_grab(False)
        display.set_keyboard_grab(self.full_screen)
        handlers = {
            "vnc-initialized": self.on_initialized,
            "vnc-auth-credential": self.on_credential,
            "vnc-auth-failure": self.on_auth_failure,
            "vnc-auth-unsupported": self.on_auth_unsupported,
            "vnc-error": self.on_error,
            "vnc-disconnected": self.on_disconnected,
            "vnc-server-cut-text": self.on_server_text,
        }
        for signal_name, handler in handlers.items():
            display.connect(signal_name, lambda widget, *args, handler=handler: handler(generation, *args))
        display.connect("focus-in-event", lambda *_: self.send_clipboard())
        display.connect("button-press-event", lambda widget, event: widget.grab_focus() and False)
        if fit:
            self.holder.pack_start(display, True, True, 0)
        else:
            scroll = Gtk.ScrolledWindow(hexpand=True, vexpand=True)
            scroll.add(display)
            self.holder.pack_start(scroll, True, True, 0)
        self.holder.show_all()
        return display

    def open(self, generation, host, port):
        if self.display is None or generation != self.generation:
            return
        if not self.display.open_host(host, str(port)):
            self.ended("Не удалось начать подключение к серверу VNC.", failed=True)

    # --- gtk-vnc signals ---------------------------------------------------------------------
    def current(self, generation):
        return not self.closed and generation == self.generation and self.display is not None

    def on_initialized(self, generation):
        if not self.current(generation):
            return
        self.initialized = True
        self.status = "подключён"
        self.changed()
        self.focus()
        if self.profile.fullscreen and not self.full_screen:
            self.enter_full_screen()

    def on_credential(self, generation, values):
        if not self.current(generation):
            return
        GtkVnc = load_gtk_vnc()
        kinds = credential_kinds(values)
        if int(GtkVnc.DisplayCredential.PASSWORD) in kinds and self.credential is None:
            GLib.idle_add(self.ask_password, generation, kinds)
            return
        self.give_credentials(kinds)

    def give_credentials(self, kinds):
        GtkVnc = load_gtk_vnc()
        values = {int(GtkVnc.DisplayCredential.USERNAME): self.profile.user or "",
                  int(GtkVnc.DisplayCredential.PASSWORD): self.credential or "",
                  int(GtkVnc.DisplayCredential.CLIENTNAME): "ssh-manager"}
        for kind in kinds:
            if kind not in values or self.display.set_credential(kind, values[kind]):
                self.failure = "Сервер VNC не поддерживает доступный способ входа."
                self.display.close()
                return

    def ask_password(self, generation, kinds, wrong=False):
        if not self.current(generation):
            return False
        password = ask_vnc_password(self.owner, self.get_toplevel(), self.profile, wrong)
        if not self.current(generation):
            return False
        if password is None:
            if kinds is None:
                self.ended("Неверный пароль VNC.", failed=True)
            else:
                self.failure = "Нужен пароль VNC."
                self.display.close()
            return False
        self.credential = password
        if kinds is None:
            self.start()
        else:
            self.give_credentials(kinds)
        return False

    def on_auth_failure(self, generation, reason=""):
        if self.current(generation):
            self.wrong_password = True
            self.failure = "Неверный пароль VNC."

    def on_auth_unsupported(self, generation, *_):
        if self.current(generation):
            self.failure = "Сервер VNC не поддерживает доступный способ входа."

    def on_error(self, generation, text=""):
        if self.current(generation) and not self.failure and text:
            self.failure = text

    def on_disconnected(self, generation):
        if not self.current(generation):
            return
        if self.wrong_password:
            self.credential = None
            self.status = "подключение"
            self.changed()
            GLib.idle_add(self.ask_password, generation, None, True)
            return
        if self.failure:
            self.ended(self.failure, failed=True)
        else:
            self.ended("Сервер VNC закрыл соединение.", failed=not self.initialized)

    def on_server_text(self, generation, text):
        if self.current(generation) and self.profile.clipboard and not self.profile.view_only and text:
            self.received = text
            self.clipboard.set_text(text, -1)

    # --- clipboard to the server ---------------------------------------------------------------
    def clipboard_changed(self, *_):
        if self.display is not None and self.display.has_focus():
            self.send_clipboard()

    def send_clipboard(self):
        if self.display is None or not self.initialized or not self.profile.clipboard or self.profile.view_only:
            return False
        generation = self.generation

        def received(clipboard, text):
            if text and text != self.received and text != self.sent and self.current(generation) and self.initialized:
                self.sent = text
                self.display.client_cut_text(text)
        self.clipboard.request_text(received)
        return False

    # --- end, full screen, close -----------------------------------------------------------------
    def ended(self, detail, failed):
        self.live = False
        self.generation += 1
        if self.full_screen:
            self.leave_full_screen()
        self.close_connection()
        self.status = "отключён (ошибка)" if failed else "отключён"
        self.ended_title.set_text("Подключение прервано" if failed else "Сеанс завершён")
        self.ended_detail.set_text(f"{self.profile.name} · {self.address}\n{detail}".strip())
        self.pages.set_visible_child_name("ended")
        self.changed()

    def restart(self):
        self.start()

    def close_connection(self):
        display, self.display = self.display, None
        if display is not None:
            display.close()
        for child in self.holder.get_children():
            self.holder.remove(child)
            child.destroy()
        with self.tunnel_lock:
            if self.tunnel_cancel is not None:
                self.tunnel_cancel.set()
                self.tunnel_cancel = None
            tunnel, self.tunnel = self.tunnel, None
        if tunnel is not None:
            threading.Thread(target=tunnel.close, daemon=False).start()

    def enter_full_screen(self):
        if self.window is not None:
            self.window.set_full_screen(True)
        else:
            self.owner.enter_full_screen(self)

    def leave_full_screen(self):
        if self.window is not None:
            self.window.set_full_screen(False)
        else:
            self.owner.leave_full_screen()

    def set_full_screen(self, value):
        self.full_screen = value
        self.bar.set_visible(value)
        if self.display is not None:
            self.display.set_keyboard_grab(value)

    def close_request(self):
        if self.window is not None:
            self.window.destroy()
        else:
            self.owner.close_session(self, confirm_close=False)

    def stop(self, *_):
        if self.closed:
            return
        super().stop()
        self.generation += 1
        if self.clipboard_handler:
            self.clipboard.disconnect(self.clipboard_handler)
            self.clipboard_handler = 0
        self.close_connection()


class VncWindow(Gtk.Window):
    """«Открыть в отдельном окне» for VNC: the session in a window of its own (src/VncWindow.cs)."""

    def __init__(self, owner, profile, credential=None):
        super().__init__(title=profile.name + " — VNC", application=owner.get_application())
        self.set_default_size(1024, 700)
        if owner.get_icon_name():
            self.set_icon_name(owner.get_icon_name())
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        mark(box, "sshm-terminal-area")
        self.toolbar = Gtk.Box(spacing=6, margin=6)
        title = Gtk.Label(label=profile.name + "  ·  " + profile.address, xalign=0)
        mark(title, "sshm-terminal-text")
        self.toolbar.pack_start(title, True, True, 0)
        self.session = VncSession(owner, profile, 1, credential, window=self)
        self.full = action_button("Весь экран", lambda: self.set_full_screen(True), "outline", icon="server", width=116)
        for widget in (self.full,
                       action_button("Переподключить", self.session.restart, "outline", icon="refresh", width=150),
                       action_button("Отключить", self.destroy, "outline", danger=True, width=100)):
            self.toolbar.pack_start(widget, False, False, 0)
        box.pack_start(self.toolbar, False, False, 0)
        box.pack_start(self.session, True, True, 0)
        self.add(box)
        self.connect("destroy", lambda *_: self.session.stop())
        self.show_all()
        self.session.bar.hide()
        self.session.start()

    def set_full_screen(self, value):
        if value and (not self.session.live or not self.session.initialized):
            return
        self.toolbar.set_visible(not value)
        self.session.set_full_screen(value)
        (self.fullscreen if value else self.unfullscreen)()


def ask_vnc_password(owner, parent, profile, wrong=False):
    """The entered VNC password (saved first when asked to), or None when the user cancels."""
    dialog = VncLogonDialog(parent if isinstance(parent, Gtk.Window) else owner, profile, wrong)
    try:
        while True:
            if dialog.run() != Gtk.ResponseType.OK:
                return None
            password, remember = dialog.values()
            if password and not any(char in password for char in "\0\r\n"):
                break
    finally:
        dialog.destroy()
    if remember:
        try:
            owner.vault.save(profile.secret, password)
        except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
            message(owner, error, Gtk.MessageType.ERROR)
    return password


def embedding_supported():
    display = Gdk.Display.get_default()
    return display is not None and type(display).__name__ == "X11Display"


def ask_credential(owner, profile, saved):
    """(user, domain, password) for an RDP connection, asking first when no password is saved."""
    if saved is not None and profile.user:
        return profile.user, profile.domain, saved
    dialog = LogonDialog(owner, profile)
    try:
        if dialog.run() != Gtk.ResponseType.OK:
            return None
        user, domain, password, remember = dialog.values(profile)
    finally:
        dialog.destroy()
    if remember and password:
        if (user, domain) == (profile.user, profile.domain):
            try:
                owner.vault.save(profile.secret, password)
            except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
                message(owner, error, Gtk.MessageType.ERROR)
        else:
            message(owner, "Пароль сохраняется только для пользователя из настроек подключения. "
                           "Измените пользователя в профиле, чтобы запомнить пароль для другой учётной записи.")
    return user, domain, password


class SessionRow(Gtk.ListBoxRow):
    def __init__(self, session, color):
        super().__init__()
        self.session = session
        line = Gtk.Box(spacing=8, margin_start=9, margin_end=6, margin_top=4, margin_bottom=4)
        self.dot = Gtk.Label(valign=Gtk.Align.START, margin_top=2)
        line.pack_start(self.dot, False, False, 0)
        texts = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        self.title = Gtk.Label(xalign=0, ellipsize=Pango.EllipsizeMode.END)
        mark(self.title, "sshm-emphasis")
        self.subtitle = Gtk.Label(xalign=0, ellipsize=Pango.EllipsizeMode.END)
        mark(self.subtitle, "sshm-muted")
        texts.pack_start(self.title, False, False, 0)
        texts.pack_start(self.subtitle, False, False, 0)
        line.pack_start(texts, True, True, 0)
        self.add(line)
        self.update(color)

    def update(self, color):
        session = self.session
        self.dot.set_markup(f'<span foreground="{color}" size="small">●</span>')
        self.title.set_text(session.title)
        self.subtitle.set_text(f"{session.protocol} · " + (session.address if session.live else "Сеанс завершён"))


class SessionsPage(Gtk.Box):
    def __init__(self, owner, header):
        super().__init__(orientation=Gtk.Orientation.VERTICAL)
        self.owner = owner
        self.active = None
        self.order = 0
        self.refreshing = False
        self.pack_start(header, False, False, 0)
        body = Gtk.Box(spacing=12)
        self.pack_start(body, True, True, 0)

        sidebar = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        sidebar.set_size_request(234, -1)
        self.search = Gtk.SearchEntry(placeholder_text="Найти сеанс")
        self.search.connect("search-changed", lambda *_: self.refresh())
        self.search.connect("activate", lambda *_: self.open_first())
        sidebar.pack_start(self.search, False, False, 0)
        self.count = Gtk.Label(xalign=0, margin_start=2)
        mark(self.count, "sshm-muted")
        sidebar.pack_start(self.count, False, False, 0)
        self.list = Gtk.ListBox()
        self.list.connect("row-selected", self.row_selected)
        self.list.connect("button-press-event", self.list_pressed)
        self.list.connect("popup-menu", lambda widget: self.menu(widget, None))
        self.list.connect("row-activated", lambda *_: self.active and self.active.focus())
        scroll = Gtk.ScrolledWindow(vexpand=True, hscrollbar_policy=Gtk.PolicyType.NEVER)
        scroll.add(self.list)
        mark(scroll, "sshm-frame")
        sidebar.pack_start(scroll, True, True, 0)
        body.pack_start(sidebar, False, False, 0)
        self.sidebar = sidebar

        workspace = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=7)
        # Title and buttons share the first row; the detail line spans the whole width (src/SessionsPage.cs:264-291).
        info = Gtk.Grid(column_spacing=8, row_spacing=2)
        self.title = Gtk.Label(xalign=0, ellipsize=Pango.EllipsizeMode.END, hexpand=True, width_chars=8)
        mark(self.title, "sshm-emphasis")
        self.detail = Gtk.Label(xalign=0, ellipsize=Pango.EllipsizeMode.END)
        mark(self.detail, "sshm-muted")
        info.attach(self.title, 0, 0, 1, 1)
        info.attach(self.detail, 0, 1, 2, 1)
        buttons = Gtk.Box(spacing=6)
        info.attach(buttons, 1, 0, 1, 1)
        self.files = action_button("Файлы", lambda: self.active and owner.open_files(self.active.profile), icon="folder", width=86)
        self.reconnect = action_button("Переподключить", lambda: self.active and self.active.restart(), icon="refresh", width=150)
        self.detach = action_button("В окне", lambda: self.active and owner.detach_session(self.active), icon="move", width=92,
                                    tooltip="Переоткрыть сеанс в отдельном окне")
        self.full_screen = action_button("Весь экран", lambda: self.active and owner.enter_full_screen(self.active),
                                         icon="server", width=116)
        self.snippets = action_button("Команды", lambda: self.active and self.snippet_menu(self.snippets), icon="terminal",
                                      width=110, tooltip="Сохранённые команды")
        self.duplicate = action_button("Новый сеанс этого сервера", lambda: self.active and owner.duplicate_session(self.active),
                                       "quiet", icon="plus", width=220)
        self.close = action_button("Закрыть", lambda: self.active and owner.close_session(self.active), "quiet",
                                   danger=True, width=81)
        for widget in (self.files, self.snippets, self.reconnect, self.detach, self.full_screen, self.duplicate, self.close):
            buttons.pack_start(widget, False, False, 0)
        self.info = info
        workspace.pack_start(info, False, False, 0)
        self.views = Gtk.Stack()
        empty = Gtk.Label(label="Выберите сервер в каталоге, чтобы начать сеанс.", wrap=True)
        mark(empty, "sshm-terminal-text")
        holder = Gtk.Box()
        mark(holder, "sshm-terminal-area")
        holder.pack_start(empty, True, True, 0)
        self.views.add_named(holder, "empty")
        workspace.pack_start(self.views, True, True, 0)
        body.pack_start(workspace, True, True, 0)
        self.workspace_info = info

    # --- list --------------------------------------------------------------------------------
    def add(self, session):
        self.views.add(session)
        self.activate(session, focus=True)

    def remove(self, session):
        self.views.remove(session)
        if session is self.active:
            self.active = None
            remaining = sorted(self.owner.sessions, key=lambda item: item.last_used, reverse=True)
            self.activate(remaining[0] if remaining else None, focus=True)
        else:
            self.refresh()

    def activate(self, session, focus=False):
        if session is not self.active:
            self.active = session
            if session is not None:
                self.order += 1
                session.last_used = self.order
        self.views.set_visible_child(session if session is not None else self.views.get_child_by_name("empty"))
        self.refresh()
        if focus and session is not None:
            GLib.idle_add(session.focus)

    def refresh(self):
        owner = self.owner
        query = self.search.get_text().strip().casefold()
        self.refreshing = True
        try:
            for row in self.list.get_children():
                self.list.remove(row)
            for session in owner.sessions:
                group = owner.group_record(owner.group_of(session.profile))
                haystack = " ".join((session.title, session.address, group["name"] if group else "", session.protocol))
                if query in haystack.casefold():
                    color = (group["color"] if group and group["color"] else owner.theme.color("teal")) if session.live \
                        else owner.theme.color("line")
                    row = SessionRow(session, color)
                    self.list.add(row)
                    if session is self.active:
                        self.list.select_row(row)
            self.list.show_all()
        finally:
            self.refreshing = False
        self.count.set_text(f"Открыто: {sum(session.live for session in owner.sessions)}")
        self.refresh_details()

    def refresh_details(self):
        session = self.active
        self.title.set_text(session.title if session else "Сеансы SSH, RDP и VNC")
        if session is None:
            self.detail.set_text("")
        else:
            group = self.owner.group_record(self.owner.group_of(session.profile))
            parts = [session.protocol, session.address]
            if group:
                parts.append(group["name"])
            if session.forwarding_failed:
                parts.append("ошибка проброса порта")
            elif session.without_forwarding:
                parts.append("без проброса")
            if session.status:
                parts.append(session.status)
            self.detail.set_text("  ·  ".join(parts))
        rdp = isinstance(session, (RdpSession, VncSession))
        self.files.set_visible(session is not None and not rdp)
        self.snippets.set_visible(session is not None and not rdp)
        for widget in (self.reconnect, self.detach, self.full_screen):
            widget.set_visible(rdp)
        self.full_screen.set_sensitive(rdp and session.live and session.embedded and session.status == "подключён")
        self.duplicate.set_sensitive(session is not None)
        self.close.set_sensitive(session is not None)

    def row_selected(self, listing, row):
        if not self.refreshing and row is not None:
            self.activate(row.session)

    def list_pressed(self, listing, event):
        row = listing.get_row_at_y(int(event.y))
        if row is None:
            return False
        if event.button == 3 and event.type == Gdk.EventType.BUTTON_PRESS:
            listing.select_row(row)
            self.menu(listing, event)
            return True
        if event.button == 1:
            GLib.idle_add(row.session.focus)
        return False

    def open_first(self):
        row = self.list.get_selected_row() or self.list.get_row_at_index(0)
        if row is not None:
            self.activate(row.session, focus=True)

    def focus_search(self):
        self.search.grab_focus()

    def cycle(self):
        others = [item for item in self.owner.sessions if item is not self.active and item.live]
        if others:
            self.activate(max(others, key=lambda item: item.last_used), focus=True)

    def menu(self, widget, event):
        session, owner = self.active, self.owner
        if session is None:
            return False
        rdp = isinstance(session, (RdpSession, VncSession))
        items = [Item("Переименовать сеанс", lambda: owner.rename_session(session), icon="edit"),
                 Item("Новый сеанс этого сервера", lambda: owner.duplicate_session(session), icon="plus")]
        if not rdp:
            items.append(Item("Файлы (SFTP)", lambda: owner.open_files(session.profile), icon="folder"))
        else:
            if session.live and session.embedded and session.status == "подключён":
                items.append(Item(f"Полноэкранный режим {session.protocol}", lambda: owner.enter_full_screen(session),
                                  icon="server"))
            items += [Item("Открыть в отдельном окне", lambda: owner.detach_session(session), icon="move"),
                      Item("Переподключить", session.restart, icon="refresh")]
        if session.forwarding_failed:
            items.append(Item("Новый сеанс без проброса портов", lambda: owner.duplicate_session(session, forwarding=False),
                              icon="terminal"))
        items.append(Item("Закрыть сеанс", lambda: owner.close_session(session), icon="delete", danger=True))
        return menu_at(widget, event, items)

    def snippet_menu(self, widget):
        """«Команды»: saved commands with «Отправить», «Во все открытые сеансы», «Добавить…», «Удалить»."""
        owner = self.owner
        popover = Gtk.Popover(relative_to=widget)
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6, margin=10)
        everywhere = Gtk.CheckButton(label="Во все открытые сеансы")
        snippets = owner.settings.get("snippets", [])
        if not snippets:
            empty = Gtk.Label(label="Сохранённых команд пока нет.", xalign=0)
            mark(empty, "sshm-muted")
            box.pack_start(empty, False, False, 0)
        for index, item in enumerate(snippets):
            row = Gtk.Box(spacing=6)
            title = Gtk.Label(label=item["name"], xalign=0, hexpand=True, ellipsize=Pango.EllipsizeMode.END)
            title.set_tooltip_text(item["command"])
            row.pack_start(title, True, True, 0)

            def send(command=item["command"]):
                targets = [session for session in owner.sessions if isinstance(session, SshSession) and session.live] \
                    if everywhere.get_active() else [self.active]
                for target in targets:
                    if isinstance(target, SshSession):
                        target.send(command)
                popover.popdown()
            row.pack_start(action_button("Отправить", send, "outline"), False, False, 0)
            row.pack_start(action_button("", lambda index=index: (owner.remove_snippet(index), popover.popdown()), "quiet",
                                         icon="delete", danger=True, tooltip="Удалить"), False, False, 0)
            box.pack_start(row, False, False, 0)
        box.pack_start(everywhere, False, False, 0)
        box.pack_start(action_button("Добавить…", lambda: (popover.popdown(), owner.add_snippet()), "quiet", icon="plus"),
                       False, False, 0)
        popover.add(box)
        box.set_size_request(360, -1)
        popover.show_all()
        popover.popup()

    def set_chrome(self, visible):
        self.sidebar.set_visible(visible)
        self.info.set_visible(visible)
