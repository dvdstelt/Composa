#!/usr/bin/env bash
# Builds a .deb from a staged tree.
#
#   usage: deb.sh <rid> <staging-dir> <output-dir>
#
# A .deb is an ar archive of debian-binary, control.tar and data.tar, so it is built here with ar
# and tar rather than dpkg-deb. That keeps the build working on any distribution, including the
# Fedora this was developed on, instead of only where dpkg happens to be installed.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

RID="${1:?usage: deb.sh <rid> <staging-dir> <output-dir>}"
STAGE="${2:?}"
OUT="${3:?}"
VERSION="$(app_version)"
DEB_VERSION="$(deb_version "$VERSION")"
ARCH="$(deb_arch "$RID")"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/control" "$OUT"

INSTALLED_KB=$(du -sk "$STAGE" | cut -f1)

cat > "$WORK/control/control" <<CONTROL
Package: $APP
Version: $DEB_VERSION
Architecture: $ARCH
Maintainer: $MAINTAINER
Installed-Size: $INSTALLED_KB
Depends: libc6, libx11-6, libice6, libsm6, libfontconfig1, libgcc-s1, libstdc++6, zlib1g
Recommends: imagemagick
Section: graphics
Priority: optional
Homepage: $HOMEPAGE
Description: $SUMMARY
 Composa is a layer-based image editor with Photoshop-style tools and
 shortcuts: layers with masks, clipping, blend modes and effects, a full
 selection and brush set, adjustments, text, and Photoshop and camera RAW
 import.
 .
 ImageMagick is recommended rather than required: it is needed only to open
 HEIC, AVIF, TIFF and camera RAW files.
CONTROL

# Refreshing these caches is what makes the launcher, its icon and the .cmps file type appear.
# Each is guarded: a desktop without one of these tools must not fail the install.
cat > "$WORK/control/postinst" <<'POSTINST'
#!/bin/sh
set -e
if [ "$1" = "configure" ]; then
    command -v update-desktop-database >/dev/null && update-desktop-database -q /usr/share/applications || true
    command -v update-mime-database    >/dev/null && update-mime-database /usr/share/mime || true
    command -v gtk-update-icon-cache   >/dev/null && gtk-update-icon-cache -q -f /usr/share/icons/hicolor || true
fi
exit 0
POSTINST

cat > "$WORK/control/postrm" <<'POSTRM'
#!/bin/sh
set -e
if [ "$1" = "remove" ] || [ "$1" = "purge" ]; then
    command -v update-desktop-database >/dev/null && update-desktop-database -q /usr/share/applications || true
    command -v update-mime-database    >/dev/null && update-mime-database /usr/share/mime || true
    command -v gtk-update-icon-cache   >/dev/null && gtk-update-icon-cache -q -f /usr/share/icons/hicolor || true
fi
exit 0
POSTRM

chmod 755 "$WORK/control/postinst" "$WORK/control/postrm"

# md5sums lets dpkg detect locally modified files; symlinks and directories are excluded.
( cd "$STAGE" && find . -type f -printf '%P\0' | xargs -0 md5sum > "$WORK/control/md5sums" ) || true

TAR_OPTS=(--owner=0 --group=0 --numeric-owner --sort=name --mtime=@0)
tar -C "$WORK/control" "${TAR_OPTS[@]}" -czf "$WORK/control.tar.gz" .
tar -C "$STAGE"        "${TAR_OPTS[@]}" -cJf "$WORK/data.tar.xz" .
echo "2.0" > "$WORK/debian-binary"

FILE="$OUT/${APP}_${DEB_VERSION}_${ARCH}.deb"
rm -f "$FILE"
# The member order is fixed by the format: debian-binary first, then control, then data.
( cd "$WORK" && ar rc "$FILE" debian-binary control.tar.gz data.tar.xz )
echo "built $FILE"
