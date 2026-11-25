"""Tray icon (StatusNotifier via Ayatana AppIndicator) with «Открыть SSH Manager» / «Выход» (docs/ui-spec.md §5)."""

import gi
from gi.repository import Gtk


def _indicator_module():
    for name in ("AyatanaAppIndicator3", "AppIndicator3"):
        try:
            gi.require_version(name, "0.1")
            module = __import__("gi.repository", fromlist=[name])
            return getattr(module, name)
        except (ValueError, ImportError, AttributeError):
            continue
    return None


class Tray:
    def __init__(self, window, icon):
        self.window = window
        self.indicator = None
        module = _indicator_module()
        if module is None:
            return
        try:
            indicator = module.Indicator.new("io.github.clarkufa.SSHManager", icon or "utilities-terminal",
                                             module.IndicatorCategory.APPLICATION_STATUS)
            indicator.set_title("SSH Manager")
            indicator.set_status(module.IndicatorStatus.ACTIVE)
        except Exception:  # noqa: BLE001 - a broken indicator must not stop the app
            return
        menu = Gtk.Menu()
        open_item = Gtk.MenuItem(label="Открыть SSH Manager")
        open_item.connect("activate", lambda *_: window.restore())
        quit_item = Gtk.MenuItem(label="Выход")
        quit_item.connect("activate", lambda *_: window.quit_app())
        menu.append(open_item)
        menu.append(Gtk.SeparatorMenuItem())
        menu.append(quit_item)
        menu.show_all()
        indicator.set_menu(menu)
        try:
            indicator.set_secondary_activate_target(open_item)
        except AttributeError:
            pass
        self.indicator = indicator

    @property
    def available(self):
        """True only when a tray host shows the icon; otherwise hiding the window would lose it."""
        if self.indicator is None:
            return False
        try:
            return bool(self.indicator.get_property("connected"))
        except TypeError:
            return False
