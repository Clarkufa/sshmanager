import importlib
import json
import sys
import tempfile
import types
import unittest
from pathlib import Path

try:
    import cryptography  # noqa: F401
    import fcntl  # noqa: F401
    AVAILABLE = True
except ImportError:
    AVAILABLE = False


def load_backup():
    """Import the backup module without a display: GTK is only needed by its dialogs."""
    try:
        import gi
        gi.require_version("Gtk", "3.0")
        from gi.repository import GLib, Gtk  # noqa: F401
    except (ImportError, ValueError):
        gi = types.ModuleType("gi")
        repository = types.ModuleType("gi.repository")
        repository.GLib = types.SimpleNamespace()
        repository.Gtk = types.SimpleNamespace()
        gi.repository = repository
        sys.modules.setdefault("gi", gi)
        sys.modules.setdefault("gi.repository", repository)
        sys.modules.setdefault("ssh_manager.widgets", types.SimpleNamespace(
            chooser=None, confirm=None, menu_at=None, message=None, prompt=None))
    return importlib.import_module("ssh_manager.backup")


class MemoryVault:
    def __init__(self):
        self.items = {}

    def lookup(self, account):
        return self.items.get(account.key)

    def save(self, account, password):
        if password:
            self.items[account.key] = password
        else:
            self.items.pop(account.key, None)


@unittest.skipUnless(AVAILABLE, "needs Linux and the cryptography package")
class RdpBackupTest(unittest.TestCase):
    def setUp(self):
        self.backup = load_backup()
        from ssh_manager.config import Config
        from ssh_manager.rdp import DesktopProfile
        self.folder = tempfile.TemporaryDirectory()
        root = Path(self.folder.name)
        (root / "ssh").mkdir()
        (root / "ssh/config").write_text("Host demo\n    HostName 10.0.0.9\n")
        self.config = Config(root / "ssh/config", root / "state")
        self.first = DesktopProfile("a" * 32, "A", "10.1.1.1", 3389, "alice", "CORP").validate()
        self.second = DesktopProfile("b" * 32, "B", "10.1.1.1", 3390, "bob").validate()
        settings = {"desktops": [self.first.__dict__, self.second.__dict__]}
        self.config.state.mkdir(mode=0o700)
        self.config.settings_path.write_text(json.dumps(settings))
        self.vault = MemoryVault()
        self.vault.save(self.first.secret, "alice-pass")
        self.vault.save(self.second.secret, "bob-pass")
        self.archive = root / "copy.sshbackup"

    def tearDown(self):
        self.folder.cleanup()

    def test_protected_backup_restores_each_rdp_password(self):
        count = self.backup.export(self.config, self.vault, self.archive, "backup-secret")
        self.assertEqual(count, 2)
        contents, valid = self.backup.decode(self.archive, "backup-secret")
        self.assertIn("app/passwords/rdp-" + self.first.id, contents)
        empty = MemoryVault()
        self.backup.restore(self.config, empty, contents, valid)
        self.assertEqual(empty.lookup(self.first.secret), "alice-pass")
        self.assertEqual(empty.lookup(self.second.secret), "bob-pass")

    def test_plain_backup_has_no_passwords(self):
        self.assertEqual(self.backup.export(self.config, self.vault, self.archive), 0)
        contents, _ = self.backup.decode(self.archive, None)
        self.assertFalse(any(name.startswith("app/passwords/") for name in contents))


if __name__ == "__main__":
    unittest.main()
