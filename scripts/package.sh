#!/usr/bin/env bash
# Builds a self-contained release for one platform: the app and CLI (with the .NET runtime) plus the
# redistributable FFmpeg + libvmaf CUDA build in an ffmpeg/ folder beside them. Unpack and run; the
# only requirement is an NVIDIA driver for GPU scoring.
#
#   ./scripts/build-ffmpeg-windows.sh && ./scripts/package.sh win-x64     # -> .zip
#   ./scripts/build-ffmpeg-linux-docker.sh && ./scripts/package.sh linux-x64   # -> .tar.gz
#   VQ_VERSION=1.0.0 ./scripts/package.sh linux-x64
#
# Layout (the root holds only the application and its settings):
#   VideoQuality[.exe]  the app: .NET runtime, Avalonia and every managed/native library in one file
#   settings.json       created on the first settings change (portable; see SettingsFile)
#   ffmpeg/             ffmpeg, ffprobe and the libav/libvmaf/dav1d libraries
#   tools/vq[.exe]      the command-line tool, also a single file
#   tools/mkvtoolnix/   mkvmerge and mkvextract (GPL-2.0, upstream binaries) for Extract/Mux
#   tools/ffmpeg-encode/ the encoder ffmpeg (GPL-3.0 static build: x264, x265, SVT-AV1, libass, …)
#   licenses/           THIRD-PARTY-NOTICES.txt and license texts
#   logs/               created at run time
#   (Linux) videoquality.png, install-desktop-entry.sh   application-menu integration
set -euo pipefail

rid=${1:?usage: package.sh win-x64|linux-x64}
root=$(cd "$(dirname "$0")/.." && pwd)
version=${VQ_VERSION:-0.1.0+$(git -C "$root" rev-parse --short HEAD)}
name=VideoQuality-${version//+/-}-$rid
out=$root/artifacts/$name

case $rid in
    win-x64)
        exe=.exe
        stage=${VQ_WIN_WORK:-$HOME/build-win}/stage/ffmpeg
        build_script=scripts/build-ffmpeg-windows.sh
        archive=$name.zip
        extra=()
        ;;
    linux-x64)
        exe=
        stage=${VQ_LINUX_WORK:-$HOME/build-linux}/stage/ffmpeg
        build_script=scripts/build-ffmpeg-linux-docker.sh
        archive=$name.tar.gz
        # No libicu dependency: minimal installs and containers do not have it. The app formats and
        # compares culture-invariantly anyway; PredefinedCulturesOnly=false still lets the Semi theme
        # create its en-US culture (with invariant data) instead of throwing.
        extra=(-p:InvariantGlobalization=true -p:PredefinedCulturesOnly=false)
        ;;
    *)
        echo "unsupported runtime: $rid (win-x64 or linux-x64)" >&2
        exit 1
        ;;
esac

staged_licenses=$(dirname "$stage")/licenses
if [[ ! -f $stage/ffmpeg$exe || ! -f $staged_licenses/FFmpeg-LGPL-2.1.txt ]]; then
    echo "no staged FFmpeg (and licenses) in $(dirname "$stage"); run $build_script first" >&2
    exit 1
fi

if grep -q -- '--enable-nonfree\|--enable-cuda-nvcc' "$stage/BUILDINFO.txt"; then
    echo "the staged FFmpeg is a nonfree build and must not be redistributed; rebuild it" >&2
    exit 1
fi

rm -rf "$out" "$root/artifacts/$archive"
mkdir -p "$out/licenses"

