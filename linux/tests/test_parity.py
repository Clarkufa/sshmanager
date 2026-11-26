"""Logic behind the Windows-like Linux UI that does not need a display."""
import json
import unittest
from unittest import mock

from ssh_manager import rdp
from ssh_manager.config import parse_settings
from ssh_manager.launch import ssh_command_text, terminal_command
from ssh_manager.rdp import DesktopProfile


class ForwardingTest(unittest.TestCase):
    def setUp(self):
        try:
            from ssh_manager.sessions import forwarding_failed
        except (ImportError, ValueError):
            self.skipTest("needs GTK and VTE")
        self.failed = forwarding_failed

    def test_detects_openssh_forwarding_errors(self):
        self.assertTrue(self.failed("bind [127.0.0.1]:8080: Address already in use\r\nchannel_setup_fwd_listener_tcpip: "
                                    "cannot listen to port: 8080"))
        self.assertTrue(self.failed("Warning: Could not request local forwarding."))
        self.assertTrue(self.failed("\x1b[1mcannot listen to port\x1b[0m: 22"))
        self.assertFalse(self.failed("Welcome to Ubuntu"))


class ExternalTerminalTest(unittest.TestCase):
    def test_prefers_the_system_alternative_with_one_shell_string(self):
        which = {"x-terminal-emulator": "/usr/bin/x-terminal-emulator"}.get
        self.assertEqual(terminal_command(["ssh", "web prod"], which),
                         ["/usr/bin/x-terminal-emulator", "-e", "ssh 'web prod'"])

    def test_gnome_terminal_gets_argv_after_double_dash(self):
        which = {"gnome-terminal": "/usr/bin/gnome-terminal"}.get
        self.assertEqual(terminal_command(["ssh", "-F", "/tmp/c", "db"], which),
                         ["/usr/bin/gnome-terminal", "--", "ssh", "-F", "/tmp/c", "db"])

    def test_none_without_terminal(self):
        self.assertIsNone(terminal_command(["ssh", "db"], lambda name: None))
        self.assertEqual(ssh_command_text("db"), "ssh db")


class EmbeddedRdpCommandTest(unittest.TestCase):
    def profile(self, **values):
        fields = dict(id="f" * 32, name="Office", host="10.0.0.5", port=3389, user="admin", domain="")
        fields.update(values)
        return DesktopProfile(**fields).validate()

    def test_embedded_session_uses_the_parent_window_and_its_size(self):
        which = {"xfreerdp3": "/usr/bin/xfreerdp3", "sdl-freerdp3": "/usr/bin/sdl-freerdp3"}.get
        with mock.patch.object(rdp.shutil, "which", side_effect=which), mock.patch.dict(rdp.os.environ, {}, clear=True):
            argv, environment = self.profile().command("secret", parent=0x2a00005, size=(900, 500))
        self.assertEqual(argv[0], "/usr/bin/xfreerdp3")
        lines = environment[rdp.ARGS_VARIABLE].split("\n")
        self.assertIn(f"/parent-window:{0x2a00005}", lines)
        self.assertIn("/size:900x500", lines)
        self.assertIn("/dynamic-resolution", lines)
        self.assertIn("/cert:tofu", lines)
        self.assertNotIn("/f", lines)

    def test_sign_in_dialog_account_overrides_the_profile(self):
        with mock.patch.object(rdp.shutil, "which", side_effect=lambda name: "/usr/bin/xfreerdp3" if name == "xfreerdp3" else None):
            _, environment = self.profile().command("pw", user="other", domain="CORP")
        lines = environment[rdp.ARGS_VARIABLE].split("\n")
        self.assertIn("/u:other", lines)
        self.assertIn("/d:CORP", lines)


class SettingsCompatibilityTest(unittest.TestCase):
    def test_unknown_desktop_fields_from_newer_versions_are_ignored(self):
        desktop = {"id": "a" * 32, "name": "x", "host": "10.0.0.1", "port": 3389, "user": "", "domain": "", "group": "",
                   "width": 0, "height": 0, "clipboard": False, "fullscreen": False, "future_option": "x"}
        settings = parse_settings(json.dumps({"desktops": [desktop], "future": True}).encode())
        self.assertEqual(settings["desktops"][0]["host"], "10.0.0.1")
        self.assertNotIn("future_option", settings["desktops"][0])


if __name__ == "__main__":
    unittest.main()
