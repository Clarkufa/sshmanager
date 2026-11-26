#!/bin/sh
# Starts the VNC test server and runs the Linux app's embedded VNC check against it (Docker required).
set -eu
cd "$(dirname "$0")/../.."
docker build -q -t sshm-vnc-test tests/vnc >/dev/null
docker build -q -t sshm-linux-dev tests/linux-ui >/dev/null
docker network create sshm-net >/dev/null 2>&1 || true
docker rm -f sshm-vnc-check >/dev/null 2>&1 || true
mkdir -p build/vnc-shared
rm -f build/vnc-shared/restart build/vnc-shared/done
docker run -d --name sshm-vnc-check --network sshm-net sshm-vnc-test >/dev/null
sleep 4
# The check asks for a server restart by creating build/vnc-shared/restart; the file is removed once it is back.
(
    while [ ! -f build/vnc-shared/done ]; do
        if [ -f build/vnc-shared/restart ]; then
            docker restart -t 1 sshm-vnc-check >/dev/null
            sleep 4
            rm -f build/vnc-shared/restart
        fi
        sleep 0.5
    done
) &
watcher=$!
# The check's own exit code decides the result; the log is filtered only for reading.
status=0
docker run --rm --network sshm-net -v "$PWD:/work" sshm-linux-dev sh -c \
    'timeout 240 xvfb-run -a -s "-screen 0 1280x800x24" dbus-run-session python3 -B tests/linux-ui/vnc_embedded.py sshm-vnc-check' \
    > build/vnc-check.log 2>&1 || status=$?
touch build/vnc-shared/done
wait $watcher 2>/dev/null || true
grep -v -E "AT-SPI|dbus-daemon|^$" build/vnc-check.log || true
docker rm -f sshm-vnc-check >/dev/null
[ "$status" -eq 0 ] && echo "PASS embedded VNC" || echo "FAIL embedded VNC (exit $status)"
exit $status
