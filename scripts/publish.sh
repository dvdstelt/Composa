#!/usr/bin/env bash
# Builds a self-contained Linux release into dist/composa-<rid>/ and a tarball next to it.
set -euo pipefail
cd "$(dirname "$0")/.."
RID="${1:-linux-x64}"
OUT="dist/composa-$RID"
rm -rf "$OUT"
dotnet publish src/Composa.App -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "$OUT"
cp packaging/composa.desktop packaging/composa.svg packaging/composa-mime.xml scripts/install.sh "$OUT/"
cp LICENSE README.md "$OUT/"
tar -C dist -czf "dist/composa-$RID.tar.gz" "composa-$RID"
echo "Published to $OUT and dist/composa-$RID.tar.gz"
