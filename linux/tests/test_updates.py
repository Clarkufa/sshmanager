import hashlib
import io
import json
from pathlib import Path
import shutil
import sys
import tarfile
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch

from ssh_manager.updates import (
    ARCHIVE, MANIFEST, BundleReplacement, TOP_FILES, Update, UpdateError, installed_version, unpack_application,
)


class UpdatesTests(unittest.TestCase):
    def test_release_must_be_newer_and_have_one_valid_asset(self):
        release = {
            "tag_name": "v1.8.6",
            "draft": False,
            "prerelease": False,
            "assets": [{"name": ARCHIVE, "state": "uploaded", "size": 3, "digest": "sha256:" + "a" * 64}],
        }
        self.assertEqual(Update.from_release(json.dumps(release), current="1.8.5", name=ARCHIVE).version, "1.8.6")
        self.assertIsNone(Update.from_release(json.dumps(release), current="1.8.6", name=ARCHIVE))
        release["assets"].append(release["assets"][0])
        with self.assertRaises(UpdateError):
            Update.from_release(json.dumps(release), current="1.8.5", name=ARCHIVE)
        release["prerelease"] = True
        with self.assertRaises(UpdateError):
            Update.from_release(json.dumps(release), current="1.8.5", name=ARCHIVE)

    def test_download_rejects_wrong_checksum(self):
        package = b"trusted package bytes"
        update = Update("1.8.6", ARCHIVE, len(package), hashlib.sha256(package).hexdigest())
        with patch("ssh_manager.updates.request", return_value=io.BytesIO(package)):
            downloaded = update.download()
        try:
            self.assertEqual(downloaded.read_bytes(), package)
        finally:
            shutil.rmtree(downloaded.parent)
        with patch("ssh_manager.updates.request", return_value=io.BytesIO(b"x" * len(package))):
            with self.assertRaises(UpdateError):
                update.download()

    def test_archive_path_traversal_is_rejected(self):
        with TemporaryDirectory() as temp:
            root = Path(temp)
            archive = root / "archive.tar.gz"
            with tarfile.open(archive, "w:gz") as tar:
                member = tarfile.TarInfo("ssh-manager-1.8.6/../../outside")
                member.size = 1
                tar.addfile(member, io.BytesIO(b"x"))
            with self.assertRaises(UpdateError):
                unpack_application(archive, root / "app", "1.8.6")
            self.assertFalse((root / "outside").exists())

    @unittest.skipUnless(sys.platform.startswith("linux"), "renameat2 is Linux-specific")
    def test_atomic_update_and_rollback_preserve_user_files(self):
        with TemporaryDirectory() as temp:
            work = Path(temp)
            live = work / "installed"
            source = work / "ssh-manager-1.8.6"
            for path, version in ((live, "1.8.5"), (source, "1.8.6")):
                modules = path / "ssh_manager"
                modules.mkdir(parents=True)
                for filename in TOP_FILES - {MANIFEST}:
                    (path / filename).write_text(filename)
                (modules / "__init__.py").write_text(f'VERSION = "{version}"\n')
                (modules / "app.py").touch()
                checksums = {item.relative_to(path).as_posix(): hashlib.sha256(item.read_bytes()).hexdigest()
                             for item in path.rglob("*") if item.is_file()}
                (path / MANIFEST).write_text(json.dumps(checksums))

            archive = work / "release.tar.gz"
            with tarfile.open(archive, "w:gz") as tar:
                tar.add(source, arcname=source.name)

            local_file = live / "my-notes"
            local_file.touch()
            with self.assertRaises(UpdateError):
                BundleReplacement.install(archive, live, "1.8.6")
            self.assertTrue(local_file.is_file())
            local_file.unlink()
            modified = live / "ssh_manager" / "app.py"
            modified.write_text("# customised")
            with self.assertRaises(UpdateError):
                BundleReplacement.install(archive, live, "1.8.6")
            self.assertEqual(modified.read_text(), "# customised")
            modified.write_text("")

            install = BundleReplacement.install(archive, live, "1.8.6")
            self.assertEqual(installed_version(live), "1.8.6")
            with self.assertRaises(UpdateError):
                BundleReplacement.install(archive, live, "1.8.6")
            install.rollback()
            self.assertEqual(installed_version(live), "1.8.5")
