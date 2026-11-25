from dataclasses import dataclass, field

from gi.repository import Gdk, Gtk

from .glyphs import Glyph
from .theme import mark


def action_button(text, action, style="outline", icon=None, danger=False, width=None, tooltip=None):
    """A button in one of the Windows styles: primary, outline, quiet, tab (docs/ui-spec.md §1)."""
    widget = Gtk.Button()
    box = Gtk.Box(spacing=6, halign=Gtk.Align.CENTER)
    widget.glyph = Glyph(icon) if icon else None
    if widget.glyph:
        box.pack_start(widget.glyph, False, False, 0)
    widget.label = Gtk.Label(label=text)
    box.pack_start(widget.label, False, False, 0)
    widget.add(box)
    mark(widget, "sshm-button", style)
    if danger:
        mark(widget, "danger")
    if width:
        widget.set_size_request(width, -1)
    if tooltip:
        widget.set_tooltip_text(tooltip)
    if action is not None:
        widget.connect("clicked", lambda *_: action())
    return widget


def set_button_text(widget, text):
    widget.label.set_text(text)


def button(text, action, suggested=False):
    return action_button(text, action, "primary" if suggested else "outline")


@dataclass
class Item:
    label: str
    action: object = None
    icon: str = None
    checked: object = None
    danger: bool = False
    enabled: bool = True
    children: list = field(default_factory=list)


def build_menu(items, attach=None):
    menu = Gtk.Menu()
    mark(menu, "sshm-menu")
    if attach is not None:
        menu.attach_to_widget(attach, None)
    for entry in items:
        if entry is None:
            menu.append(Gtk.SeparatorMenuItem())
            continue
        if isinstance(entry, tuple):
            entry = Item(entry[0], entry[1], enabled=entry[1] is not None)
        if entry.checked is not None:
            item = Gtk.CheckMenuItem(label=entry.label)
            item.set_active(bool(entry.checked))
        else:
            item = Gtk.MenuItem()
            box = Gtk.Box(spacing=8)
            image = Glyph(entry.icon) if entry.icon else Gtk.Box()
            image.set_size_request(16, 16)
            box.pack_start(image, False, False, 0)
            box.pack_start(Gtk.Label(label=entry.label, xalign=0), True, True, 0)
            item.add(box)
        if entry.danger:
            mark(item, "danger")
        item.set_sensitive(entry.enabled and (entry.action is not None or bool(entry.children)))
        if entry.children:
            item.set_submenu(build_menu(entry.children))
        elif entry.action is not None:
            item.connect("activate", lambda _, work=entry.action: work())
        menu.append(item)
    menu.show_all()
    return menu


def menu_at(widget, event, actions):
    menu = build_menu(actions, widget)
    menu.connect("selection-done", lambda *_: menu.destroy())
    if event is not None and event.type == Gdk.EventType.BUTTON_PRESS:
        menu.popup_at_pointer(event)
    else:
        menu.popup_at_widget(widget, Gdk.Gravity.SOUTH_WEST, Gdk.Gravity.NORTH_WEST, None)
    return True


def themed(dialog):
    return mark(dialog, "sshm")


def message(owner, text, kind=Gtk.MessageType.INFO):
    dialog = themed(Gtk.MessageDialog(transient_for=owner, modal=True, message_type=kind,
                                      buttons=Gtk.ButtonsType.OK, text=str(text)))
    dialog.run()
    dialog.destroy()


def confirm(owner, text, details=None, title=None):
    dialog = themed(Gtk.MessageDialog(transient_for=owner, modal=True, message_type=Gtk.MessageType.QUESTION,
                                      buttons=Gtk.ButtonsType.NONE, text=text))
    if title:
        dialog.set_title(title)
    dialog.add_buttons("Нет", Gtk.ResponseType.NO, "Да", Gtk.ResponseType.YES)
    if details is not None:
        dialog.format_secondary_text(details)
    dialog.set_default_response(Gtk.ResponseType.NO)
    answer = dialog.run()
    dialog.destroy()
    return answer == Gtk.ResponseType.YES


def dialog_buttons(dialog, commit, cancel="Отмена"):
    """Cancel on the left, the primary action rightmost, Enter = primary (docs/ui-spec.md §4)."""
    cancel_button = dialog.add_button(cancel, Gtk.ResponseType.CANCEL)
    commit_button = dialog.add_button(commit, Gtk.ResponseType.OK)
    mark(cancel_button, "sshm-button", "outline")
    mark(commit_button, "sshm-button", "primary")
    dialog.set_default_response(Gtk.ResponseType.OK)
    return commit_button


def prompt(owner, title, initial="", hidden=False, label=None, commit="Продолжить", max_length=0):
    dialog = themed(Gtk.Dialog(title=title, transient_for=owner, modal=True))
    dialog_buttons(dialog, commit)
    area = dialog.get_content_area()
    area.set_border_width(16)
    area.set_spacing(8)
    if label:
        caption = Gtk.Label(label=label, xalign=0)
        mark(caption, "sshm-emphasis")
        area.add(caption)
    entry = Gtk.Entry(text=initial, visibility=not hidden, activates_default=True, width_chars=37,
                      max_length=max_length)
    area.add(entry)
    dialog.show_all()
    response = dialog.run()
    value = entry.get_text()
    dialog.destroy()
    return value if response == Gtk.ResponseType.OK else None


def chooser(owner, title, action, proposed="", directory=None, filters=()):
    """filters: (name, pattern) pairs; the first one is selected."""
    dialog = Gtk.FileChooserNative.new(title, owner, action, "Выбрать", "Отмена")
    if action == Gtk.FileChooserAction.SAVE:
        dialog.set_current_name(proposed)
        dialog.set_do_overwrite_confirmation(True)
    if directory:
        dialog.set_current_folder(str(directory))
    for name, pattern in filters:
        rule = Gtk.FileFilter()
        rule.set_name(name)
        rule.add_pattern(pattern)
        dialog.add_filter(rule)
    response = dialog.run()
    filename = dialog.get_filename()
    dialog.destroy()
    return filename if response == Gtk.ResponseType.ACCEPT else None
