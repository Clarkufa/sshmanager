"""Screenshots of the Linux app under Xvfb for comparison with the Windows UI.

docker run --rm -v "$PWD":/work sshm-linux-dev sh -c \
  'xvfb-run -s "-screen 0 1280x800x24" dbus-run-session python3 -B tests/linux-ui/screens.py /work/build/linux-screens'
"""
import json
import os
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "linux"))
os.environ.setdefault("GSETTINGS_BACKEND", "memory")

from ssh_manager import app, files, update_ui  # noqa: E402
from ssh_manager.app import Application, Config, Gtk  # noqa: E402
from gi.repository import Gdk, GLib  # noqa: E402

OUT = Path(sys.argv[1] if len(sys.argv) > 1 else "/tmp/linux-screens")
OUT.mkdir(parents=True, exist_ok=True)


class MemoryVault:
    items = {}

    def lookup(self, account):
        return self.items.get(account.key) if account else None

    def save(self, account, password):
        if password:
            self.items[account.key] = password
        else:
            self.items.pop(account.key, None)


app.Vault = MemoryVault


class FakeRemote:
    """SFTP stand-in: an empty remote folder, no network."""

    def __init__(self, *_):
        self.client = self

    def open(self):
        return "/srv"

    def listdir_attr(self, path):
        return []

    def normalize(self, path):
        return path

    def close(self):
        pass


files.Remote = FakeRemote
update_ui.UpdateController.check = lambda self, manual=False: None
app.Window.ssh_arguments = lambda self, profile, account: (
    ["/bin/sh", "-c", "printf 'Connection reset by peer\\r\\n'; exit 255"] if profile.alias == "lab" else
    ["/bin/sh", "-c", f"printf 'Welcome to {profile.alias}\\r\\nLast login: today\\r\\n'; exec /bin/sh"])

home = Path(tempfile.mkdtemp())
(home / "ssh").mkdir()
(home / "ssh/config").write_text("""Host web-prod
    HostName 10.0.0.12
    User deploy
    Port 2222
    ProxyJump bastion
    UserKnownHostsFile ~/.ssh/known_hosts_work
    LocalForward 8080 localhost:80

Host db-1
    HostName 10.0.0.20
    User postgres

Host bastion
    HostName bastion.example.com
    User admin

Host lab
    HostName 192.168.1.50
""")
groups = [{"id": "a" * 32, "name": "Продакшен", "color": "#20804E"},
          {"id": "b" * 32, "name": "Лаборатория", "color": "#2563EB"}]
settings = {"groups": groups, "members": {"web-prod": "a" * 32, "db-1": "a" * 32, "lab": "b" * 32}, "default": "",
            "dark": False, "sort": {}, "favorites": ["ssh:web-prod", "rdp:" + "c" * 32],
            "recent": [{"key": "ssh:db-1", "at": 1}],
            "snippets": [{"name": "Свободное место", "command": "df -h"}, {"name": "Журнал", "command": "journalctl -n 50"}],
            "desktops": [{"id": "c" * 32, "name": "vm-ad", "host": "192.168.2.10", "port": 3389, "user": "Администратор",
                          "domain": "", "group": "a" * 32, "width": 0, "height": 0, "clipboard": True, "fullscreen": False,
                          "drives": ["/home/me/Share"], "gateway": "gw.example.com"},
                         {"id": "d" * 32, "name": "terminal-srv", "host": "10.0.5.4", "port": 3390, "user": "ops",
                          "domain": "CORP", "group": "", "width": 1280, "height": 800, "clipboard": False, "fullscreen": False}],
            "vnc": [{"id": "e" * 32, "name": "Пульт лаборатории", "host": "192.168.1.50", "port": 5900,
                     "group": "b" * 32, "via": "lab"}]}
# «Известные хосты» show a demo file next to the test config, never the real ~/.ssh/known_hosts.
(home / "ssh/known_hosts").write_text(
    "# demo\n"
    "web.example.com,192.168.10.21 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl\n"
    "[db.internal.example.com]:2222 ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBEmKSENjQEez"
    "OmxkZMy7opKgwFB9nkt5YRrYMjNuG5N87uRgg6CLrbo5wAdT/y6v0mKV0U2w0WZ2YB/++Tpockg=\n"
    "|1|JfKTdBh7rNbXkVAQCRp4OQoPfmI=|USECr3SWf1JUPsms5AqfD5QfxkM= ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQC8\n"
    "@cert-authority *.corp.example ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl\n")
(home / "state").mkdir()
(home / "state/settings.json").write_text(json.dumps(settings, ensure_ascii=False))

application = Application(Config(home / "ssh/config", home / "state"), isolated=True)
application.register(None)
application.activate()
window = next(item for item in application.get_windows() if isinstance(item, app.Window))
window.move(0, 0)
steps = []


def shot(name):
    root = Gdk.get_default_root_window()
    pixbuf = Gdk.pixbuf_get_from_window(root, 0, 0, root.get_width(), root.get_height())
    pixbuf.savev(str(OUT / f"{name}.png"), "png", [], [])
    print("saved", name, flush=True)


