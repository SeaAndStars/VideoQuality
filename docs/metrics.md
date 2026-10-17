# Metrics

## What VMAF measures

VMAF is a full-reference perceptual metric. It decodes both videos to raw frames, extracts
hand-crafted features — VIF (detail preservation at several scales), ADM (detail loss and additive
impairment), motion — and feeds them to a small SVM regressor trained on subjective scores. It is not
a neural network and does not generate or enhance anything.

Because it never looks at the bitstream, the codec is irrelevant (H.264, HEVC, AV1, VP9 all work) as
long as both files decode to aligned frames at the same resolution.

## Models

| Model | When to use |
|---|---|
| `vmaf_v0.6.1` | Default. Assumes 1080p viewing conditions. |
| `vmaf_4k_v0.6.1` | 4K viewing conditions. |
| `vmaf_v0.6.1neg` | NEG model: penalises sharpening/contrast changes that inflate plain VMAF. |

The model is selectable in the toolbar and via `--model` in the CLI.

## Frame pairing

libvmaf's default framesync pairs the reference frame with the nearest **lower or equal** timestamp.
When the two files use different time bases, timestamps round differently — for example the
distorted file at `0.067 s` and the reference at `0.06667 s` — and the default pairs the wrong frame
for many frames. On the test pair this produced a mean of **69.6** instead of **91.9**, plus a bogus
301st frame from end-of-stream padding.

This project always runs:

```
ts_sync_mode=nearest:shortest=1
```

`nearest` gives frame-index pairing, which is what the UI does; `shortest` stops framesync padding a
duplicate frame at EOF. Sanity check: feeding the same file twice yields 300 frames at mean 99.95.

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

`libvmaf_cuda` accepts `yuv420p` only, so a 10-bit input on the GPU path is converted to 8-bit by
`scale_cuda` before scoring. Measured on a 10-bit HEVC encode:

| Backend | Mean | p1 |
|---|---|---|
| CUDA (8-bit conversion on GPU) | 93.848 | 91.050 |
| CPU (true 10-bit) | 93.888 | 91.080 |

The difference is small but real. Use CPU when 10-bit precision is the point; use CUDA for
throughput. Never mix backends inside one comparison — each report records which one produced it.

## Reading the pooled stats

| Stat | Meaning | Watch for |
|---|---|---|
| Mean | Average VMAF | Hides short bad scenes |
| Harmonic mean | Mean of reciprocals, punishes low outliers | Closer to perceived quality than the mean |
| p1 | 1st percentile | The "worst 1%" — use it to catch dips |
| Min / max | Extremes | A single frame can dominate |
| σ | Standard deviation | High σ means inconsistent quality |

A clip with mean 92 but p1 70 is not the same as one with mean 92 and p1 88.

## Test content

VMAF was trained on natural video. Synthetic patterns (`testsrc2`, colour bars) score unusually high
and compress unusually well, so they are useful for exercising the pipeline, not for judging codecs.
Keep at least one real, camera-captured clip in any test set.

## CPU versus GPU

The two backends are different implementations. On 8-bit content they agreed to within 0.01 in
testing (91.90 both); expect small differences elsewhere. GPU results are also affected by the 8-bit
conversion described above when the source is 10-bit.
