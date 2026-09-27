#!/usr/bin/env bash
#
# Deploy the backend to the VPS as a Docker container behind Caddy.
#
#   OPENCLICKY_SERVER=user@host scripts/deploy-backend.sh        # rsync, build the image, (re)start it
#   OPENCLICKY_SERVER=user@host scripts/deploy-backend.sh --env   # ...and upload backend/.dev.vars too
#
# First-time server setup is included: /opt/openclicky, the Caddy site block, ufw already allows 443.
# Needs: ssh to $OPENCLICKY_SERVER working with a key, and a DNS A record for $OPENCLICKY_API_HOST
# pointing at that server.
#
# There is deliberately no default target. This repository is public, and a default of
# `root@<address>` published both where the server is and that it is reached as root — to anyone
# reading the repo, and to every fork. Set it in your shell or an untracked env file:
#
#   export OPENCLICKY_SERVER=root@api.openclicky.flowsxr.com
#
# Prefer the hostname over a raw IP: the address can change without this command changing, and a
# hostname is already public in a way an origin IP is not.
set -euo pipefail

if [[ -z "${OPENCLICKY_SERVER:-}" ]]; then
  cat >&2 <<'USAGE'
OPENCLICKY_SERVER is not set — this script has no default deploy target on purpose.

  export OPENCLICKY_SERVER=root@api.openclicky.flowsxr.com   # ssh target, host or user@host
  export OPENCLICKY_API_HOST=api.openclicky.flowsxr.com      # optional, the public hostname Caddy serves

Then run this script again.
USAGE
  exit 2
fi

SERVER="$OPENCLICKY_SERVER"
API_HOST="${OPENCLICKY_API_HOST:-api.openclicky.flowsxr.com}"
REMOTE_DIR=/opt/openclicky
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
UPLOAD_ENV=0
for arg in "$@"; do
  case "$arg" in
    --env) UPLOAD_ENV=1 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

echo "▸ syncing source to $SERVER:$REMOTE_DIR/src"
ssh "$SERVER" "mkdir -p $REMOTE_DIR/src"
rsync -az --delete \
  --exclude node_modules --exclude dist --exclude build --exclude .git --exclude '.dev.vars' --exclude '*.profraw' \
  --exclude macos --exclude docs --exclude reference \
  "$REPO_DIR/" "$SERVER:$REMOTE_DIR/src/"

if [[ $UPLOAD_ENV -eq 1 ]]; then
  echo "▸ uploading backend/.dev.vars as $REMOTE_DIR/backend.env"
  scp -q "$REPO_DIR/backend/.dev.vars" "$SERVER:$REMOTE_DIR/backend.env"
  ssh "$SERVER" "chmod 600 $REMOTE_DIR/backend.env"
fi

echo "▸ building the image and starting the container"
ssh "$SERVER" bash -s -- "$API_HOST" <<'REMOTE'
set -euo pipefail
API_HOST="$1"
cd /opt/openclicky
[[ -f backend.env ]] || { echo "missing /opt/openclicky/backend.env (run with --env once)" >&2; exit 1; }
cp src/backend/deploy/docker-compose.yml docker-compose.yml
docker build -q -f src/backend/Dockerfile -t openclicky-backend:latest src >/dev/null
docker compose up -d --remove-orphans
# Caddy site block, added once.
if ! grep -q "$API_HOST" /etc/caddy/Caddyfile; then
  { echo; sed "s/__API_HOST__/$API_HOST/" src/backend/deploy/Caddyfile.openclicky; } >> /etc/caddy/Caddyfile
  caddy validate --config /etc/caddy/Caddyfile >/dev/null && systemctl reload caddy
  echo "  added the Caddy site block"
fi
sleep 2
curl -sf http://127.0.0.1:8787/health && echo "  backend healthy on 127.0.0.1:8787"
docker image prune -f >/dev/null
REMOTE
echo "▸ done — https://$API_HOST/health once DNS resolves"
