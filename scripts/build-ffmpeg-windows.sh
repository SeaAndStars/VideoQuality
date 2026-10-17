#!/usr/bin/env bash
# Cross-compiles FFmpeg n9.0.2 + libvmaf v3.2.1 (CUDA) + dav1d for 64-bit Windows from Linux/WSL,
# matching the Linux stack in docs/ffmpeg-cuda.md so both platforms score identically.
#
# Why cross-compiling works for CUDA: neither FFmpeg nor libvmaf links the CUDA runtime. Device code
# is compiled by the *Linux* nvcc into PTX/fatbin and embedded as byte arrays; the host side loads
# nvcuda.dll at run time through nv-codec-headers' dynlink loader. The Windows driver JIT-compiles
# (or directly runs) the embedded code.
#
# Licensing: the result is redistributable. FFmpeg is built LGPL (no --enable-gpl, nothing here needs
# it) with --enable-cuda-llvm: FFmpeg classes --enable-cuda-nvcc as *nonfree*, which would make the
# binaries non-redistributable. clang compiles the same CUDA filters (scale_cuda, …) to PTX for
# sm_75, which the driver JIT-compiles for any Turing-or-newer GPU. libvmaf (BSD-2-Clause-Patent)
# still compiles its kernels with nvcc into a fatbin for sm_75…sm_120.
#
# Requires: mingw-w64 (posix threads), meson, ninja, nasm, pkg-config, git, clang, CUDA toolkit
# (nvcc, bin2c, headers).
#   apt-get install -y gcc-mingw-w64-x86-64-posix g++-mingw-w64-x86-64-posix nasm meson ninja-build clang
#
#   ./scripts/build-ffmpeg-windows.sh                       # stages ~/build-win/stage/ffmpeg
#   VQ_WIN_PREFIX=/mnt/c/ffmpeg ./scripts/build-ffmpeg-windows.sh   # also installs C:\ffmpeg\bin
#
# scripts/package-windows.sh bundles the staged folder into the release zip.
set -euo pipefail

work=${VQ_WIN_WORK:-$HOME/build-win}
install_dir=${VQ_WIN_PREFIX:-}
jobs=$(nproc)
cuda=${CUDA_HOME:-/usr/local/cuda}
host=x86_64-w64-mingw32

src=$work/src
build=$work/build
prefix=$work/prefix
mkdir -p "$src" "$build" "$prefix"
export PATH=$cuda/bin:$PATH
export PKG_CONFIG_LIBDIR=$prefix/lib/pkgconfig
export PKG_CONFIG_PATH=

fetch() { # url tag dir
    [[ -d $src/$3 ]] || git -c advice.detachedHead=false clone -q --depth 1 --branch "$2" "$1" "$src/$3"
}

fetch https://code.ffmpeg.org/FFmpeg/nv-codec-headers.git n13.0.19.1 nv-codec-headers
fetch https://github.com/Netflix/vmaf.git v3.2.1 vmaf-3.2.1
fetch https://code.videolan.org/videolan/dav1d.git 1.5.1 dav1d
fetch https://git.ffmpeg.org/ffmpeg.git n9.0.2 ffmpeg-9.0.2

cat > "$build/cross.txt" <<EOF
[binaries]
c = '$host-gcc-posix'
cpp = '$host-g++-posix'
ar = '$host-ar'
strip = '$host-strip'
windres = '$host-windres'
pkg-config = 'pkg-config'

[built-in options]
c_link_args = ['-static-libgcc']
cpp_link_args = ['-static-libgcc', '-static-libstdc++']

[host_machine]
system = 'windows'
cpu_family = 'x86_64'
cpu = 'x86_64'
endian = 'little'
EOF

echo "== nv-codec-headers"
make -C "$src/nv-codec-headers" PREFIX="$prefix" install >/dev/null

