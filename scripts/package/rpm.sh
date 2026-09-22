#!/usr/bin/env bash
# Builds an .rpm from a staged tree.
#
#   usage: rpm.sh <rid> <staging-dir> <output-dir>
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

RID="${1:?usage: rpm.sh <rid> <staging-dir> <output-dir>}"
STAGE="$(cd "${2:?}" && pwd)"
OUT="${3:?}"
VERSION="$(app_version)"
ARCH="$(rpm_arch "$RID")"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK"/{BUILD,RPMS,SPECS} "$OUT"

cat > "$WORK/SPECS/$APP.spec" <<SPEC
# The payload is an already-published self-contained build, so none of RPM's post-processing
# applies: stripping or rewriting build ids on these binaries would only risk breaking them.
%global debug_package %{nil}
%global __os_install_post %{nil}
%define _build_id_links none

# The bundled .NET and Skia libraries would otherwise drag a long list of auto-detected
# dependencies into the package, so the real ones are declared by hand below.
AutoReqProv: no

Name:           $APP
Version:        $(rpm_version "$VERSION")
Release:        $(rpm_release "$VERSION")
Summary:        $SUMMARY
License:        MIT
URL:            $HOMEPAGE
BuildArch:      $ARCH

Requires:       glibc
Requires:       libX11
Requires:       libICE
Requires:       libSM
Requires:       fontconfig
Requires:       libstdc++
# Needed only to open HEIC, AVIF, TIFF and camera RAW, so it is a suggestion rather than a need.
Recommends:     ImageMagick

%description
Composa is a layer-based image editor with Photoshop-style tools and shortcuts:
layers with masks, clipping, blend modes and effects, a full selection and brush
set, adjustments, text, and Photoshop and camera RAW import.

ImageMagick is recommended rather than required: it is needed only to open HEIC,
AVIF, TIFF and camera RAW files.

%install
cp -a $STAGE/. %{buildroot}/

# Refreshing these caches is what makes the launcher, its icon and the .cmps file type appear.
# Each is guarded so a desktop without one of these tools does not fail the transaction.
%post
command -v update-desktop-database >/dev/null && update-desktop-database -q /usr/share/applications || true
command -v update-mime-database    >/dev/null && update-mime-database /usr/share/mime || true
command -v gtk-update-icon-cache   >/dev/null && gtk-update-icon-cache -q -f /usr/share/icons/hicolor || true
exit 0

%postun
if [ \$1 -eq 0 ]; then
    command -v update-desktop-database >/dev/null && update-desktop-database -q /usr/share/applications || true
    command -v update-mime-database    >/dev/null && update-mime-database /usr/share/mime || true
    command -v gtk-update-icon-cache   >/dev/null && gtk-update-icon-cache -q -f /usr/share/icons/hicolor || true
fi
exit 0

%files
/usr/lib/$APP
/usr/bin/$APP
/usr/share/applications/$APP.desktop
/usr/share/mime/packages/$APP.xml
/usr/share/metainfo/$APP_ID.metainfo.xml
/usr/share/icons/hicolor/*/apps/$APP.*
%doc /usr/share/doc/$APP/README.md
%doc /usr/share/doc/$APP/CHANGELOG.md
%license /usr/share/doc/$APP/copyright

%changelog
SPEC

rpmbuild --define "_topdir $WORK" --define "_rpmdir $OUT" \
         --define "_rpmfilename %%{NAME}-%%{VERSION}-%%{RELEASE}.%%{ARCH}.rpm" \
         -bb "$WORK/SPECS/$APP.spec" >"$WORK/log" 2>&1 || { tail -30 "$WORK/log"; exit 1; }
echo "built $(ls "$OUT"/$APP-*.rpm | tail -1)"
