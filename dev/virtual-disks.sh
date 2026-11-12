#!/bin/bash
# Simulated disks for the test instance: disk images on the system SSD attached as loop devices, one per
# filesystem DiskHaven supports. Lets every disk feature be tested without touching a real disk.
#   dev/virtual-disks.sh up      create (first time) and attach
#   dev/virtual-disks.sh down    unmount and detach (images stay)
#   dev/virtual-disks.sh status
# Sizes can be raised later with SIZE_<NAME>=..., e.g. SIZE_ARSIV=60G (only when the image is created).
set -euo pipefail
DIR=/srv/diskhaven-test/images
# name:filesystem:size:label  (labels: exFAT max 11, FAT max 11 upper case)
DISKS=("ARSIV:ext4:${SIZE_ARSIV:-8G}:arsiv" "USB-EXFAT:exfat:${SIZE_USB_EXFAT:-4G}:USB-EXFAT" "USB-NTFS:ntfs:${SIZE_USB_NTFS:-4G}:USB-NTFS" "USB-FAT:vfat:${SIZE_USB_FAT:-2G}:USBFAT")

attached() { losetup -j "$1" -O NAME --noheadings; }

case "${1:-}" in
  up)
    sudo mkdir -p "$DIR"
    for d in "${DISKS[@]}"; do
      IFS=: read -r name fs size label <<<"$d"; img="$DIR/$name.img"
      if [ ! -f "$img" ]; then
        sudo truncate -s "$size" "$img"
        case $fs in
          ext4)  sudo mkfs.ext4 -q -L "$label" "$img" ;;
          exfat) sudo mkfs.exfat -L "$label" "$img" >/dev/null ;;
          ntfs)  sudo mkfs.ntfs -q -F -Q -L "$label" "$img" ;;
          vfat)  sudo mkfs.vfat -n "$label" "$img" >/dev/null ;;
        esac
      fi
      [ -n "$(attached "$img")" ] || sudo losetup -f --show "$img" >/dev/null
      echo "$name ($fs) -> $(attached "$img")"
    done ;;
  down)
    for d in "${DISKS[@]}"; do
      IFS=: read -r name _ _ _ <<<"$d"; img="$DIR/$name.img"; dev=$(attached "$img")
      [ -z "$dev" ] && continue
      { findmnt -rn -S "$dev" -o TARGET || true; } | while read -r m; do sudo umount "$m"; done
      sudo losetup -d "$dev"; echo "$name detached"
    done ;;
  status)
    losetup -l | grep -F "$DIR" || echo "no virtual disks attached" ;;
  *) echo "usage: $0 up|down|status"; exit 1 ;;
esac
