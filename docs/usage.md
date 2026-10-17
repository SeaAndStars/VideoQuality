# Usage

## Opening files

Three ways to get media in:

1. **Reference…** then **Encodes…** — the second picker accepts multiple files and scores each one.
2. Drag files onto the window: the first file becomes the reference if none is set, the rest are
   added as encodes.
3. Startup arguments: `VideoQuality.App <reference> <encode> [<encode> …]`.

Adding an encode opens it, builds a packet index for its true bitrate, and scores it against the
reference. Re-scoring an existing row uses **Re-score**.

## The window

```
┌ Reference… Encodes… Re-score Play │ View ▾ [mode controls] │ Fit 1:1 zoom 0–100 ┐
├──────────────── viewer: two panes or one (wipe / flicker) ─────────────────────┤
├────────────────────── VMAF graph ──────────────┬────── encode table ───────────┤
├───────────────── frame / VMAF readout + slider ────────────────────────────────┤
├───────────────────────── pooled stats ────────────────────────────────────────┤
└──────────── status · backend · progress/Cancel · Settings ────────────────────┘
```

- **Pane headers** carry the file name, resolution, codec, frame rate, duration, a bit-depth badge
  (`10-bit`) and a `lossy` badge when the reference itself is a delivery codec.
- **Encode table**: colour swatch (matches the curve), name, mean, p1, true video bitrate, file size,
  and ✕ to remove. Selecting a row swaps the viewer to that encode and thickens its curve.
- **Graph**: per-frame VMAF for every scored encode. Autoscales by default; `0–100` pins the axis for
  cross-clip comparison. The dashed line is the selected encode's mean, the red line is VMAF 85.
- **Status bar**: current operation, progress bar and Cancel while scoring, capability summary, and
  **Settings** (ffmpeg path and what was detected).

## Compare modes

| Mode | What you see |
|---|---|
| **Side by side** | Reference left, selected encode right, same viewport. |
| **Wipe** | One image, reference left of the divider and encode right. Drag anywhere near the divider; the handle has `‹ ›` chevrons and each side is labelled. |
| **Flicker** | One image toggling between reference and encode. Hold `F` to reveal the encode, or enable **Auto flicker** (~4 Hz). |
| **Difference** | Reference next to a false-colour difference map with a gain slider, an **Auto** gain button (99.9th percentile), a luma/chroma/both switch, and a legend showing the current scale (`ΔY/C 0–64 · gain 4×`). |

Difference gain multiplies the raw difference before the colormap, so `gain 4×` shows differences up
to 64 as full brightness. **Auto** picks a gain that puts the 99.9th percentile near the top of the
ramp.

## Zoom and pan

All panes share one viewport (`ViewportState`), so both sides always show the same pixels.

- Mouse wheel zooms about the cursor (0.02×–16×).
- Drag to pan.
- **Fit** returns to fit-to-pane; **1:1** shows screen pixels per image pixel.
- The zoom readout shows the effective scale and `(fit)` when in fit mode.

The viewer height is derived from the image aspect ratio, so fit mode has no letterbox dead space.
At 1:1 the image is clipped to its pane rather than painted over the headers.

## Keyboard

| Key | Action |
|---|---|
| `←` / `→` | Step one frame |
| `Shift` + `←` / `→` | Step ten frames |
| `Home` / `End` | First / last frame |
| `Space` | Play or pause (native frame rate) |
| `F` (hold) | Reveal the encode in Flicker mode |

Playback is clock-driven: each tick computes the target frame from elapsed wall time, so a slow
decode drops frames instead of playing in slow motion.

## Scoring behaviour

- Backend **Auto** picks CUDA whenever `libvmaf_cuda` is present and an NVIDIA GPU is visible;
  otherwise CPU. Both can be forced in the toolbar.
- Progress comes from `ffmpeg -progress pipe:1` and shows frame count and encoding speed. Cancel
  kills the ffmpeg process tree and discards the partial log.
- A 10-bit source scored on CUDA is converted to 8-bit by `scale_cuda` because `libvmaf_cuda`
  accepts `yuv420p` only; the status line says so. Select **CPU** when full 10-bit precision matters.
- Scores are paired by frame index (`ts_sync_mode=nearest`, `shortest=1`), not by wall-clock
  timestamp.

## CLI

```
vq probe  [--ffmpeg PATH]
    Resolve and describe the ffmpeg build: version, libvmaf, libvmaf_cuda, NVIDIA GPU.

vq score <reference> <distorted> [--gpu|--cpu] [--model NAME] [--ffmpeg PATH] [--json out.csv]
    Run VMAF. Default backend is Auto (CUDA when available).

vq frame <video> [index] [--ppm out.ppm]
    Decode one frame through FFmpeg.AutoGen and optionally write a PPM.
```

`score` prints backend, frame count, mean, harmonic mean, p1, min, max, standard deviation, elapsed
time and the exact ffmpeg command line. `--json` writes `frame,vmaf` rows.

The CLI resolves ffmpeg the same way the app does and prints the resolved path, so a
`probe`/`score` mismatch is impossible.
