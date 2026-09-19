#!/usr/bin/env bash
# Builds a self-contained Linux release into dist/compositor-<rid>/ and a tarball next to it.
set -euo pipefail
cd "$(dirname "$0")/.."
RID="${1:-linux-x64}"
OUT="dist/compositor-$RID"
rm -rf "$OUT"
dotnet publish src/Compositor.App -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "$OUT"
cp packaging/compositor.desktop packaging/compositor.svg packaging/compositor-mime.xml scripts/install.sh "$OUT/"
cp LICENSE README.md "$OUT/"
tar -C dist -czf "dist/compositor-$RID.tar.gz" "compositor-$RID"
echo "Published to $OUT and dist/compositor-$RID.tar.gz"
