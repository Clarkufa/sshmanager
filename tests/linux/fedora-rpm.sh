#!/bin/sh
set -eu
release_dir=${1:?usage: fedora-rpm.sh <dir with source.tar>}
destination="$release_dir/rpm"
dnf -y --setopt=install_weak_deps=False install rpm-build python3 tar gzip findutils xorg-x11-server-Xvfb dbus-daemon
source_dir=$(mktemp -d)
tar -xf "$release_dir/source.tar" -C "$source_dir"
cd "$source_dir"
version=$(PYTHONPATH=linux python3 -B -c 'from ssh_manager import VERSION; print(VERSION)')
PYTHONPATH=linux python3 -B -m unittest discover -s linux/tests -v
sh linux/build.sh "$destination"
package="$destination/ssh-manager-desktop-$version-1.noarch.rpm"
test "$(rpm -qp --queryformat '%{NAME} %{VERSION} %{ARCH}' "$package")" = "ssh-manager-desktop $version noarch"
if rpm -q ssh-manager-desktop >/dev/null 2>&1; then
    rpm -e ssh-manager-desktop
fi
dnf -y --setopt=install_weak_deps=False install "$package"
rpm -V ssh-manager-desktop
test "$(ssh-manager --version)" = "$version"
test "$(readlink -f /usr/bin/ssh-manager)" = /usr/share/ssh-manager/ssh-manager
test -f /usr/share/applications/ssh-manager.desktop
test -f /usr/share/icons/hicolor/256x256/apps/ssh-manager.png
Xvfb :90 -screen 0 1024x768x24 >/tmp/ssh-manager-xvfb.log 2>&1 &
export DISPLAY=:90
timeout 5 sh -c 'until [ -S /tmp/.X11-unix/X90 ]; do sleep 0.1; done'
dbus-run-session -- python3 -B - <<'PY'
from pathlib import Path
from tempfile import TemporaryDirectory
import sys

sys.path.insert(0, '/usr/share/ssh-manager')
from ssh_manager.app import Application, Config, Gtk, Vte, Window
from ssh_manager.updates import RPM, RPM_ROOT, PACKAGE_ASSETS

assert Gtk.init_check()[0]
assert Vte.Terminal()
assert PACKAGE_ASSETS[RPM_ROOT] == RPM
with TemporaryDirectory() as path:
    root = Path(path)
    config = root / 'config'
    config.write_text('Host fixture\n    HostName example.invalid\n')
    app = Application(Config(config, root / 'settings'), isolated=True)
    assert app.register(None)
    app.activate()
    window = next(window for window in app.get_windows() if isinstance(window, Window))
    assert window.get_visible()
    assert window.updates.root == RPM_ROOT
    window.destroy()
    app.quit()
print('PASS Fedora 44: real DNF install, RPM verification, GTK window and embedded VTE')
PY
