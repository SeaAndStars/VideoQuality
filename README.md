# VideoQuality

A desktop tool for comparing encoded video against a reference: per-frame VMAF, a synced frame
viewer, and multi-encode comparison. GPU-first — every score runs through `libvmaf_cuda` when the
ffmpeg build and machine support it, including 10-bit sources.

![The app after scoring five encodes on the CUDA backend](docs/screenshot.png)

## What it does

- **Scores with libvmaf** via ffmpeg, preferring the CUDA filter, and parses the JSON log into
  per-frame scores plus pooled stats (mean, harmonic mean, p1, min, max, σ). **VMAF NEG is the
  default** — it does not reward sharpening or contrast changes the way plain VMAF does. Standard
  VMAF and the 4K model remain one click (or `--model`) away, and mixing models in one comparison is
  flagged.
- **Drag and drop with explicit roles**: drop on **Reference** or **Compare**, or anywhere for the
  automatic rule; folders, paste and Open With go through the same router. Rows appear at once and
  move through probe → index → score as jobs, two CUDA scores at a time.
- **Compares N encodes against one reference**: overlaid curves, a summary table with mean, p1,
  true video bitrate and file size, and a selectable row that drives the viewer.
- **Shows both videos frame by frame**: side by side, wipe with a draggable divider, A/B flicker, or
  an amplified difference heatmap — with synced zoom/pan across every pane.
- **Decodes through FFmpeg.AutoGen**, not external frame extraction, with exact seeks, an LRU
  cache, and a keyframe walk that skips frames nobody sees, so stepping and scrubbing stay quick
  (see [benchmarks](docs/development.md#benchmarks)).
- **Reports what it is actually doing**: backend and precision (`CUDA·8b` for converted 10-bit),
  ffmpeg path, bit depth, colour metadata, true bitrate, a progress bar with Cancel, and a session
  log in a `logs/` directory beside the binary.
- **Tested**: 50 unit/ABI tests (progress parser, stats, packet index, exports, difference imaging,
  job state machine and lanes, drop routing, model defaults, libavcodec ABI) plus 4 integration tests that run when
  `VQ_TEST_VIDEO` is set (54 total) and skip otherwise.

## Quick start

```bash
dotnet build VideoQuality.slnx

# reference first, then any number of encodes
dotnet run --project VideoQuality.App -- ref.mkv encode-a.mp4 encode-b.mp4

# or open the app and drop files on it (or use Reference / Encodes)
dotnet run --project VideoQuality.App
```

Headless:

```bash
dotnet run --project VideoQuality.Cli -- probe
dotnet run --project VideoQuality.Cli -- score ref.mkv dist.mp4 --csv out.csv --json out.json
dotnet run --project VideoQuality.Cli -- frame dist.mp4 42 --ppm frame.ppm
dotnet run --project VideoQuality.Cli -- diag
```

The app finds a VMAF-enabled ffmpeg automatically (`$VQ_FFMPEG`, `$FFMPEG_ROOT/bin/ffmpeg`,
`/opt/ffmpeg/bin/ffmpeg`, or `ffmpeg` on PATH when it has libvmaf). The resolved path is shown in
**Settings**. If your distro build has no VMAF, see the build guide.

## Documentation

| Guide | Contents |
|---|---|
| [Usage](docs/usage.md) | UI tour, compare modes, keyboard and mouse, CLI reference |
| [FFmpeg + CUDA setup](docs/ffmpeg-cuda.md) | Building ffmpeg with `libvmaf_cuda`, GPU policy, 10-bit behaviour, troubleshooting |
| [Architecture](docs/architecture.md) | Projects, data flow, threading, decoding and indexing |
| [Metrics](docs/metrics.md) | What VMAF measures, frame pairing, bitrate, interpreting stats |
| [Development](docs/development.md) | Prerequisites, build, tests, verification, Git workflow |

## Requirements

- .NET 10 SDK.
- FFmpeg **9.x** (`libavcodec.so.63`) built with `--enable-libvmaf`; add
  `--enable-cuda-nvcc --enable-ffnvcodec` for GPU scoring and `--enable-libdav1d` for AV1 on the CPU
  path. (There is no `--enable-libvmaf-cuda` switch — see [ffmpeg-cuda.md](docs/ffmpeg-cuda.md).)
- `FFmpeg.AutoGen` 8.0.0.1, which matches the installed `libavcodec.so.62`. The bindings and the
  native `libav*` major version must agree.
- For GPU scoring: an NVIDIA GPU, a driver new enough for CUDA 12.8+ on Blackwell (`sm_120`), and an
  ffmpeg built against a CUDA-enabled libvmaf.

## Status

Working and verified on Ubuntu 26.04, CUDA 13.1, RTX 5060 Ti: 8-bit and 10-bit scoring, H.264/HEVC/AV1,
1080p playback (900 frames at 30 fps), wipe/flicker/difference, multi-encode overlays, the encode
table, and 54 tests (50 unit/ABI + 4 integration; integration skips without `VQ_TEST_VIDEO`).

Not implemented yet: rate-distortion plots/BD-rate, worst-frames navigation, hover tooltips, score
caching and PNG export, resolution-mismatch scaling, frame-offset control, colour-matrix-aware RGB
conversion (the matrix is displayed and mismatches warned, but swscale's default is still used), the
in-app log panel (file logging and crash handlers exist), and single-instance forwarding of Open With
files to a running window.
