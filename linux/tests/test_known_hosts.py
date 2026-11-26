import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from ssh_manager.config import Document, Profile
from ssh_manager.known_hosts import KnownHosts, host_name, resolve, scan, valid_setting

ED25519 = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl"
KEYGEN = shutil.which("ssh-keygen")


class KnownHostsTest(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.path = Path(self.folder.name) / "known_hosts"
        self.path.write_text("# comment stays\n"
                             f"plain.example,10.9.9.9 {ED25519}\n"
                             f"[port.example]:2200 {ED25519}\n"
                             f"@cert-authority *.corp {ED25519}\n")

    def tearDown(self):
        self.folder.cleanup()

    def test_entries_and_search(self):
        hosts = KnownHosts(self.path)
        self.assertEqual(len(hosts.entries), 3)
        self.assertEqual(hosts.entries[2].marker, "@cert-authority")
        self.assertEqual(len(hosts.search("plain")), 1)
        self.assertEqual(len(hosts.search("")), 3)
        self.assertTrue(hosts.entries[2].is_for("db.corp"))

    @unittest.skipUnless(KEYGEN, "needs ssh-keygen")
    def test_fingerprint_like_ssh_keygen(self):
        key = Path(self.folder.name) / "key.pub"
        key.write_text(ED25519 + "\n")
        expected = subprocess.run([KEYGEN, "-l", "-f", str(key)], capture_output=True, text=True).stdout.split()[1]
        self.assertEqual(KnownHosts(self.path).entries[0].fingerprint, expected)

    @unittest.skipUnless(KEYGEN, "needs ssh-keygen")
    def test_hashed_names_found_only_by_exact_host(self):
        subprocess.run([KEYGEN, "-H", "-f", str(self.path)], capture_output=True)
        hosts = KnownHosts(self.path)
        self.assertGreaterEqual(sum(entry.hashed for entry in hosts.entries), 2)
        found = hosts.search("port.example:2200")
        self.assertEqual([entry.display for entry in found], ["[port.example]:2200"])
        self.assertEqual(hosts.search("port.exam"), [])

    def test_remove_and_replace_keep_other_lines(self):
        hosts = KnownHosts(self.path)
        hosts.remove([hosts.entries[0]])
        text = self.path.read_text()
        self.assertTrue(text.startswith("# comment stays\n"))
        self.assertNotIn("plain.example", text)
        self.assertTrue(self.path.with_name("known_hosts.old").exists())
        self.assertEqual(self.path.stat().st_mode & 0o777, 0o600)
        KnownHosts(self.path).replace("[port.example]:2200", ["[port.example]:2200 ssh-rsa AAAAB3NzaC1yc2E"])
        ports = [entry for entry in KnownHosts(self.path).entries if entry.hosts == "[port.example]:2200"]
        self.assertEqual([entry.key_type for entry in ports], ["ssh-rsa"])

    def test_names_paths_and_settings(self):
        self.assertEqual(host_name("h", 22), "h")
        self.assertEqual(host_name("[h]", "2200"), "[h]:2200")
        self.assertEqual(resolve("", "/x/known_hosts"), Path("/x/known_hosts"))
        self.assertEqual(resolve("~/.ssh/kh", "/x"), Path.home() / ".ssh/kh")
        self.assertTrue(valid_setting("~/.ssh/known_hosts_work"))
        self.assertTrue(valid_setting("/etc/ssh/hosts"))
        self.assertFalse(valid_setting("hosts.txt"))
        with self.assertRaises(ValueError):
            scan("bad host", 22)

    def test_config_keeps_user_known_hosts_file(self):
        document = Document(b"Host web\n    HostName 10.0.0.5\n")
        before = document.profiles[0]
        after = Profile("web", "10.0.0.5", known_hosts="/home/me/My Keys/known_hosts")
        written = document.update(before, after)
        self.assertIn(b'UserKnownHostsFile "/home/me/My Keys/known_hosts"', written.raw)
        self.assertEqual(written.profiles[0].known_hosts, "/home/me/My Keys/known_hosts")
        cleared = written.update(written.profiles[0], Profile("web", "10.0.0.5"))
        self.assertNotIn(b"UserKnownHostsFile", cleared.raw)
        with self.assertRaises(ValueError):
            Profile("web", known_hosts="relative/hosts").validate()


if __name__ == "__main__":
    unittest.main()
