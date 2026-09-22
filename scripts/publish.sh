#!/usr/bin/env bash
# Builds the portable tarball into dist/. For every package format, use scripts/package/all.sh.
set -euo pipefail
exec "$(dirname "$0")/package/tarball.sh" "${1:-linux-x64}" "$(dirname "$0")/../dist"
