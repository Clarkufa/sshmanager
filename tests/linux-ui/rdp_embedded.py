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


def remote_size():
    parts = remote().replace(" ", "").split("x")
    try:
        return int(parts[0]), int(parts[1])
    except (ValueError, IndexError):
        return None


failures = []


def check(label, condition):
    print(("ok    " if condition else "FAIL  ") + label, flush=True)
    if not condition:
        failures.append(label)


def follows(session, seconds=15):
    """The remote desktop takes the size of the area it is embedded in (dynamic resolution)."""
    def matches():
        area = session.area.get_allocation()
        size = remote_size()
        return size is not None and abs(size[0] - area.width) <= 8 and abs(size[1] - area.height) <= 8
    return wait(matches, seconds)


def area_changes(session, previous, seconds=8):
    """Waits for the window's new size to reach the embedded area before comparing resolutions."""
    def changed():
        area = session.area.get_allocation()
        return (area.width, area.height) != previous
    return wait(changed, seconds)


def area(session):
    allocation = session.area.get_allocation()
    return allocation.width, allocation.height


def shot(name):
    root = Gdk.get_default_root_window()
    Gdk.pixbuf_get_from_window(root, 0, 0, root.get_width(), root.get_height()).savev(str(OUT / f"{name}.png"), "png", [], [])


def report(label, session):
    area = session.area.get_allocation()
    print(f"{label}: area {area.width}x{area.height} -> remote {remote()} · status {session.status}", flush=True)


def scenario():
    try:
        steps()
    except Exception as error:  # a broken step must fail the check, not hang it
        import traceback
        traceback.print_exc()
        failures.append(f"exception: {error}")
    application.quit()
    return False


def steps():
    window.select_protocol("rdp")
    window.connect_profile(desktop)
    session = next(item for item in window.sessions if isinstance(item, RdpSession))
    check("RDP opens embedded under X11", session.embedded)
    wait(lambda: session.status == "подключён" or not session.live, 60)
    if session.status == "отключён (код 145)":
        # Give a cancelled initial connection one retry on the temporary xrdp server.
        print(f"FreeRDP cancelled the first connection: {session.ended_detail.get_text()!r}", flush=True)
        session.restart()
        wait(lambda: session.status == "подключён" or not session.live, 60)
    check("connected to xrdp", session.status == "подключён")
    check("remote desktop matches the window", follows(session))
    report("window", session)
    shot("20-rdp-embedded")
    before = area(session)
    window.resize(1200, 760)
    check("remote desktop follows a resize", area_changes(session, before) and follows(session))
    report("resized", session)
    before = area(session)
    window.enter_full_screen(session)
    window.resize(1280, 800)  # no window manager under Xvfb: make the window as large as the screen
    check("full screen uses the whole screen",
          area_changes(session, before) and follows(session) and (remote_size() or (0, 0))[0] >= 1270)
    report("full screen", session)
    shot("21-rdp-full-screen")
    before = area(session)
    window.leave_full_screen()
    window.resize(1000, 640)
    check("leaving full screen shrinks it back",
          area_changes(session, before) and follows(session) and (remote_size() or (9999, 0))[0] < 1100)
    report("left full screen", session)
    window.detach_session(session)
    detached = next(item for item in window.sessions if isinstance(item, RdpSession))
    check("«В окне» reconnects in a separate window",
          wait(lambda: detached.status == "подключён", 30) and detached.embedded is False and len(window.sessions) == 1)
    shot("23-rdp-separate-window")
    import signal
    os.kill(detached.pid, signal.SIGKILL)
    check("a killed client ends the session", wait(lambda: not detached.live, 10))
    pump(1)
    check(f"overlay explains it: {detached.ended_title.get_text()}", bool(detached.ended_title.get_text()))
    shot("22-rdp-ended")
    window.close_session(detached, confirm_close=False)


GLib.timeout_add(800, scenario)
application.run([])
print("failures:", len(failures), flush=True)
sys.exit(1 if failures else 0)
