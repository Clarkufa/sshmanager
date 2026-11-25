"""Embedded RDP check: the Linux app runs FreeRDP inside its window against the tests/xrdp server.

The xrdp container must write its current resolution to /work/build/rdp-shared/size (see run-rdp-check.sh).
"""
import json
import os
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "linux"))
os.environ.setdefault("GSETTINGS_BACKEND", "memory")

from ssh_manager import app, update_ui  # noqa: E402
from ssh_manager.app import Application, Config, Gtk  # noqa: E402
from ssh_manager.sessions import RdpSession  # noqa: E402
from gi.repository import Gdk, GLib  # noqa: E402

SIZE = ROOT / "build" / "rdp-shared" / "size"
OUT = ROOT / "build" / "linux-screens"
OUT.mkdir(parents=True, exist_ok=True)
HOST = sys.argv[1] if len(sys.argv) > 1 else "sshm-xrdp"


class MemoryVault:
    items = {}

    def lookup(self, account):
        return self.items.get(account.key) if account else None

    def save(self, account, password):
        self.items[account.key] = password


app.Vault = MemoryVault
update_ui.UpdateController.check = lambda self, manual=False: None
home = Path(tempfile.mkdtemp())
(home / "ssh").mkdir()
(home / "ssh/config").write_text("")
profile = {"id": "e" * 32, "name": "xrdp", "host": HOST, "port": 3389, "user": "tester", "domain": "", "group": "",
           "width": 0, "height": 0, "clipboard": False, "fullscreen": False}
(home / "state").mkdir()
(home / "state/settings.json").write_text(json.dumps({"desktops": [profile], "dark": False}))

application = Application(Config(home / "ssh/config", home / "state"), isolated=True)
application.register(None)
application.activate()
window = next(item for item in application.get_windows() if isinstance(item, app.Window))
window.move(0, 0)
window.resize(1000, 640)
desktop = window.desktop_profiles()[0]
MemoryVault.items[desktop.secret.key] = "Rdp-Test-2026"
results = []


def wait(predicate, seconds):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        while Gtk.events_pending():
            Gtk.main_iteration_do(False)
        if predicate():
            return True
        time.sleep(0.05)
    return predicate()


def pump(seconds):
    wait(lambda: False, seconds)


def remote():
    try:
        return SIZE.read_text().strip()
    except OSError:
        return "?"


def shot(name):
    root = Gdk.get_default_root_window()
    Gdk.pixbuf_get_from_window(root, 0, 0, root.get_width(), root.get_height()).savev(str(OUT / f"{name}.png"), "png", [], [])


def report(label, session):
    area = session.area.get_allocation()
    line = f"{label}: area {area.width}x{area.height} -> remote {remote()} · status {session.status}"
    print(line, flush=True)
    results.append(line)


def scenario():
    window.select_protocol("rdp")
    window.connect_profile(desktop)
    session = next(item for item in window.sessions if isinstance(item, RdpSession))
    print("embedded:", session.embedded, flush=True)
    if not wait(lambda: session.status == "подключён" or not session.live, 60):
        print("FAIL: not connected; status", session.status, flush=True)
    pump(6)
    report("window", session)
    shot("20-rdp-embedded")
    window.resize(1200, 760)
    pump(6)
    report("resized", session)
    window.enter_full_screen(session)
    window.resize(1280, 800)  # no window manager under Xvfb: make the window as large as the screen
    pump(6)
    report("full screen", session)
    shot("21-rdp-full-screen")
    window.leave_full_screen()
    window.resize(1000, 640)
    pump(6)
    report("left full screen", session)
    window.detach_session(session)
    detached = next(item for item in window.sessions if isinstance(item, RdpSession))
    wait(lambda: detached.status == "подключён" or not detached.live, 30)
    pump(4)
    print("separate window:", detached.embedded is False, detached.status, "· sessions", len(window.sessions), flush=True)
    shot("23-rdp-separate-window")
    import os as _os
    import signal as _signal
    _os.kill(detached.pid, _signal.SIGKILL)
    wait(lambda: not detached.live, 10)
    pump(1)
    print("after kill:", detached.status, "|", detached.ended_title.get_text(), flush=True)
    shot("22-rdp-ended")
    window.close_session(detached, confirm_close=False)
    application.quit()
    return False


GLib.timeout_add(800, scenario)
application.run([])
