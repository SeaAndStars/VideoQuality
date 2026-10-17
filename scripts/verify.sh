#!/usr/bin/env bash
# Clean-room verification: catches missing solution projects, stale build outputs,
# failing tests and an unusable ffmpeg.
#
#   ./scripts/verify.sh
#   VQ_TEST_VIDEO=/tmp/vqtest/ref.mkv ./scripts/verify.sh   # also runs integration tests
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root"

echo "== solution projects"
projects=$(dotnet sln VideoQuality.slnx list)
echo "$projects"

for required in VideoQuality.App VideoQuality.Core VideoQuality.Cli VideoQuality.Tests; do
    if ! grep -q "$required" <<<"$projects"; then
        echo "FAIL: $required is missing from VideoQuality.slnx" >&2
        exit 1
    fi
done

echo
echo "== clean build"
dotnet build VideoQuality.slnx --no-incremental -c Release

echo
echo "== tests"
if [[ -n "${VQ_TEST_VIDEO:-}" ]]; then
    dotnet test VideoQuality.Tests/VideoQuality.Tests.csproj -c Release
else
    dotnet test VideoQuality.Tests/VideoQuality.Tests.csproj -c Release
    echo "(integration tests were skipped; set VQ_TEST_VIDEO to run them)"
fi

echo
echo "== ffmpeg capabilities"
dotnet run --project VideoQuality.Cli -c Release -- probe

echo
echo "OK"
