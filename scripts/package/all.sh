#!/usr/bin/env bash
# Builds every Linux package for one architecture.
#
#   usage: all.sh [rid] [output-dir]      default: linux-x64, dist/
#
# The tarball is published on its own because it is the one single-file build. Everything else is
# packed from one staged tree, so a layout mistake shows up in all three at once rather than in
# whichever format happened to be tested.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$HERE/common.sh"

RID="${1:-linux-x64}"
OUT="$(ensure_dir "${2:-$BUILD}")"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

# Resolve the version once and hand it to every format, so they cannot disagree and so the restore
# MinVer needs happens a single time.
COMPOSA_VERSION="$(app_version)"
export COMPOSA_VERSION
echo "Building Composa $COMPOSA_VERSION for $RID"

echo "==> tarball"
"$HERE/tarball.sh" "$RID" "$OUT"

echo "==> staging for the package formats"
# A package installed by apt or dnf must never nag about an update its package manager owns.
UPDATE_CHANNEL=managed "$HERE/stage.sh" "$RID" "$STAGE"

echo "==> deb"
"$HERE/deb.sh" "$RID" "$STAGE" "$OUT"

echo "==> rpm"
"$HERE/rpm.sh" "$RID" "$STAGE" "$OUT"

# The AppImage is the download for anyone whose distribution is neither Debian nor Fedora shaped,
# so it is built from the same tree but keeps the github update channel: nothing manages it.
echo "==> AppImage"
UPDATE_CHANNEL=github "$HERE/stage.sh" "$RID" "$STAGE" >/dev/null
"$HERE/appimage.sh" "$RID" "$STAGE" "$OUT"

echo
echo "Built for $RID:"
ls -1sh "$OUT"
