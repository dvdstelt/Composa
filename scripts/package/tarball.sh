#!/usr/bin/env bash
# Builds the portable tarball: a single-file executable plus the per-user install script.
#
#   usage: tarball.sh <rid> <output-dir>
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

RID="${1:?usage: tarball.sh <rid> <output-dir>}"
OUT="${2:?}"
VERSION="$(app_version)"
NAME="$APP-$VERSION-$RID"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/$NAME" "$OUT"

# One executable is the whole point of this format, so this is the one build that is single-file.
dotnet publish "$ROOT/src/Composa.App" -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
  -p:UpdateChannel=github -o "$WORK/$NAME"

cp "$ROOT/packaging/$APP.desktop" "$ROOT/packaging/$APP.svg" "$ROOT/packaging/$APP-mime.xml" \
   "$ROOT/packaging/$APP.metainfo.xml" "$ROOT/scripts/install.sh" \
   "$ROOT/LICENSE" "$ROOT/README.md" "$ROOT/CHANGELOG.md" "$WORK/$NAME/"
mkdir -p "$WORK/$NAME/icons" && cp "$ROOT"/packaging/icons/$APP-*.png "$WORK/$NAME/icons/"

tar -C "$WORK" --owner=0 --group=0 --numeric-owner -czf "$OUT/$NAME.tar.gz" "$NAME"
echo "built $OUT/$NAME.tar.gz"
