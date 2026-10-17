# VideoQuality

A small desktop tool for comparing two videos frame by frame and looking at per-frame VMAF.

- **Scoring** — shells out to `ffmpeg` with libvmaf; uses the CUDA filter (`libvmaf_cuda`) when the
  build and machine support it, otherwise the CPU filter. Auto by default, but either can be forced.
- **Frame viewer** — decodes both files to BGRA through FFmpeg.AutoGen (no `ffmpeg` process, no
  external frame extraction) and shows them side by side, wiped, flickered, or as a difference map.
- **Synced zoom and pan** — wheel to zoom around the cursor, drag to pan, `Fit` and `1:1` buttons.
  Both panes share one viewport, so the same pixels are always on screen at the same scale.
- **Multi-encode comparison** — one reference, N encodes. Curves overlay on the graph (autoscaled by
  default, with a `0–100` opt-in); the table shows mean, p1, true video bitrate and file size;
  selecting a row swaps the viewer to that encode.
- **One cursor** — the slider, the arrow keys, playback and clicking the score graph all set a single
  `CurrentFrame`, and both decoders seek to it. Exact seeks (keyframe → decode forward), with a small
  LRU cache so stepping back and forth is cheap.
- **Difference view** — perceptual heatmap with a gain slider, an auto-gain button (normalises the
  99.9th percentile) and a luma / chroma / both switch, with the scale shown in the legend.
- **Progress and cancel** — scoring progress comes from `ffmpeg -progress`, shown in the status bar
  with frame counts, encoding speed and a Cancel button.
- **Codec facts in the header** — bit depth, colour matrix/range and a "lossy" badge on the
  reference; a 10-bit encode automatically falls back to CPU VMAF when the backend is Auto.
- **Pooled stats** — mean, harmonic mean, 1st percentile, min, max and standard deviation.

VMAF is a full-reference metric built from hand-crafted features (VIF, ADM, motion) and a small SVM
regressor. It never looks at the bitstream, so the codec (H.264 / HEVC / AV1 / …) does not matter.

![The app after scoring a clip on the CUDA backend](docs/screenshot.png)

## Layout

```
VideoQuality.Core/
  Scoring/VmafRunner.cs       ffmpeg + libvmaf / libvmaf_cuda, JSON parsing, capability probing
  Models/VmafStats.cs         pooled statistics
  Decoding/FrameDecoder.cs    FFmpeg.AutoGen: open, seek, decode to BGRA
  Decoding/PacketIndex.cs     packet walk (no decode): true bitrate, frame count, keyframes
  Decoding/FfmpegRuntime.cs   locates the native libav* libraries for the bindings
  Imaging/PixelDiff.cs        difference heatmap
VideoQuality.App/
  ViewModels/MainViewModel.cs single frame cursor, encoders, playback, compare modes
  ViewModels/EncodeEntry.cs   one scored encode (report, bitrate, colour)
  ViewModels/ViewportState.cs shared zoom/pan state
  Controls/FrameView.cs       one image with an explicit viewport
  Controls/WipeView.cs        two images with a draggable divider
  Views/MainWindow.axaml      viewer, ScottPlot graph, encode table, slider
VideoQuality.Cli/
  Program.cs                  headless probe / score / decode-frame commands
```

## Build

```bash
dotnet build VideoQuality.slnx
```

Requires the .NET 10 SDK. `FFmpeg.AutoGen` 8.0.0.1 matches FFmpeg 8.x (`libavcodec.so.62`); the
bindings and the installed `libav*` major version must agree.

## Run

```bash
# Open a reference and score any number of encodes immediately
dotnet run --project VideoQuality.App -- /path/ref.mkv /path/a.mp4 /path/b.mp4 /path/c.mkv

# or start empty and use Reference… / Encodes… (multi-select); drop files on the window too
dotnet run --project VideoQuality.App
```

The `ffmpeg` box at the bottom right shows which executable the scorer uses. With no configuration
the app resolves one: an explicit path wins, otherwise it uses `ffmpeg` on `PATH` when that build
already has libvmaf, otherwise it looks for a custom build in `$VQ_FFMPEG`,
`$FFMPEG_ROOT/bin/ffmpeg`, `/opt/ffmpeg/bin/ffmpeg` and a couple of standard prefixes. The resolved
path is displayed in the box, so it is always clear which build is in use — type another path to
override it. On this machine the distro ffmpeg has no libvmaf, so it resolves to
`/opt/ffmpeg/bin/ffmpeg` automatically.

Keyboard: `←`/`→` step one frame, `Shift` + `←`/`→` step ten, `Home`/`End` jump to the ends, `Space`
plays or pauses at the native frame rate, and holding `F` reveals the encode in Flicker mode (or use
the Auto flicker checkbox). Clicking the graph jumps to a frame; in Wipe mode drag the divider.

The graph autoscales to the data by default so encodes are distinguishable; the `0–100` checkbox
pins it to the full range when comparing across clips. The dashed line is the selected encode's mean;
the red line marks VMAF 85. The `Settings` button in the status bar holds the ffmpeg path.

While a score runs, the status bar shows `name: 439/900 frames · 439 fps`, a progress bar and Cancel.

