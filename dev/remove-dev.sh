#!/bin/bash
# Removes the DiskHaven test instance from this machine. Asks before deleting its data and virtual disks.
set -euo pipefail
cd "$(dirname "$0")"
docker compose down --rmi all --volumes
sudo systemctl disable --now diskhaven-test-diskd 2>/dev/null || true
sudo rm -f /etc/systemd/system/diskhaven-test-diskd.service && sudo systemctl daemon-reload
for m in $(findmnt -rn -o TARGET | grep '^/srv/diskhaven-test/disks/' || true); do sudo umount "$m"; done
./virtual-disks.sh down || true
sudo rm -rf /usr/local/lib/diskhaven-test /etc/diskhaven-test /var/lib/diskhaven-test /run/diskhaven-test
read -rp "Also delete test data and virtual disk images in /srv/diskhaven-test? [y/N] " a
[ "$a" = y ] && sudo rm -rf /srv/diskhaven-test && echo "deleted"
