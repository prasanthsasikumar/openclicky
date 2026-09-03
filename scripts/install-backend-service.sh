#!/usr/bin/env bash
# Install (or refresh) a launchd user agent that keeps the OpenClicky backend running at login,
# so the menu-bar app works when launched from Spotlight. Reads backend/.dev.vars via node.ts.
#   scripts/install-backend-service.sh            # install + start
#   scripts/install-backend-service.sh --uninstall
set -euo pipefail
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LABEL="org.openclicky.backend"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
NODE_BIN="$(command -v node)"
LOG_DIR="$HOME/Library/Logs/OpenClicky"
mkdir -p "$LOG_DIR" "$HOME/Library/LaunchAgents"

if [[ "${1:-}" == "--uninstall" ]]; then
  launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
  rm -f "$PLIST"
  echo "removed $LABEL"
  exit 0
fi

(cd "$REPO_DIR" && npm run build -w backend >/dev/null)
cat > "$PLIST" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$LABEL</string>
  <key>ProgramArguments</key>
  <array>
    <string>$NODE_BIN</string>
    <string>$REPO_DIR/backend/dist/node.js</string>
  </array>
  <key>WorkingDirectory</key><string>$REPO_DIR/backend</string>
  <key>EnvironmentVariables</key>
  <dict>
    <key>PORT</key><string>8787</string>
    <key>PATH</key><string>$(dirname "$NODE_BIN"):/usr/local/bin:/usr/bin:/bin</string>
  </dict>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>StandardOutPath</key><string>$LOG_DIR/backend.log</string>
  <key>StandardErrorPath</key><string>$LOG_DIR/backend.err.log</string>
</dict>
</plist>
PLIST
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
launchctl bootstrap "gui/$(id -u)" "$PLIST"
launchctl kickstart -k "gui/$(id -u)/$LABEL"
sleep 2
if curl -sf http://localhost:8787/health >/dev/null; then
  echo "backend service running: $LABEL → http://localhost:8787 (logs in $LOG_DIR)"
else
  echo "backend did not answer on 8787; see $LOG_DIR/backend.err.log" >&2
  exit 1
fi
