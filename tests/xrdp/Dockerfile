# Throwaway RDP server for testing SSH Manager's RDP client (test account only).
FROM ubuntu:24.04
RUN apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        xrdp xorgxrdp xfce4 xfce4-terminal dbus-x11 x11-xserver-utils xauth ssl-cert \
    && rm -rf /var/lib/apt/lists/* \
    && adduser xrdp ssl-cert \
    && useradd -m -s /bin/bash tester && echo 'tester:Rdp-Test-2026' | chpasswd \
    && echo xfce4-session > /home/tester/.xsession && chown tester:tester /home/tester/.xsession
COPY remote-size.sh /usr/local/bin/remote-size
RUN chmod 755 /usr/local/bin/remote-size
EXPOSE 3389
CMD ["sh", "-c", "rm -f /var/run/xrdp/*.pid; /usr/sbin/xrdp-sesman && exec /usr/sbin/xrdp --nodaemon"]
