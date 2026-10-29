#!/bin/bash
# Removes DiskHaven (container, image, disk agent). Data and settings folders are KEPT;
# delete them yourself if you really want to: /srv/diskhaven/data, /srv/diskhaven/config
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run with sudo."; exit 1; }
REPO=$(cd "$(dirname "$0")/.." && pwd)
docker compose -f "$REPO/deploy/compose.yml" --env-file "$REPO/deploy/.env" down --rmi local || true
for m in $(findmnt -rn -o TARGET | grep '^/srv/diskhaven/disks/' || true); do umount "$m"; done
systemctl disable --now diskhaven-diskd 2>/dev/null || true
rm -f /etc/systemd/system/diskhaven-diskd.service && systemctl daemon-reload
rm -rf /usr/local/lib/diskhaven /run/diskhaven
echo "Removed. Kept: /srv/diskhaven/data, /srv/diskhaven/config, /etc/diskhaven"
