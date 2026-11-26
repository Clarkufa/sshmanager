import tempfile
import unittest
from pathlib import Path
from unittest import mock

from ssh_manager import rdp
from ssh_manager.extras import (copy_key_command, generate_key, mobaxterm, mremoteng, parse_quick, putty_sessions,
                                quick_alias)
from ssh_manager.rdp import DesktopProfile


class QuickConnectTest(unittest.TestCase):
    def test_forms(self):
        self.assertEqual(parse_quick("root@10.0.0.5:2222"), ("ssh", "root", "10.0.0.5", 2222))
        self.assertEqual(parse_quick("example.com"), ("ssh", "", "example.com", 22))
        self.assertEqual(parse_quick("rdp://admin@[fe80::1]"), ("rdp", "admin", "fe80::1", 3389))
        self.assertEqual(parse_quick("rdp://srv:3390/"), ("rdp", "", "srv", 3390))
        for bad in ("", "a b", "host:99999", "user@"):
            with self.assertRaises(ValueError):
                parse_quick(bad)
        self.assertEqual(quick_alias("10.0.0.5", ["10.0.0.5"]), "10.0.0.5-2")


class ImportTest(unittest.TestCase):
    def setUp(self):
        self.folder = Path(tempfile.mkdtemp())

    def test_putty(self):
        sessions = self.folder / "sessions"
        sessions.mkdir()
        (sessions / "web%20prod").write_text("HostName=10.0.0.1\nPortNumber=2222\nUserName=deploy\nProtocol=ssh\n")
        (sessions / "serial").write_text("Protocol=serial\nHostName=COM1\n")
        found = putty_sessions(sessions)
        self.assertEqual([(item.name, item.host, item.port, item.user) for item in found], [("web prod", "10.0.0.1", 2222, "deploy")])

    def test_mremoteng(self):
        path = self.folder / "confCons.xml"
        path.write_text('<Connections><Node Name="F" Type="Container"><Node Name="db" Type="Connection" Protocol="SSH2" '
                        'Hostname="10.0.0.2" Port="22" Username="pg"/><Node Name="ad" Type="Connection" Protocol="RDP" '
                        'Hostname="dc1" Port="3389" Username="admin" Domain="CORP"/></Node></Connections>')
        found = mremoteng(path)
        self.assertEqual([(item.protocol, item.name, item.domain) for item in found], [("ssh", "db", ""), ("rdp", "ad", "CORP")])

    def test_mobaxterm(self):
        path = self.folder / "s.mxtsessions"
        path.write_text("[Bookmarks]\nSubRep=\nImgNum=42\nlab=#109#0%192.168.1.5%22%root%%-1%-1%%%\n"
                        "win=#91#4%10.0.0.9%3390%ops%%-1%-1%\n")
        found = mobaxterm(path)
        self.assertEqual([(item.protocol, item.host, item.port, item.user) for item in found],
                         [("ssh", "192.168.1.5", 22, "root"), ("rdp", "10.0.0.9", 3390, "ops")])


class KeysTest(unittest.TestCase):
    def test_generate_calls_ssh_keygen(self):
        run = mock.Mock(return_value=mock.Mock(returncode=0, stderr=""))
        path = Path(tempfile.mkdtemp()) / "id_new"
        self.assertEqual(generate_key(path, "me@pc", run).name, "id_new.pub")
        self.assertIn("ed25519", run.call_args[0][0])

    def test_copy_command_quotes_the_key(self):
        command = copy_key_command("ssh-ed25519 AAAAC3Nza me@pc\n")
        self.assertIn("'ssh-ed25519 AAAAC3Nza me@pc'", command)
        with self.assertRaises(ValueError):
            copy_key_command("not a key; rm -rf ~")


class RdpAdvancedTest(unittest.TestCase):
    def test_drives_printers_gateway(self):
        profile = DesktopProfile("a" * 32, "x", "10.0.0.1", user="u", drives=["/home/me/Share"], printers=True,
                                 multimon=True, gateway="gw.example.com", gateway_user="gwuser").validate()
        with mock.patch.object(rdp.shutil, "which", side_effect=lambda name: "/usr/bin/xfreerdp3" if name == "xfreerdp3" else None):
            _, environment = profile.command("pw")
        lines = environment[rdp.ARGS_VARIABLE].split("\n")
        self.assertIn("/drive:Share1,/home/me/Share", lines)
        self.assertIn("/printer", lines)
        self.assertIn("/multimon", lines)
        self.assertIn("/gateway:g:gw.example.com,u:gwuser,p:pw", lines)
        with self.assertRaises(ValueError):
            DesktopProfile("a" * 32, "x", "h", drives=("relative",)).validate()


if __name__ == "__main__":
    unittest.main()
