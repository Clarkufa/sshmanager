import unittest

try:
    from ssh_manager.config import Document, Profile
except ImportError:  # fcntl: Linux only
    Document = None


@unittest.skipIf(Document is None, "needs Linux")
class ForwardsTest(unittest.TestCase):
    def test_edit_keeps_comments_and_writes_multi_token_forwards(self):
        raw = b"Host web\n    HostName 10.0.0.1\n    LocalForward 8080 localhost:80   # api\n    User root\n"
        document = Document(raw)
        before = document.profiles[0]
        self.assertEqual(before.forwards, (("LocalForward", "8080 localhost:80"),))
        after = Profile(before.alias, before.host, before.user, before.port, before.keys, proxy_jump="bastion",
                        forwards=(("LocalForward", "9090 localhost:90"), ("DynamicForward", "1080")))
        text = document.update(before, after).raw.decode()
        self.assertIn("LocalForward 9090 localhost:90  # api", text)
        self.assertIn("DynamicForward 1080", text)
        self.assertIn("ProxyJump bastion", text)
        self.assertNotIn("8080", text)
        again = Document(text.encode()).profiles[0]
        self.assertEqual(again.proxy_jump, "bastion")
        self.assertEqual(set(again.forwards), {("LocalForward", "9090 localhost:90"), ("DynamicForward", "1080")})

    def test_new_profile_and_validation(self):
        text = Document(b"").update(None, Profile("new", "h", forwards=(("RemoteForward", "2222 localhost:22"),),
                                                  proxy_jump="a@b:2")).raw.decode()
        self.assertIn("RemoteForward 2222 localhost:22", text)
        self.assertIn("ProxyJump a@b:2", text)
        with self.assertRaises(ValueError):
            Profile("x", forwards=(("LocalForward", "bad"),)).validate()
        with self.assertRaises(ValueError):
            Profile("x", proxy_jump="a b").validate()


if __name__ == "__main__":
    unittest.main()
