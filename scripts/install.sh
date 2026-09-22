#!/usr/bin/env bash
# Installs the portable tarball for the current user: binary, launcher, icons and the .cmps file type.
#
# This is the per-user path for the tarball. A .deb or .rpm does all of this through the package
# manager instead, and an AppImage needs none of it.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
SOURCE="$HERE"
[ -f "$SOURCE/composa" ] || { echo "No published build found next to this script." >&2; exit 1; }

PREFIX="${PREFIX:-$HOME/.local}"
install -Dm755 "$SOURCE/composa" "$PREFIX/lib/composa/composa"
find "$SOURCE" -maxdepth 1 -name '*.so' -exec install -m644 {} "$PREFIX/lib/composa/" \;
mkdir -p "$PREFIX/bin"
ln -sf "$PREFIX/lib/composa/composa" "$PREFIX/bin/composa"

install -Dm644 "$SOURCE/composa.svg"          "$PREFIX/share/icons/hicolor/scalable/apps/composa.svg"
for size in 16 32 48 64 128 256; do
  [ -f "$SOURCE/icons/composa-$size.png" ] &&
    install -Dm644 "$SOURCE/icons/composa-$size.png" \
                   "$PREFIX/share/icons/hicolor/${size}x${size}/apps/composa.png"
done
install -Dm644 "$SOURCE/composa.desktop"      "$PREFIX/share/applications/composa.desktop"
install -Dm644 "$SOURCE/composa-mime.xml"     "$PREFIX/share/mime/packages/composa.xml"
install -Dm644 "$SOURCE/composa.metainfo.xml" "$PREFIX/share/metainfo/org.composa.Composa.metainfo.xml"

command -v update-mime-database    >/dev/null && update-mime-database "$PREFIX/share/mime" || true
command -v update-desktop-database >/dev/null && update-desktop-database "$PREFIX/share/applications" || true
command -v gtk-update-icon-cache   >/dev/null && gtk-update-icon-cache -q "$PREFIX/share/icons/hicolor" || true

echo "Installed. Launch Composa from your application menu, or run: composa"
case ":$PATH:" in
  *":$PREFIX/bin:"*) ;;
  *) echo "Note: $PREFIX/bin is not on your PATH." ;;
esac
