#!/usr/bin/env bash
# Lays out the filesystem tree that every Linux package format packs, so a layout bug is fixed once.
#
#   usage: stage.sh <rid> <staging-dir> [--single-file]
#
# --single-file is for the tarball, where one executable is the point. The packages leave it off:
# inside a .deb, .rpm or AppImage a single-file build only adds an extraction into /tmp on first
# launch and buys nothing.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

RID="${1:?usage: stage.sh <rid> <staging-dir> [--single-file]}"
STAGE="${2:?usage: stage.sh <rid> <staging-dir> [--single-file]}"
SINGLE_FILE=false
[ "${3:-}" = "--single-file" ] && SINGLE_FILE=true

rm -rf "$STAGE"
mkdir -p "$STAGE/usr/lib/$APP" "$STAGE/usr/bin" "$STAGE/usr/share/applications" \
         "$STAGE/usr/share/mime/packages" "$STAGE/usr/share/metainfo" "$STAGE/usr/share/doc/$APP"

PUBLISH_ARGS=(-c Release -r "$RID" --self-contained true -p:DebugType=none)
if $SINGLE_FILE; then
  PUBLISH_ARGS+=(-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true)
fi
# UpdateChannel travels with the build: a package installed by apt or dnf must never nag about an
# update its package manager owns. The property is read by the update check added in Phase 2.
PUBLISH_ARGS+=("-p:UpdateChannel=${UPDATE_CHANNEL:-github}")

dotnet publish "$ROOT/src/Composa.App" "${PUBLISH_ARGS[@]}" -o "$STAGE/usr/lib/$APP"

ln -sf "../lib/$APP/$APP" "$STAGE/usr/bin/$APP"

install -Dm644 "$ROOT/packaging/$APP.desktop"       "$STAGE/usr/share/applications/$APP.desktop"
install -Dm644 "$ROOT/packaging/$APP-mime.xml"      "$STAGE/usr/share/mime/packages/$APP.xml"
install -Dm644 "$ROOT/packaging/$APP.metainfo.xml"  "$STAGE/usr/share/metainfo/$APP_ID.metainfo.xml"
install -Dm644 "$ROOT/packaging/$APP.svg"           "$STAGE/usr/share/icons/hicolor/scalable/apps/$APP.svg"
for size in 16 32 48 64 128 256; do
  install -Dm644 "$ROOT/packaging/icons/$APP-$size.png" \
                 "$STAGE/usr/share/icons/hicolor/${size}x${size}/apps/$APP.png"
done
install -Dm644 "$ROOT/LICENSE"      "$STAGE/usr/share/doc/$APP/copyright"
install -Dm644 "$ROOT/README.md"    "$STAGE/usr/share/doc/$APP/README.md"
install -Dm644 "$ROOT/CHANGELOG.md" "$STAGE/usr/share/doc/$APP/CHANGELOG.md"

chmod 755 "$STAGE/usr/lib/$APP/$APP"
echo "staged $RID into $STAGE"
