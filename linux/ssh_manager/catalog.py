"""The server catalog: protocol switch, group tabs, tools row and the server table (docs/ui-spec.md §2)."""

import math

from gi.repository import Gdk, GLib, GObject, Gtk, Pango, PangoCairo

from .rdp import DesktopProfile, ordered
from . import glyphs
from .theme import TAB_COLORS, mark, rgba
from .widgets import Item, action_button, build_menu, menu_at

ALL_NAME = "Все серверы"
UNGROUPED_NAME = "Без вкладки"
# Header click cycles ascending → descending → original order (src/ConnectionsWindow.cs:301-308).
SORT_CYCLE = {"name": ("name", "name_desc"), "address": ("address", "address_desc")}


def next_order(current, column):
    ascending, descending = SORT_CYCLE[column]
    return descending if current == ascending else "original" if current == descending else ascending


class PillRenderer(Gtk.CellRenderer):
    """«Файлы» / «Подключиться» drawn as a Mint pill with a Teal icon and text."""
    text = GObject.Property(type=str, default="")
    icon = GObject.Property(type=str, default="")

    def __init__(self, theme, minimum):
        super().__init__()
        self.theme = theme
        self.minimum = minimum

    def do_get_preferred_width(self, widget):
        return self.minimum, self.minimum

    def do_get_preferred_height(self, widget):
        return 45, 45

    def do_render(self, cr, widget, background, area, flags):
        if not self.text:
            return
        inset = area.height / 6
        x, y = area.x + 5, area.y + inset
        width, height = area.width - 10, area.height - 2 * inset
        radius = min(7, height / 2)
        cr.new_sub_path()
        cr.arc(x + width - radius, y + radius, radius, -math.pi / 2, 0)
        cr.arc(x + width - radius, y + height - radius, radius, 0, math.pi / 2)
        cr.arc(x + radius, y + height - radius, radius, math.pi / 2, math.pi)
        cr.arc(x + radius, y + radius, radius, math.pi, 3 * math.pi / 2)
        cr.close_path()
        mint, teal = rgba(self.theme.color("mint")), rgba(self.theme.color("teal"))
        Gdk.cairo_set_source_rgba(cr, mint)
        cr.fill()
        left = x + 7
        glyphs.draw(cr, self.icon, left, y + (height - 16) / 2, teal)
        left += 22
        layout = widget.create_pango_layout(self.text)
        font = widget.get_style_context().get_font(Gtk.StateFlags.NORMAL).copy()
        font.set_weight(Pango.Weight.SEMIBOLD)
        layout.set_font_description(font)
        layout.set_width(int(max(0, x + width - left - 4) * Pango.SCALE))
        layout.set_ellipsize(Pango.EllipsizeMode.END)
        _, text_height = layout.get_pixel_size()
        cr.move_to(left, y + (height - text_height) / 2)
        Gdk.cairo_set_source_rgba(cr, teal)
        PangoCairo.show_layout(cr, layout)


class GlyphRenderer(Gtk.CellRenderer):
    """The Teal server glyph in front of the name, like the Windows grid."""

    def __init__(self, theme):
        super().__init__()
        self.theme = theme

    def do_get_preferred_width(self, widget):
        return 28, 28

    def do_get_preferred_height(self, widget):
        return 45, 45

    def do_render(self, cr, widget, background, area, flags):
        glyphs.draw(cr, "server", area.x + 9, area.y + (area.height - 16) / 2, rgba(self.theme.color("teal")))


