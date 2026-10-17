# FFmpeg and CUDA setup

## Why a custom build

Distro packages usually ship **without** libvmaf at all: Ubuntu's `ffmpeg` 8.0.1 configure line has
no `--enable-libvmaf`, so `ffmpeg -filters` lists only the internal `vmafmotion` filter. A
`libvmaf_cuda` filter additionally requires ffmpeg compiled against a **CUDA-enabled libvmaf** —
there is no separate `--enable-libvmaf-cuda` switch; the filter is enabled automatically when
libvmaf exposes `libvmaf_cuda.h` and `ffnvcodec` is available.

The verified stack:

| Component | Version |
|---|---|
| OS | Ubuntu 26.04 |
| FFmpeg | **n9.0.2**, built from source, shared libraries |
| libvmaf | **v3.2.1** with CUDA |
| .NET bindings | `FFmpeg.AutoGen` **9.0.1.1** |
| CUDA toolkit | 13.1 (`cuda-nvcc-13-1`, `cuda-cudart-dev-13-1`) |
| nv-codec-headers | tag `n13.0.19.1` (also builds FFmpeg 9) |
| GPU | RTX 5060 Ti (Blackwell, `sm_120`) |

FFmpeg 9 is an ABI break: every library major moved. The bindings must match the sonames that get
loaded:

| Library | FFmpeg 9 soname |
|---|---|
| libavcodec | `.so.63` |
| libavutil | `.so.61` |
| libavformat | `.so.63` |
| libavdevice | `.so.63` |
| libavfilter | `.so.12` |
| libswscale | `.so.10` |
| libswresample | `.so.7` |

`FfmpegRuntime` refuses to start on a mismatch: it looks for `libavcodec.so.63`, then checks
`avcodec_version() >> 16` and throws (and logs) if the loaded major differs from
`ExpectedAvcodecMajor`. That is why the app can see both the distro `.so.62` and this `.so.63` without
silently loading the wrong one.

## Building side by side

Everything installs under versioned prefixes and is exposed by a symlink, so rollback is one command.

```bash
# 0. toolchain
apt-get install -y --no-install-recommends \
    cuda-nvcc-13-1 cuda-cudart-dev-13-1 nasm meson libdav1d-dev

# 1. nv-codec-headers (pinned; master renamed an NVENC field and breaks 8.x/9.x)
git clone --depth 1 --branch n13.0.19.1 https://code.ffmpeg.org/FFmpeg/nv-codec-headers.git
cd nv-codec-headers && make install

# 2. libvmaf v3.2.1 with CUDA
git clone --depth 1 --branch v3.2.1 https://github.com/Netflix/vmaf.git vmaf-3.2.1
cd vmaf-3.2.1/libvmaf
export PATH=/usr/local/cuda/bin:$PATH
export NVCC_PREPEND_FLAGS="-arch=sm_120"
meson setup build --prefix=/opt/vmaf-3.2.1 --buildtype=release --default-library=shared \
      -Denable_cuda=true -Denable_tests=false -Denable_docs=false -Denable_float=true
ninja -C build && ninja -C build install

# 3. FFmpeg n9.0.2, shared
git clone --depth 1 --branch n9.0.2 https://git.ffmpeg.org/ffmpeg.git ffmpeg-9.0.2
cd ffmpeg-9.0.2
export PKG_CONFIG_PATH=/opt/vmaf-3.2.1/lib/x86_64-linux-gnu/pkgconfig
./configure --prefix=/opt/ffmpeg-9.0.2 --disable-doc --disable-debug \
    --enable-gpl --enable-nonfree --enable-libvmaf --enable-libdav1d \
    --enable-cuda-nvcc --enable-ffnvcodec --enable-shared \
    --nvccflags="-gencode arch=compute_120,code=sm_120" \
    --extra-cflags=-I/usr/local/cuda/include \
    --extra-ldflags="-L/opt/vmaf-3.2.1/lib/x86_64-linux-gnu \
        -Wl,-rpath,/opt/vmaf-3.2.1/lib/x86_64-linux-gnu \
        -Wl,-rpath,/opt/ffmpeg-9.0.2/lib -Wl,-rpath,/usr/local/cuda/lib64"
make -j"$(nproc)" && make install

# 4. expose it
ln -sfn /opt/ffmpeg-9.0.2 /opt/ffmpeg
```

Why each unusual flag is needed:

- `--enable-shared` — `FFmpeg.AutoGen` loads `libav*.so`; a static-only build leaves the viewer with
  the distro's older `.so.62`.
- `--nvccflags=...compute_120...` — FFmpeg's default nvcc target is `compute_60`, which CUDA 13
  dropped; configure fails at `check_nvcc` without this.
- `--default-library=shared` for libvmaf — a static libvmaf pulls in libstdc++ and fails ffmpeg's
  pkg-config link check.
