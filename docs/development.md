# Development

## Prerequisites

- .NET 10 SDK (`dotnet --list-sdks`).
- FFmpeg 8.x with libvmaf; CUDA-enabled build for GPU scoring — see [ffmpeg-cuda.md](ffmpeg-cuda.md).
- `FFmpeg.AutoGen` 9.0.1.1 is pinned in `VideoQuality.Core.csproj` and matches `libavcodec.so.63`.
  If you upgrade ffmpeg across a major version, upgrade the bindings and
  `FfmpegRuntime.ExpectedAvcodecMajor` in lockstep; the runtime refuses a mismatch.

## Build and run

```bash
dotnet build VideoQuality.slnx
dotnet build VideoQuality.slnx -c Release

dotnet run --project VideoQuality.App
dotnet run --project VideoQuality.App -- ref.mkv encode-a.mp4 encode-b.mp4

dotnet run --project VideoQuality.Cli -- probe
```

The UI needs a display. Headless machines can run it under Xvfb for smoke tests:

```bash
Xvfb :99 -screen 0 1600x1000x24 &
DISPLAY=:99 dotnet run --project VideoQuality.App -- ref.mkv dist.mp4
DISPLAY=:99 import -window root /tmp/shot.png   # ImageMagick
```

## Test media

```bash
# 720p reference, lossless-ish, plus two encodes at different CRFs
ffmpeg -f lavfi -i testsrc2=size=1280x720:rate=30:duration=10 \
       -pix_fmt yuv420p -c:v libx264 -crf 0 -preset ultrafast ref.mkv
ffmpeg -i ref.mkv -c:v libx264 -crf 30 -preset medium -pix_fmt yuv420p dist.mp4
ffmpeg -i ref.mkv -c:v libx264 -crf 24 -preset medium -pix_fmt yuv420p dist2.mp4

# 10-bit HEVC
ffmpeg -i ref.mkv -c:v libx265 -crf 28 -preset faster -pix_fmt yuv420p10le dist10.mp4
```

## Scripts

```bash
./scripts/verify.sh            # clean build, project check, tests, ffmpeg probe

VQ_MEDIA_DIR=/tmp/vqtest \
VQ_PROGRESS_REF=/tmp/vqtest/ref1080.mkv VQ_PROGRESS_ENCODE=/tmp/vqtest/dist1080.mp4 \
    ./scripts/capture-docs.sh  # regenerate docs/screenshot*.png
```

`verify.sh` fails if a project is missing from the solution, which is how the missing
`VideoQuality.App` entry was caught. CI can run it unchanged.

## Tests

```bash
dotnet test VideoQuality.Tests/VideoQuality.Tests.csproj
```

Unit tests cover the pure parts: the `-progress` parser, pooled statistics, packet-index bitrate and
keyframe math, CSV/JSON export, and the difference image. Integration tests are marked
`[SkippableFact]` and skip unless a real clip and a VMAF-enabled ffmpeg are available:

```bash
VQ_TEST_VIDEO=/path/to/ref.mkv dotnet test VideoQuality.Tests/VideoQuality.Tests.csproj
```

With `VQ_TEST_VIDEO` set they verify that the same file scores ≈100, that CPU and CUDA agree on 8-bit
input, that a decoded frame matches ffmpeg's own BGRA extraction, and that frames reached by stepping
backwards are identical to a fresh decode. Set `VQ_FFMPEG` if the ffmpeg to use is not the
auto-resolved one.

## Benchmarks

`VideoQuality.Benchmarks` is a BenchmarkDotNet project for the hot paths in `VideoQuality.Core`:

```bash
dotnet run -c Release --project VideoQuality.Benchmarks -- --filter '*'
dotnet run -c Release --project VideoQuality.Benchmarks -- --filter '*Decoder*'
```

- `PixelDiffBenchmarks`: difference image and auto-gain percentile on a synthetic 1080p pair.
- `DecoderBenchmarks`: the viewer's access patterns on real media — sequential playback, the
  reference/encode pair serial versus concurrent, ten left-arrow steps, ten random slider jumps.
  Decoders are reopened per iteration, so each measurement starts cold. Media comes from
  `VQ_BENCH_REF` / `VQ_BENCH_DIST` (default `/tmp/vqtest/ref1080.mkv` and `dist1080.mp4`, 1080p
  H.264, 250-frame GOP).

Results go to `BenchmarkDotNet.Artifacts/` (ignored). Reference numbers, Ryzen 7 9700X, .NET 10:

| Benchmark | Before | After | |
|---|---:|---:|---|
| Difference image, 1080p | 45.1 ms | 5.0 ms | 9× — fixed point, colour LUT, parallel rows |
| Auto-gain percentile, 1080p | 31.2 ms | 4.6 ms | 6.8× |
| 10 backward steps | 931 ms | 80 ms | 11.6× — backward window + non-ref skip |
| 10 random jumps | 352 ms | 270 ms | 1.3× — non-ref skip |
| 60 frames, reference + encode | 202 ms | 90 ms | 2.2× — concurrent decoders, no zeroing |

