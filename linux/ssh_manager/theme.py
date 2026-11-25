"""Colours, fonts and widget styles shared with the Windows app (docs/ui-spec.md §1)."""

from gi.repository import Gdk, Gtk

# Same tokens and values as src/Theme.cs.
PALETTES = {
    False: dict(canvas="#F6F8FB", surface="#FFFFFF", ink="#102235", muted="#485C6D", line="#DFE6EE",
                selection="#EAF2FA", mint="#E7F6F2", teal="#00645C", danger="#A42435", primary="#00645C",
                primary_hover="#00786B", disabled="#7E8F9E"),
    True: dict(canvas="#171F2B", surface="#202B3A", ink="#E9EFF8", muted="#AABACF", line="#3B4C60",
               selection="#2A4359", mint="#21413E", teal="#85E1CC", danger="#FFA3AC", primary="#146859",
               primary_hover="#197565", disabled="#899BB0"),
}

# Tab colour marks in the Windows order (src/TabColor.cs). Stored as the hex value.
TAB_COLORS = (
    ("Синяя", "#2563EB"), ("Зелёная", "#20804E"), ("Бирюзовая", "#0E7B82"), ("Янтарная", "#B36306"),
    ("Красная", "#C52E49"), ("Фиолетовая", "#7E4CC8"), ("Серая", "#64748B"),
)

TERMINAL = dict(background="#111A27", foreground="#E1E8F1", selection="#2B4F62", cursor="#90E1C7",
                empty="#DCDCDC")

ICONS = dict(plus="list-add-symbolic", refresh="view-refresh-symbolic", edit="document-edit-symbolic",
             delete="user-trash-symbolic", terminal="utilities-terminal-symbolic", server="network-server-symbolic",
             folder="folder-symbolic", move="go-next-symbolic", move_left="go-previous-symbolic",
             copy="edit-copy-symbolic", key="dialog-password-symbolic", browse="system-search-symbolic",
             moon="weather-clear-night-symbolic", sun="weather-clear-symbolic", desktop="video-display-symbolic", dot="media-record-symbolic")


