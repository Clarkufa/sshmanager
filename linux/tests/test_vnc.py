import json
import tempfile
import unittest
from dataclasses import asdict
from pathlib import Path

from ssh_manager.config import parse_settings
from ssh_manager.extras import mobaxterm, mremoteng, parse_quick
from ssh_manager.rdp import DesktopProfile
from ssh_manager.vnc import SshTunnel, TunnelError, VncProfile

try:
    from test_backup_rdp import AVAILABLE, MemoryVault, load_backup
except ImportError:  # run from another folder
    AVAILABLE = False


def profile(**values):
    fields = dict(id="c" * 32, name="Камера", host="10.0.0.7", port=5901)
    fields.update(values)
    return VncProfile(**fields).validate()


class VncProfileTest(unittest.TestCase):
    def test_defaults(self):
        item = VncProfile("d" * 32, "Сборка", "build.local").validate()
        self.assertEqual((item.port, item.scale, item.clipboard, item.view_only, item.via), (5900, "fit", True, False, ""))
        self.assertEqual(item.address, "build.local:5900")
        self.assertEqual(profile(host="fe80::1").address, "[fe80::1]:5901")

    def test_validation(self):
        for values in ({"port": 0}, {"port": 70000}, {"scale": "zoom"}, {"via": "bad alias"}, {"via": "-x"},
                       {"host": "a b"}, {"name": " "}, {"id": "xyz"}, {"view_only": "1"}, {"name": "a\nb"}):
            with self.subTest(values=values), self.assertRaises(ValueError):
                profile(**values)
        self.assertEqual(profile(via="bastion.1").via, "bastion.1")

    def test_secret_is_separate_from_rdp(self):
        vnc = profile()
        rdp = DesktopProfile("c" * 32, "Камера", "10.0.0.7", 5901).validate()
        self.assertNotEqual(vnc.secret.key, rdp.secret.key)
        self.assertEqual(vnc.secret.user, "-")
        self.assertEqual(profile(user="pi").secret.user, "pi")


class VncSettingsTest(unittest.TestCase):
    def test_parse(self):
        group = "e" * 32
        entry = asdict(profile(group=group, scale="native", view_only=True, clipboard=False, via="bastion"))
        entry["future_field"] = 1
        settings = parse_settings(json.dumps({
            "groups": [{"id": group, "name": "Цех", "color": ""}], "vnc": [entry],
            "favorites": ["vnc:" + "c" * 32], "sort": {"vnc:" + group: "address_desc"}}).encode())
        self.assertEqual(len(settings["vnc"]), 1)
        self.assertNotIn("future_field", settings["vnc"][0])
        self.assertEqual(settings["vnc"][0]["via"], "bastion")
        self.assertEqual(settings["sort"], {"vnc:" + group: "address_desc"})

    def test_old_settings_have_an_empty_list(self):
        self.assertEqual(parse_settings(b'{"desktops": []}')["vnc"], [])

    def test_rejects_broken_entries(self):
        good = asdict(profile())
        for vnc in ([good, good], [dict(good, group="f" * 32)], [dict(good, port="5900")], ["x"], {}):
            with self.subTest(vnc=vnc), self.assertRaises(ValueError):
                parse_settings(json.dumps({"vnc": vnc}).encode())


class VncQuickAndImportTest(unittest.TestCase):
    def test_quick(self):
        self.assertEqual(parse_quick("vnc://pi@10.1.1.5"), ("vnc", "pi", "10.1.1.5", 5900))
        self.assertEqual(parse_quick("VNC://[fe80::2]:5901/"), ("vnc", "", "fe80::2", 5901))

    def test_mremoteng(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "confCons.xml"
            path.write_text('<Connections><Node Name="screen" Type="Connection" Protocol="VNC" Hostname="v" Port="5901"/>'
                            '<Node Name="plain" Type="Connection" Protocol="VNC" Hostname="w"/>'
                            '<Node Name="tel" Type="Connection" Protocol="Telnet" Hostname="t"/></Connections>')
            found = mremoteng(path)
        self.assertEqual([(item.protocol, item.host, item.port) for item in found], [("vnc", "v", 5901), ("vnc", "w", 5900)])

    def test_mobaxterm(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "export.mxtsessions"
            path.write_text("[Bookmarks]\nSubRep=\nImgNum=42\nscreen=#128#5%10.0.0.10%5900%%-1%\nother=#128#5%10.0.0.11%%%-1%\n")
            found = mobaxterm(path)
        self.assertEqual([(item.protocol, item.name, item.port) for item in found], [("vnc", "screen", 5900), ("vnc", "other", 5900)])


class SshTunnelTest(unittest.TestCase):
    def test_missing_ssh_alias_fails_with_a_clear_message(self):
        from ssh_manager.config import Config, Profile

        class Vault:
            def lookup(self, account):
                return None
        with tempfile.TemporaryDirectory() as folder:
            config_path = Path(folder) / "config"
            config_path.write_text("Host broken\n    HostName 127.0.0.1\n    Port 1\n    BatchMode yes\n"
                                   "    StrictHostKeyChecking no\n    UserKnownHostsFile /dev/null\n")
            config = Config(config_path, Path(folder) / "state")
            try:
                with self.assertRaises(TunnelError) as caught:
                    SshTunnel(Profile("broken", "127.0.0.1", "", "1"), config, Vault(), "127.0.0.1", 5900, timeout=10)
            except RuntimeError as error:  # no ssh client in this environment
                self.skipTest(str(error))
        self.assertTrue(str(caught.exception).startswith("Не удалось открыть SSH-туннель:"))


@unittest.skipUnless(AVAILABLE, "needs Linux and the cryptography package")
class VncBackupTest(unittest.TestCase):
    def test_protected_backup_restores_the_vnc_password(self):
        backup = load_backup()
        from ssh_manager.config import Config
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "ssh").mkdir()
            (root / "ssh/config").write_text("Host demo\n    HostName 10.0.0.9\n")
            config = Config(root / "ssh/config", root / "state")
            item = profile()
            config.state.mkdir(mode=0o700)
            config.settings_path.write_text(json.dumps({"vnc": [asdict(item)]}))
            vault = MemoryVault()
            vault.save(item.secret, "VncTest1")
            archive = root / "copy.sshbackup"
            self.assertEqual(backup.export(config, vault, archive, "backup-secret"), 1)
            contents, valid = backup.decode(archive, "backup-secret")
            self.assertIn("app/passwords/vnc-" + item.id, contents)
            empty = MemoryVault()
            backup.restore(config, empty, contents, valid)
            self.assertEqual(empty.lookup(item.secret), "VncTest1")


if __name__ == "__main__":
    unittest.main()
