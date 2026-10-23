#!/bin/bash
# HomeVault installer for Ubuntu Server 24.04.
#   sudo deploy/install.sh            install or update (safe to run again)
# Installs Docker (official repository), the disk agent (systemd, root) and the HomeVault
# container. Existing data and settings are never deleted. Settings via environment:
#   HV_USER    account that owns the files        (default: the user running sudo)
#   HV_DATA    folder HomeVault serves            (default: /srv/homevault/data)
#   HV_CONFIG  accounts, disk settings, reports   (default: /srv/homevault/config)
#   HV_PORT    web port                           (default: 8095)
set -euo pipefail

[ "$(id -u)" -eq 0 ] || { echo "Run with sudo."; exit 1; }
. /etc/os-release
[ "$ID" = ubuntu ] || echo "Warning: tested on Ubuntu 24.04 only (found $PRETTY_NAME)."

REPO=$(cd "$(dirname "$0")/.." && pwd)
HV_USER=${HV_USER:-${SUDO_USER:-}}
[ -n "$HV_USER" ] && id "$HV_USER" >/dev/null 2>&1 || { echo "Set HV_USER to an existing account."; exit 1; }
HV_UID=$(id -u "$HV_USER"); HV_GID=$(id -g "$HV_USER")
HV_DATA=${HV_DATA:-/srv/homevault/data}
HV_CONFIG=${HV_CONFIG:-/srv/homevault/config}
HV_PORT=${HV_PORT:-8095}
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
usermod -aG docker "$HV_USER"

step "Folders"
install -d -o "$HV_UID" -g "$HV_GID" "$HV_DATA" "$HV_CONFIG"
install -d /srv/homevault/disks /var/lib/homevault /etc/homevault

step "Disk agent"
install -d /usr/local/lib/homevault
install -m 0644 "$REPO/agent/homevault-diskd.py" /usr/local/lib/homevault/
if [ ! -f /etc/homevault/diskd.json ]; then
  cat > /etc/homevault/diskd.json <<JSON
{
  "mountRoot": "/srv/homevault/disks",
  "socket": "/run/homevault/diskd.sock",
  "stateFile": "/var/lib/homevault/diskd-known.json",
  "uid": $HV_UID,
  "gid": $HV_GID,
  "usb": true,
  "virtualDir": null
}
JSON
fi
install -m 0644 "$REPO/agent/homevault-diskd.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable homevault-diskd >/dev/null
systemctl restart homevault-diskd

step "HomeVault container"
cat > "$REPO/deploy/.env" <<ENV
HV_UID=$HV_UID
HV_GID=$HV_GID
HV_DATA=$HV_DATA
HV_CONFIG=$HV_CONFIG
HV_PORT=$HV_PORT
ENV
docker compose -f "$REPO/deploy/compose.yml" --env-file "$REPO/deploy/.env" up -d --build

step "Done"
for i in $(seq 30); do curl -fs "http://127.0.0.1:$HV_PORT/api/health" >/dev/null && break; sleep 1; done
curl -fs "http://127.0.0.1:$HV_PORT/api/health" >/dev/null && echo "HomeVault is running: http://$(hostname -I | awk '{print $1}'):$HV_PORT" \
  || { echo "HomeVault did not answer; see: docker logs homevault"; exit 1; }
[ -f "$HV_CONFIG/setup-token.txt" ] && echo "First login: setup code is in $HV_CONFIG/setup-token.txt"
