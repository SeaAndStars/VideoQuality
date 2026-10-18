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

Two test projects cover all three assemblies, and the bar is **100% line and branch coverage** of
VideoQuality.Core, VideoQuality.App and VideoQuality.Cli. Nothing is excluded by hand; only
compiler- and source-generated code is left out.

| Project | What it runs |
|---|---|
| `VideoQuality.Tests` | Core and CLI: builders, parsers, fonts, decoding of generated clips, the encode queue, mkvtoolnix and VMAF against scripted tools, and the real app entry point started with `--headless --self-test` |
| `VideoQuality.App.Tests` | Every view model and view on Avalonia.Headless: real controls, mouse, keyboard, drag and drop, toasts, pickers |

The tests make their own media with the GPL encoder ffmpeg, so they need it and MKVToolNix:

```bash
./scripts/fetch-ffmpeg-gpl.sh linux-x64 ~/vq-encoder
export VQ_ENCODER_FFMPEG=~/vq-encoder/ffmpeg     # or put a GPL ffmpeg on PATH
sudo apt install mkvtoolnix                       # mkvmerge / mkvextract on PATH
dotnet test VideoQuality.Tests && dotnet test VideoQuality.App.Tests
```

External processes go through `IProcessRunner`: `ScriptedRunner` answers in memory and
`FakeTool` is a real executable with scripted output, so CUDA, missing GPUs, failed and cancelled
runs are all tested without the hardware. The App takes the same seams through `AppServices`
(processes, settings store, environment), `PageIo.Dialogs` (file pickers) and `ToastHost.Ask`
(questions).

Two tests are opt-in and skipped unless their variables are set:

| Test | Run with | What it does |
|---|---|---|
| `Screenshots` | `VQ_SCREENSHOTS=/dir [VQ_SCREENSHOT_LANG=zh-Hans]` | Renders every page at 1600×1000 (plus a filled-in Pipeline and a tall shot of every step) for design review |
| `EndToEnd` | `VQ_E2E_SOURCE=ep.mkv VQ_E2E_OUT=/dir [VQ_E2E_ENCODER=x265]` | Runs the real pipeline on an episode and prints each step's result ([pipeline guide](pipeline.md#running-it-without-the-window)) |

```bash
VQ_SCREENSHOTS=/tmp/shots dotnet test VideoQuality.App.Tests --filter Screenshots
```

### Coverage

```bash
dotnet tool restore
./scripts/coverage.sh                       # Linux run; fails below 100%
./scripts/coverage.sh windows.json          # merged with a Windows run's coverlet JSON
VQ_COVERAGE_MIN=99 ./scripts/coverage.sh    # a lower bar while writing tests
```

