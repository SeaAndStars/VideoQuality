#!/usr/bin/env bash
# Regenerates the documentation screenshots from a real run.
#
# Requires: dotnet, Xvfb, xdotool, ImageMagick (import), and the test media.
# The app is launched once per compare mode with --mode=<name>, so no UI clicking
# is needed for the main screenshots.
#
#   VQ_MEDIA_DIR=/tmp/vqtest ./scripts/capture-docs.sh
#
# Optional progress screenshot:
#   VQ_PROGRESS_REF=/tmp/vqtest/ref1080.mkv VQ_PROGRESS_ENCODE=/tmp/vqtest/dist1080.mp4 \
#       ./scripts/capture-docs.sh
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
media=${VQ_MEDIA_DIR:-/tmp/vqtest}
display=${VQ_DISPLAY:-:99}
settle=${VQ_SETTLE:-22}
ref=${VQ_REF:-$media/ref.mkv}
encodes=(${VQ_ENCODES:-$media/dist.mp4 $media/dist2.mp4 $media/dist3.mp4 $media/dist10.mp4 $media/dist_av1.mp4})
app="$root/VideoQuality.App/bin/Debug/net10.0/VideoQuality.App.dll"

for file in "$ref" "${encodes[@]}"; do
    if [[ ! -f "$file" ]]; then
        echo "missing test media: $file" >&2
        echo "set VQ_MEDIA_DIR or generate clips as described in docs/development.md" >&2
        exit 1
    fi
done

if ! command -v import >/dev/null; then
    echo "ImageMagick 'import' is required" >&2
    exit 1
fi

if ! xdpyinfo -display "$display" >/dev/null 2>&1; then
    echo "starting Xvfb on $display"
    Xvfb "$display" -screen 0 1600x1000x24 >/dev/null 2>&1 &
    sleep 1
fi

dotnet build "$root/VideoQuality.slnx" -v q

capture() {
    local mode=$1
    local output=$2
    shift 2

    DISPLAY=$display dotnet "$app" "$@" "--mode=$mode" >/dev/null 2>&1 &
    local pid=$!
    sleep "$settle"

    DISPLAY=$display import -window root "$root/docs/$output"
    kill "$pid" >/dev/null 2>&1 || true
    wait "$pid" >/dev/null 2>&1 || true
    echo "wrote docs/$output"
}

capture sidebyside screenshot.png "$ref" "${encodes[@]}"
capture wipe screenshot-wipe.png "$ref" "${encodes[@]}"
capture difference screenshot-difference.png "$ref" "${encodes[@]}"

# Progress needs a score that is still running when the screenshot is taken.
if [[ -n "${VQ_PROGRESS_REF:-}" && -n "${VQ_PROGRESS_ENCODE:-}" ]]; then
    DISPLAY=$display dotnet "$app" "$VQ_PROGRESS_REF" "$VQ_PROGRESS_ENCODE" >/dev/null 2>&1 &
    pid=$!
    sleep "${VQ_PROGRESS_SETTLE:-12}"

    window=$(DISPLAY=$display xdotool search --name "VideoQuality" | head -1)
    DISPLAY=$display xdotool mousemove --window "$window" 255 25 click 1
    sleep 1
    DISPLAY=$display import -window root "$root/docs/screenshot-progress.png"
    kill "$pid" >/dev/null 2>&1 || true
    wait "$pid" >/dev/null 2>&1 || true
    echo "wrote docs/screenshot-progress.png"
else
    echo "skipping progress screenshot (set VQ_PROGRESS_REF and VQ_PROGRESS_ENCODE)" >&2
fi
