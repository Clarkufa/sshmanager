"""Minimal Xlib access for the embedded RDP session: find and resize FreeRDP's window inside ours."""

import ctypes
import ctypes.util

_lib = None


def _xlib():
    global _lib
    if _lib is None:
        name = ctypes.util.find_library("X11")
        if not name:
            raise OSError("libX11 недоступна")
        lib = ctypes.CDLL(name)
        lib.XOpenDisplay.restype = ctypes.c_void_p
        lib.XOpenDisplay.argtypes = [ctypes.c_char_p]
        lib.XQueryTree.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(ctypes.c_ulong),
                                   ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.POINTER(ctypes.c_ulong)),
                                   ctypes.POINTER(ctypes.c_uint)]
        lib.XResizeWindow.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_uint, ctypes.c_uint]
        lib.XMoveResizeWindow.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_int,
                                          ctypes.c_uint, ctypes.c_uint]
        lib.XSetInputFocus.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
        lib.XFree.argtypes = [ctypes.c_void_p]
        lib.XFlush.argtypes = [ctypes.c_void_p]
        lib.XCloseDisplay.argtypes = [ctypes.c_void_p]
        _lib = lib
    return _lib


class Display:
    """A private connection to the X server; independent of GTK's own connection."""

    def __init__(self):
        self.lib = _xlib()
        self.handle = self.lib.XOpenDisplay(None)
        if not self.handle:
            raise OSError("Не удалось подключиться к X-серверу")

    def children(self, window):
        root, parent = ctypes.c_ulong(), ctypes.c_ulong()
        items, count = ctypes.POINTER(ctypes.c_ulong)(), ctypes.c_uint()
        if not self.lib.XQueryTree(self.handle, window, ctypes.byref(root), ctypes.byref(parent),
                                   ctypes.byref(items), ctypes.byref(count)):
            return []
        try:
            return [items[index] for index in range(count.value)]
        finally:
            if items:
                self.lib.XFree(items)

    def fit_children(self, window, width, height):
        """Gives every child window the full size of `window`; returns how many there are."""
        found = self.children(window)
        for child in found:
            self.lib.XMoveResizeWindow(self.handle, child, 0, 0, max(1, width), max(1, height))
        self.lib.XFlush(self.handle)
        return len(found)

    def focus_child(self, window):
        found = self.children(window)
        if found:
            self.lib.XSetInputFocus(self.handle, found[-1], 2, 0)  # RevertToParent, CurrentTime
            self.lib.XFlush(self.handle)

    def close(self):
        if self.handle:
            self.lib.XCloseDisplay(self.handle)
            self.handle = None
