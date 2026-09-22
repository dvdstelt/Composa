#!/usr/bin/env bash
# Installs a published build for the current user: binary, launcher, icon and the .composa file type.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
SOURCE="$HERE"
[ -f "$SOURCE/composa" ] || SOURCE="$HERE/../dist/composa-linux-x64"
[ -f "$SOURCE/composa" ] || { echo "No published build found. Run scripts/publish.sh first." >&2; exit 1; }
PREFIX="${PREFIX:-$HOME/.local}"
install -Dm755 "$SOURCE/composa" "$PREFIX/lib/composa/composa"
find "$SOURCE" -maxdepth 1 -name '*.so' -exec install -m644 {} "$PREFIX/lib/composa/" \;
mkdir -p "$PREFIX/bin"
ln -sf "$PREFIX/lib/composa/composa" "$PREFIX/bin/composa"
install -Dm644 "$SOURCE/composa.svg" "$PREFIX/share/icons/hicolor/scalable/apps/composa.svg"
install -Dm644 "$SOURCE/composa.desktop" "$PREFIX/share/applications/composa.desktop"
install -Dm644 "$SOURCE/composa-mime.xml" "$PREFIX/share/mime/packages/composa.xml"
command -v update-mime-database >/dev/null && update-mime-database "$PREFIX/share/mime" || true
command -v update-desktop-database >/dev/null && update-desktop-database "$PREFIX/share/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q "$PREFIX/share/icons/hicolor" || true
echo "Installed. Launch Composa from your application menu, or run: composa"