echo "== publish $rid ($version)"
# Single-file: native libraries (Skia, HarfBuzz, ANGLE) are bundled too and extracted once per
# version to a temp folder on first start. Compression roughly halves the executable.
publish_tmp=$(mktemp -d)
trap 'rm -rf "$publish_tmp"' EXIT
publish() { # project output-file [extra msbuild args…]
    local project=$1 target=$2
    shift 2
    dotnet publish "$root/VideoQuality.$project" -c Release -r "$rid" --self-contained "${extra[@]}" "$@" \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -p:Version="${version%%+*}" -p:InformationalVersion="$version" \
        -p:DebugType=None -p:GenerateDocumentationFile=false \
        -o "$publish_tmp/$project" -v q
    mkdir -p "$(dirname "$target")"
    cp "$publish_tmp/$project/VideoQuality.$project$exe" "$target"
    chmod +x "$target"
}
publish App "$out/VideoQuality$exe"
# The CLI has no UI framework, so trimming unused framework code is safe (partial mode trims only
# framework assemblies); it removes most of the second runtime copy.
publish Cli "$out/tools/vq$exe" -p:PublishTrimmed=true -p:TrimMode=partial

echo "== bundle FFmpeg"
cp -a "$stage" "$out/ffmpeg"

echo "== bundle MKVToolNix"
"$root/scripts/fetch-mkvtoolnix.sh" "$rid" "$out/tools/mkvtoolnix"
mkvtoolnix_version=$(cat "$out/tools/mkvtoolnix/VERSION")
rm "$out/tools/mkvtoolnix/VERSION"
mkdir -p "$out/licenses/mkvtoolnix"
mv "$out/tools/mkvtoolnix/licenses/"* "$out/licenses/mkvtoolnix/"
rmdir "$out/tools/mkvtoolnix/licenses"

echo "== bundle the encoder ffmpeg (GPL)"
"$root/scripts/fetch-ffmpeg-gpl.sh" "$rid" "$out/tools/ffmpeg-encode"
encoder_version=$(cat "$out/tools/ffmpeg-encode/VERSION")
rm "$out/tools/ffmpeg-encode/VERSION"
mv "$out/tools/ffmpeg-encode/LICENSE-GPL-3.0.txt" "$out/licenses/FFmpeg-encoder-GPL-3.0.txt"

if [[ $rid == linux-x64 ]]; then
    echo "== desktop integration"
    rsvg-convert -w 256 -h 256 "$root/VideoQuality.App/Assets/app-icon.svg" -o "$out/videoquality.png"
    cat > "$out/install-desktop-entry.sh" <<'EOF'
