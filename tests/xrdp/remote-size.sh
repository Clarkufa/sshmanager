#!/bin/sh
# Prints the current screen size of the newest xrdp X session.
display=$(ps -o args= -C Xorg | sed -n 's/.*Xorg \(:[0-9]*\).*/\1/p' | tail -1)
DISPLAY=$display XAUTHORITY=/home/tester/.Xauthority xrandr --current 2>&1 | sed -n '1s/.*current \([0-9]* x [0-9]*\).*/\1/p'