def step(function):
    steps.append(function)
    return function


def in_dialog(name, opener):
    """Opens a modal dialog, captures it and closes it from inside its own loop."""
    def capture():
        shot(name)
        for toplevel in Gtk.Window.list_toplevels():
            if isinstance(toplevel, Gtk.Dialog) and toplevel.get_visible():
                toplevel.response(Gtk.ResponseType.CANCEL)
        return False
    GLib.timeout_add(900, capture)
    opener()


# No catalog table: the start page in the workspace and the grouped «Серверы» column (docs/ui-spec.md §2).
step(lambda: shot("01-start-ssh-light"))
step(lambda: window.set_sort("address"))
step(lambda: shot("03-column-sorted-address"))
step(lambda: window.set_sort("original"))
step(lambda: window.select_protocol("rdp"))
step(lambda: shot("04-column-rdp"))
step(lambda: window.select_protocol("vnc"))
step(lambda: shot("19-column-vnc"))
step(lambda: (window.select_protocol("ssh"), window.switch_theme())[1])
step(lambda: shot("05-start-ssh-dark"))
step(lambda: window.section_menu(window.sessions_page.server_panel.listing, None, "a" * 32))
step(lambda: shot("06-tab-menu"))
def close_popups():
    for toplevel in Gtk.Window.list_toplevels():
        if toplevel.get_window_type() == Gtk.WindowType.POPUP and toplevel.get_visible():
            toplevel.hide()


step(close_popups)
step(lambda: window.column_menu(window.sessions_page.server_panel))
step(lambda: shot("07-column-menu"))
step(close_popups)
step(lambda: window.switch_theme())
step(lambda: window.connect_profile(window.document.profiles[0]))
step(lambda: window.connect_profile(window.document.profiles[0], True))
step(lambda: window.connect_profile(window.document.profiles[2]))
step(lambda: None)
step(lambda: shot("08-sessions"))
step(lambda: window.sessions_page.server_panel.toggle("b" * 32))
step(lambda: shot("26-server-column-folded"))
step(lambda: window.sessions_page.server_panel.listing.select_row(next(row for row in window.sessions_page.server_panel.listing.get_children() if row.profile is not None)))
step(lambda: window.sessions_page.server_panel.menu(None))
step(lambda: shot("27-server-column-menu"))
step(close_popups)
step(lambda: print("collapsed saved:", json.loads((home / "state/settings.json").read_text())["collapsed"], flush=True))
step(lambda: window.sessions_page.server_panel.toggle("b" * 32))
step(lambda: window.switch_theme())
step(lambda: shot("09-sessions-dark"))
step(lambda: window.switch_theme())
step(lambda: window.open_files(window.document.profiles[1]))
step(lambda: None)
step(lambda: shot("21-files-panel"))
step(lambda: window.resize(820, 600))
step(lambda: None)
step(lambda: shot("22-narrow-files"))
step(lambda: window.sessions_page.activate(window.sessions[0]))
step(lambda: shot("23-narrow-session"))
step(lambda: window.show_start())
step(lambda: shot("24-narrow-start"))
step(lambda: window.sessions_page.toggle_servers())
step(lambda: None)
step(lambda: shot("25-narrow-servers-shown"))
step(lambda: (window.sessions_page.toggle_servers(), window.resize(1280, 720))[1])
step(lambda: window.files[next(iter(window.files))].request_close())
step(lambda: None)
step(lambda: window.show_start())
step(lambda: in_dialog("10-dialog-ssh", lambda: window.edit_profile(window.document.profiles[0])))
step(lambda: in_dialog("11-dialog-rdp", lambda: window.edit_desktop(window.desktop_profiles()[0])))
step(lambda: in_dialog("20-dialog-vnc", lambda: window.edit_vnc(window.vnc_profiles()[0])))
step(lambda: in_dialog("12-dialog-picker", window.pick_server))
step(lambda: in_dialog("13-dialog-new-tab", window.new_group))
step(lambda: in_dialog("15-dialog-settings", window.open_settings))
step(lambda: in_dialog("28-known-hosts", lambda: window.open_known_hosts(None)))
step(lambda: in_dialog("16-dialog-import", window.import_other))
step(lambda: window.connect_profile(next(item for item in window.document.profiles if item.alias == "lab")))
step(lambda: None)
step(lambda: window.connect_profile(window.document.profiles[1]))
step(lambda: window.sessions_page.snippet_menu(window.sessions_page.snippets))
step(lambda: shot("17-snippets"))
step(close_popups)
step(lambda: window.sessions_page.activate(next(item for item in window.sessions if item.profile.alias == "lab")))
step(lambda: shot("18-ssh-ended"))
step(lambda: window.show_start())
step(lambda: application.quit())


def run_steps():
    # Escape closes any open popup menu between steps.
    if steps:
        function = steps.pop(0)
        try:
            function()
        except Exception as error:  # noqa: BLE001 - report and keep going
            print("step failed:", error, flush=True)
            import traceback
            traceback.print_exc()
        return True
    return False


GLib.timeout_add(1100, run_steps)
application.run([])