#!/usr/bin/env sh
# Adds VideoQuality to the application menu and "Open With" for videos, for the current user,
# pointing at this folder. Run it again after moving the folder; remove with --uninstall.
set -e
here=$(cd "$(dirname "$0")" && pwd)
apps=${XDG_DATA_HOME:-$HOME/.local/share}/applications
icons=${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps
if [ "$1" = "--uninstall" ]; then
    rm -f "$apps/videoquality.desktop" "$icons/videoquality.png"
    echo "removed"; exit 0
fi
mkdir -p "$apps" "$icons"
cp "$here/videoquality.png" "$icons/videoquality.png"
cat > "$apps/videoquality.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=VideoQuality
GenericName=Video quality comparison
Comment=Compare encodes against a reference with VMAF
Exec="$here/VideoQuality" %F
Icon=videoquality
Terminal=false
Categories=AudioVideo;Video;
MimeType=video/mp4;video/x-matroska;video/quicktime;video/webm;video/x-msvideo;video/mp2t;video/x-ivf;video/x-yuv4mpeg;
Keywords=VMAF;encode;compare;quality;
DESKTOP
command -v update-desktop-database >/dev/null && update-desktop-database "$apps" >/dev/null 2>&1 || true
echo "installed: $apps/videoquality.desktop"
EOF
    chmod +x "$out/install-desktop-entry.sh"
fi

echo "== notices"
cp "$root/LICENSE" "$out/licenses/VideoQuality-MIT.txt"
cp "$staged_licenses"/*.txt "$out/licenses/"

if [[ $rid == win-x64 ]]; then
    mkvtoolnix_note='The Windows tools are statically linked with Qt 6 (LGPL-3.0), Boost (BSL-1.0) and other
libraries; the licenses upstream ships with them are in licenses/mkvtoolnix/.'
    runtime_note='  mingw-w64 winpthreads, libgcc, libstdc++   MIT/BSD-style; GPL-3.0 with the GCC Runtime Library
                       Exception, which permits redistribution of these runtime DLLs.
  GPU scoring loads the NVIDIA driver (nvcuda.dll) installed on the machine; none of it is shipped.'
else
    mkvtoolnix_note='The libraries in tools/mkvtoolnix/lib come from the upstream AppImage and are loaded
dynamically (replaceable): Qt 6 Core (LGPL-3.0), Boost (BSL-1.0), ICU (Unicode License), GLib, GnuTLS,
nettle, libtasn1, p11-kit, libidn2, libunistring (LGPL-2.1+ / GPL-compatible), libffi (MIT),
PCRE (BSD), libFLAC, libogg, libvorbis (BSD), libdvdread (GPL-2.0+), cmark (BSD-2-Clause).'
    runtime_note='  The libraries link only against the system C runtime and zlib (glibc and zlib are not shipped).
  GPU scoring loads the NVIDIA driver (libcuda.so.1) installed on the machine; none of it is shipped.'
fi

{
    cat <<EOF
VideoQuality $version ($rid) — third-party notices

FFMPEG
This package includes FFmpeg (https://ffmpeg.org) in ffmpeg/, licensed under the GNU Lesser General
Public License 2.1 or later (licenses/FFmpeg-LGPL-2.1.txt). It is built without --enable-gpl and
without --enable-nonfree. The libraries are loaded dynamically and can be replaced with a compatible
build.

Corresponding source: FFmpeg tag n9.0.2, https://git.ffmpeg.org/ffmpeg.git
Build script:         $build_script in https://github.com/SeaAndStars/VideoQuality
Build details:
$(sed 's/^/  /' "$stage/BUILDINFO.txt")

MKVTOOLNIX
tools/mkvtoolnix/ holds mkvmerge and mkvextract from MKVToolNix $mkvtoolnix_version
(https://mkvtoolnix.download), the upstream release binaries (on Linux, only their library search
path is set with patchelf so they find tools/mkvtoolnix/lib), licensed under the GNU General
Public License 2.0 (licenses/mkvtoolnix/MKVToolNix-GPL-2.0.txt). VideoQuality runs them as separate
programs. Corresponding source: mkvtoolnix-$mkvtoolnix_version.tar.xz, attached to every VideoQuality
release on GitHub and at https://mkvtoolnix.download/sources/.
$mkvtoolnix_note

ENCODER FFMPEG (tools/ffmpeg-encode/)
The Encode, Trim & Merge and Pipeline pages run a separate, statically linked FFmpeg build
($encoder_version) from BtbN/FFmpeg-Builds (https://github.com/BtbN/FFmpeg-Builds), configured with
--enable-gpl --enable-version3 and libraries such as x264, x265, SVT-AV1, libass and the NVENC/QSV/AMF
interfaces; licensed under the GNU General Public License 3.0 (licenses/FFmpeg-encoder-GPL-3.0.txt).
VideoQuality only runs it as a separate program; it is not linked into VideoQuality.
Corresponding source: the FFmpeg source of that commit is attached to every VideoQuality release on
GitHub; the exact library versions are pinned by the build scripts at the same tag of
https://github.com/BtbN/FFmpeg-Builds. A copy of the complete corresponding source is available on
request for three years (open an issue at https://github.com/SeaAndStars/VideoQuality).

NATIVE LIBRARIES (in ffmpeg/)
  libvmaf v3.2.1       BSD-2-Clause-Patent    https://github.com/Netflix/vmaf
  dav1d 1.5.1          BSD-2-Clause           https://code.videolan.org/videolan/dav1d
  nv-codec-headers     MIT                    https://code.ffmpeg.org/FFmpeg/nv-codec-headers
$runtime_note

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
} > "$out/licenses/THIRD-PARTY-NOTICES.txt"

echo "== archive"
if [[ $rid == win-x64 ]]; then
    (cd "$root/artifacts" && zip -qr "$archive" "$name")
else
    # tar keeps the executable bits and the libav soname symlinks.
    tar -C "$root/artifacts" -czf "$root/artifacts/$archive" "$name"
fi
du -sh "$out" "$root/artifacts/$archive" | sed 's/^/  /'
echo "done: artifacts/$archive"
