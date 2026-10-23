#!/bin/bash
# Simulated external USB disks for development: small exFAT disk images attached as loop devices.
# Lets disk features (detect, mount, eject, sync) be tested without touching real disks.
#   dev/virtual-disks.sh up   [image-dir]   create (first time) and attach
#   dev/virtual-disks.sh down [image-dir]   unmount and detach
#   dev/virtual-disks.sh status
set -euo pipefail
DIR=${2:-/mnt/immich/homevault-dev/disks}
# name:size  (sizes mirror the real 2 TB / 1 TB / 1 TB disks at a tiny scale)
DISKS=("USB-2TB:400M" "USB-1TB-A:200M" "USB-1TB-B:200M")

attached() { losetup -j "$1" -O NAME --noheadings; }

case "${1:-}" in
  up)
    mkdir -p "$DIR"
    for d in "${DISKS[@]}"; do
      name=${d%%:*}; size=${d##*:}; img="$DIR/$name.img"
      if [ ! -f "$img" ]; then
        truncate -s "$size" "$img"
        mkfs.exfat -L "${name:0:11}" "$img" >/dev/null   # exFAT labels are max 11 chars
      fi
      [ -n "$(attached "$img")" ] || sudo losetup -f --show "$img" >/dev/null
      echo "$name -> $(attached "$img")"
    done ;;
  down)
    for d in "${DISKS[@]}"; do
      img="$DIR/${d%%:*}.img"; dev=$(attached "$img")
      [ -z "$dev" ] && continue
      { findmnt -rn -S "$dev" -o TARGET || true; } | while read -r m; do sudo umount "$m"; done
      sudo losetup -d "$dev"; echo "${d%%:*} detached"
    done ;;
  status)
    losetup -l | grep -F "$DIR" || echo "no virtual disks attached" ;;
  *) echo "usage: $0 up|down|status [image-dir]"; exit 1 ;;
esac
