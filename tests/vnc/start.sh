#!/bin/sh
# Starts sshd and a TigerVNC server on display :1 (port 5901), then keeps the container alive.
set -e
/usr/sbin/sshd
rm -f /tmp/.X1-lock /tmp/.X11-unix/X1
su tester -c "vncserver :1 -geometry 1280x800 -depth 24 -localhost no -SecurityTypes $SECURITY -xstartup /home/tester/.vnc/xstartup"
exec sleep infinity
