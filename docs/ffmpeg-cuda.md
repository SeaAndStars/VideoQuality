# FFmpeg and CUDA setup

## Why a custom build

Distro packages usually ship **without** libvmaf at all: Ubuntu's `ffmpeg` 8.0.1 configure line has
no `--enable-libvmaf`, so `ffmpeg -filters` lists only the internal `vmafmotion` filter. A
`libvmaf_cuda` filter additionally requires ffmpeg compiled against a **CUDA-enabled libvmaf** —
there is no separate `--enable-libvmaf-cuda` switch; the filter is enabled automatically when
libvmaf exposes `libvmaf_cuda.h` and `ffnvcodec` is available.

The reference build for this project:

| Component | Version |
|---|---|
| OS | Ubuntu 26.04 |
| FFmpeg | 8.0.1, built from source |
| libvmaf | master (3.2.0-dev), CUDA enabled |
| CUDA toolkit | 13.1 (`cuda-nvcc-13-1`, `cuda-cudart-dev-13-1`) |
| nv-codec-headers | tag `n13.0.19.1` |
| GPU | RTX 5060 Ti (Blackwell, `sm_120`) |

Installed to `/opt/vmaf` (libvmaf) and `/opt/ffmpeg` (ffmpeg), deliberately outside `/usr` so the
system packages stay untouched. The custom ffmpeg has no libx264, which is why it is **not** placed
on `PATH` — the app resolves it by path instead.

## Building

```bash
# 0. CUDA compiler, headers, AV1 decoder (Ubuntu package names)
apt-get install -y --no-install-recommends cuda-nvcc-13-1 cuda-cudart-dev-13-1 nasm meson libdav1d-dev

# 1. nv-codec-headers
git clone https://code.ffmpeg.org/FFmpeg/nv-codec-headers.git
cd nv-codec-headers
git checkout n13.0.19.1   # master renamed an NVENC struct field and breaks ffmpeg 8.0.x
make install

# 2. libvmaf with CUDA (v3.0.0 does not compile against CUDA 13; master does)
git clone https://github.com/Netflix/vmaf.git
cd vmaf
git checkout 94f3ff0dd8d0062e39b27d0eadfc57457022b663   # verified with CUDA 13.1 + ffmpeg 8.0.1
cd libvmaf
export PATH=/usr/local/cuda/bin:$PATH
export NVCC_PREPEND_FLAGS="-arch=sm_120"
meson setup build --prefix=/opt/vmaf --buildtype=release --default-library=shared \
      -Denable_cuda=true -Denable_tests=false -Denable_docs=false -Denable_float=true
ninja -C build && ninja -C build install

# 3. ffmpeg
git clone --depth 1 --branch n8.0.1 https://git.ffmpeg.org/ffmpeg.git ffmpeg-8.0.1
cd ffmpeg-8.0.1
export PKG_CONFIG_PATH=/opt/vmaf/lib/x86_64-linux-gnu/pkgconfig
./configure --prefix=/opt/ffmpeg --disable-doc --enable-gpl --enable-nonfree \
    --enable-libvmaf --enable-libdav1d --enable-cuda-nvcc --enable-ffnvcodec \
    --nvccflags="-gencode arch=compute_120,code=sm_120" \
    --extra-cflags=-I/usr/local/cuda/include \
    --extra-ldflags="-L/opt/vmaf/lib/x86_64-linux-gnu -Wl,-rpath,/opt/vmaf/lib/x86_64-linux-gnu -Wl,-rpath,/usr/local/cuda/lib64"
make -j"$(nproc)" && make install
```

Why each unusual flag is needed:

- `--nvccflags=...compute_120...` — FFmpeg's default nvcc target is `compute_60`, which CUDA 13
  dropped. Configure fails at `check_nvcc` without this.
- `--default-library=shared` for libvmaf — a static libvmaf pulls in libstdc++ and fails ffmpeg's
  `pkg-config` link check.
- `NVCC_PREPEND_FLAGS` for libvmaf — its meson build compiles PTX without an arch flag; PTX for
  `sm_120` is what the driver JITs on Blackwell.