- `NVCC_PREPEND_FLAGS` for libvmaf — its meson build compiles PTX without an arch flag.
- `-Wl,-rpath` — lets the ffmpeg binary find `libvmaf.so.3` without `LD_LIBRARY_PATH`.

Rollback: `ln -sfn /opt/ffmpeg-8.0.1 /opt/ffmpeg` (the previous build is kept). Rolling back across a
major version also means downgrading `FFmpeg.AutoGen` and `ExpectedAvcodecMajor`, because the bindings
are ABI-specific.

## Verifying

```bash
/opt/ffmpeg/bin/ffmpeg -filters | grep vmaf
#  .. libvmaf       VV->V
#  .. libvmaf_cuda  VV->V

dotnet run --project VideoQuality.Cli -- probe
# ffmpeg: /opt/ffmpeg/bin/ffmpeg
#   version     : ffmpeg version n9.0.2
#   libvmaf     : yes
#   libvmaf_cuda: yes
#   nvidia gpu  : yes
#   cuda usable : yes
#   libav       : /opt/ffmpeg/lib (avcodec 4129126)   # 63.1.102
```

`vq frame` additionally proves the bindings load the same soname and that decoding works.

## Windows

The Windows build is cross-compiled from Linux/WSL with mingw-w64, at the same versions as the Linux
stack (FFmpeg n9.0.2, libvmaf v3.2.1 with CUDA, dav1d 1.5.1, nv-codec-headers n13.0.19.1), and is
**redistributable**:

- FFmpeg is LGPL (no `--enable-gpl`; nothing the app uses needs it).
- CUDA filters use `--enable-cuda-llvm` (clang → PTX for `sm_75`, JIT-compiled by the driver for any
  Turing-or-newer GPU). `--enable-cuda-nvcc` is on FFmpeg's *nonfree* list and makes the binaries
  non-redistributable, so the Linux developer build's flags must not be copied into a release.
- libvmaf (BSD-2-Clause-Patent) compiles its own kernels with nvcc into a fatbin for `sm_75`…`sm_120`.

Cross-compiling CUDA works because neither FFmpeg nor libvmaf links the CUDA runtime: device code is
embedded as PTX/fatbin, and `nvcuda.dll` is loaded at run time through nv-codec-headers' dynlink
loader. The script patches one line of libvmaf's meson build so that nvcc is a build-machine tool,
and builds libvmaf inside its source tree because the nvcc command uses relative include paths.

### Release package

```bash
apt-get install -y gcc-mingw-w64-x86-64-posix g++-mingw-w64-x86-64-posix nasm meson ninja-build clang zip
./scripts/build-ffmpeg-windows.sh     # stages ~/build-win/stage/ffmpeg (VQ_WIN_PREFIX=/mnt/c/ffmpeg also installs)
./scripts/package-windows.sh          # artifacts/VideoQuality-<version>-win-x64.zip  (VQ_VERSION=1.0.0)
```

