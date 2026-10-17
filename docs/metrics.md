# Metrics

## What VMAF measures

VMAF is a full-reference perceptual metric. It decodes both videos to raw frames, extracts
hand-crafted features — VIF (detail preservation at several scales), ADM (detail loss and additive
impairment), motion — and feeds them to a small SVM regressor trained on subjective scores. It is not
a neural network and does not generate or enhance anything.

Because it never looks at the bitstream, the codec is irrelevant as long as both files decode to
aligned frames at the same resolution. H.264, HEVC and AV1 are exercised in the test set; AV1 needs
dav1d for the CPU path (see [ffmpeg-cuda.md](ffmpeg-cuda.md)).

## Default model: VMAF NEG

The default is **`vmaf_v0.6.1neg`** (NEG). Plain VMAF rewards sharpening and contrast changes —
enhancements that make an encode look punchier without preserving more detail — which is exactly the
wrong signal when comparing encoders. NEG removes that enhancement gain from the regression.

| Model | When to use |
|---|---|
| `vmaf_v0.6.1neg` (**default**) | Encoder comparisons; ignores sharpening/contrast gain. |
| `vmaf_v0.6.1` | Comparing with published plain-VMAF numbers. |
| `vmaf_4k_v0.6.1` | 4K viewing conditions (plain, not NEG). |

The model is always passed explicitly to libvmaf (`model=version=…`), never left to the filter
default, and it is recorded on every report. The table shows a `MODEL` column, the graph title reads
`VMAF NEG per frame`, and scores are labelled `VMAF NEG` so they are not confused with published
plain-VMAF figures. Mixing models in one comparison is flagged (`MIXED MODELS`), the same rule as
mixing backends.

## NEG verification

Sharpening is the classic inflation case. A sharpened encode (`unsharp=5:5:1.5`, CRF 18) scores:

| Model | CPU | CUDA |
|---|---|---|
| plain VMAF | **100.000** | **100.000** |
| NEG | **86.223** | **86.223** |

Plain VMAF is saturated at 100; NEG rejects the enhancement. Identical CPU/CUDA NEG values also prove
the CUDA path applies the NEG options rather than silently scoring plain VMAF.

CPU↔CUDA agreement on NEG, 8-bit content: 89.863 vs 89.861 (0.002). AV1 NEG: 91.207 CPU (dav1d) vs
91.203 CUDA (`av1_cuvid`). Repeated runs vary by about 0.01.

## Baseline numbers

Current test set, NEG, CUDA, libvmaf 3.2.1 / FFmpeg 9.0.2:

| Encode | Codec | Mean | p1 | Video bitrate |
|---|---|---|---|---|
| `dist.mp4` | H.264 CRF 30 | 89.852 | 86.539 | 1151 kb/s |
| `dist2.mp4` | H.264 CRF 24 | 95.415 | 93.520 | 2790 kb/s |
| `dist3.mp4` | HEVC CRF 30 | 90.975 | 87.315 | 1543 kb/s |
| `dist10.mp4` | HEVC 10-bit CRF 28 | 92.755 | 90.058 | 2063 kb/s |
| `dist_av1.mp4` | AV1 CRF 40 | 91.203 | 80.562 | 2095 kb/s |

These are NEG scores and are not comparable with the plain-VMAF numbers published elsewhere.

## Frame pairing

libvmaf's default framesync pairs the reference frame with the nearest **lower or equal** timestamp.
When the two files use different time bases, timestamps round differently — for example the
distorted file at `0.067 s` and the reference at `0.06667 s` — and the default pairs the wrong frame
for many frames. On the test pair this produced a mean of **69.6** instead of **91.9** (plain VMAF),
plus a bogus 301st frame from end-of-stream padding.

This project always runs:

```
ts_sync_mode=nearest:shortest=1
```

`nearest` gives frame-index pairing, which is what the UI does; `shortest` stops framesync padding a
duplicate frame at EOF. Sanity check: feeding the same file twice yields 300 frames at ≈100.

## Bitrate

The table reports the **video stream** bitrate: the sum of video packet sizes divided by the video
stream duration, gathered by `PacketIndex` without decoding. File size ÷ duration — the obvious
alternative — includes audio and container overhead and is only used if the packet walk fails.

That matters for codec comparisons: with file-size bitrate the HEVC point looked like 1549 kb/s;
the video-only value is 1543 kb/s. Small here, larger with a lossless audio track.

## A lossy reference

`ref.mkv` in the test set is itself H.264. VMAF then measures distance from an already-degraded
source, which compresses the meaningful range. The app shows a `lossy` badge on the reference header
when the codec is a delivery codec. Real measurements should use a lossless (FFV1/rawvideo) or very
high-bitrate reference.

## Bit depth and the CUDA path

`libvmaf_cuda` in libvmaf 3.2.1 still accepts `yuv420p` only: `p010le` and `yuv420p10le` both fail
with `Invalid argument` (re-verified against FFmpeg 9.0.2). A 10-bit input on the GPU path is
therefore converted to 8-bit by `scale_cuda` before scoring.

Measured on the 10-bit HEVC encode with NEG:

| Backend | Mean | p1 |
|---|---|---|
| CUDA (8-bit conversion on GPU) | 92.755 | 90.058 |
| CPU (true 10-bit) | 92.883 | 90.229 |

The gap is 0.13 here. Treat it as a lower bound: the test encode was derived from an 8-bit reference,
so there was little true 10-bit detail to lose. Choose CPU when 10-bit precision is the point; use
CUDA for throughput. Never mix backends inside one comparison — each report records which one produced
it, and the table shows `CUDA·8b` for converted runs.

## HDR

The standard VMAF models (`vmaf_v0.6.1`, `vmaf_4k_v0.6.1`) and NEG are trained on SDR content. Scores
on PQ or HLG material are not meaningful on either backend. libvmaf 3.2.1 ships newer VMAF v1 models
(including HFR/4K variants) whose scale is not comparable with v0; this tool does not wire them in yet,
and would flag them as a different model if it did. Use a metric designed for HDR rather than reading
these numbers as quality.

## Reading the pooled stats

| Stat | Meaning | Watch for |
|---|---|---|
| Mean | Average score | Hides short bad scenes |
| Harmonic mean | Mean of reciprocals, punishes low outliers | Closer to perceived quality than the mean |
| p1 | 1st percentile | The "worst 1%" — use it to catch dips |
| Min / max | Extremes | A single frame can dominate |
| σ | Standard deviation | High σ means inconsistent quality |

A clip with mean 92 but p1 70 is not the same as one with mean 92 and p1 88.

## Test content

VMAF was trained on natural video. Synthetic patterns (`testsrc2`, colour bars) score unusually high
and compress unusually well, so they are useful for exercising the pipeline, not for judging codecs.
Keep at least one real, camera-captured clip in any test set.
