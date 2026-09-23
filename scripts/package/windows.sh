#!/usr/bin/env bash
# Builds the Windows downloads for one architecture: a portable zip and an Inno Setup installer,
# both packed from the same published tree.
#
#   usage: windows.sh [rid] [output-dir] [--no-installer]     default: win-x64, dist/
#
# Runs in Git Bash on Windows, and on Linux as well, where .NET cross-publishes the same build. The
# installer needs Inno Setup's compiler, which only runs on Windows, so a Linux run passes
# --no-installer and builds the zip alone. Without that flag a missing compiler is an error rather
# than a quietly smaller release.
#
# Neither is single-file, unlike the Linux tarball. With the native libraries bundled, a single-file
# build unpacks some 40 MB into %TEMP% on its first launch, which is slow and exactly the behaviour
# that antivirus software distrusts; a folder in a zip is what Windows users expect anyway.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

RID="${1:-win-x64}"
OUT="$(ensure_dir "${2:-$BUILD}")"
INSTALLER=true
[ "${3:-}" = "--no-installer" ] && INSTALLER=false

VERSION="$(app_version)"
ARCH="$(inno_arch "$RID")"
NAME="$APP-$VERSION-$RID"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
TREE="$WORK/$NAME"

echo "Building Composa $VERSION for $RID"

# Nothing manages a Windows install, so this build announces new releases itself.
dotnet publish "$ROOT/src/Composa.App" -c Release -r "$RID" --self-contained true \
  -p:DebugType=none -p:UpdateChannel=github -o "$TREE"
cp "$ROOT/LICENSE" "$ROOT/README.md" "$ROOT/CHANGELOG.md" "$TREE/"

# Bundling ImageMagick redistributes LGPL libraries, and the notices are what makes that allowed.
# The project file adds them; this makes sure no change there can ship a build without them.
for notice in THIRD-PARTY-NOTICES.txt ImageMagick-NOTICE.txt; do
  [ -f "$TREE/$notice" ] || { echo "windows.sh: $notice is missing from the published build." >&2; exit 1; }
done

echo "==> zip"
rm -f "$OUT/$NAME.zip"
if command -v 7z >/dev/null; then
  (cd "$WORK" && 7z a -tzip -mx=9 "$OUT/$NAME.zip" "$NAME" >/dev/null)
elif command -v zip >/dev/null; then
  (cd "$WORK" && zip -qr9 "$OUT/$NAME.zip" "$NAME")
else
  echo "windows.sh: neither 7z nor zip is available to build the portable zip." >&2; exit 1
fi
echo "built $OUT/$NAME.zip"

$INSTALLER || exit 0

echo "==> installer"
ISCC="$(command -v iscc || command -v ISCC.exe || true)"
for candidate in "/c/Program Files (x86)/Inno Setup 6/ISCC.exe" "/c/Program Files/Inno Setup 6/ISCC.exe"; do
  [ -z "$ISCC" ] && [ -f "$candidate" ] && ISCC="$candidate"
done
[ -n "$ISCC" ] || { echo "windows.sh: Inno Setup 6 (ISCC.exe) was not found. Install it, or pass --no-installer." >&2; exit 1; }

# ISCC is a Windows program and needs Windows paths. Git Bash would otherwise rewrite anything that
# looks like a path, including the /D switches themselves, so its conversion is switched off and
# the paths are converted explicitly.
winpath() { if command -v cygpath >/dev/null; then cygpath -w "$1"; else echo "$1"; fi; }
MSYS2_ARG_CONV_EXCL='*' "$ISCC" /Q \
  "/DAppVersion=$VERSION" \
  "/DFileVersion=$(win_file_version "$VERSION")" \
  "/DArchitecture=$ARCH" \
  "/DSourceDir=$(winpath "$TREE")" \
  "/DOutputDir=$(winpath "$OUT")" \
  "/DOutputName=$NAME-setup" \
  "$(winpath "$ROOT/packaging/windows/composa.iss")"
echo "built $OUT/$NAME-setup.exe"