class CatalogPage(Gtk.Box):
    COLUMNS = (object, str, str, str, str, str, str, str, str)  # profile, name, address, user, port, tab, files, action, icon

    def __init__(self, owner):
        super().__init__(orientation=Gtk.Orientation.VERTICAL)
        self.owner = owner
        self.rows = []

        protocols = Gtk.Box(spacing=4, margin_bottom=6)
        self.protocol_tabs = {}
        for kind, caption, icon in (("ssh", "SSH", "terminal"), ("rdp", "RDP", "server")):
            tab = action_button(caption, lambda kind=kind: owner.select_protocol(kind), "tab", icon=icon, width=86)
            self.protocol_tabs[kind] = tab
            protocols.pack_start(tab, False, False, 0)
        self.pack_start(protocols, False, False, 0)

        strip = Gtk.Box(spacing=6, margin_bottom=8)
        self.group_strip = Gtk.Box(spacing=5)
        group_scroll = Gtk.ScrolledWindow(hscrollbar_policy=Gtk.PolicyType.AUTOMATIC, vscrollbar_policy=Gtk.PolicyType.NEVER)
        group_scroll.add(self.group_strip)
        group_scroll.set_min_content_height(36)
        strip.pack_start(group_scroll, True, True, 0)
        strip.pack_end(action_button("Новая вкладка", owner.new_group, "quiet", icon="plus", width=140), False, False, 0)
        self.pack_start(strip, False, False, 0)

        tools = Gtk.Box(spacing=4, margin_bottom=8)
        self.search = Gtk.SearchEntry(placeholder_text="Найти сервер")
        self.search.set_size_request(265, -1)
        self.search.connect("search-changed", lambda *_: self.render())
        tools.pack_start(self.search, False, False, 0)
        self.delete = action_button("Удалить", lambda: owner.delete_entry(self.selected()), "quiet", icon="delete",
                                    danger=True, width=95)
        self.edit = action_button("Изменить", lambda: owner.edit_catalog_entry(self.selected()), "quiet", icon="edit", width=105)
        backups = action_button("Резервные копии", None, "quiet", icon="folder", width=154)
        backups.connect("clicked", lambda widget: menu_at(widget, None, [
            Item("Экспорт архива…", owner.export_backup, icon="copy"),
            Item("Импорт архива…", owner.import_backup, icon="folder")]))
        for widget in (self.delete, self.edit, action_button("Обновить", owner.reload, "quiet", icon="refresh", width=106),
                       backups):
            tools.pack_end(widget, False, False, 0)
        self.pack_start(tools, False, False, 0)

        self.model = Gtk.ListStore(*self.COLUMNS)
        self.view = Gtk.TreeView(model=self.model, enable_search=False, activate_on_single_click=False)
        self.view.set_grid_lines(Gtk.TreeViewGridLines.HORIZONTAL)
        self.view.get_selection().connect("changed", lambda *_: self.update_buttons())
        self.view.connect("button-press-event", self.pressed)
        self.view.connect("key-press-event", self.key_pressed)
        self.view.connect("row-activated", lambda view, path, column: self.activate(path, column))
        self.columns = {}
        name = Gtk.TreeViewColumn()
        icon = GlyphRenderer(owner.theme)
        name.pack_start(icon, False)
        text = Gtk.CellRendererText(weight=Pango.Weight.SEMIBOLD, ellipsize=Pango.EllipsizeMode.END, ypad=12)
        name.pack_start(text, True)
        name.add_attribute(text, "text", 1)
        self._column(name, "name", 103, 30)
        for key, index, minimum, weight in (("address", 2, 105, 34), ("user", 3, 103, 23), ("port", 4, 64, 0), ("tab", 5, 79, 22)):
            renderer = Gtk.CellRendererText(ellipsize=Pango.EllipsizeMode.END, ypad=12)
            column = Gtk.TreeViewColumn("", renderer, text=index)
            self._column(column, key, minimum, weight)
        for key, index, minimum, icon_name in (("files", 6, 100, "folder"), ("action", 7, 159, "terminal")):
            renderer = PillRenderer(owner.theme, minimum)
            renderer.set_property("icon", icon_name)
            column = Gtk.TreeViewColumn("", renderer, text=index)
            self._column(column, key, minimum, 0)
        for key in ("name", "address"):
            self.columns[key].set_clickable(True)
            self.columns[key].connect("clicked", lambda _, key=key: owner.sort_by(key))
            self.columns[key].get_widget().set_tooltip_text("Нажмите для сортировки")
        scroll = Gtk.ScrolledWindow(vexpand=True)
        scroll.add(self.view)
        frame = Gtk.Overlay()
        frame.add(scroll)
        mark(frame, "sshm-frame")
        self.empty = Gtk.Label(wrap=True, justify=Gtk.Justification.CENTER, margin=16, no_show_all=True)
        mark(self.empty, "sshm-muted")
        frame.add_overlay(self.empty)
        frame.set_overlay_pass_through(self.empty, True)
        self.pack_start(frame, True, True, 0)

    def _column(self, column, key, minimum, weight):
        header = Gtk.Label()
        header.show()
        column.set_widget(header)
        column.set_min_width(minimum)
        column.set_resizable(weight > 0)
        column.set_expand(weight > 0)
        self.columns[key] = column
        self.view.append_column(column)

    # --- rendering -----------------------------------------------------------------------------
    def set_protocol(self, protocol):
        for kind, tab in self.protocol_tabs.items():
            context = tab.get_style_context()
            (context.add_class if kind == protocol else context.remove_class)("selected")
        ssh = protocol == "ssh"
        titles = {"name": "Псевдоним" if ssh else "Название", "address": "Адрес", "user": "Пользователь", "port": "Порт",
                  "tab": "Вкладка", "files": "SFTP", "action": "Терминал" if ssh else "Рабочий стол"}
        for key, title in titles.items():
            self.columns[key].get_widget().set_text(title)
        self.columns["files"].set_visible(ssh)
        self.search.set_text("")

    def render_groups(self):
        owner = self.owner
        for widget in self.group_strip.get_children():
            self.group_strip.remove(widget)
        for group in [{"id": "", "name": ALL_NAME, "color": ""}, *owner.settings["groups"]]:
            tab = Gtk.Button()
            label = Gtk.Label()
            dot = f'<span foreground="{group["color"]}">●</span>  ' if group["color"] else ""
            label.set_markup(dot + GLib.markup_escape_text(group["name"]))
            label.set_max_width_chars(28)
            label.set_ellipsize(Pango.EllipsizeMode.END)
            tab.add(label)
            mark(tab, "sshm-button", "tab")
            if group["id"] == owner.active_group:
                mark(tab, "selected")
            tab.connect("clicked", lambda _, g=group: owner.select_group(g["id"]))
            tab.connect("button-press-event", lambda widget, event, g=group:
                        self.group_menu(widget, event, g) if event.button == 3 else False)
            tab.connect("popup-menu", lambda widget, g=group: self.group_menu(widget, None, g))
            if group["id"]:
                self.enable_group_drag(tab, group)
            self.group_strip.pack_start(tab, False, False, 0)
        self.group_strip.show_all()

    def group_menu(self, widget, event, group):
        owner = self.owner
        startup = Item("Открывать при запуске", lambda: owner.default_group(group["id"]),
                       checked=owner.settings["default"] == group["id"])
        if not group["id"]:
            return menu_at(widget, event, [startup])
        groups = owner.settings["groups"]
        index = groups.index(group)
        colors = [Item("Без метки", lambda: owner.mark_group(group, ""), checked=not group["color"])]
        colors += [Item(name, lambda value=value: owner.mark_group(group, value),
                        checked=group["color"].lower() == value.lower()) for name, value in TAB_COLORS]
        return menu_at(widget, event, [
            Item("Переименовать вкладку", lambda: owner.rename_group(group), icon="edit"),
            Item("Цветовая метка", icon="dot", children=colors),
            Item("Переместить влево", lambda: owner.shift_group(group, -1), icon="move_left", enabled=index > 0),
            Item("Переместить вправо", lambda: owner.shift_group(group, 1), icon="move", enabled=index < len(groups) - 1),
            startup,
            None,
            Item("Удалить вкладку", lambda: owner.delete_group(group), icon="delete", danger=True),
        ])

    def enable_group_drag(self, widget, group):
        owner = self.owner
        types = [Gtk.TargetEntry.new("application/x-ssh-manager-tab", Gtk.TargetFlags.SAME_APP, 0)]
        widget.drag_source_set(Gdk.ModifierType.BUTTON1_MASK, types, Gdk.DragAction.MOVE)
        widget.drag_dest_set(Gtk.DestDefaults.ALL, types, Gdk.DragAction.MOVE)
        widget.connect("drag-data-get", lambda _, context, data, code, time: data.set(data.get_target(), 8, group["id"].encode()))

        def drop(target, context, x, y, data, code, time):
            groups = owner.settings["groups"]
            moved = next((item for item in groups if item["id"].encode() == data.get_data()), None)
            if not moved or moved is group:
                Gtk.drag_finish(context, False, False, time)
                return
            groups.remove(moved)
            groups.insert(groups.index(group) + (x >= target.get_allocated_width() / 2), moved)
            Gtk.drag_finish(context, True, False, time)
            owner.save_settings()
            self.render_groups()

        widget.connect("drag-data-received", drop)

    def render(self, keep=None):
        owner = self.owner
        keep = keep if keep is not None else self.selected()
        self.model.clear()
        self.rows = []
        ssh = owner.protocol == "ssh"
        order = owner.settings["sort"].get(owner.sort_key, "original")
        for key, column in self.columns.items():
            column.set_sort_indicator(False)
        if order != "original":
            column = self.columns["address" if order.startswith("address") else "name"]
            column.set_sort_indicator(True)
            column.set_sort_order(Gtk.SortType.DESCENDING if order.endswith("_desc") else Gtk.SortType.ASCENDING)
        if ssh and owner.document is None:
            self.show_empty("Не удалось прочитать SSH config. Нажмите «Обновить», чтобы повторить попытку.")
            owner.update_status(0, 0)
            return
        everything = owner.catalog_profiles()
        query = self.search.get_text().strip().casefold()
        names = {group["id"]: group["name"] for group in owner.settings["groups"]}
        matches = []
        for profile in everything:
            tab = names.get(owner.group_of(profile), "")
            if ssh:
                haystack = " ".join((profile.alias, profile.host, profile.user, tab))
            else:
                haystack = " ".join((profile.name, profile.address, profile.account, tab))
            if query in haystack.casefold():
                matches.append((profile, tab))
        selected_path = None
        for profile, tab in ordered_with_tabs(matches, order):
            sessions = owner.session_count(profile)
            action = f"К сеансу · {sessions}" if sessions else "Подключиться"
            if ssh:
                values = (profile, profile.alias, profile.host or "—", profile.user or "—", profile.port or "—",
                          tab or "—", "Файлы", action, "server")
            else:
                values = (profile, profile.name, profile.host, profile.account or "—", str(profile.port), tab or "—",
                          "", action, "server")
            iterator = self.model.append(values)
            if keep is not None and same_profile(profile, keep):
                selected_path = self.model.get_path(iterator)
        if selected_path is not None:
            self.view.get_selection().select_path(selected_path)
        if matches:
            self.empty.hide()
        elif query:
            self.show_empty("По вашему запросу ничего не найдено.")
        elif not ssh:
            self.show_empty("Добавьте RDP-подключение или импортируйте профиль .rdp.")
        elif owner.active_group:
            self.show_empty("Добавьте сервер или перенесите сюда подключение из «Все серверы».")
        else:
            self.show_empty("Добавьте сервер, чтобы создать первое подключение.")
        owner.update_status(len(matches), len(everything), bool(query))
        self.update_buttons()

    def show_empty(self, text):
        self.empty.set_text(text)
        self.empty.show()

    def selected(self):
        model, item = self.view.get_selection().get_selected()
        return model[item][0] if item is not None else None

    def update_buttons(self):
        chosen = self.selected() is not None
        self.edit.set_sensitive(chosen)
        self.delete.set_sensitive(chosen)

    # --- input -------------------------------------------------------------------------------
    def pressed(self, view, event):
        hit = view.get_path_at_pos(int(event.x), int(event.y))
        if hit is None:
            return False
        path, column, _, _ = hit
        view.get_selection().select_path(path)
        profile = self.model[path][0]
        if event.button == 3 and event.type == Gdk.EventType.BUTTON_PRESS:
            self.owner.profile_menu(view, event, profile)
            return True
        if event.button == 1 and event.type == Gdk.EventType.BUTTON_PRESS and self.run_pill(column, profile):
            return True
        if event.button == 1 and event.type == Gdk.EventType._2BUTTON_PRESS and column not in (
                self.columns["files"], self.columns["action"]):
            self.owner.edit_catalog_entry(profile)
            return True
        return False

    def run_pill(self, column, profile):
        if column is self.columns["files"] and not isinstance(profile, DesktopProfile):
            self.owner.open_files(profile)
            return True
        if column is self.columns["action"]:
            self.owner.connect_profile(profile)
            return True
        return False

    def activate(self, path, column):
        profile = self.model[path][0]
        if not self.run_pill(column, profile):
            self.owner.edit_catalog_entry(profile)

    def key_pressed(self, view, event):
        profile = self.selected()
        if profile is None:
            return False
        if event.keyval == Gdk.KEY_Menu or (event.keyval == Gdk.KEY_F10 and event.state & Gdk.ModifierType.SHIFT_MASK):
            self.owner.profile_menu(view, None, profile)
            return True
        if event.keyval in (Gdk.KEY_space, Gdk.KEY_Return, Gdk.KEY_KP_Enter):
            _, column = view.get_cursor()
            if column is not None and self.run_pill(column, profile):
                return True
        if event.keyval == Gdk.KEY_Delete:
            self.owner.delete_entry(profile)
            return True
        return False


def same_profile(first, second):
    if isinstance(first, DesktopProfile) or isinstance(second, DesktopProfile):
        return getattr(first, "id", None) == getattr(second, "id", None)
    return first.alias.lower() == second.alias.lower()


def ordered_with_tabs(matches, order):
    tabs = {id(profile): tab for profile, tab in matches}
    return [(profile, tabs[id(profile)]) for profile in ordered([profile for profile, _ in matches], order)]


__all__ = ["CatalogPage", "PillRenderer", "next_order", "ALL_NAME", "UNGROUPED_NAME", "build_menu"]
