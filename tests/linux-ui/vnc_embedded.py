"""Embedded VNC check: the Linux app shows the tests/vnc server in its window through gtk-vnc.

Run by run-vnc-check.sh: the VNC bench container is reachable as HOST on the Docker network, and the host side
restarts it when this script creates build/vnc-shared/restart.
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
# ssh asks the launcher for the saved SSH password (AskPass): point it at the real launcher, not this script.
sys.argv[0] = str(ROOT / "linux" / "ssh-manager")

from ssh_manager import app, sessions, update_ui  # noqa: E402
from ssh_manager.app import Application, Config, Gtk  # noqa: E402
from ssh_manager.auth import account_for  # noqa: E402
from ssh_manager.sessions import VncSession, VncWindow  # noqa: E402
from gi.repository import Gdk, GLib  # noqa: E402

SHARED = ROOT / "build" / "vnc-shared"
SHARED.mkdir(parents=True, exist_ok=True)
OUT = ROOT / "build" / "linux-screens"
OUT.mkdir(parents=True, exist_ok=True)
HOST = sys.argv[1] if len(sys.argv) > 1 else "sshm-vnc"
VNC_PASSWORD = "VncTest1"  # test bench only (tests/vnc)
SSH_PASSWORD = "Vnc-Test-2026"


class MemoryVault:
    items = {}

    def lookup(self, account):
        return self.items.get(account.key) if account else None

    def save(self, account, password):
        if password:
            self.items[account.key] = password
        else:
            self.items.pop(account.key, None)


prompts = []


def answer_prompt(owner, parent, profile, wrong=False):
    prompts.append(wrong)
    return VNC_PASSWORD


app.Vault = MemoryVault
sessions.ask_vnc_password = answer_prompt
update_ui.UpdateController.check = lambda self, manual=False: None
home = Path(tempfile.mkdtemp())
(home / "ssh").mkdir()
(home / "ssh/config").write_text(f"Host bench\n    HostName {HOST}\n    User tester\n    Port 22\n"
                                 "    StrictHostKeyChecking no\n    UserKnownHostsFile /dev/null\n")
direct = {"id": "a" * 32, "name": "vnc", "host": HOST, "port": 5901}
tunnelled = {"id": "b" * 32, "name": "vnc-ssh", "host": "127.0.0.1", "port": 5901, "via": "bench"}
(home / "state").mkdir()
(home / "state/settings.json").write_text(json.dumps({"vnc": [direct, tunnelled], "dark": False}))

application = Application(Config(home / "ssh/config", home / "state"), isolated=True)
application.register(None)
application.activate()
window = next(item for item in application.get_windows() if isinstance(item, app.Window))
window.move(0, 0)
window.resize(1000, 640)
profiles = {profile.name: profile for profile in window.vnc_profiles()}
# A wrong saved password first: the app must ask again and connect with the new one.
MemoryVault.items[profiles["vnc"].secret.key] = "WrongPw1"
MemoryVault.items[profiles["vnc-ssh"].secret.key] = VNC_PASSWORD
MemoryVault.items[account_for(window.ssh_profile("bench")).key] = SSH_PASSWORD


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


failures = []


def check(label, condition):
    print(("ok    " if condition else "FAIL  ") + label, flush=True)
    if not condition:
        failures.append(label)


def remote(session):
    display = session.display
    return (display.get_width(), display.get_height()) if display is not None else (0, 0)


def follows(session, seconds=15):
    """«По размеру окна»: the server's desktop takes the size of the widget (SetDesktopSize)."""
    def matches():
        if session.display is None:
            return False
        allocation = session.display.get_allocation()
        width, height = remote(session)
        return abs(width - allocation.width) <= 8 and abs(height - allocation.height) <= 8
    return wait(matches, seconds)


def shot(name):
    root = Gdk.get_default_root_window()
    Gdk.pixbuf_get_from_window(root, 0, 0, root.get_width(), root.get_height()).savev(str(OUT / f"{name}.png"), "png", [], [])


def report(label, session):
    allocation = session.display.get_allocation() if session.display is not None else None
    size = f"{allocation.width}x{allocation.height}" if allocation else "-"
    width, height = remote(session)
    print(f"{label}: widget {size} -> remote {width}x{height} · status {session.status}", flush=True)


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
    window.select_protocol("vnc")
    shot("30-vnc-catalog")
    window.connect_profile(profiles["vnc"])
    session = next(item for item in window.sessions if isinstance(item, VncSession))
    check("connected to the VNC bench", wait(lambda: session.status == "подключён", 30))
    check("a wrong saved password asks again", prompts == [True])
    check("the remote desktop follows the window", follows(session))
    report("window", session)
    pump(1)
    shot("31-vnc-embedded")
    previous_size = remote(session)
    window.resize(1200, 760)
    resized = wait(lambda: remote(session) != previous_size, 15)
    check("the remote desktop follows a resize", resized and follows(session))
    report("resized", session)
    window.enter_full_screen(session)
    window.resize(1280, 800)  # no window manager under Xvfb: make the window as large as the screen
    check("full screen shows the bar and grabs the keyboard",
          window.full_session is session and session.bar.get_visible() and session.display.get_keyboard_grab())
    pump(1)
    shot("32-vnc-full-screen")
    window.leave_full_screen()
    window.resize(1000, 640)
    check("leaving full screen gives the keyboard back", not session.display.get_keyboard_grab())

    (SHARED / "restart").write_text("1")
    check("a server restart ends the session", wait(lambda: not session.live, 30))
    check(f"overlay explains it: {session.ended_title.get_text()} / {session.ended_detail.get_text()!r}",
          session.ended_title.get_text() in ("Сеанс завершён", "Подключение прервано") and session.pages.get_visible_child_name() == "ended")
    shot("33-vnc-ended")
    check("the bench is back", wait(lambda: not (SHARED / "restart").exists(), 40))
    pump(4)
    session.restart()
    check("«Переподключить» connects again", wait(lambda: session.status == "подключён", 30))

    window.detach_session(session)
    check("«В окне» closes the embedded session", not any(isinstance(item, VncSession) for item in window.sessions))
    detached = next((item for item in Gtk.Window.list_toplevels() if isinstance(item, VncWindow)), None)
    check("«В окне» opens a separate window", detached is not None)
    if detached is not None:
        check("the separate window connects", wait(lambda: detached.session.status == "подключён", 30))
        shot("34-vnc-separate-window")
        detached.destroy()
        check("closing the window stops its session", detached.session.closed and detached.session.display is None)

    window.connect_profile(profiles["vnc-ssh"])
    tunnelled = next(item for item in window.sessions if isinstance(item, VncSession))
    check("connected through the SSH tunnel", wait(lambda: tunnelled.status == "подключён", 40))
    check("the details name the SSH server", tunnelled.address.endswith("через bench"))
    process = tunnelled.tunnel.process if tunnelled.tunnel else None
    shot("35-vnc-ssh")
    window.close_session(tunnelled, confirm_close=False)
    check("closing the session stops the tunnel", process is not None and wait(lambda: process.poll() is not None, 10))
    check("no password prompt for the saved one", prompts == [True])


GLib.timeout_add(800, scenario)
application.run([])
(SHARED / "done").write_text("1")
print("failures:", len(failures), flush=True)
sys.exit(1 if failures else 0)
