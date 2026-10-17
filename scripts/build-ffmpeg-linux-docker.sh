#!/usr/bin/env bash
# Runs scripts/build-ffmpeg-linux.sh inside Ubuntu 22.04 (glibc 2.35) so the release's bundled FFmpeg
# runs on older distros than the build machine, then copies the staged folder back to
# ~/build-linux/stage/ffmpeg. Files go in and out through stdin/stdout, so this works with a Docker
# engine that cannot see this filesystem (Docker Desktop driven from WSL).
#
#   ./scripts/build-ffmpeg-linux-docker.sh        # DOCKER=docker.exe from WSL if needed
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
docker=${DOCKER:-$(command -v docker || command -v docker.exe)}
image=videoquality-linux-build:22.04
container=vq-linux-build-$$
out=${VQ_LINUX_WORK:-$HOME/build-linux}/stage

echo "== image $image"
"$docker" build -q -t "$image" - < "$root/scripts/linux-build.Dockerfile" >/dev/null

trap '"$docker" rm -f "$container" >/dev/null 2>&1 || true' EXIT
"$docker" run -d --name "$container" "$image" sleep infinity >/dev/null
"$docker" exec -i "$container" sh -c 'cat > /build.sh && chmod +x /build.sh' < "$root/scripts/build-ffmpeg-linux.sh"

echo "== build (inside the container)"
"$docker" exec "$container" env VQ_LINUX_WORK=/work /build.sh

echo "== copy out to $out"
rm -rf "$out/ffmpeg" "$out/licenses"
mkdir -p "$out"
"$docker" exec "$container" tar -C /work/stage -cf - ffmpeg licenses | tar -C "$out" -xf -
cat "$out/ffmpeg/BUILDINFO.txt"