![Wipe comparison](docs/screenshot-wipe.png)

![Scoring progress](docs/screenshot-progress.png)

![Difference heatmap](docs/screenshot-difference.png)

## CLI

```bash
dotnet run --project VideoQuality.Cli -- probe
dotnet run --project VideoQuality.Cli -- score ref.mkv dist.mp4 --gpu
dotnet run --project VideoQuality.Cli -- frame dist.mp4 42 --ppm /tmp/f42.ppm
```

`probe` resolves the ffmpeg executable the same way the app does, then reports whether that build has
`libvmaf`, `libvmaf_cuda`, and whether an NVIDIA GPU is visible. `score` prints pooled stats and the exact command line. `frame` exercises the decoder
directly — it never runs `ffmpeg`.

## Building ffmpeg with GPU VMAF

Distro builds usually ship without the VMAF filter, and a `libvmaf_cuda` filter needs FFmpeg
compiled against a CUDA-enabled libvmaf. On Blackwell (RTX 50xx, `sm_120`) you need CUDA 12.8+.

```bash
# 1. nv-codec-headers
git clone https://code.ffmpeg.org/FFmpeg/nv-codec-headers.git
cd nv-codec-headers
git checkout n13.0.19.1   # newer headers renamed a NVENC struct field and break ffmpeg 8.0.x
make install

# 2. libvmaf with CUDA (v3.0.0 does not compile against CUDA 13; use master)
git clone --depth 1 https://github.com/Netflix/vmaf.git
cd vmaf/libvmaf
export PATH=/usr/local/cuda/bin:$PATH
export NVCC_PREPEND_FLAGS="-arch=sm_120"
meson setup build --prefix=/opt/vmaf --buildtype=release --default-library=shared \
      -Denable_cuda=true -Denable_tests=false -Denable_docs=false -Denable_float=true
ninja -C build && ninja -C build install

# 3. ffmpeg
export PKG_CONFIG_PATH=/opt/vmaf/lib/x86_64-linux-gnu/pkgconfig
./configure --prefix=/opt/ffmpeg --disable-doc --enable-gpl --enable-nonfree \
    --enable-libvmaf --enable-cuda-nvcc --enable-ffnvcodec \
    --nvccflags="-gencode arch=compute_120,code=sm_120" \
    --extra-cflags=-I/usr/local/cuda/include \
    --extra-ldflags="-L/opt/vmaf/lib/x86_64-linux-gnu -Wl,-rpath,/opt/vmaf/lib/x86_64-linux-gnu -Wl,-rpath,/usr/local/cuda/lib64"
make -j"$(nproc)" && make install
```

`libvmaf_cuda` is enabled automatically when libvmaf exposes `libvmaf_cuda.h` and `ffnvcodec` is
available; there is no separate `--enable-libvmaf-cuda` switch. `--nvccflags` is required because
FFmpeg's default `compute_60` target was dropped by CUDA 13. If you would rather not build the NVENC
path, `--disable-nvenc` avoids the header-version trap.

Verified on Ubuntu 26.04, CUDA 13.1, RTX 5060 Ti: the two backends agreed to within 0.01 on the same
8-bit clip (mean 91.90 on both). A 1080p 900-frame clip scores in about 3 s on the GPU (`libvmaf_cuda`)
and plays back at the 30 fps target with clock-based frame dropping.

## Notes and limits

- **CPU and GPU scores may differ slightly.** They are different implementations of the same metric;
  on the 8-bit test clip here they agreed to within 0.01. Do not mix backends within one comparison;
  each report records which backend produced it.
- **Bitrate is the video stream's**, summed from packet sizes by `PacketIndex`, so it excludes audio
  and container overhead. The estimate from file size is only a fallback if the packet walk fails.
- **A lossy reference is flagged.** `ref.mkv` here is H.264, so VMAF measures distance from an
  already-degraded source; the header shows a `lossy` badge. Use a lossless or very high-bitrate
  reference for real measurements.
- **Colour metadata is displayed, not applied.** The header shows the declared matrix and range and
  warns when the two files disagree, but conversion to RGB still uses swscale's default matrix. This
  is the one correctness item from the review that is not done yet.
- A missing `ffmpeg` build is resolved automatically as described above, but a build without
  `libvmaf` still cannot score — `probe` prints what was found and where to point it.
- The CUDA path converts both inputs to `yuv420p` on the GPU (`scale_cuda`). For 10-bit sources that
  loses bit depth, which the CPU path keeps.
- Both videos must have the same resolution and be frame-aligned. Compare by frame index, not
  timestamp. The filter is run with `ts_sync_mode=nearest` and `shortest=1` for exactly this reason:
  the default "lower-or-equal timestamp" pairing mispairs frames when the two files use different
  time bases (`0.067` vs `0.066667` s) and pads a bogus duplicate frame at EOF.
- The default model assumes 1080p viewing conditions; use `vmaf_4k_v0.6.1` for 4K and
  `vmaf_v0.6.1neg` when sharpening/contrast gaming matters.
- Exact seeks are correct for constant-frame-rate input. Variable-frame-rate files fall back to
  timestamp-derived indices, which are approximate.
