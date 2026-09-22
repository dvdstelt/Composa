#!/usr/bin/env bash
# Regenerates packaging/icons/ from packaging/composa.svg.
#
# The output is committed so no build or CI job needs a rasterizer installed; run this only when
# the source SVG changes. Requires ImageMagick and Python 3.
set -euo pipefail
cd "$(dirname "$0")/.."
SVG=packaging/composa.svg
OUT=packaging/icons
mkdir -p "$OUT"

# A high render density first, then a resize, keeps the small sizes from going muddy.
for size in 16 32 48 64 128 256 512 1024; do
  magick -background none -density 1200 "$SVG" -resize "${size}x${size}" -depth 8 -strip "PNG32:$OUT/composa-$size.png"
done

python3 - "$OUT" <<'PY'
import struct, sys
out = sys.argv[1]

# Windows .ico. Vista and later accept PNG payloads, which is what keeps this from being
# mostly uncompressed bitmap: raw DIB entries make the same file roughly ten times larger.
sizes = [16, 32, 48, 64, 128, 256]
blobs = [(s, open(f"{out}/composa-{s}.png", "rb").read()) for s in sizes]
ico = bytearray(struct.pack("<HHH", 0, 1, len(blobs)))
offset = 6 + 16 * len(blobs)
for s, data in blobs:
    d = 0 if s == 256 else s  # 0 encodes 256 in a directory entry
    ico += struct.pack("<BBBBHHII", d, d, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
for _, data in blobs:
    ico += data
open(f"{out}/composa.ico", "wb").write(ico)

# macOS .icns: 'icns' + total length, then <4-byte type><uint32 length including the 8-byte header><data>.
# OS X 10.7 and later accept PNG payloads for every type used here.
types = [("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256),
         ("ic09", 512), ("ic11", 32), ("ic12", 64), ("ic13", 256), ("ic14", 512), ("ic10", 1024)]
chunks = b""
for t, s in types:
    data = open(f"{out}/composa-{s}.png", "rb").read()
    chunks += t.encode("ascii") + struct.pack(">I", len(data) + 8) + data
open(f"{out}/composa.icns", "wb").write(b"icns" + struct.pack(">I", len(chunks) + 8) + chunks)

# Parse both back, so a malformed length is caught here and not on a user's machine.
b = open(f"{out}/composa.icns", "rb").read()
assert b[:4] == b"icns" and struct.unpack(">I", b[4:8])[0] == len(b), "icns header is wrong"
off = 8
while off < len(b):
    n = struct.unpack(">I", b[off + 4:off + 8])[0]
    assert n >= 8 and off + n <= len(b) and b[off + 8:off + 12] == b"\x89PNG", "icns chunk is wrong"
    off += n
assert off == len(b), "icns has trailing bytes"

b = open(f"{out}/composa.ico", "rb").read()
count = struct.unpack("<H", b[4:6])[0]
for i in range(count):
    size, off = struct.unpack("<II", b[6 + 16 * i + 8:6 + 16 * i + 16])
    assert off + size <= len(b) and b[off:off + 4] == b"\x89PNG", "ico entry is wrong"
print(f"icons written to {out}/ and verified")
PY
