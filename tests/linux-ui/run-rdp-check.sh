#!/bin/sh
# Starts the xrdp test server and runs the Linux app's embedded RDP check against it (Docker required).
set -eu
cd "$(dirname "$0")/../.."
docker build -q -t sshm-xrdp-test tests/xrdp >/dev/null
docker build -q -t sshm-linux-dev tests/linux-ui >/dev/null
docker network create sshm-net >/dev/null 2>&1 || true
docker rm -f sshm-xrdp >/dev/null 2>&1 || true
mkdir -p build/rdp-shared
rm -f build/rdp-shared/size
docker run -d --name sshm-xrdp --network sshm-net -v "$PWD/build/rdp-shared:/shared" sshm-xrdp-test >/dev/null
sleep 3
docker exec -d sshm-xrdp sh -c 'while true; do su tester -c remote-size > /shared/size.tmp 2>/dev/null && mv /shared/size.tmp /shared/size; sleep 0.5; done'
status=0
docker run --rm --network sshm-net -v "$PWD:/work" sshm-linux-dev sh -c \
    'timeout 180 xvfb-run -a -s "-screen 0 1280x800x24" dbus-run-session python3 -B tests/linux-ui/rdp_embedded.py sshm-xrdp' \
    2>&1 | grep -v -E "AT-SPI|dbus-daemon|^$" || status=$?
docker rm -f sshm-xrdp >/dev/null
exit $status
