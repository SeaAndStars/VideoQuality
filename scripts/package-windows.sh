#!/usr/bin/env bash
# Windows release zip; see scripts/package.sh.
exec "$(dirname "$0")/package.sh" win-x64 "$@"
