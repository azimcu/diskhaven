#!/bin/bash
# Host-side deployer (root, systemd timer every 2 minutes). The CI runner has no access to this host;
# this script only reads Forgejo and acts on commits CI has marked green:
#   - newest green commit on main  -> rebuilt as diskhaven:demo and put on the demo (then reset)
#   - newest green tag v*          -> rebuilt as diskhaven:latest and put on the real instance
# A failed health check rolls back to the previous image. Results are posted back to Forgejo as
# commit statuses "deploy/demo" and "deploy/prod".
set -euo pipefail
API=${DH_FORGEJO_API:-http://127.0.0.1:3005/api/v1}
REPO=${DH_FORGEJO_REPO:-azimcu/diskhaven}
TOKEN=$(cat "${DH_FORGEJO_TOKEN_FILE:-/etc/diskhaven-ci/token}")
STATE=/var/lib/diskhaven-ci; SRC=$STATE/src
PROD_COMPOSE=${DH_PROD_COMPOSE:-/home/azim/projeler/diskhaven/deploy/compose.yml}
install -d -m 700 $STATE   # private state dir; the checkout inside keeps normal file modes (the app must read them)
# the token never goes on a command line (other local users could see it in the process list)
HDR=$STATE/auth-header; (umask 077; printf 'Authorization: token %s\n' "$TOKEN" > $HDR)
# git gets the token only for these commands and only for the Forgejo address (never sent to another host)
fgit() { GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0="http.http://127.0.0.1:3005/.extraHeader" GIT_CONFIG_VALUE_0="Authorization: token $TOKEN" \
         GIT_LFS_SKIP_SMUDGE=1 git "$@"; }
exec 9>/run/diskhaven-ci.lock; flock -n 9 || exit 0

api() { curl -fsS -m 20 -H @$HDR "$API/repos/$REPO/$1"; }
status() {  # sha context state description
    curl -fsS -m 20 -o /dev/null -H @$HDR -H 'Content-Type: application/json' \
        -d "$(jq -nc --arg c "$2" --arg s "$3" --arg d "$4" '{context:$c,state:$s,description:$d}')" "$API/repos/$REPO/statuses/$1" || true; }
green() { [ "$(api "commits/$1/status" | jq -r '[.statuses[] | select(.context | startswith("deploy/") | not)] as $s
          | if ($s | length) == 0 then "none" elif all($s[]; .status == "success") then "success" else "no" end')" = success ]; }
healthy() { for _ in $(seq 1 60); do curl -fs -m 2 "$1" | grep -q ok && return 0; sleep 2; done; return 1; }
build() {   # sha tag
    # the token comes from fgit (environment of that one command): never in .git/config or on the command line
    [ -d $SRC/.git ] || fgit clone -q "http://127.0.0.1:3005/$REPO.git" $SRC
    fgit -C $SRC fetch -q --tags origin
    git -C $SRC checkout -q --detach "$1"
    docker build -q -t "$2" $SRC >/dev/null
}

# ---- demo: newest green main
SHA=$(api branches/main | jq -r .commit.id); [[ "$SHA" =~ ^[0-9a-f]{40}$ ]] || exit 1
if [ "$SHA" != "$(cat $STATE/demo 2>/dev/null)" ] && green "$SHA"; then
    echo "demo <- $SHA"; status "$SHA" deploy/demo pending "deploying to demo"
    docker tag diskhaven:demo diskhaven:demo-previous 2>/dev/null || true
    if build "$SHA" diskhaven:demo && systemctl start diskhaven-demo-reset.service && healthy http://127.0.0.1:8097/api/health; then
        echo "$SHA" > $STATE/demo; status "$SHA" deploy/demo success "live on demo.azimunal.com.tr"
    else
        docker tag diskhaven:demo-previous diskhaven:demo 2>/dev/null && systemctl start diskhaven-demo-reset.service || true
        echo "$SHA" > $STATE/demo; status "$SHA" deploy/demo failure "demo deploy failed, previous version kept"
    fi
fi

# ---- real instance: newest green v* tag (created on purpose: "yayınla")
TAG=$(api "tags?limit=20" | jq -r '[.[] | select(.name | test("^v[0-9]"))] | sort_by(.name | ltrimstr("v") | split(".") | map(tonumber? // 0)) | last | .name // empty')
if [[ "$TAG" =~ ^v[0-9]+(\.[0-9]+)*$ ]] && [ "$TAG" != "$(cat $STATE/prod 2>/dev/null)" ]; then
    TSHA=$(api "tags/$TAG" | jq -r .commit.sha)
    if green "$TSHA"; then
        echo "prod <- $TAG ($TSHA)"; status "$TSHA" deploy/prod pending "deploying $TAG"
        docker tag diskhaven:latest diskhaven:previous
        if build "$TSHA" "diskhaven:$TAG" && docker tag "diskhaven:$TAG" diskhaven:latest &&
           docker compose -f "$PROD_COMPOSE" up -d --no-build && healthy http://127.0.0.1:8095/api/health; then
            echo "$TAG" > $STATE/prod; status "$TSHA" deploy/prod success "$TAG live"
        else
            docker tag diskhaven:previous diskhaven:latest && docker compose -f "$PROD_COMPOSE" up -d --no-build || true
            echo "$TAG" > $STATE/prod; status "$TSHA" deploy/prod failure "$TAG failed, previous version restored"
        fi
    fi
fi
