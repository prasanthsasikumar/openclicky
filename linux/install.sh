#!/bin/sh
# OpenClicky for Linux, installed for this user: a virtualenv in ~/.local/openclicky, the command in
# ~/.local/bin, then OpenClicky itself starts and its first-run setup opens (on Wayland it asks for
# your password once there, to read the keyboard and type). Nothing here needs root.
#
#   sh install.sh                                    from a clone of the repository (this folder)
#   sh install.sh openclicky_linux-…-py3-none-any.whl   a downloaded release
#
# Remove: `openclicky-linux setup --remove` (Wayland only), then
#   rm -rf ~/.local/openclicky ~/.local/bin/openclicky-linux ~/.local/share/applications/openclicky.desktop \
#          ~/.config/autostart/openclicky.desktop
set -eu

here=$(cd "$(dirname "$0")" && pwd)
source_package=${1:-$here}
venv="$HOME/.local/openclicky"

say() { printf '%s\n' "$*"; }

# The distribution's Python, not a conda or pyenv one earlier on PATH: its GTK bindings are what
# the virtualenv borrows.
python=python3
[ -x /usr/bin/python3 ] && python=/usr/bin/python3

missing=""
if command -v "$python" >/dev/null 2>&1; then
    "$python" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)' || { say "OpenClicky needs Python 3.10 or later."; exit 1; }
    "$python" -c 'import gi; gi.require_version("Gtk", "3.0"); from gi.repository import Gtk' 2>/dev/null || missing="$missing gtk"
    "$python" -c 'import venv, ensurepip' 2>/dev/null || missing="$missing venv"
else
    missing="$missing python3"
fi
command -v parecord >/dev/null 2>&1 || command -v arecord >/dev/null 2>&1 || missing="$missing recorder"
if [ -n "$missing" ]; then
    say "A few things from your distribution first (missing:$missing):"
    if command -v apt-get >/dev/null 2>&1; then
        say "  sudo apt install python3-venv python3-gi gir1.2-gtk-3.0 pulseaudio-utils gstreamer1.0-pipewire gir1.2-gstreamer-1.0"
    elif command -v dnf >/dev/null 2>&1; then
        say "  sudo dnf install python3-gobject gtk3 pulseaudio-utils pipewire-gstreamer"
    elif command -v pacman >/dev/null 2>&1; then
        say "  sudo pacman -S python-gobject gtk3 libpulse gst-plugin-pipewire"
    else
        say "  Python 3 with venv, PyGObject with GTK 3, and parecord (pulseaudio-utils) or arecord"
    fi
    say "then run this again."
    exit 1
fi

say "Installing OpenClicky into $venv …"
"$python" -m venv --system-site-packages "$venv"
# Old pips (Ubuntu 22.04 ships 22.0) cannot install a pyproject-only package in place.
"$venv/bin/pip" install --quiet --upgrade pip
"$venv/bin/pip" install --quiet --upgrade "$source_package"
# The speech engine for offline dictation and the voice (Parakeet, Kokoro); the models themselves
# download from the setup window, when you choose.
"$venv/bin/pip" install --quiet "sherpa-onnx==1.13.8" || say "(the offline engine could not be installed; keys or an account still work)"

mkdir -p "$HOME/.local/bin"
ln -sf "$venv/bin/openclicky-linux" "$HOME/.local/bin/openclicky-linux"
case ":$PATH:" in
    *":$HOME/.local/bin:"*) ;;
    *) say "Add ~/.local/bin to your PATH to type openclicky-linux anywhere (most distributions do at the next login)." ;;
esac

if [ -n "${OPENCLICKY_NO_START:-}" ]; then
    say "Done. Start it with: openclicky-linux"
    exit 0
fi
say "Done. Starting OpenClicky: the setup opens now, and it is in your applications menu from here on."
exec "$venv/bin/openclicky-linux"