The zip (≈67 MB) unpacks to one folder: `VideoQuality.App.exe` and `VideoQuality.Cli.exe` with the
.NET runtime, `ffmpeg\` (ffmpeg.exe, ffprobe.exe, the libav/libvmaf/dav1d DLLs and the mingw runtime,
stripped), `licenses\` and `THIRD-PARTY-NOTICES.txt` (FFmpeg source tag and configure line for LGPL,
native library licenses, and every NuGet package's license read from its nuspec). The packager refuses
a staged FFmpeg built with `--enable-nonfree` or `--enable-cuda-nvcc`. The only thing the target
machine needs is an NVIDIA driver for GPU scoring; without one, scoring falls back to CPU.

The app prefers the bundled `ffmpeg\` for both scoring and decoding, ahead of any `ffmpeg.exe` on
PATH; an explicit path in Settings or `VQ_FFMPEG` still wins. Outside a release the DLLs are found in
`C:\ffmpeg\bin`, `FFMPEG_LIB_PATH`, `FFMPEG_ROOT\bin` or the folder of `VQ_FFMPEG`, and that folder is
added to the DLL search path, because Windows resolves `avcodec-63.dll`'s own imports (`libdav1d.dll`,
`libwinpthread-1.dll`) only from the app folder, system folders and PATH.

The first launch after unzipping can be slow while antivirus scans the new DLLs (over 15 s was
observed for the first `ffmpeg -filters`, 0.05 s afterwards); the splash covers it, and the
capability probe allows 60 s.

Verified on Windows 10 22H2, driver 617.42, RTX 5060 Ti:

| Check | Windows | Linux |
|---|---:|---:|
| 1080p H.264, 900 frames, CUDA (NEG mean, time) | 91.020, 1.6 s | 91.02, 2.6 s |
| same, CPU | 91.022, 4.7 s | 91.022 |
| AV1, CPU (dav1d) / CUDA (NVDEC) | 91.207 / 91.193 | 91.207 / 91.201 |
| 10-bit HEVC, CPU | 92.883 | 92.883 |
| decoded frame 42 (BGRA) | byte-identical | |
| app: five 720p encodes, two at a time | 0.8–0.9 s each | |

The first CUDA run takes several seconds longer while the driver JIT-compiles the PTX filters; the
driver caches the result.

## How the app chooses the backend

1. `Auto` (default): CUDA whenever `libvmaf_cuda` exists **and** `nvidia-smi -L` reports a GPU.
2. Otherwise CPU `libvmaf`.
3. `CPU` or `CUDA` can be forced in the toolbar. Forcing CUDA without a usable filter is an error,
   not a silent fallback.

Capability probing runs `ffmpeg -filters`, `ffmpeg -version` and `nvidia-smi -L`, cached per ffmpeg
path. The resolved backend is recorded on every report and shown next to the stats.

## Default model

Scores use **VMAF NEG** by default (`model=version=vmaf_v0.6.1neg`, always passed explicitly). NEG is
verified on both backends: a sharpened encode scores 100.000 with plain VMAF and 86.223 with NEG,
identically on CPU and CUDA. See [metrics.md](metrics.md).

## AV1

AV1 works end to end. With NEG, an 8-bit SVT-AV1 encode scores **91.207 on CPU (dav1d)** and
**91.203 on CUDA (`av1_cuvid`)**, and the viewer decodes it through FFmpeg.AutoGen with an exact frame
match against ffmpeg's own extraction. Without dav1d the native AV1 decoder failed on SVT-AV1 output
(`Get current frame error`); `--enable-libdav1d` fixes the CPU path, while the GPU path decodes through
NVDEC and was unaffected.

## Decoding and NVDEC failures

The CUDA scoring path uses `-hwaccel cuda`, which decodes through NVDEC (`*_cuvid`). NVDEC does not
support every profile or bit depth, so a CUDA run can fail even when the filter is present. With
backend **Auto**, the runner retries once on CPU and records `fellBackToCpu` plus the reason in the
report (and the app says so in the status line). An explicitly forced CUDA run fails loudly instead
of silently changing backend.

## 10-bit inputs

`libvmaf_cuda` accepts `yuv420p` only. Asking it for `p010le` or `yuv420p10le` fails with
`Invalid argument` — re-verified against FFmpeg 9.0.2 + libvmaf 3.2.1, so the limitation survived
the upgrade. The CUDA path therefore does:

```
[0:v]scale_cuda=format=yuv420p[dis];[1:v]scale_cuda=format=yuv420p[ref];[dis][ref]libvmaf_cuda=...
```

Decode, conversion and scoring all happen on the GPU, but precision is 8-bit. With NEG on the 10-bit
test encode: **92.755 (CUDA, converted) vs 92.883 (CPU, true 10-bit)**. The status line says that the
conversion happened and the table shows `CUDA·8b`. Choose **CPU** when 10-bit precision matters more
than throughput.

## Resolving the executable

The scorer accepts an explicit path; otherwise it resolves one in this order:

1. `$VQ_FFMPEG`
2. `$FFMPEG_ROOT/bin/ffmpeg`
3. `/opt/ffmpeg/bin/ffmpeg`
4. `/usr/local/opt/ffmpeg/bin/ffmpeg`
5. `~/.local/opt/ffmpeg/bin/ffmpeg`
6. `ffmpeg` on `PATH` (used only if it already has libvmaf)

The first candidate that reports `libvmaf`/`libvmaf_cuda` wins. The winner is printed by `probe`, used
by `score`, shown in the app's **Settings** flyout, and stored in each report.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `FFmpeg binding mismatch: ... expects libavcodec 63, but 62 was loaded` | Bindings and native libs disagree. Point `FFMPEG_LIB_PATH` at `/opt/ffmpeg/lib` or rebuild the matching ffmpeg; do not mix majors. |
| `probe` shows no libvmaf | The ffmpeg on PATH is a distro build. Point `--ffmpeg`/`VQ_FFMPEG` at the custom build. |
| `libvmaf_cuda: no` | ffmpeg was built without `--enable-cuda-nvcc`/`--enable-ffnvcodec`, or libvmaf without CUDA. |
| `nvidia gpu: no` | `nvidia-smi` missing or failing (WSL without GPU passthrough, driver broken). |
| `configure: failed checking for nvcc` | Add `--nvccflags` with `sm_120` (or your GPU's arch). |
| `nvenc.c: ... has no member named 'countingType'` | nv-codec-headers is newer than ffmpeg; use the pinned tag. |
| `undefined reference to __cxa_throw` at link | libvmaf is static; rebuild shared. |
| CUDA filter errors on 10-bit formats | Use `scale_cuda=format=yuv420p`; libvmaf_cuda does not accept `p010le`. |
| AV1 CPU score fails with `Get current frame error` | Build ffmpeg with `--enable-libdav1d`. |
| CUDA scores differ from CPU | Expected: different implementation and, for 10-bit, an 8-bit intermediate. CUDA means also vary run-to-run (~0.01). Do not mix backends within one comparison. |