Some branches only run on one OS (Windows APIs, `.exe` names, `NUL`), so 100% needs a Windows run
too. `coverage.sh` chains coverlet's JSON with `MergeWith`, which keeps the hits of each branch
(Cobertura only records how many of a line's branches ran, so it cannot combine runs), and
`scripts/coverage-paths.py` maps another checkout's paths onto this one. The release workflow does
exactly this: the Windows job uploads its JSON, and the Linux job merges it and enforces 100%. The
HTML report is written to `artifacts/coverage/report/`.

To produce the Windows JSON locally, run the same `dotnet test VideoQuality.Tests` with
`/p:CollectCoverage=true /p:CoverletOutputFormat=json` on Windows (see the `test-windows` job), from
the same commit: the line numbers must match.

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
VideoQuality.Core/         scoring, decoding, encoding, fonts, MKV, chapters, release rules, jobs (no UI)
VideoQuality.App/          Avalonia UI
VideoQuality.Cli/          headless commands
VideoQuality.Tests/        Core and CLI tests, plus the app started headless (--self-test)
VideoQuality.App.Tests/    view models and views on Avalonia.Headless
VideoQuality.Tests.FakeTool/ a scriptable stand-in for ffmpeg, mkvmerge, nvidia-smi
VideoQuality.Benchmarks/   BenchmarkDotNet benchmarks for decoding and difference imaging
packaging/linux/           .desktop entry (Open With) and launcher script
scripts/build-ffmpeg-windows.sh   cross-compiles FFmpeg + libvmaf CUDA for Windows (LGPL, redistributable)
scripts/build-ffmpeg-linux.sh     the same for Linux: relocatable ($ORIGIN), minimal deps, self-test
scripts/build-ffmpeg-linux-docker.sh  runs the Linux build in Ubuntu 22.04 (glibc 2.35 baseline)
scripts/package.sh <rid>          release archive for win-x64 (.zip) or linux-x64 (.tar.gz)
.github/workflows/release.yml     tag-triggered CI/CD (verify signature → test → FFmpeg → publish)
artifacts/                 release output (ignored)
scripts/verify.sh          clean build, solution-membership check, tests, probe
scripts/capture-docs.sh    regenerates the Compare screenshots (Xvfb, English, full frame); the toolkit
                           page screenshots come from the Screenshots test (VQ_SCREENSHOTS)
scripts/coverage.sh        both test projects with coverage, merged, 100% required
scripts/make-test-cff-font.py builds the CC0 CFF test font in VideoQuality.Tests/Fixtures
presets/                   shareable encode presets (the default online preset folder)
docs/                      user and developer documentation
  usage.md                 UI, toolkit pages and CLI
  pipeline.md              the release pipeline: inference, steps, settings, NVENC UHQ
  ffmpeg-cuda.md           build recipe, GPU policy, troubleshooting
  architecture.md          projects, data flow, threading
  metrics.md               VMAF, frame pairing, bit depth, stats
  development.md           this file
  screenshot*.png          documentation images
```

## Git workflow

Two long-lived branches; releases come only from signed tags on `main`.

| Branch / ref | Purpose |
|---|---|
| `dev` | Day-to-day work. Every commit lands here first. |
| `main` | Released history. Moves only by fast-forwarding to a tested `dev`. |
| `v*` tags | A GPG-signed tag on `main` is the **only** trigger for CI/CD. |

```bash
git switch dev
# … work, commit (signed automatically) …
git push origin dev

# release
git switch main && git merge --ff-only dev
git tag -s v0.2.0 -m "VideoQuality 0.2.0"
git push origin main v0.2.0          # → .github/workflows/release.yml
```

**Signing.** Commits and tags are signed with the release key (fingerprint
`41A9 0A5D A8C9 222F 2AE9 DA57 62D6 9FC1 BCCB B34A`), published on the owner's GitHub account
(`https://github.com/SeaAndStars.gpg`), so GitHub marks the commits Verified. The key lives on a
smartcard read by GnuPG on Windows; in WSL the repository is configured with
`gpg.program = /usr/local/bin/gpg-win`, a wrapper that calls `gpg.exe` and translates temp-file
paths so `git verify-commit` / `git log --show-signature` work too. The card's signature PIN is not
forced, so the PIN is asked once per session.

**Where trust lives.** A tag runs the workflow *from the tagged commit*, so nothing inside the
repository can be the trust anchor: whoever pushes a commit can rewrite a key file, a pinned
fingerprint, or the workflow itself. The anchors are therefore outside it:

| Anchor | Where | Who can change it |
|---|---|---|
| Signer's public key | owner's GitHub account (`github.com/<owner>.gpg`) | the account owner |
| Expected fingerprint | repository variable `RELEASE_KEY_FINGERPRINT` | repository admins |
| `main` integrity | ruleset: verified signed commits, no force push, no deletion (no bypass) | repository admins |
| Who may move `main` / create `v*` tags | rulesets with admin-only bypass | repository admins |
| Publishing | `release` environment: owner approval required, `v*` tags only | repository admins |
| Token | workflow default permissions: read-only | repository admins |

Even a malicious workflow change that skipped the checks could not publish: the write token only
exists inside the `release` environment, after the owner approves the run.

**Release pipeline** (`.github/workflows/release.yml`), on a pushed `v*` tag:

1. **verify**: the tag must be annotated, signed by a key from the owner's account whose fingerprint
   equals `RELEASE_KEY_FINGERPRINT`, and point at a commit on `main`; otherwise nothing builds.
2. **test**: `dotnet build` and the unit tests.
3. **ffmpeg-windows** / **ffmpeg-linux**: the redistributable FFmpeg + libvmaf CUDA builds,
   cached by a hash of their build scripts (a release without FFmpeg changes reuses them).
4. **release** (environment `release`, waits for approval): `scripts/package.sh` for both
   platforms, `SHA256SUMS.txt`, and the GitHub release.

Third-party actions are pinned to full commit SHAs.

Other conventions:

- `bin/`, `obj/`, `artifacts/`, IDE folders and logs are ignored. Documentation images **are**
  tracked.
- Line endings are normalised to LF for text, with binary attributes for images (`.gitattributes`).
- Commit messages: a short imperative summary, then a body explaining the why and any behaviour
  change. Group related edits (for example a feature plus its docs) in one commit.
- Test media is **not** committed. Generate it with the commands above and keep it outside the repo.

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
