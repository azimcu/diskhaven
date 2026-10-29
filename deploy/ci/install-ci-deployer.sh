#!/bin/bash
# Installs the host deployer (see deploy-from-forgejo.sh). Run as root:
#   sudo bash deploy/ci/install-ci-deployer.sh <file-with-forgejo-token>
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
command -v jq >/dev/null || apt-get install -y -q jq
install -d -m 700 /etc/diskhaven-ci
install -m 600 "${1:?token file}" /etc/diskhaven-ci/token
install -m 755 $HERE/deploy-from-forgejo.sh /usr/local/sbin/diskhaven-ci-deploy
cat > /etc/systemd/system/diskhaven-ci-deploy.service <<UNIT
[Unit]
Description=DiskHaven: deploy green Forgejo builds (main -> demo, v* tag -> real instance)
After=docker.service
[Service]
Type=oneshot
ExecStart=/usr/local/sbin/diskhaven-ci-deploy
Nice=10
UNIT
cat > /etc/systemd/system/diskhaven-ci-deploy.timer <<UNIT
[Unit]
Description=DiskHaven: check Forgejo for new green builds every 2 minutes
[Timer]
OnBootSec=3min
OnUnitActiveSec=2min
[Install]
WantedBy=timers.target
UNIT
systemctl daemon-reload
systemctl enable --now diskhaven-ci-deploy.timer
