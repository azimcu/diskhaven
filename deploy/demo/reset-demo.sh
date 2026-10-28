#!/bin/bash
# Puts the demo back to its clean copy (hourly timer, and after every CI deploy).
set -euo pipefail
C=${HV_DEMO_COMPOSE:?}; ROOT=${HV_DEMO_ROOT:?}
[ -d "$ROOT/clean/data" ] || { echo "no clean copy in $ROOT/clean"; exit 1; }
docker compose -f "$C" stop
mountpoint -q "$ROOT/live" || { echo "$ROOT/live (1 GB demo disk) is not mounted"; exit 1; }
rsync -a --delete --exclude=/lost+found "$ROOT/clean/" "$ROOT/live/"
docker compose -f "$C" up -d
