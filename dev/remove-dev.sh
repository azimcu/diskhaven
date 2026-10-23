#!/bin/bash
# Removes the HomeVault development instance from this machine. Asks before deleting data.
set -euo pipefail
cd "$(dirname "$0")"
docker compose down --rmi all --volumes
# dev disk agent
sudo systemctl disable --now homevault-dev-diskd 2>/dev/null || true
sudo rm -f /etc/systemd/system/homevault-dev-diskd.service && sudo systemctl daemon-reload
for m in $(findmnt -rn -o TARGET | grep '^/srv/homevault-dev/disks/' || true); do sudo umount "$m"; done
sudo rm -rf /usr/local/lib/homevault-dev /etc/homevault-dev /var/lib/homevault-dev /run/homevault-dev /srv/homevault-dev
./virtual-disks.sh down || true
read -rp "Also delete dummy data and virtual disks in /mnt/immich/homevault-dev? [y/N] " a
[ "$a" = y ] && rm -rf /mnt/immich/homevault-dev && echo "deleted"
