#!/usr/bin/env bash
# Linux release tarball; see scripts/package.sh.
exec "$(dirname "$0")/package.sh" linux-x64 "$@"
