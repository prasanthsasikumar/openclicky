#!/usr/bin/env bash
#
# Deploy the backend to the VPS as a Docker container behind Caddy.
#
#   scripts/deploy-backend.sh            # rsync the repo, build the image on the server, (re)start it
#   scripts/deploy-backend.sh --env      # ...and also upload backend/.dev.vars as /opt/openclicky/backend.env
#
# First-time server setup is included: /opt/openclicky, the Caddy site block, ufw already allows 443.
# Needs: ssh root@$SERVER working with a key; DNS A record api.openclicky.flowsxr.com -> the server.
set -euo pipefail

SERVER="${OPENCLICKY_SERVER:-root@104.168.48.121}"
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
ssh "$SERVER" bash -s <<'REMOTE'
set -euo pipefail
cd /opt/openclicky
[[ -f backend.env ]] || { echo "missing /opt/openclicky/backend.env (run with --env once)" >&2; exit 1; }
cp src/backend/deploy/docker-compose.yml docker-compose.yml
docker build -q -f src/backend/Dockerfile -t openclicky-backend:latest src >/dev/null
docker compose up -d --remove-orphans
# Caddy site block, added once.
if ! grep -q "api.openclicky.flowsxr.com" /etc/caddy/Caddyfile; then
  { echo; cat src/backend/deploy/Caddyfile.openclicky; } >> /etc/caddy/Caddyfile
  caddy validate --config /etc/caddy/Caddyfile >/dev/null && systemctl reload caddy
  echo "  added the Caddy site block"
fi
sleep 2
curl -sf http://127.0.0.1:8787/health && echo "  backend healthy on 127.0.0.1:8787"
docker image prune -f >/dev/null
REMOTE
echo "▸ done — https://api.openclicky.flowsxr.com/health once DNS resolves"
