# Architecture

## Projects

```
VideoQuality.Core      no UI; safe to use from tests or other tools
  Scoring/
    VmafRunner.cs      ffmpeg process, CUDA/CPU filter graphs, JSON parsing, capability probing
    VmafCapabilities.cs
    VmafBackend.cs     Auto/Cpu/Cuda + Cuda→"CUDA" display name
  Decoding/
    FrameDecoder.cs    FFmpeg.AutoGen decode to BGRA, exact seeks, LRU cache
    PacketIndex.cs     demux-only packet walk: true bitrate, frame count, keyframes
    FfmpegRuntime.cs   locates libav*.so for the bindings, av_strerror helper
  Imaging/PixelDiff.cs difference image + percentile helper
  Models/              VmafFrame, VmafStats, VmafReport, VideoInfo

VideoQuality.App       Avalonia 12 desktop UI
  ViewModels/          MainViewModel, EncodeEntry, ViewportState, CompareMode
  Controls/            FrameView (one image), WipeView (two images + divider)
  Views/               MainWindow.axaml(.cs): layout, plot, input handling
  Converters/          enum → friendly labels

VideoQuality.Cli       probe / score / frame commands, no UI
```

## Data flow

```
            ┌──────────────┐
 reference ─► FrameDecoder ├─► BGRA frames ─► WriteableBitmap ─► FrameView / WipeView
            └──────────────┘                                    ▲
                   ▲                                            │ zoom/pan (shared ViewportState)
 encode ───────────┘                                            │
                   │                                            │
            ┌──────┴────────┐                                   │
            │  PacketIndex  ├─► true bitrate, frame count ──► EncodeEntry ──► table
            └───────────────┘
            ┌───────────────┐
 both files─►  VmafRunner   ├─► JSON log ─► VmafReport ─► graph + stats
            └───────────────┘      ▲
                                   └── ffmpeg (libvmaf_cuda preferred)
```

## Threading

- The UI thread owns all Avalonia objects, including `WriteableBitmap`s.
- Decoding happens on the thread pool. One `SemaphoreSlim` serialises decode work because
  `FrameDecoder` is not thread safe.
- Each frame request increments `_loadVersion`; results from a stale request are discarded, so rapid
  slider movement cannot show an out-of-order frame.
- Scoring is async: the ffmpeg process writes progress to stdout, which is parsed on a background
  task; the UI updates through `IProgress<VmafProgress>`.
- Playback uses a `DispatcherTimer` plus a `Stopwatch`: the target frame is computed from elapsed
  time, so a slow decode drops frames rather than slowing playback.

## Decoding

`FrameDecoder` opens the file, picks the best video stream, and converts frames to BGRA through
swscale into a reused context.

Seeking is exact for constant-frame-rate files:

1. Seek to the keyframe at or before the target PTS (`avformat_seek_file` + `AVSEEK_FLAG_BACKWARD`).
2. Decode forward, deriving the frame index from `best_effort_timestamp × time_base × fps`.
3. Return the first frame at or after the target index.

An LRU cache of eight decoded frames makes ±a-few-frames stepping cheap; a forward jump of more than
240 frames re-seeks. Frames are cached under their **actual** decoded index so a timestamp that
overshoots cannot poison the cache.

`VideoInfo` also carries pixel format, bit depth, colour matrix/range, and whether the codec is lossy
(H.264/HEVC/AV1 versus FFV1/rawvideo/…).

## Packet index

`PacketIndex.Build(path)` demuxes without decoding and records `(pts, dts, size, keyframe)` for every
video packet. The app uses it for:

- **True video bitrate** — sum of video packet sizes ÷ video duration, excluding audio and container
  overhead. The file-size estimate is only a fallback.
- **Frame count cross-check** — packet count versus the decoded/container count.
- **Keyframe positions** — currently unused by the UI; the data is there for I-frame markers and a
  bitrate trace.

## Colour

The pane header shows the declared matrix and range, and a mismatch between reference and encode
raises a warning. `FrameDecoder` still converts YUV→RGB with swscale's default matrix, which is the
one known correctness gap: the warning tells you when it matters, but the conversion is not yet
matrix-aware.

## Where to extend

- A second metric (PSNR/SSIM/CAMBI) can be added to the same filter graph and parsed from the same
  libvmaf JSON.
- Keyframe markers and a bitrate trace have their data ready in `PacketIndex`.
- Worst-frames navigation only needs a sorted view over `VmafReport.Frames`.
- The job/logging model would wrap `ScoreEntryAsync`, `PacketIndex.Build` and future exports.
