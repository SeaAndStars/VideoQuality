#!/usr/bin/env bash
# Puts the encoder ffmpeg (GPL: x264, x265, SVT-AV1, libass, NVENC/QSV/AMF, …) into a folder for
# packaging. It is a separate, statically linked program that VideoQuality only ever runs as an
# external process; the in-process libav libraries in ffmpeg/ stay the LGPL build.
#
#   ./scripts/fetch-ffmpeg-gpl.sh win-x64   DEST    # ffmpeg.exe + LICENSE
#   ./scripts/fetch-ffmpeg-gpl.sh linux-x64 DEST    # ffmpeg      + LICENSE
#   ./scripts/fetch-ffmpeg-gpl.sh source    DEST    # FFmpeg source of the exact commit (GPL)
#
# The binaries are BtbN/FFmpeg-Builds month-end autobuilds (kept long-term upstream), pinned by
# SHA-256 from the release's own checksums.sha256. Downloads are cached in
# ${VQ_FFMPEG_GPL_CACHE:-~/.cache/videoquality/ffmpeg-gpl}.
set -euo pipefail

tag=autobuild-2026-09-30-13-08
build=ffmpeg-n8.1.3-9-g29e619e767
commit=29e619e767
builds_repo=https://github.com/BtbN/FFmpeg-Builds
win_sha256=7b801cdd3a1a0bb54ae6f572187e68b4ed54f52086cfee133fab2180bfb429fa
linux_sha256=97ce978979194b5cf7e06a5e68020dbdaa7a4f3294c5452b6a1bc347100dbb79

what=${1:?usage: fetch-ffmpeg-gpl.sh win-x64|linux-x64|source DEST}
dest=${2:?usage: fetch-ffmpeg-gpl.sh win-x64|linux-x64|source DEST}
cache=${VQ_FFMPEG_GPL_CACHE:-$HOME/.cache/videoquality/ffmpeg-gpl}
mkdir -p "$cache"

fetch() { # url sha256 -> path of the verified file in the cache
    local url=$1 sha=$2 file
    file=$cache/$(basename "$url")
    if [[ ! -f $file ]] || ! echo "$sha  $file" | sha256sum -c --status; then
        curl -fsSL --retry 3 -o "$file.part" "$url"
        if ! echo "$sha  $file.part" | sha256sum -c --status; then
            echo "checksum mismatch for $url" >&2
            rm -f "$file.part"
            exit 1
        fi
        mv "$file.part" "$file"
    fi
    echo "$file"
}

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
rm -rf "$dest"
mkdir -p "$dest"

case $what in
    win-x64)
        name=$build-win64-gpl-8.1
        zip=$(fetch "$builds_repo/releases/download/$tag/$name.zip" "$win_sha256")
        unzip -q "$zip" "$name/bin/ffmpeg.exe" "$name/LICENSE.txt" -d "$work"
        cp "$work/$name/bin/ffmpeg.exe" "$dest/"
        cp "$work/$name/LICENSE.txt" "$dest/LICENSE-GPL-3.0.txt"
        ;;
    linux-x64)
        name=$build-linux64-gpl-8.1
        tarball=$(fetch "$builds_repo/releases/download/$tag/$name.tar.xz" "$linux_sha256")
        tar -xJf "$tarball" -C "$work" "$name/bin/ffmpeg" "$name/LICENSE.txt"
        cp "$work/$name/bin/ffmpeg" "$dest/"
        chmod 755 "$dest/ffmpeg"
        cp "$work/$name/LICENSE.txt" "$dest/LICENSE-GPL-3.0.txt"
        "$dest/ffmpeg" -hide_banner -version | head -n1
        ;;
    source)
        # FFmpeg at the exact commit; the libraries' versions are pinned by the build scripts at
        # the same tag of $builds_repo (named in the notices).
        curl -fsSL --retry 3 -o "$dest/ffmpeg-$commit.tar.gz" "https://github.com/FFmpeg/FFmpeg/archive/$commit.tar.gz"
        ;;
    *)
        echo "unknown target: $what" >&2
        exit 1
        ;;
esac

printf '%s\n' "$build ($tag)" > "$dest/VERSION"
echo "ffmpeg-gpl $build ($what) -> $dest"
