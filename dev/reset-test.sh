#!/bin/bash
# Puts the test instance back to a clean state for the next test run: disks unmounted, virtual disk images
# formatted again, disk registrations forgotten. The test account and the main storage stay.
# Only /srv/diskhaven-test and the test agent are touched.
set -euo pipefail
cd "$(dirname "$0")"
docker compose stop >/dev/null
for m in $(findmnt -rn -o TARGET | grep '^/srv/diskhaven-test/disks/' || true); do sudo umount "$m"; done
./virtual-disks.sh down >/dev/null || true
sudo find /srv/diskhaven-test/disks -mindepth 1 -maxdepth 1 -type d -empty -delete
sudo rm -f /srv/diskhaven-test/images/*.img /srv/diskhaven-test/config/disks.json /var/lib/diskhaven-test/diskd-known.json
sudo rm -rf /srv/diskhaven-test/config/sync-history
./setup-test.sh >/dev/null
echo "test instance reset"
