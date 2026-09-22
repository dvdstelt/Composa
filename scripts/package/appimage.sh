#!/usr/bin/env bash
# Builds an AppImage from a staged tree: the one download that runs on any distribution.
#
#   usage: appimage.sh <rid> <staging-dir> <output-dir>
#
# appimagetool is fetched if it is not already on PATH; set APPIMAGETOOL to point at a local copy.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

RID="${1:?usage: appimage.sh <rid> <staging-dir> <output-dir>}"
STAGE="$(abspath "${2:?}")"
OUT="$(ensure_dir "${3:?}")"
VERSION="$(app_version)"
ARCH="$(rpm_arch "$RID")" # AppImage spells architectures the way rpm does.

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
APPDIR="$WORK/$APP_NAME.AppDir"
mkdir -p "$APPDIR"

cp -a "$STAGE"/. "$APPDIR"/

# An AppImage is mounted at a path that changes every run, so the entry point has to resolve
# itself through $APPDIR rather than assume /usr.
cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
APPDIR="$(dirname "$(readlink -f "$0")")"
export PATH="$APPDIR/usr/bin:$PATH"
exec "$APPDIR/usr/lib/composa/composa" "$@"
APPRUN
chmod 755 "$APPDIR/AppRun"

# appimagetool looks for the desktop entry and icon at the top level of the AppDir, and reads
# .DirIcon for the icon a file manager shows.
cp "$STAGE/usr/share/applications/$APP.desktop" "$APPDIR/$APP.desktop"
cp "$ROOT/packaging/icons/$APP-256.png" "$APPDIR/$APP.png"
ln -sf "$APP.png" "$APPDIR/.DirIcon"

TOOL="${APPIMAGETOOL:-$(command -v appimagetool || true)}"
if [ -z "$TOOL" ]; then
  echo "Fetching appimagetool for $ARCH" >&2
  TOOL="$WORK/appimagetool"
  curl -fsSL -o "$TOOL" \
    "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$ARCH.AppImage"
  chmod +x "$TOOL"
fi

FILE="$OUT/$APP_NAME-$VERSION-$ARCH.AppImage"
rm -f "$FILE"
# --appimage-extract-and-run keeps appimagetool from needing FUSE, which CI runners lack.
ARCH="$ARCH" "$TOOL" --appimage-extract-and-run "$APPDIR" "$FILE"
echo "built $FILE"
