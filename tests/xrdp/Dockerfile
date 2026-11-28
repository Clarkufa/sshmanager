# Throwaway RDP and SSH server for testing SSH Manager (test account only).
FROM ubuntu:24.04
RUN apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        xrdp xorgxrdp xfce4 xfce4-terminal dbus-x11 x11-xserver-utils xauth ssl-cert openssh-server fuse xclip \
    && rm -rf /var/lib/apt/lists/* \
    && adduser xrdp ssl-cert \
    && useradd -m -s /bin/bash tester && echo 'tester:Rdp-Test-2026' | chpasswd \
    && echo xfce4-session > /home/tester/.xsession && chown tester:tester /home/tester/.xsession \
    && mkdir -p /run/sshd && printf 'PasswordAuthentication yes\nAllowTcpForwarding yes\n' > /etc/ssh/sshd_config.d/test.conf
COPY remote-size.sh /usr/local/bin/remote-size
RUN chmod 755 /usr/local/bin/remote-size
EXPOSE 3389 22
CMD ["sh", "-c", "rm -f /var/run/xrdp/*.pid; /usr/sbin/sshd && /usr/sbin/xrdp-sesman && exec /usr/sbin/xrdp --nodaemon"]