Scoring is ffmpeg's time and is NVDEC-bound on CUDA (900 1080p frames: 2.6 s scoring versus a 2.0 s
decode-only floor). Two CUDA scores running at once finish in 3.5 s versus 5.4 s back to back, which
is why the job model allows two concurrent CUDA scores.

## Logging and diagnostics

Every build writes a session log to `logs/vq-YYYYMMDD.log` beside the executable
(`VideoQuality.App/bin/<config>/net10.0/logs/`), which keeps it easy to find. `VQ_LOG_DIR` overrides
the location; if the directory is read-only the logger falls back to
`$XDG_STATE_HOME/videoquality/logs`. In-process libav warnings and decode errors are bridged into the
same file.

- startup session header: core version, .NET, OS, ffmpeg path and capabilities, libav path;
- every score: backend, model, source bit depth, exact command line, result and duration;
- CUDA failures and CPU fallbacks, with the exception;
- unhandled exceptions and unobserved task exceptions from the app.

`vq diag` prints the same header, and `vq probe` prints the log path. The app shows it in
**Settings**.

## Verification checklist

1. `probe` resolves the custom ffmpeg and reports `libvmaf`, `libvmaf_cuda`, GPU.
2. `score` prints the expected backend and a plausible mean; run once with `--cpu` and once with
   `--gpu` and compare.
3. Decoder correctness: `frame N --ppm` output should match ffmpeg's own extraction exactly.

   ```bash
   dotnet run --project VideoQuality.Cli -- frame dist.mp4 42 --ppm f.ppm
   ffmpeg -i dist.mp4 -vf "select=eq(n\,42)" -vframes 1 e.png
   convert f.ppm f.png && compare -metric RMSE f.png e.png null:   # expect 0
   ```

4. UI smoke test: launch with two files, check the graph draws, step with arrows, click the graph,
   switch modes, toggle Difference and Auto gain, play briefly.
5. Keyboard `Space` playback should advance roughly `fps × elapsed` frames, independent of decode
   speed.

## Repository layout

```
VideoQuality.slnx          solution
VideoQuality.Core/         scoring, decoding, models, jobs (no UI)
VideoQuality.App/          Avalonia UI
VideoQuality.Cli/          headless commands
VideoQuality.Tests/        xUnit unit tests + skippable integration tests
VideoQuality.Benchmarks/   BenchmarkDotNet benchmarks for decoding and difference imaging
packaging/linux/           .desktop entry (Open With) and launcher script
scripts/build-ffmpeg-windows.sh   cross-compiles FFmpeg + libvmaf CUDA for Windows (LGPL, redistributable)
scripts/package-windows.sh        self-contained Windows release zip with ffmpeg bundled
artifacts/                 release output (ignored)
scripts/verify.sh          clean build, solution-membership check, tests, probe
scripts/capture-docs.sh    regenerates the documentation screenshots
docs/                      user and developer documentation
  usage.md                 UI and CLI
  ffmpeg-cuda.md           build recipe, GPU policy, troubleshooting
  architecture.md          projects, data flow, threading
  metrics.md               VMAF, frame pairing, bit depth, stats
  development.md           this file
  screenshot*.png          documentation images
```

## Git workflow

The repository is the source of truth; every change lands as a commit on `main`.

- `bin/`, `obj/`, IDE folders and logs are ignored (see `.gitignore`). Documentation images **are**
  tracked — they are referenced by the docs.
- Line endings are normalised to LF for text, with binary attributes for images (`.gitattributes`).
- Commit messages: a short imperative summary, then a body explaining the why and any behaviour
  change. Group related edits (for example a feature plus its docs) in one commit.
- Test media is **not** committed. Generate it with the commands above and keep it outside the repo.

Typical loop:

```bash
git status
dotnet build VideoQuality.slnx -c Release
dotnet run --project VideoQuality.Cli -- probe
git add -A
git commit -m "Short imperative summary

Why this change, what behaviour it changes, and how it was verified."
```

## Roadmap

Short term, in rough priority:

1. **Structured logging.** Scoring and indexing run through `JobService` with per-row state, a
   status-bar summary, failure toasts and per-row re-score. The file log, session header, libav bridge
   and crash handlers are in place; the in-app log panel and diagnostic bundle remain. Single-instance
   forwarding (a second launch sends its files to the running window) is also still open.
2. Colour-matrix-aware YUV→RGB conversion (currently metadata-only).
3. Rate-distortion plot and BD-rate, with ΔVMAF/Δbitrate against a chosen baseline.
4. Worst-frames navigation and a graph hover tooltip.
5. PSNR/SSIM/CAMBI from the same libvmaf pass.
6. Score caching keyed by file hash + model + backend, and PNG export.
7. Resolution-mismatch scaling and frame-offset control.
