#!/bin/bash
# Removes the HomeVault development instance from this machine. Asks before deleting data.
set -euo pipefail
cd "$(dirname "$0")"
docker compose down --rmi all --volumes
./virtual-disks.sh down || true
read -rp "Also delete dummy data and virtual disks in /mnt/immich/homevault-dev? [y/N] " a
[ "$a" = y ] && rm -rf /mnt/immich/homevault-dev && echo "deleted"
