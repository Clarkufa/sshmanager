from gi.repository import Gdk, Gtk


def button(text, action, suggested=False):
    widget = Gtk.Button(label=text)
    widget.connect("clicked", lambda *_: action())
    if suggested:
        widget.get_style_context().add_class("suggested-action")
    return widget


def message(owner, text, kind=Gtk.MessageType.INFO):
    dialog = Gtk.MessageDialog(transient_for=owner, modal=True, message_type=kind,
                               buttons=Gtk.ButtonsType.OK, text=str(text))
    dialog.run()
    dialog.destroy()


def confirm(owner, text, details=None):
    dialog = Gtk.MessageDialog(transient_for=owner, modal=True, message_type=Gtk.MessageType.QUESTION,
                               buttons=Gtk.ButtonsType.NONE, text=text)
    dialog.add_buttons("Нет", Gtk.ResponseType.NO, "Да", Gtk.ResponseType.YES)
    if details is not None:
        dialog.format_secondary_text(details)
    dialog.set_default_response(Gtk.ResponseType.NO)
    answer = dialog.run()
    dialog.destroy()
    return answer == Gtk.ResponseType.YES


def prompt(owner, title, initial="", hidden=False):
    dialog = Gtk.Dialog(title=title, transient_for=owner, modal=True)
    dialog.add_button("Отмена", Gtk.ResponseType.CANCEL)
    dialog.add_button("Продолжить", Gtk.ResponseType.OK)
    dialog.set_default_response(Gtk.ResponseType.OK)
    dialog.get_widget_for_response(Gtk.ResponseType.OK).get_style_context().add_class("suggested-action")
    entry = Gtk.Entry(text=initial, visibility=not hidden, activates_default=True, width_chars=37)
    dialog.get_content_area().set_border_width(14)
    dialog.get_content_area().add(entry)
    dialog.show_all()
    response = dialog.run()
    value = entry.get_text()
    dialog.destroy()
    return value if response == Gtk.ResponseType.OK else None


def chooser(owner, title, action, proposed="", directory=None):
    dialog = Gtk.FileChooserNative.new(title, owner, action, "Выбрать", "Отмена")
    if action == Gtk.FileChooserAction.SAVE:
        dialog.set_current_name(proposed)
        dialog.set_do_overwrite_confirmation(True)
    if directory:
        dialog.set_current_folder(str(directory))
    response = dialog.run()
    filename = dialog.get_filename()
    dialog.destroy()
    return filename if response == Gtk.ResponseType.ACCEPT else None


def menu_at(widget, event, actions):
    menu = Gtk.Menu()
    menu.attach_to_widget(widget, None)
    for label, action in actions:
        item = Gtk.MenuItem(label=label)
        item.set_sensitive(action is not None)
        if action:
            item.connect("activate", lambda item, work=action: work())
        menu.append(item)
    menu.connect("selection-done", lambda *_: menu.destroy())
    menu.show_all()
    if event:
        menu.popup_at_pointer(event)
    else:
        menu.popup_at_widget(widget, Gdk.Gravity.SOUTH_WEST, Gdk.Gravity.NORTH_WEST, None)
    return True
