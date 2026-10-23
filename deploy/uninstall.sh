#!/bin/bash
# Removes HomeVault (container, image, disk agent). Data and settings folders are KEPT;
# delete them yourself if you really want to: /srv/homevault/data, /srv/homevault/config
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run with sudo."; exit 1; }
REPO=$(cd "$(dirname "$0")/.." && pwd)
docker compose -f "$REPO/deploy/compose.yml" --env-file "$REPO/deploy/.env" down --rmi local || true
for m in $(findmnt -rn -o TARGET | grep '^/srv/homevault/disks/' || true); do umount "$m"; done
systemctl disable --now homevault-diskd 2>/dev/null || true
rm -f /etc/systemd/system/homevault-diskd.service && systemctl daemon-reload
rm -rf /usr/local/lib/homevault /run/homevault
echo "Removed. Kept: /srv/homevault/data, /srv/homevault/config, /etc/homevault"
