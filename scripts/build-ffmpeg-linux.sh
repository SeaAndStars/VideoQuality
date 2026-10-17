#!/usr/bin/env bash
# Builds a relocatable, redistributable FFmpeg n9.0.2 + libvmaf v3.2.1 (CUDA) + dav1d for Linux
# x86-64, the same stack as scripts/build-ffmpeg-windows.sh, for bundling in the Linux release.
#
# Not the developer build from docs/ffmpeg-cuda.md: that one uses --enable-cuda-nvcc and
# --enable-nonfree, which make it non-redistributable. Here FFmpeg is LGPL with --enable-cuda-llvm
# (clang -> sm_75 PTX), autodetection is off so the libraries depend on little beyond glibc and
# zlib, and every binary gets RUNPATH=$ORIGIN so the folder works wherever it is unpacked.
#
# Requires: gcc, meson, ninja, nasm, pkg-config, git, clang, patchelf, CUDA toolkit (nvcc, bin2c).
#   ./scripts/build-ffmpeg-linux.sh          # stages ~/build-linux/stage/ffmpeg
set -euo pipefail

work=${VQ_LINUX_WORK:-$HOME/build-linux}
jobs=$(nproc)
cuda=${CUDA_HOME:-/usr/local/cuda}

src=$work/src
build=$work/build
prefix=$work/prefix
mkdir -p "$src" "$build" "$prefix"
export PATH=$cuda/bin:$PATH
export PKG_CONFIG_PATH=$prefix/lib/pkgconfig
export PKG_CONFIG_LIBDIR=$prefix/lib/pkgconfig

# libvmaf embeds its built-in models (vmaf_v0.6.1neg, …) only when xxd exists, and silently builds
# without them otherwise; every score would then fail with "could not load libvmaf model".
command -v xxd >/dev/null || { echo "xxd is required (apt-get install xxd)" >&2; exit 1; }

fetch() { # url tag dir
    [[ -d $src/$3 ]] || git -c advice.detachedHead=false clone -q --depth 1 --branch "$2" "$1" "$src/$3"
}

fetch https://code.ffmpeg.org/FFmpeg/nv-codec-headers.git n13.0.19.1 nv-codec-headers
fetch https://github.com/Netflix/vmaf.git v3.2.1 vmaf-3.2.1
fetch https://code.videolan.org/videolan/dav1d.git 1.5.1 dav1d
fetch https://git.ffmpeg.org/ffmpeg.git n9.0.2 ffmpeg-9.0.2

echo "== nv-codec-headers"
make -C "$src/nv-codec-headers" PREFIX="$prefix" install >/dev/null

echo "== dav1d"
meson setup --reconfigure "$build/dav1d" "$src/dav1d" \
    --prefix="$prefix" --libdir=lib --buildtype=release --default-library=shared \
    -Denable_tools=false -Denable_tests=false >/dev/null
ninja -C "$build/dav1d" install >/dev/null

echo "== libvmaf (CUDA)"
# The nvcc command line uses include paths relative to a build directory inside libvmaf/, and only
# the C compiler gets c_args: nvcc needs the prefix (nv-codec-headers) passed separately.
export NVCC_PREPEND_FLAGS="-I$prefix/include"
vmaf_build=$src/vmaf-3.2.1/libvmaf/build-linux
meson setup --reconfigure "$vmaf_build" "$src/vmaf-3.2.1/libvmaf" \
    --prefix="$prefix" --libdir=lib --buildtype=release --default-library=shared \
    -Denable_cuda=true -Denable_tests=false -Denable_docs=false -Denable_float=true \
    -Dc_args="-I$prefix/include" >/dev/null
ninja -C "$vmaf_build" install >/dev/null

echo "== FFmpeg"
mkdir -p "$build/ffmpeg"
cd "$build/ffmpeg"
"$src/ffmpeg-9.0.2/configure" \
    --prefix="$prefix" --disable-doc --disable-debug --enable-shared --disable-static \
    --disable-autodetect --enable-zlib \
    --enable-libvmaf --enable-libdav1d \
    --enable-cuda-llvm --enable-ffnvcodec --enable-nvdec --enable-cuvid \
    --nvcc=clang --nvccflags="--cuda-gpu-arch=sm_75 -O2" \
    --extra-cflags="-I$prefix/include -I$cuda/include" \
    --extra-ldflags="-L$prefix/lib" >/dev/null