echo "== dav1d"
meson setup --reconfigure "$build/dav1d" "$src/dav1d" --cross-file "$build/cross.txt" \
    --prefix="$prefix" --libdir=lib --buildtype=release --default-library=shared \
    -Denable_tools=false -Denable_tests=false >/dev/null
ninja -C "$build/dav1d" install >/dev/null

echo "== libvmaf (CUDA)"
# nvcc only produces device code here, so it is a build-machine tool. Upstream asks meson for a
# host-machine CUDA compiler, which does not exist when the host is Windows.
vmaf_meson=$src/vmaf-3.2.1/libvmaf/src/meson.build
sed -i "s/add_languages('cuda', required : true)\$/add_languages('cuda', required : true, native : true)/; \
        s/meson.get_compiler('cuda')\$/meson.get_compiler('cuda', native : true)/" "$vmaf_meson"
# The nvcc command line uses include paths relative to a build directory inside libvmaf/.
vmaf_build=$src/vmaf-3.2.1/libvmaf/build-win
meson setup --reconfigure "$vmaf_build" "$src/vmaf-3.2.1/libvmaf" --cross-file "$build/cross.txt" \
    --prefix="$prefix" --libdir=lib --buildtype=release --default-library=shared \
    -Denable_cuda=true -Denable_tests=false -Denable_docs=false -Denable_float=true \
    -Dc_args="-I$prefix/include" >/dev/null
ninja -C "$vmaf_build" install >/dev/null

echo "== FFmpeg"
mkdir -p "$build/ffmpeg"
cd "$build/ffmpeg"
"$src/ffmpeg-9.0.2/configure" \
    --prefix="$prefix" --arch=x86_64 --target-os=mingw32 --cross-prefix=$host- \
    --cc=$host-gcc-posix --cxx=$host-g++-posix --pkg-config=pkg-config --enable-cross-compile \
    --disable-doc --disable-debug --enable-shared --disable-static \
    --enable-libvmaf --enable-libdav1d \
    --enable-cuda-llvm --enable-ffnvcodec --enable-nvdec --enable-cuvid \
    --enable-d3d11va --enable-dxva2 \
    --nvcc=clang --nvccflags="--cuda-gpu-arch=sm_75 -O2" \
    --extra-cflags="-I$prefix/include -I$cuda/include" \
    --extra-ldflags="-L$prefix/lib -static-libgcc" >/dev/null
make -j"$jobs" >/dev/null
make install >/dev/null

echo "== stage"
# Everything a Windows machine needs to run ffmpeg.exe and load the DLLs, in one flat folder.
stage=$work/stage/ffmpeg
rm -rf "$stage"
mkdir -p "$stage"
cp "$prefix"/bin/ffmpeg.exe "$prefix"/bin/ffprobe.exe "$prefix"/bin/*.dll "$stage/"
# mingw runtime the DLLs import (redistributable under the GCC runtime library exception).
for dll in libwinpthread-1.dll libstdc++-6.dll libgcc_s_seh-1.dll; do
    cp "$($host-gcc-posix -print-file-name=$dll)" "$stage/"
done
# Distro mingw runtimes ship with symbols (libstdc++ is 26 MB unstripped).
$host-strip --strip-unneeded "$stage"/*.dll "$stage"/*.exe
{
    echo "FFmpeg n9.0.2 (LGPL-2.1-or-later), libvmaf v3.2.1 (CUDA), dav1d 1.5.1, nv-codec-headers n13.0.19.1"
    echo "Built by scripts/build-ffmpeg-windows.sh with mingw-w64 GCC $($host-gcc-posix -dumpversion)."
    echo "FFmpeg configure: $(sed -n 's/^#define FFMPEG_CONFIGURATION "\(.*\)"$/\1/p' "$build/ffmpeg/config.h")"
} > "$stage/BUILDINFO.txt"

if [[ -n $install_dir ]]; then
    echo "== install to $install_dir"
    mkdir -p "$install_dir/bin"
    cp "$stage"/* "$install_dir/bin/"
fi

echo "done: $stage"
