#!/usr/bin/env bash
# Installs a published build for the current user: binary, launcher, icon and the .compositor file type.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
SOURCE="$HERE"
[ -f "$SOURCE/compositor" ] || SOURCE="$HERE/../dist/compositor-linux-x64"
[ -f "$SOURCE/compositor" ] || { echo "No published build found. Run scripts/publish.sh first." >&2; exit 1; }
PREFIX="${PREFIX:-$HOME/.local}"
install -Dm755 "$SOURCE/compositor" "$PREFIX/lib/compositor/compositor"
find "$SOURCE" -maxdepth 1 -name '*.so' -exec install -m644 {} "$PREFIX/lib/compositor/" \;
mkdir -p "$PREFIX/bin"
ln -sf "$PREFIX/lib/compositor/compositor" "$PREFIX/bin/compositor"
install -Dm644 "$SOURCE/compositor.svg" "$PREFIX/share/icons/hicolor/scalable/apps/compositor.svg"
install -Dm644 "$SOURCE/compositor.desktop" "$PREFIX/share/applications/compositor.desktop"
install -Dm644 "$SOURCE/compositor-mime.xml" "$PREFIX/share/mime/packages/compositor.xml"
command -v update-mime-database >/dev/null && update-mime-database "$PREFIX/share/mime" || true
command -v update-desktop-database >/dev/null && update-desktop-database "$PREFIX/share/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q "$PREFIX/share/icons/hicolor" || true
echo "Installed. Launch Compositor from your application menu, or run: compositor"
