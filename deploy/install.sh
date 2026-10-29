#!/bin/bash
# DiskHaven installer for Ubuntu Server 24.04.
#   sudo deploy/install.sh            install or update (safe to run again)
# Installs Docker (official repository), the disk agent (systemd, root) and the DiskHaven
# container. Existing data and settings are never deleted. Settings via environment:
#   DH_USER    account that owns the files        (default: the user running sudo)
#   DH_DATA    folder DiskHaven serves            (default: /srv/diskhaven/data)
#   DH_CONFIG  accounts, disk settings, reports   (default: /srv/diskhaven/config)
#   DH_PORT    web port                           (default: 8095)
set -euo pipefail

[ "$(id -u)" -eq 0 ] || { echo "Run with sudo."; exit 1; }
. /etc/os-release
[ "$ID" = ubuntu ] || echo "Warning: tested on Ubuntu 24.04 only (found $PRETTY_NAME)."

REPO=$(cd "$(dirname "$0")/.." && pwd)
DH_USER=${DH_USER:-${SUDO_USER:-}}
[ -n "$DH_USER" ] && id "$DH_USER" >/dev/null 2>&1 || { echo "Set DH_USER to an existing account."; exit 1; }
DH_UID=$(id -u "$DH_USER"); DH_GID=$(id -g "$DH_USER")
DH_DATA=${DH_DATA:-/srv/diskhaven/data}
DH_CONFIG=${DH_CONFIG:-/srv/diskhaven/config}
DH_PORT=${DH_PORT:-8095}
step() { echo; echo "==> $*"; }

step "System packages"
apt-get update -q
DEBIAN_FRONTEND=noninteractive apt-get install -y -q ca-certificates curl git rsync \
  smartmontools hdparm udisks2 exfatprogs ntfs-3g python3

step "Docker"
if ! command -v docker >/dev/null || ! docker compose version >/dev/null 2>&1; then
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" \
    > /etc/apt/sources.list.d/docker.list
  apt-get update -q
  DEBIAN_FRONTEND=noninteractive apt-get install -y -q docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
fi
usermod -aG docker "$DH_USER"

step "Folders"
install -d -o "$DH_UID" -g "$DH_GID" "$DH_DATA" "$DH_CONFIG"
install -d /srv/diskhaven/disks /var/lib/diskhaven /etc/diskhaven

step "Disk agent"
install -d /usr/local/lib/diskhaven
install -m 0644 "$REPO/agent/diskhaven-diskd.py" /usr/local/lib/diskhaven/
if [ ! -f /etc/diskhaven/diskd.json ]; then
  cat > /etc/diskhaven/diskd.json <<JSON
{
  "mountRoot": "/srv/diskhaven/disks",
  "socket": "/run/diskhaven/diskd.sock",
  "stateFile": "/var/lib/diskhaven/diskd-known.json",
  "uid": $DH_UID,
  "gid": $DH_GID,
  "usb": true,
  "virtualDir": null
}
JSON
fi
install -m 0644 "$REPO/agent/diskhaven-diskd.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable diskhaven-diskd >/dev/null
systemctl restart diskhaven-diskd

step "DiskHaven container"
# extra settings someone added (e.g. DH_OTLP_* for observability) survive a re-install
EXTRA=$(grep -E '^DH_OTLP_' "$REPO/deploy/.env" 2>/dev/null || true)
cat > "$REPO/deploy/.env" <<ENV
DH_UID=$DH_UID
DH_GID=$DH_GID
DH_DATA=$DH_DATA
DH_CONFIG=$DH_CONFIG
DH_PORT=$DH_PORT
ENV
[ -n "$EXTRA" ] && echo "$EXTRA" >> "$REPO/deploy/.env"
docker compose -f "$REPO/deploy/compose.yml" --env-file "$REPO/deploy/.env" up -d --build

step "Done"
for i in $(seq 30); do curl -fs "http://127.0.0.1:$DH_PORT/api/health" >/dev/null && break; sleep 1; done
curl -fs "http://127.0.0.1:$DH_PORT/api/health" >/dev/null && echo "DiskHaven is running: http://$(hostname -I | awk '{print $1}'):$DH_PORT" \
  || { echo "DiskHaven did not answer; see: docker logs diskhaven"; exit 1; }
[ -f "$DH_CONFIG/setup-token.txt" ] && echo "First login: setup code is in $DH_CONFIG/setup-token.txt"
