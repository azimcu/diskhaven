#!/bin/bash
# Creates the public demo instance. Run as root from the repo:  sudo bash deploy/demo/install-demo.sh
# Safe to run again: the clean copy is rebuilt only with --reseed.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd); REPO=$(cd "$HERE/../.." && pwd)
ROOT=/srv/homevault-demo
PASSWORD=homevault-demo          # shown on the demo login page on purpose
PY=${DEMO_PYTHON:-/home/azim/.local/share/claude-tarayici/venv/bin/python}
export PLAYWRIGHT_BROWSERS_PATH=${PLAYWRIGHT_BROWSERS_PATH:-/home/azim/.local/share/claude-tarayici/browsers}

# the live copy sits on its own 1 GB disk image, so nothing a visitor does can fill the host disk
if ! mountpoint -q $ROOT/live; then
    mkdir -p $ROOT/live
    [ -f $ROOT/live.img ] || { fallocate -l 1G $ROOT/live.img; mkfs.ext4 -q -L hv-demo -m 0 $ROOT/live.img; }
    grep -q "$ROOT/live.img" /etc/fstab || { cp -a /etc/fstab /etc/fstab.before-homevault-demo
        echo "$ROOT/live.img $ROOT/live ext4 loop,nofail,noatime,nodev,nosuid,noexec 0 2" >> /etc/fstab; systemctl daemon-reload; }
    mount $ROOT/live
fi
getent passwd homevault-demo >/dev/null || useradd -r -s /usr/sbin/nologin -d $ROOT homevault-demo
HV_UID=$(id -u homevault-demo); HV_GID=$(id -g homevault-demo)
cat > $HERE/.env <<ENV
HV_UID=$HV_UID
HV_GID=$HV_GID
HV_DEMO_ROOT=$ROOT
HV_DEMO_PASSWORD=$PASSWORD
ENV
docker image inspect homevault:demo >/dev/null 2>&1 || docker build -t homevault:demo "$REPO"

if [ ! -d $ROOT/clean/data ] || [ "${1:-}" = --reseed ]; then
    docker compose -f $HERE/compose.yml down 2>/dev/null || true
    rm -rf $ROOT/clean $ROOT/live/data $ROOT/live/config; mkdir -p $ROOT/clean/config
    "$PY" "$REPO/deploy/demo/seed-demo.py" $ROOT/clean/data
    chown -R $HV_UID:$HV_GID $ROOT/clean
    # first start on the clean copy, create the demo account with the setup code, stop
    cp -a $ROOT/clean/data $ROOT/clean/config $ROOT/live/
    docker compose -f $HERE/compose.yml up -d
    for i in $(seq 1 60); do curl -fs -m 2 http://127.0.0.1:8097/api/health >/dev/null && break; sleep 1; done
    TOKEN=$(cat $ROOT/live/config/setup-token.txt)
    curl -fs -m 10 -H 'X-HomeVault: 1' -H 'Content-Type: application/json' \
        -d "{\"token\":\"$TOKEN\",\"userName\":\"demo\",\"password\":\"$PASSWORD\"}" http://127.0.0.1:8097/api/auth/setup >/dev/null
    docker compose -f $HERE/compose.yml stop
    rm -rf $ROOT/clean; mkdir -p $ROOT/clean
    mv $ROOT/live/data $ROOT/live/config $ROOT/clean/
    rm -rf $ROOT/clean/data/.homevault/uploads/* 2>/dev/null || true
fi

install -m 0755 $HERE/reset-demo.sh /usr/local/sbin/homevault-demo-reset
cat > /etc/systemd/system/homevault-demo-reset.service <<UNIT
[Unit]
Description=HomeVault demo: reset to the clean copy
After=docker.service
[Service]
Type=oneshot
Environment=HV_DEMO_COMPOSE=$HERE/compose.yml HV_DEMO_ROOT=$ROOT
ExecStart=/usr/local/sbin/homevault-demo-reset
UNIT
cat > /etc/systemd/system/homevault-demo-reset.timer <<UNIT
[Unit]
Description=HomeVault demo: reset every hour
[Timer]
OnCalendar=hourly
Persistent=true
[Install]
WantedBy=timers.target
UNIT
systemctl daemon-reload
systemctl enable --now homevault-demo-reset.timer
systemctl start homevault-demo-reset.service
for i in $(seq 1 60); do curl -fs -m 2 http://127.0.0.1:8097/api/health >/dev/null && break; sleep 1; done
curl -fs -m 5 http://127.0.0.1:8097/api/health && echo " demo ready on :8097"
