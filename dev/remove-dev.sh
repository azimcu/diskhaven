#!/bin/bash
# Removes the DiskHaven development instance from this machine. Asks before deleting data.
set -euo pipefail
cd "$(dirname "$0")"
docker compose down --rmi all --volumes
# dev disk agent
sudo systemctl disable --now diskhaven-dev-diskd 2>/dev/null || true
sudo rm -f /etc/systemd/system/diskhaven-dev-diskd.service && sudo systemctl daemon-reload
for m in $(findmnt -rn -o TARGET | grep '^/srv/diskhaven-dev/disks/' || true); do sudo umount "$m"; done
sudo rm -rf /usr/local/lib/diskhaven-dev /etc/diskhaven-dev /var/lib/diskhaven-dev /run/diskhaven-dev /srv/diskhaven-dev
./virtual-disks.sh down || true
read -rp "Also delete dummy data and virtual disks in /mnt/immich/diskhaven-dev? [y/N] " a
[ "$a" = y ] && rm -rf /mnt/immich/diskhaven-dev && echo "deleted"
