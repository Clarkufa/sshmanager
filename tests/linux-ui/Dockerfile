# Headless environment for the Linux app: GTK 3, VTE, FreeRDP 3, Xvfb, Secret Service tools.
# Used for UI screenshots and the embedded RDP check against tests/xrdp.
FROM ubuntu:24.04
RUN apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        python3 python3-gi python3-gi-cairo gir1.2-gtk-3.0 gir1.2-vte-2.91 gir1.2-ayatanaappindicator3-0.1 \
        python3-paramiko python3-cryptography openssh-client libsecret-tools \
        freerdp3-x11 xvfb xauth x11-utils dbus-x11 fonts-dejavu-core adwaita-icon-theme \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /work
