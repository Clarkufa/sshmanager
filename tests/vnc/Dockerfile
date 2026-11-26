# Throwaway VNC and SSH server for testing SSH Manager (test account only).
# VNC password: VncTest1 (classic VNC passwords use 8 characters). SSH: tester / Vnc-Test-2026.
FROM ubuntu:24.04
RUN apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        tigervnc-standalone-server tigervnc-tools xfce4 xfce4-terminal dbus-x11 x11-xserver-utils xauth \
        openssh-server \
    && rm -rf /var/lib/apt/lists/* \
    && useradd -m -s /bin/bash tester && echo 'tester:Vnc-Test-2026' | chpasswd \
    && mkdir -p /home/tester/.vnc && printf 'VncTest1\n' | vncpasswd -f > /home/tester/.vnc/passwd \
    && chmod 600 /home/tester/.vnc/passwd && chown -R tester:tester /home/tester/.vnc \
    && printf '#!/bin/sh\nunset SESSION_MANAGER DBUS_SESSION_BUS_ADDRESS\nexec dbus-launch --exit-with-session startxfce4\n' > /home/tester/.vnc/xstartup \
    && chmod 755 /home/tester/.vnc/xstartup && chown tester:tester /home/tester/.vnc/xstartup \
    && mkdir -p /run/sshd && printf 'PasswordAuthentication yes\nAllowTcpForwarding yes\n' > /etc/ssh/sshd_config.d/test.conf
COPY start.sh /usr/local/bin/start-vnc-bench
RUN chmod 755 /usr/local/bin/start-vnc-bench
EXPOSE 5901 22
# SECURITY=None starts the server without a password (for the "no password" check).
ENV SECURITY=VncAuth
CMD ["/usr/local/bin/start-vnc-bench"]