- `-Wl,-rpath` — lets `/opt/ffmpeg/bin/ffmpeg` find `libvmaf.so.3` without `LD_LIBRARY_PATH`.
- `--enable-libdav1d` — the native AV1 decoder in this build failed on SVT-AV1 output
  (`Get current frame error`); dav1d decodes it, and it is what the distro build uses too.

## Verifying

```bash
/opt/ffmpeg/bin/ffmpeg -filters | grep vmaf
#  .. libvmaf       VV->V
#  .. libvmaf_cuda  VV->V

dotnet run --project VideoQuality.Cli -- probe
# ffmpeg: /opt/ffmpeg/bin/ffmpeg
#   libvmaf     : yes
#   libvmaf_cuda: yes
#   nvidia gpu  : yes
#   cuda usable : yes
```

## How the app chooses the backend

1. `Auto` (default): CUDA whenever `libvmaf_cuda` exists **and** `nvidia-smi -L` reports a GPU.
2. Otherwise CPU `libvmaf`.
3. `CPU` or `CUDA` can be forced in the toolbar. Forcing CUDA without a usable filter is an error,
   not a silent fallback.

Capability probing runs `ffmpeg -filters`, `ffmpeg -version` and `nvidia-smi -L`, cached per ffmpeg
path. The resolved backend is recorded on every report and shown next to the stats.

## AV1

AV1 is exercised end to end: an 8-bit SVT-AV1 encode scores **92.508 on CPU (dav1d)** and
**92.442 on CUDA (`av1_cuvid`)**, and the viewer decodes it through FFmpeg.AutoGen with an exact frame
match against ffmpeg's own extraction. Without dav1d, CPU AV1 scoring of SVT-AV1 output fails; the GPU
path was unaffected because NVDEC does the decoding.

## Decoding and NVDEC failures

The CUDA scoring path uses `-hwaccel cuda`, which decodes through NVDEC (`*_cuvid`). NVDEC does not
support every profile or bit depth, so a CUDA run can fail even when the filter is present. With
backend **Auto**, the runner retries once on CPU and records `fellBackToCpu` plus the reason in the
report (and the app says so in the status line). An explicitly forced CUDA run fails loudly instead
of silently changing backend.

## 10-bit inputs

`libvmaf_cuda` accepts `yuv420p` only. Asking it for `p010le` or `yuv420p10le` fails with
`Invalid argument`, verified against ffmpeg 8.0.1 + libvmaf master. The CUDA path therefore does:

```
[0:v]scale_cuda=format=yuv420p[dis];[1:v]scale_cuda=format=yuv420p[ref];[dis][ref]libvmaf_cuda=...
```

Decode, conversion and scoring all happen on the GPU, but precision is 8-bit. On the 10-bit HEVC test
encode this measured **93.848 (CUDA) vs 93.888 (CPU)** — a 0.04 difference, and the status line says
that the conversion happened. Choose **CPU** when 10-bit precision matters more than throughput.

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
| `probe` shows no libvmaf | The ffmpeg on PATH is a distro build. Point `--ffmpeg`/`VQ_FFMPEG` at the custom build. |
| `libvmaf_cuda: no` | ffmpeg was built without `--enable-cuda-nvcc`/`--enable-ffnvcodec`, or libvmaf was built without CUDA. |
| `nvidia gpu: no` | `nvidia-smi` missing or failing (WSL without GPU passthrough, driver broken). |
| `configure: failed checking for nvcc` | Add `--nvccflags` with `sm_120` (or your GPU's arch). |
| `nvenc.c: ... has no member named 'countingType'` | nv-codec-headers is newer than ffmpeg; use tag `n13.0.19.1`. |
| `undefined reference to __cxa_throw` at link | libvmaf is static; rebuild shared. |
| CUDA scores differ from CPU | Expected: different implementation and, for 10-bit, an 8-bit intermediate. Do not mix backends within a comparison. |
| CUDA filter errors on 10-bit formats | Use `scale_cuda=format=yuv420p`; the filter does not accept `p010le`. |
| AV1 CPU score fails with `Get current frame error` | Build ffmpeg with `--enable-libdav1d`; the native decoder failed on SVT-AV1 output. |
