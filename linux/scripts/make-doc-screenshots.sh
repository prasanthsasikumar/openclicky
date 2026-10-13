#!/usr/bin/env bash
# Makes docs/media/linux/*.png on a private X display (Xvfb, plus xfwm4 for title bars and a
# compositor when it is installed), so the desktop you are using is never touched.
# Usage: linux/scripts/make-doc-screenshots.sh [--live]
set -euo pipefail
linux_directory="$(cd "$(dirname "$0")/.." && pwd)"
python="${linux_directory}/.venv/bin/python"
[ -x "$python" ] || python=python3

for number in $(seq 80 99); do
  [ -e "/tmp/.X11-unix/X${number}" ] || [ -e "/tmp/.X${number}-lock" ] || break
done
Xvfb ":${number}" -screen 0 1280x800x24 -nolisten tcp >/dev/null 2>&1 &
server=$!
window_manager=""
cleanup() { [ -n "$window_manager" ] && kill "$window_manager" 2>/dev/null; kill "$server" 2>/dev/null; }
trap cleanup EXIT
for _ in $(seq 50); do [ -e "/tmp/.X11-unix/X${number}" ] && break; sleep 0.1; done

if command -v xfwm4 >/dev/null; then
  DISPLAY=":${number}" xfwm4 --compositor=on >/dev/null 2>&1 &
  window_manager=$!
  sleep 1.5
fi

# An empty OpenClicky home unless --live: the Settings picture shows example keys, never yours.
home="$(mktemp -d)"
if [ "${1:-}" = "--live" ]; then home="${OPENCLICKY_HOME:-$HOME/.openclicky}"; fi
# Its own config and state folders: the Settings picture never touches your autostart entry or log.
DISPLAY=":${number}" NO_AT_BRIDGE=1 GDK_BACKEND=x11 OPENCLICKY_HOME="$home" \
  XDG_CONFIG_HOME="$(mktemp -d)" XDG_STATE_HOME="$(mktemp -d)" \
  "$python" "${linux_directory}/scripts/make-doc-screenshots.py" "$@"
