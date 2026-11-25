Name:           ssh-manager-desktop
Version:        %{ssh_manager_version}
Release:        1
Summary:        Native SSH, SFTP and RDP desktop manager
License:        MIT
URL:            https://github.com/Clarkufa/sshmanager
Source0:        ssh-manager-linux-%{version}.tar.gz
Source1:        ssh-manager.desktop
BuildArch:      noarch

Requires:       python3 >= 3.10
Requires:       python3-gobject
Requires:       python3dist(paramiko)
Requires:       python3dist(cryptography)
Requires:       gobject-introspection
Requires:       gtk3
Requires:       vte291
Requires:       /usr/bin/ssh
Requires:       /usr/bin/secret-tool
Recommends:     /usr/bin/pkexec
Recommends:     freerdp
Recommends:     libayatana-appindicator-gtk3

%description
Manage OpenSSH profiles, SSH sessions in an embedded VTE terminal, SFTP
transfers in a two-pane GTK file manager, and RDP profiles for FreeRDP.

%prep
%setup -q -n ssh-manager-%{version}

%install
install -d %{buildroot}%{_bindir} %{buildroot}%{_datadir}/ssh-manager/ssh_manager
install -m 0755 ssh-manager %{buildroot}%{_datadir}/ssh-manager/ssh-manager
install -m 0644 icon.png %{buildroot}%{_datadir}/ssh-manager/icon.png
install -m 0644 ssh_manager/*.py %{buildroot}%{_datadir}/ssh-manager/ssh_manager/
install -D -m 0644 "%{SOURCE1}" %{buildroot}%{_datadir}/applications/ssh-manager.desktop
install -D -m 0644 icon.png %{buildroot}%{_datadir}/icons/hicolor/256x256/apps/ssh-manager.png
ln -s ../share/ssh-manager/ssh-manager %{buildroot}%{_bindir}/ssh-manager

%files
%license LICENSE
%doc README.md
%{_bindir}/ssh-manager
%{_datadir}/ssh-manager/
%{_datadir}/applications/ssh-manager.desktop
%{_datadir}/icons/hicolor/256x256/apps/ssh-manager.png