def css(dark):
    c = PALETTES[dark]
    t = TERMINAL
    return f"""
window.sshm, window.sshm .sshm-page, dialog.sshm, messagedialog.sshm {{
    background-color: {c['canvas']}; color: {c['ink']};
}}
.sshm label {{ color: {c['ink']}; }}
.sshm .sshm-title {{ font-size: 21pt; font-weight: 600; }}
.sshm .sshm-emphasis {{ font-weight: 600; }}
.sshm label.sshm-muted, .sshm .sshm-muted {{ color: {c['muted']}; }}
.sshm label.sshm-danger {{ color: {c['danger']}; }}

.sshm button.sshm-button {{
    min-height: 32px; padding: 0 12px; border-radius: 7px; font-weight: 600;
    background-image: none; box-shadow: none; text-shadow: none; -gtk-icon-shadow: none;
    border: 1px solid transparent; outline-color: {c['teal']};
}}
.sshm button.sshm-button label {{ color: inherit; }}
.sshm button.sshm-button .sshm-glyph {{ color: {c['teal']}; }}
.sshm button.outline {{ background-color: {c['surface']}; color: {c['ink']}; border-color: {c['line']}; }}
.sshm button.outline:hover {{ background-color: {c['selection']}; }}
.sshm button.primary {{ background-color: {c['primary']}; color: #FFFFFF; }}
.sshm button.primary .sshm-glyph {{ color: #FFFFFF; }}
.sshm button.primary:hover {{ background-color: {c['primary_hover']}; }}
.sshm button.primary:disabled {{ background-color: {c['line']}; color: {c['disabled']}; }}
.sshm button.quiet {{ background-color: transparent; color: {c['ink']}; }}
.sshm button.quiet:hover {{ background-color: {c['selection']}; }}
.sshm button.tab {{ background-color: transparent; color: {c['muted']}; min-height: 28px; }}
.sshm button.tab:hover {{ background-color: {c['selection']}; }}
.sshm button.tab.selected {{ background-color: {c['mint']}; color: {c['teal']}; }}
.sshm button.danger, .sshm button.danger .sshm-glyph {{ color: {c['danger']}; }}
.sshm button.sshm-button:disabled {{ color: {c['disabled']}; }}
.sshm button.sshm-button:disabled .sshm-glyph {{ color: {c['disabled']}; }}
.sshm button.sshm-button:focus {{ border-color: {c['teal']}; }}

.sshm entry {{
    background-color: {c['surface']}; color: {c['ink']}; border: 1px solid {c['line']};
    border-radius: 7px; min-height: 32px; box-shadow: none; background-image: none;
}}
.sshm entry:focus {{ border-color: {c['teal']}; }}
.sshm entry image {{ color: {c['muted']}; }}

.sshm .sshm-frame {{ border: 1px solid {c['line']}; background-color: {c['surface']}; }}
.sshm treeview.view {{ background-color: {c['surface']}; color: {c['ink']}; }}
.sshm treeview.view:selected {{ background-color: {c['selection']}; color: {c['ink']}; }}
.sshm treeview.view header button {{
    background-color: {c['canvas']}; background-image: none; color: {c['muted']}; box-shadow: none;
    border: none; border-bottom: 1px solid {c['line']}; min-height: 35px; padding: 0 8px;
}}
.sshm treeview.view header button label {{ color: {c['muted']}; }}
.sshm list, .sshm list row {{ background-color: {c['surface']}; color: {c['ink']}; }}
.sshm list row:selected {{ background-color: {c['selection']}; }}
.sshm list row:selected label {{ color: {c['ink']}; }}

.sshm menu, .sshm .menu, menu.sshm-menu {{
    background-color: {c['surface']}; color: {c['ink']}; border: 1px solid {c['line']};
}}
menu.sshm-menu menuitem {{ color: {c['ink']}; padding: 6px 10px; }}
menu.sshm-menu menuitem:hover {{ background-color: {c['selection']}; color: {c['ink']}; }}
menu.sshm-menu menuitem .sshm-glyph {{ color: {c['teal']}; }}
menu.sshm-menu menuitem:disabled .sshm-glyph, menu.sshm-menu menuitem:disabled label {{ color: {c['disabled']}; }}
menu.sshm-menu menuitem.danger, menu.sshm-menu menuitem.danger label,
menu.sshm-menu menuitem.danger .sshm-glyph {{ color: {c['danger']}; }}
menu.sshm-menu separator {{ background-color: {c['line']}; }}

.sshm .sshm-terminal-area {{ background-color: {t['background']}; }}
.sshm label.sshm-terminal-text {{ color: {t['empty']}; }}
.sshm .sshm-overlay {{ background-color: {t['background']}; }}
.sshm label.sshm-overlay-title {{ color: {t['empty']}; font-size: 21pt; font-weight: 600; }}
.sshm label.sshm-overlay-detail {{ color: #A9A9A9; }}
vte-terminal {{ padding: 10px; }}
""".encode()


class Theme:
    """Applies the palette to the whole screen and switches it between light and dark."""

    def __init__(self):
        self.provider = Gtk.CssProvider()
        self.dark = None
        screen = Gdk.Screen.get_default()
        if screen is not None:
            Gtk.StyleContext.add_provider_for_screen(screen, self.provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)

    def apply(self, dark):
        dark = bool(dark)
        if dark == self.dark:
            return
        self.dark = dark
        settings = Gtk.Settings.get_default()
        if settings is not None:
            settings.set_property("gtk-application-prefer-dark-theme", dark)
        self.provider.load_from_data(css(dark))

    def color(self, token):
        return PALETTES[bool(self.dark)][token]


def rgba(value):
    color = Gdk.RGBA()
    color.parse(value)
    return color


def mark(widget, *classes):
    """Adds the app's style classes; every top-level window carries `sshm` so the rules apply."""
    context = widget.get_style_context()
    for name in classes:
        context.add_class(name)
    return widget
