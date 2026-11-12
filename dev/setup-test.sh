#!/bin/bash
# Sets up (or updates) the test instance: folders on the SSD, the test disk agent, virtual disks, the container.
# Touches nothing of the real instance (/srv/diskhaven, diskhaven-diskd, port 8095).
set -euo pipefail
cd "$(dirname "$0")"
sudo mkdir -p /srv/diskhaven-test/{data,config,disks,images} /usr/local/lib/diskhaven-test /etc/diskhaven-test
sudo chown 1000:1000 /srv/diskhaven-test/data /srv/diskhaven-test/config
sudo install -m 0644 ../agent/diskhaven-diskd.py /usr/local/lib/diskhaven-test/
sudo install -m 0644 agent/diskd.json /etc/diskhaven-test/diskd.json
sudo install -m 0644 agent/diskhaven-test-diskd.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable diskhaven-test-diskd >/dev/null 2>&1
sudo systemctl restart diskhaven-test-diskd
./virtual-disks.sh up
docker compose build
docker compose up -d
echo "test instance: http://192.168.1.249:8090"
