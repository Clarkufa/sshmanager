import unittest
from unittest import mock

from ssh_manager import rdp
from ssh_manager.rdp import ARGS_VARIABLE, DesktopProfile


def profile(port, **values):
    fields = dict(id=f"{port:032x}", name="Office", host="10.0.0.5", port=port, user="admin", domain="CORP")
    fields.update(values)
    return DesktopProfile(**fields).validate()


class DesktopCommandTest(unittest.TestCase):
    def client(self, name):
        return mock.patch.object(rdp.shutil, "which", side_effect=lambda candidate: "/usr/bin/" + name if candidate == name else None)

    def test_freerdp3_gets_explicit_credentials_from_environment(self):
        with self.client("xfreerdp3"), mock.patch.dict(rdp.os.environ, {"DISPLAY": ":0"}):
            argv, environment = profile(3390).command("p@ss word")
        self.assertEqual(argv, ["/usr/bin/xfreerdp3", "/args-from:env:" + ARGS_VARIABLE])
        lines = environment[ARGS_VARIABLE].split("\n")
        self.assertIn("/v:10.0.0.5:3390", lines)
        self.assertIn("/u:admin", lines)
        self.assertIn("/d:CORP", lines)
        self.assertIn("/p:p@ss word", lines)
        self.assertIn("/dynamic-resolution", lines)
        self.assertFalse(any("p@ss" in argument for argument in argv))

    def test_freerdp2_passes_password_on_masked_command_line(self):
        rdp._versions.clear()
        run = mock.Mock(return_value=mock.Mock(stdout="This is FreeRDP version 2.11.7 (2.11.7)"))
        with self.client("xfreerdp"), mock.patch.dict(rdp.os.environ, {"DISPLAY": ":0"}), mock.patch.object(rdp.subprocess, "run", run):
            argv, environment = profile(3389, width=1280, height=720).command("secret")
        self.assertEqual(environment, {})
        self.assertEqual(argv[0], "/usr/bin/xfreerdp")
        self.assertIn("/p:secret", argv)
        self.assertIn("/size:1280x720", argv)
        self.assertNotIn("/dynamic-resolution", argv)

    def test_without_password_no_credentials_are_sent(self):
        with self.client("xfreerdp3"), mock.patch.dict(rdp.os.environ, {"DISPLAY": ":0"}):
            argv, environment = profile(3389).command(None)
        self.assertEqual(environment, {})
        self.assertFalse(any(argument.startswith("/p:") for argument in argv))

    def test_each_port_has_its_own_secret(self):
        first, second = profile(3389), profile(3390)
        self.assertNotEqual(first.secret.key, second.secret.key)
        self.assertEqual(first.secret.user, "CORP\\admin")

    def test_multiline_password_is_rejected(self):
        with self.client("xfreerdp3"), mock.patch.dict(rdp.os.environ, {"DISPLAY": ":0"}):
            with self.assertRaises(ValueError):
                profile(3389).command("a\n/p:b")


if __name__ == "__main__":
    unittest.main()
