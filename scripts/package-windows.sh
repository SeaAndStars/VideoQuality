#!/usr/bin/env bash
# Builds a self-contained Windows release: the app and CLI (with the .NET runtime) plus the FFmpeg +
# libvmaf CUDA build in an ffmpeg\ folder beside them. Unzip and run; nothing else to install apart
# from an NVIDIA driver for GPU scoring.
#
#   ./scripts/build-ffmpeg-windows.sh        # once, or after changing FFmpeg/libvmaf
#   ./scripts/package-windows.sh             # -> artifacts/VideoQuality-<version>-win-x64.zip
#   VQ_VERSION=1.0.0 ./scripts/package-windows.sh
#
# Layout:
#   VideoQuality.App.exe, VideoQuality.Cli.exe, .NET runtime files
#   ffmpeg\        ffmpeg.exe, ffprobe.exe, avcodec-63.dll, …, libvmaf.dll, libdav1d.dll
#   licenses\      third-party license texts
#   THIRD-PARTY-NOTICES.txt
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
work=${VQ_WIN_WORK:-$HOME/build-win}
stage=$work/stage/ffmpeg
src=$work/src
version=${VQ_VERSION:-0.1.0+$(git -C "$root" rev-parse --short HEAD)}
name=VideoQuality-${version//+/-}-win-x64
out=$root/artifacts/$name

if [[ ! -f $stage/ffmpeg.exe ]]; then
    echo "no staged FFmpeg in $stage; run scripts/build-ffmpeg-windows.sh first" >&2
    exit 1
fi

if grep -q -- '--enable-nonfree\|--enable-cuda-nvcc' "$stage/BUILDINFO.txt"; then
    echo "the staged FFmpeg is a nonfree build and must not be redistributed; rebuild it" >&2
    exit 1
fi

rm -rf "$out" "$out.zip"
mkdir -p "$out/licenses"

echo "== publish ($version)"
for project in App Cli; do
    dotnet publish "$root/VideoQuality.$project" -c Release -r win-x64 --self-contained \
        -p:Version="${version%%+*}" -p:InformationalVersion="$version" \
        -p:DebugType=None -p:GenerateDocumentationFile=false \
        -o "$out" -v q
done

# Native debug symbols from packages (SkiaSharp's alone are ~100 MB) are not needed at run time.
find "$out" -maxdepth 1 -name '*.pdb' -delete

echo "== bundle FFmpeg"
cp -r "$stage" "$out/ffmpeg"

echo "== notices"
cp "$src/ffmpeg-9.0.2/COPYING.LGPLv2.1" "$out/licenses/FFmpeg-LGPL-2.1.txt"
cp "$src/vmaf-3.2.1/LICENSE" "$out/licenses/libvmaf-BSD-2-Clause-Patent.txt"
cp "$src/dav1d/COPYING" "$out/licenses/dav1d-BSD-2-Clause.txt"
sed -n '1,/\*\//p' "$src/nv-codec-headers/include/ffnvcodec/dynlink_loader.h" > "$out/licenses/nv-codec-headers-MIT.txt"
cp /usr/share/doc/mingw-w64-common/copyright "$out/licenses/mingw-w64-winpthreads.txt"
cp /usr/share/doc/gcc-mingw-w64-base/copyright "$out/licenses/gcc-runtime-library-exception.txt"

{
    cat <<EOF
VideoQuality $version — third-party notices

FFMPEG
This package includes FFmpeg (https://ffmpeg.org) in ffmpeg\\, licensed under the GNU Lesser General
Public License 2.1 or later (licenses\\FFmpeg-LGPL-2.1.txt). It is built without --enable-gpl and
without --enable-nonfree. The DLLs are loaded dynamically and can be replaced with a compatible build.

Corresponding source: FFmpeg tag n9.0.2, https://git.ffmpeg.org/ffmpeg.git
Build script:         scripts/build-ffmpeg-windows.sh in this project's repository
Build details:
$(sed 's/^/  /' "$stage/BUILDINFO.txt")

NATIVE LIBRARIES (in ffmpeg\\)
  libvmaf v3.2.1       BSD-2-Clause-Patent    https://github.com/Netflix/vmaf
  dav1d 1.5.1          BSD-2-Clause           https://code.videolan.org/videolan/dav1d
  nv-codec-headers     MIT                    https://code.ffmpeg.org/FFmpeg/nv-codec-headers
  mingw-w64 winpthreads, libgcc, libstdc++   MIT/BSD-style; GPL-3.0 with the GCC Runtime Library
                       Exception, which permits redistribution of these runtime DLLs.
  GPU scoring loads the NVIDIA driver (nvcuda.dll) installed on the machine; none of it is shipped.

.NET PACKAGES
EOF
    # License of every NuGet package the app and CLI ship, from the packages' own metadata.
    python3 - "$root" <<'PY'
import json, os, re, sys
root = sys.argv[1]
seen = {}
for project in ("VideoQuality.App", "VideoQuality.Cli"):
    assets = json.load(open(os.path.join(root, project, "obj", "project.assets.json")))
    folder = next(iter(assets["packageFolders"]))
    for key, lib in assets["libraries"].items():
        if lib.get("type") != "package":
            continue
        name, version = key.split("/")
        nuspec = os.path.join(folder, lib["path"], f"{name.lower()}.nuspec")
        license = "see package"
        if os.path.exists(nuspec):
            text = open(nuspec, encoding="utf-8").read()
            m = re.search(r"<license[^>]*>([^<]+)</license>", text) or re.search(r"<licenseUrl>([^<]+)</licenseUrl>", text)
            if m:
                license = m.group(1).strip()
        seen[name] = (version, license)
for name in sorted(seen, key=str.lower):
    version, license = seen[name]
    print(f"  {name} {version}".ljust(58) + license)
PY
    echo
    echo ".NET runtime (self-contained): MIT, https://github.com/dotnet/runtime"
} > "$out/THIRD-PARTY-NOTICES.txt"

echo "== zip"
(cd "$root/artifacts" && zip -qr "$name.zip" "$name")
du -sh "$out" "$out.zip" | sed 's/^/  /'
echo "done: artifacts/$name.zip"