make -j"$jobs" >/dev/null
make install >/dev/null

echo "== stage"
# One flat folder: binaries and the exact sonames they load, all finding each other via $ORIGIN.
stage=$work/stage/ffmpeg
rm -rf "$stage"
mkdir -p "$stage"
cp "$prefix/bin/ffmpeg" "$prefix/bin/ffprobe" "$stage/"
for lib in avcodec avdevice avfilter avformat avutil swresample swscale vmaf dav1d; do
    cp -P "$prefix"/lib/lib$lib.so* "$stage/"
done
# Keep only the soname links (libavcodec.so.63 -> libavcodec.so.63.x.y); drop the dev .so links.
find "$stage" -maxdepth 1 -type l -name '*.so' -delete
for file in "$stage"/ffmpeg "$stage"/ffprobe $(find "$stage" -maxdepth 1 -type f -name '*.so.*'); do
    patchelf --set-rpath '$ORIGIN' "$file"
    strip --strip-unneeded "$file"
done
{
    echo "FFmpeg n9.0.2 (LGPL-2.1-or-later), libvmaf v3.2.1 (CUDA), dav1d 1.5.1, nv-codec-headers n13.0.19.1"
    echo "Built by scripts/build-ffmpeg-linux.sh with $(gcc --version | head -1); RUNPATH=\$ORIGIN."
    echo "FFmpeg configure: $(sed -n 's/^#define FFMPEG_CONFIGURATION "\(.*\)"$/\1/p' "$build/ffmpeg/config.h")"
    echo "Requires glibc >= $(objdump -T "$stage"/ffmpeg "$stage"/*.so.* 2>/dev/null \
        | grep -o 'GLIBC_[0-9.]*' | sed 's/GLIBC_//' | sort -V | tail -1)"
} > "$stage/BUILDINFO.txt"

# License texts travel with the binaries, so packaging needs no source checkout.
licenses=$work/stage/licenses
rm -rf "$licenses"
mkdir -p "$licenses"
cp "$src/ffmpeg-9.0.2/COPYING.LGPLv2.1" "$licenses/FFmpeg-LGPL-2.1.txt"
cp "$src/vmaf-3.2.1/LICENSE" "$licenses/libvmaf-BSD-2-Clause-Patent.txt"
cp "$src/dav1d/COPYING" "$licenses/dav1d-BSD-2-Clause.txt"
sed -n '1,/\*\//p' "$src/nv-codec-headers/include/ffnvcodec/dynlink_loader.h" > "$licenses/nv-codec-headers-MIT.txt"

# Anything still resolved outside the folder besides the C runtime and zlib is a portability bug.
echo "== external dependencies"
for file in "$stage"/ffmpeg "$stage"/*.so.*; do
    ldd "$file" | awk '/=>/ {print $1}'
done | sort -u | grep -vE '^(libc|libm|libdl|librt|libpthread|libgcc_s|libstdc\+\+|ld-linux|linux-vdso|libz)\.' \
    | grep -vE '^(libav|libsw|libvmaf|libdav1d)' || true

# Self-test the staged folder exactly as shipped: built-in NEG model, dav1d, libvmaf.
echo "== self-test"
"$stage/ffmpeg" -v error -nostdin \
    -f lavfi -i testsrc2=size=320x240:rate=25:duration=0.4 \
    -f lavfi -i testsrc2=size=320x240:rate=25:duration=0.4 \
    -lavfi "[0:v][1:v]libvmaf=model=version=vmaf_v0.6.1neg" -f null - \
    || { echo "self-test failed: the staged FFmpeg cannot score with the built-in NEG model" >&2; exit 1; }
# Capture first: with pipefail, grep -q closing the pipe early would fail ffmpeg with SIGPIPE.
decoders=$("$stage/ffmpeg" -hide_banner -decoders 2>/dev/null)
grep -q libdav1d <<<"$decoders" || { echo "self-test failed: libdav1d decoder missing" >&2; exit 1; }
echo "self-test passed"

echo "done: $stage"
