#!/bin/sh
set -eu

source_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo=$(dirname -- "$source_dir")
output=${1:-"$repo/build/linux"}
mkdir -p "$output"
output=$(CDPATH= cd -- "$output" && pwd)
version=$(PYTHONPATH="$source_dir" python3 -B -c 'from ssh_manager import VERSION; print(VERSION)')
stage=$(mktemp -d)
trap 'rm -r -- "$stage"' EXIT HUP INT TERM

app="$stage/ssh-manager-$version"
install -d "$app/ssh_manager"
install -m 0755 "$source_dir/ssh-manager" "$app/ssh-manager"
install -m 0644 "$source_dir/ssh_manager/"*.py "$app/ssh_manager/"
install -m 0644 "$repo/assets/AppMark.png" "$app/icon.png"
install -m 0644 "$source_dir/README.md" "$app/README.md"
install -m 0644 "$repo/LICENSE" "$app/LICENSE"
tar -C "$stage" -czf "$output/ssh-manager-linux-$version.tar.gz" "ssh-manager-$version"

if command -v dpkg-deb >/dev/null; then
    pkg="$stage/package"
    install -d "$pkg/DEBIAN" "$pkg/usr/lib/ssh-manager/ssh_manager" "$pkg/usr/bin" \
        "$pkg/usr/share/applications" "$pkg/usr/share/icons/hicolor/256x256/apps" "$pkg/usr/share/doc/ssh-manager-desktop"
    install -m 0755 "$app/ssh-manager" "$pkg/usr/lib/ssh-manager/ssh-manager"
    install -m 0644 "$app/ssh_manager/"*.py "$pkg/usr/lib/ssh-manager/ssh_manager/"
    install -m 0644 "$app/icon.png" "$pkg/usr/lib/ssh-manager/icon.png"
    install -m 0644 "$app/icon.png" "$pkg/usr/share/icons/hicolor/256x256/apps/ssh-manager.png"
    install -m 0644 "$app/README.md" "$pkg/usr/share/doc/ssh-manager-desktop/README.md"
    install -m 0644 "$app/LICENSE" "$pkg/usr/share/doc/ssh-manager-desktop/copyright"
    install -m 0644 "$source_dir/ssh-manager.desktop" "$pkg/usr/share/applications/ssh-manager.desktop"
    ln -s ../lib/ssh-manager/ssh-manager "$pkg/usr/bin/ssh-manager"
    cat > "$pkg/DEBIAN/control" <<EOF
Package: ssh-manager-desktop
Version: $version
Section: net
Priority: optional
Architecture: all
Maintainer: SSH Manager contributors <3994042+Clarkufa@users.noreply.github.com>
Depends: python3 (>= 3.10), python3-gi, gir1.2-gtk-3.0, gir1.2-vte-2.91, python3-paramiko, python3-cryptography, openssh-client, libsecret-tools
Homepage: https://github.com/Clarkufa/sshmanager
Description: Native SSH and SFTP desktop manager
 OpenSSH connection profiles, embedded VTE sessions and graphical SFTP
 using GTK, with desktop password storage through Secret Service.
EOF
    dpkg-deb --root-owner-group --build "$pkg" "$output/ssh-manager-desktop_${version}_all.deb"
fi
printf '%s\n' "$output/ssh-manager-linux-$version.tar.gz"
