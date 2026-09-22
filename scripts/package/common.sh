# Shared settings and helpers for the packaging scripts. Sourced, not run.

APP=composa
APP_NAME=Composa
APP_ID=org.composa.Composa
MAINTAINER="Dennis van der Stelt <dennis.vanderstelt@gmail.com>"
HOMEPAGE="https://github.com/dvdstelt/Composa"
SUMMARY="Layer-based image editor for compositing and retouching"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BUILD="$ROOT/dist"

# Every format spells the same architecture differently, which is a classic source of a download
# that installs nowhere. The mapping lives here once.
#   .NET RID        linux-x64     linux-arm64
#   dpkg            amd64         arm64
#   rpm / AppImage  x86_64        aarch64
deb_arch() { case "$1" in linux-x64) echo amd64 ;; linux-arm64) echo arm64 ;; *) echo "unknown RID: $1" >&2; return 1 ;; esac; }
rpm_arch() { case "$1" in linux-x64) echo x86_64 ;; linux-arm64) echo aarch64 ;; *) echo "unknown RID: $1" >&2; return 1 ;; esac; }

# MinVer derives this from the nearest git tag, so it matches what the application reports about
# itself. The MinVer target answers in under a second, unlike a full build.
#
# That target ships inside MinVer's NuGet package, so it does not exist until the project has been
# restored. A developer's checkout is always restored already; a CI checkout never is, which is
# exactly the difference that has to be handled here rather than assumed away.
#
# COMPOSA_VERSION lets all.sh resolve the version once and pass it to every format, instead of each
# script paying for its own restore.
app_version() {
  if [ -n "${COMPOSA_VERSION:-}" ]; then echo "$COMPOSA_VERSION"; return 0; fi

  dotnet restore "$ROOT/src/Composa.App/Composa.App.csproj" >/dev/null || {
    echo "common.sh: dotnet restore failed, so the version cannot be determined." >&2
    return 1
  }

  local version
  # No 2>/dev/null here. Hiding MSBuild's stderr once turned a one-line error into a build that
  # failed with no output at all.
  version="$(dotnet msbuild "$ROOT/src/Composa.App/Composa.App.csproj" -t:MinVer -getProperty:MinVerVersion -v:q -nologo | tr -d '[:space:]')" || {
    echo "common.sh: MinVer could not be asked for the version." >&2
    return 1
  }
  [ -n "$version" ] || { echo "common.sh: MinVer returned an empty version." >&2; return 1; }
  echo "$version"
}

# Debian and RPM both reject a '-' in a version, which every pre-release from MinVer contains.
# 0.2.1-alpha.0.7 becomes 0.2.1~alpha.0.7 for dpkg, which sorts it *before* 0.2.1 as intended,
# and 0.2.1 with release 0.alpha.0.7 for rpm, which is that ecosystem's equivalent.
deb_version() { echo "${1/-/\~}"; }
rpm_version() { echo "${1%%-*}"; }
rpm_release() { case "$1" in *-*) echo "0.${1#*-}" ;; *) echo 1 ;; esac; }
