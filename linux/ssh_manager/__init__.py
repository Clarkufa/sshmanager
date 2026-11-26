VERSION = "1.12.0"

try:
    import gi

    for _name, _version in (("Gtk", "3.0"), ("Gdk", "3.0"), ("Vte", "2.91"), ("PangoCairo", "1.0"), ("GdkX11", "3.0")):
        try:
            gi.require_version(_name, _version)
        except ValueError:
            pass
except ImportError:  # unit tests of the pure modules run without PyGObject
    pass
