"""Line icons drawn exactly like the Windows app's glyphs (src/Theme.cs Icon): 16×16, 1.5 px, round caps.

Drawing them ourselves keeps the look identical across distributions and icon themes."""

import math

from gi.repository import Gdk, Gtk

from .theme import mark


def _trace(cr, *points):
    cr.move_to(points[0], points[1])
    for index in range(2, len(points), 2):
        cr.line_to(points[index], points[index + 1])
    cr.stroke()


def _line(cr, x1, y1, x2, y2):
    cr.move_to(x1, y1)
    cr.line_to(x2, y2)
    cr.stroke()


def _ellipse(cr, x, y, width, height):
    cr.save()
    cr.translate(x + width / 2, y + height / 2)
    cr.scale(width / 2, height / 2)
    cr.arc(0, 0, 1, 0, 2 * math.pi)
    cr.restore()


def draw(cr, name, x, y, color):
    """Draws glyph `name` with its top-left corner at (x, y) in `color` (Gdk.RGBA)."""
    cr.save()
    cr.translate(x, y)
    Gdk.cairo_set_source_rgba(cr, color)
    cr.set_line_width(1.5)
    cr.set_line_cap(1)  # ROUND
    cr.set_line_join(1)  # ROUND
    if name == "plus":
        _line(cr, 8, 3, 8, 13)
        _line(cr, 3, 8, 13, 8)
    elif name == "refresh":
        cr.arc(8, 8, 6, math.radians(25), math.radians(335))
        cr.stroke()
        _trace(cr, 14, 2, 14, 6, 10, 6)
    elif name == "edit":
        _trace(cr, 3, 10, 11, 2, 14, 5, 6, 13, 2, 14, 3, 10)
        _line(cr, 9, 4, 12, 7)
    elif name == "delete":
        _trace(cr, 4, 6, 5, 14, 11, 14, 12, 6)
        _line(cr, 2, 4, 14, 4)
        _trace(cr, 6, 4, 6, 2, 10, 2, 10, 4)
    elif name == "terminal":
        _trace(cr, 3, 4, 7, 8, 3, 12)
        _line(cr, 9, 12, 14, 12)
    elif name in ("server", "desktop"):
        cr.rectangle(2, 2, 12, 5)
        cr.rectangle(2, 9, 12, 5)
        cr.stroke()
        _line(cr, 10, 4.5, 11, 4.5)
        _line(cr, 10, 11.5, 11, 11.5)
    elif name == "folder":
        _trace(cr, 2, 13, 2, 3, 6, 3, 8, 5, 14, 5, 14, 13, 2, 13)
    elif name == "move":
        _line(cr, 2, 8, 14, 8)
        _trace(cr, 10, 4, 14, 8, 10, 12)
    elif name == "move_left":
        _line(cr, 2, 8, 14, 8)
        _trace(cr, 6, 4, 2, 8, 6, 12)
    elif name == "copy":
        cr.rectangle(5, 5, 9, 9)
        cr.stroke()
        _trace(cr, 2, 11, 2, 2, 11, 2)
    elif name == "key":
        _ellipse(cr, 2, 2, 7, 7)
        cr.stroke()
        _trace(cr, 8, 8, 14, 14, 14, 11)
    elif name == "browse":
        _ellipse(cr, 2, 2, 8, 8)
        cr.stroke()
        _line(cr, 9, 9, 14, 14)
    elif name == "moon":
        cr.move_to(8, 2)
        cr.curve_to(1, 2, 0, 13, 8, 14)
        cr.curve_to(11, 14, 13, 12, 14, 9)
        cr.curve_to(9, 12, 5, 7, 8, 2)
        cr.close_path()
        cr.stroke()
    elif name == "sun":
        _ellipse(cr, 5, 5, 6, 6)
        cr.stroke()
        for x1, y1, x2, y2 in ((8, 1, 8, 3), (8, 13, 8, 15), (1, 8, 3, 8), (13, 8, 15, 8),
                               (3, 3, 4, 4), (12, 12, 13, 13), (12, 4, 13, 3), (3, 13, 4, 12)):
            _line(cr, x1, y1, x2, y2)
    elif name == "dot":
        _ellipse(cr, 2, 2, 12, 12)
        cr.fill()
    cr.restore()


class Glyph(Gtk.DrawingArea):
    """A 16×16 glyph that takes its colour from CSS (`color` of .sshm-glyph), so buttons and menus tint it."""

    def __init__(self, name):
        super().__init__()
        self.name = name
        self.set_size_request(16, 16)
        self.set_valign(Gtk.Align.CENTER)
        mark(self, "sshm-glyph")
        self.connect("draw", self._draw)

    def set_name_glyph(self, name):
        self.name = name
        self.queue_draw()

    def _draw(self, widget, cr):
        color = self.get_style_context().get_color(self.get_state_flags())
        draw(cr, self.name, 0, (self.get_allocated_height() - 16) / 2, color)
        return False
