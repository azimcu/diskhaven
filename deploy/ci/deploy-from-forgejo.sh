#!/bin/bash
# Host-side deployer (root, systemd timer every 2 minutes). The CI runner has no access to this host;
# this script only reads Forgejo and acts on commits CI has marked green:
#   - newest green commit on main  -> rebuilt as homevault:demo and put on the demo (then reset)
#   - newest green tag v*          -> rebuilt as homevault:latest and put on the real instance
# A failed health check rolls back to the previous image. Results are posted back to Forgejo as
# commit statuses "deploy/demo" and "deploy/prod".
set -euo pipefail
API=${HV_FORGEJO_API:-http://127.0.0.1:3005/api/v1}
REPO=${HV_FORGEJO_REPO:-azimcu/homevault}
TOKEN=$(cat "${HV_FORGEJO_TOKEN_FILE:-/etc/homevault-ci/token}")
STATE=/var/lib/homevault-ci; SRC=$STATE/src
PROD_COMPOSE=${HV_PROD_COMPOSE:-/home/azim/projeler/homevault/deploy/compose.yml}
umask 077; install -d -m 700 $STATE
exec 9>/run/homevault-ci.lock; flock -n 9 || exit 0

api() { curl -fsS -m 20 -H "Authorization: token $TOKEN" "$API/repos/$REPO/$1"; }
status() {  # sha context state description
    curl -fsS -m 20 -o /dev/null -H "Authorization: token $TOKEN" -H 'Content-Type: application/json' \
        -d "$(jq -nc --arg c "$2" --arg s "$3" --arg d "$4" '{context:$c,state:$s,description:$d}')" "$API/repos/$REPO/statuses/$1" || true; }
green() { [ "$(api "commits/$1/status" | jq -r '[.statuses[] | select(.context | startswith("deploy/") | not)] as $s
          | if ($s | length) == 0 then "none" elif all($s[]; .status == "success") then "success" else "no" end')" = success ]; }
healthy() { for _ in $(seq 1 60); do curl -fs -m 2 "$1" | grep -q ok && return 0; sleep 2; done; return 1; }
build() {   # sha tag
    # the token goes in per command (-c before the subcommand), never into .git/config
    [ -d $SRC/.git ] || git -c "http.extraHeader=Authorization: token $TOKEN" clone -q "http://127.0.0.1:3005/$REPO.git" $SRC
    git -C $SRC -c "http.extraHeader=Authorization: token $TOKEN" fetch -q --tags origin
    git -C $SRC checkout -q --detach "$1"
    docker build -q -t "$2" $SRC >/dev/null
}

# ---- demo: newest green main
SHA=$(api branches/main | jq -r .commit.id); [[ "$SHA" =~ ^[0-9a-f]{40}$ ]] || exit 1
if [ "$SHA" != "$(cat $STATE/demo 2>/dev/null)" ] && green "$SHA"; then
    echo "demo <- $SHA"; status "$SHA" deploy/demo pending "deploying to demo"
    docker tag homevault:demo homevault:demo-previous 2>/dev/null || true
    if build "$SHA" homevault:demo && /usr/local/sbin/homevault-demo-reset && healthy http://127.0.0.1:8097/api/health; then
        echo "$SHA" > $STATE/demo; status "$SHA" deploy/demo success "live on demo.azimunal.com.tr"
    else
        docker tag homevault:demo-previous homevault:demo 2>/dev/null && /usr/local/sbin/homevault-demo-reset || true
        echo "$SHA" > $STATE/demo; status "$SHA" deploy/demo failure "demo deploy failed, previous version kept"
    fi
fi

# ---- real instance: newest green v* tag (created on purpose: "yayınla")
TAG=$(api "tags?limit=20" | jq -r '[.[] | select(.name | test("^v[0-9]"))] | sort_by(.name | ltrimstr("v") | split(".") | map(tonumber? // 0)) | last | .name // empty')
if [[ "$TAG" =~ ^v[0-9]+(\.[0-9]+)*$ ]] && [ "$TAG" != "$(cat $STATE/prod 2>/dev/null)" ]; then
    TSHA=$(api "tags/$TAG" | jq -r .commit.sha)
    if green "$TSHA"; then
        echo "prod <- $TAG ($TSHA)"; status "$TSHA" deploy/prod pending "deploying $TAG"
        docker tag homevault:latest homevault:previous
        if build "$TSHA" "homevault:$TAG" && docker tag "homevault:$TAG" homevault:latest &&
           docker compose -f "$PROD_COMPOSE" up -d --no-build && healthy http://127.0.0.1:8095/api/health; then
            echo "$TAG" > $STATE/prod; status "$TSHA" deploy/prod success "$TAG live"
        else
            docker tag homevault:previous homevault:latest && docker compose -f "$PROD_COMPOSE" up -d --no-build || true
            echo "$TAG" > $STATE/prod; status "$TSHA" deploy/prod failure "$TAG failed, previous version restored"
        fi
    fi
fi
