#!/usr/bin/env bash
# Puts MKVToolNix's mkvmerge and mkvextract (what Extract, Mux and the release pipeline run) into a
# folder for packaging, from the upstream release binaries:
#
#   ./scripts/fetch-mkvtoolnix.sh win-x64   DEST    # portable Windows build (static .exe)
#   ./scripts/fetch-mkvtoolnix.sh linux-x64 DEST    # from the AppImage: binaries + the libraries
#                                                   # they load, RUNPATH=$ORIGIN/lib
#   ./scripts/fetch-mkvtoolnix.sh source    DEST    # the corresponding source (GPL-2.0)
#
# Downloads are cached in ${VQ_MKVTOOLNIX_CACHE:-~/.cache/videoquality/mkvtoolnix} and checked
# against the SHA-256 pinned below. The pins were taken from upstream's published checksums for
# 97.0, and the source tarball's detached signature was verified against Moritz Bunkus's key
# (D919 9745 B054 5F2E 8197 062B 0F92 290A 445B 9007). Bumping the version means re-doing both.
set -euo pipefail

version=97.0
win_sha256=f7b7fc9430eb403137664b6f43464863b6a30f6925c51e8fbbca7e4d8938f572
appimage_sha256=00b89460bf5663bf20e57860fa6066833a55a6e6177a5626eacc62898beac013
source_sha256=5d43bf66e011ff5af09516a2dba2fb717b1631791a3a7498fcf74849a86929d3

what=${1:?usage: fetch-mkvtoolnix.sh win-x64|linux-x64|source DEST}
dest=${2:?usage: fetch-mkvtoolnix.sh win-x64|linux-x64|source DEST}
cache=${VQ_MKVTOOLNIX_CACHE:-$HOME/.cache/videoquality/mkvtoolnix}
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
        zip=$(fetch "https://mkvtoolnix.download/windows/releases/$version/mkvtoolnix-64-bit-$version.zip" "$win_sha256")
        unzip -q "$zip" 'mkvtoolnix/mkvmerge.exe' 'mkvtoolnix/mkvextract.exe' \
            'mkvtoolnix/doc/COPYING.txt' 'mkvtoolnix/doc/licenses/*' -d "$work"
        # The Windows command-line tools are statically linked (system DLLs only).
        cp "$work/mkvtoolnix/mkvmerge.exe" "$work/mkvtoolnix/mkvextract.exe" "$dest/"
        mkdir -p "$dest/licenses"
        cp "$work/mkvtoolnix/doc/COPYING.txt" "$dest/licenses/MKVToolNix-GPL-2.0.txt"
        cp "$work/mkvtoolnix/doc/licenses/"* "$dest/licenses/"
        ;;
    linux-x64)
        command -v patchelf >/dev/null || { echo "patchelf is required" >&2; exit 1; }
        appimage=$(fetch "https://mkvtoolnix.download/appimage/MKVToolNix_GUI-$version-x86_64.AppImage" "$appimage_sha256")
        # Extract without running the AppImage (it is untrusted until unpacked and inspected; the
        # runtime needs FUSE anyway): the squashfs starts right after the ELF runtime.
        offset=$(python3 - "$appimage" <<'PY'
import struct, sys
data = open(sys.argv[1], "rb").read(64)
shoff, = struct.unpack_from("<Q", data, 0x28)
shentsize, shnum = struct.unpack_from("<HH", data, 0x3A)
print(shoff + shentsize * shnum)
PY
)
        unsquashfs -q -n -d "$work/root" -o "$offset" "$appimage" usr/bin/mkvmerge usr/bin/mkvextract usr/lib >/dev/null
        root=$work/root
        mkdir -p "$dest/lib"
        cp "$root/usr/bin/mkvmerge" "$root/usr/bin/mkvextract" "$dest/"
        # Ship exactly the AppImage libraries the two tools load (Qt6Core, Boost, ICU, FLAC, …);
        # glibc, libstdc++, zlib and gmp come from the system like they do for the AppImage itself.
        LD_LIBRARY_PATH=$root/usr/lib ldd "$root/usr/bin/mkvmerge" "$root/usr/bin/mkvextract" |
            awk -v lib="$root/usr/lib/" 'index($3, lib) == 1 { print $3 }' | sort -u |
            while read -r so; do cp -L "$so" "$dest/lib/"; done
        patchelf --set-rpath '$ORIGIN/lib' "$dest/mkvmerge" "$dest/mkvextract"
        for so in "$dest"/lib/*; do patchelf --set-rpath '$ORIGIN' "$so"; done
        chmod 755 "$dest/mkvmerge" "$dest/mkvextract"
        chmod 644 "$dest"/lib/*
        "$dest/mkvmerge" --version | head -n1
        mkdir -p "$dest/licenses"
        source_tar=$(fetch "https://mkvtoolnix.download/sources/mkvtoolnix-$version.tar.xz" "$source_sha256")
        tar -xJf "$source_tar" -C "$work" "mkvtoolnix-$version/COPYING"
        cp "$work/mkvtoolnix-$version/COPYING" "$dest/licenses/MKVToolNix-GPL-2.0.txt"
        ;;
    source)
        cp "$(fetch "https://mkvtoolnix.download/sources/mkvtoolnix-$version.tar.xz" "$source_sha256")" "$dest/"
        ;;
    *)
        echo "unknown target: $what" >&2
        exit 1
        ;;
esac

echo "$version" > "$dest/VERSION" 2>/dev/null || true
echo "mkvtoolnix $version ($what) -> $dest"
