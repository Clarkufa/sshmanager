"""Publishes the release built by release.yml, with the same checks as publish.ps1.

The files from every platform are uploaded to a draft, compared with the local files by size and SHA-256,
and only then is the draft published as the latest release. With PUBLISH other than "true" nothing is created.
"""
import hashlib
import json
import os
import subprocess
import sys
from pathlib import Path

repo = os.environ["REPO"]
version = os.environ["VERSION"]
publish = os.environ.get("PUBLISH") == "true"
tag = "v" + version
dist = Path("dist")
notes = Path("docs/releases") / f"{version}.md"
labels = {
    "SSH-Manager-macOS.dmg": "macOS - DMG for Apple Silicon and Intel",
    "SSH-Manager-Linux.deb": "Linux - DEB for Debian and Ubuntu",
    "SSH-Manager-Linux.rpm": "Linux - RPM for Fedora",
    "SSH-Manager-Linux.tar.gz": "Linux - TAR.GZ",
    "SshManagerSetup.msi": None,
    "SshManager.exe": None,
    "SSH-Manager-Update.json": "Windows update metadata",
}


def gh(*args, check=True):
    result = subprocess.run(["gh", *args], capture_output=True, text=True)
    if check and result.returncode != 0:
        sys.exit(f"gh {' '.join(args)} failed: {result.stderr.strip()}")
    return result


def describe(path):
    data = path.read_bytes()
    return {"name": path.name, "size": len(data), "digest": "sha256:" + hashlib.sha256(data).hexdigest(), "state": "uploaded"}


def numbers(text):
    return tuple(int(part) for part in text.lstrip("v").split("."))


for name in labels:
    if name != "SSH-Manager-Update.json" and not ((dist / name).is_file() and (dist / name).stat().st_size > 0):
        sys.exit(f"Release file {name} is missing or empty.")
if not notes.is_file():
    sys.exit(f"{notes} is missing.")

latest = json.loads(gh("api", f"repos/{repo}/releases/latest").stdout)["tag_name"]
if numbers(version) <= numbers(latest):
    sys.exit(f"{tag} must be newer than the current release {latest}.")
if gh("release", "view", tag, "--repo", repo, "--json", "tagName", check=False).returncode == 0:
    sys.exit(f"Release {tag} already exists.")

windows = [describe(dist / "SshManagerSetup.msi"), describe(dist / "SshManager.exe")]
update = {"tag_name": tag, "draft": False, "prerelease": False, "assets": windows}
(dist / "SSH-Manager-Update.json").write_text(json.dumps(update, indent=2), encoding="utf-8")
expected = {name: describe(dist / name) for name in labels}
for item in expected.values():
    print(f"{item['name']}: {item['size']} bytes, {item['digest']}")
if not publish:
    print(f"Checked {tag}; nothing published (manual run).")
    sys.exit(0)

uploads = [str(dist / name) + (f"#{label}" if label else "") for name, label in labels.items()]
gh("release", "create", tag, *uploads, "--repo", repo, "--verify-tag", "--title", f"SSH Manager {version} - Windows, macOS, Linux",
   "--notes-file", str(notes), "--draft", "--latest=false")
draft = json.loads(gh("release", "view", tag, "--repo", repo, "--json", "isDraft,assets").stdout)
assets = {asset["name"]: asset for asset in draft["assets"]}
if not draft["isDraft"] or len(assets) != len(expected):
    sys.exit(f"Draft {tag} must contain exactly {len(expected)} files.")
for name, item in expected.items():
    asset = assets.get(name)
    if not asset or asset.get("state") != "uploaded" or asset.get("size") != item["size"] or asset.get("digest") != item["digest"]:
        sys.exit(f"Uploaded {name} does not match the built file.")
if json.loads(gh("api", f"repos/{repo}/releases/latest").stdout)["tag_name"] != latest:
    sys.exit("The latest release changed while publishing; check the builds and publish the draft by hand.")
gh("release", "edit", tag, "--repo", repo, "--draft=false", "--latest")
print(f"Published {tag}.")
