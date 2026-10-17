# Usage

## Opening files

Every way in goes through one router, so the rules are the same everywhere:

1. **Drag and drop.** While files are over the window, the viewer dims and two zones appear:
   **Reference** (left) and **Compare** (right). The zone you drop on decides the role. Dropping
   anywhere else behaves like startup arguments (below). With nothing loaded, the same two zones are
   the first-run screen.
2. **Reference** / **Encodes** in the toolbar (`Ctrl+O` / `Ctrl+Shift+O`): the same as dropping on
   the matching zone.
3. **Paste** (`Ctrl+V`): files on the clipboard, or one path per line of text (`file://` URIs work).
4. **Startup arguments and "Open With"**:
   `VideoQuality.App [<reference>] [<encode> …] [--mode=wipe|flicker|difference]`. A single file is
   enough; it becomes the reference.

The rules:

| Where | What happens |
|---|---|
| Anywhere else / arguments / paste | The first file becomes the reference if none is set; everything else is an encode. |
| **Reference** zone | The first file becomes the reference. Extra files are queued as encodes, with an **Undo** toast. |
| **Compare** zone | Every file is an encode. Folders are scanned (not recursively) for `.mkv .mp4 .mov .webm .avi .ts .m2ts .ivf .y4m`. |

- Replacing the reference while encodes exist makes every score stale. You are asked once
  ("Re-score 4 encodes?", default yes). Choosing **No** leaves the rows marked stale; **Update
  stale** in the toolbar re-scores them later.
- Non-video files, missing files and non-local items (`smb://`, `mtp://`) are skipped, and a toast
  says why. Files that are already loaded are ignored, with a toast.
- Rows appear immediately as **queued**, then go through probe + packet index and scoring as jobs.
  Encodes dropped before a reference wait for one. A resolution, frame-rate or frame-count mismatch
  with the reference puts a warning icon on the row (scaling is not implemented yet).
- Two CUDA scores run at once; CPU scores run one at a time because libvmaf already uses every core.

On Linux, `packaging/linux/` has a `.desktop` entry (`MimeType=video/*`, `Exec=videoquality %F`) and
a launcher script so file managers can **Open With** the app.

## The window

The app opens with a splash screen while it loads the FFmpeg libraries and finds a VMAF-capable
ffmpeg, then shows the main window. The window draws its own dark title bar (app name on the left,
the current comparison in the middle, minimise/maximise/close on the right) instead of the
system's.

The UI uses the Semi theme with Ursa controls and Fluent System Icons, in four layers:

| Layer | Used for |
|---|---|
| base | window background, status line |
| surface | cards: viewer panes, graph, encode table, timeline |
| raised | the toolbar and labels that float over video |
| overlay | drop zones, toasts, confirmation dialogs |

```
┌ raised toolbar ─────────────────────────────────────────────────────────────────┐
│ Reference Encodes Re-score │ ⏮ ▶ ⏭ │ [▥|⇔|ϟ|◪] mode controls │ ⊙ 1:1 % │ 0–100 │ NEG CUDA ⚙ │
├──────────────── viewer: two panes or one (wipe / flicker) ──────────────────────┤
╞════════════════ splitter (drag; double-click to auto-size) ═════════════════════╡
├────────────────────── score graph ─────────────┬────── encode table ────────────┤
├──────────────── frame / VMAF readout, slider, pooled stats ─────────────────────┤
└──────────────────── status · backend · progress / Cancel ───────────────────────┘
```

- **Toolbar**: icon + label for the file actions, icon-only for the rest; every button has a tooltip
  with its shortcut. The view switch is a segmented icon control (side by side, wipe, flicker,
  difference). The **NEG** and **CUDA** chips show the model and backend, and open Settings (model,
  backend, ffmpeg path, detection result, log file location).
- **Pane headers** carry the file name, resolution, codec, frame rate, duration, a bit-depth chip
  (`10-bit`) and an amber `lossy` chip when the reference itself is a delivery codec.
- **Encode table**: a status icon (queued, working, done, failed, stale), a colour dot that matches the
  curve (click it to hide the curve), name, model, mean, p1 and true video bitrate. Hovering a row
  shows **Re-score**, **Reveal in folder** and **Remove**. Tooltips give the backend/precision
  (`CUDA·8b` means the GPU path converted a 10-bit source), file size and any error. Selecting a row
  swaps the viewer to that encode and thickens its curve.
- **Graph**: per-frame VMAF for every scored encode, in the Okabe–Ito palette so the curves stay
  distinguishable with colour-vision deficiencies. Autoscales by default; `0–100` pins the axis for
  cross-clip comparison. The dashed line is the selected encode's mean; the dotted line is VMAF 85.
  **Smooth** applies a 5-frame moving average; **Δ baseline** plots the difference from the first
  visible encode.
- **Splitter**: drag it to trade viewer height for graph height. The graph keeps a minimum height,
  so wipe mode cannot crush it. Double-click to go back to sizing from the video's aspect ratio.

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
- **Fit** (the target icon) returns to fit-to-pane; **1:1** shows screen pixels per image pixel.
- The zoom readout shows the effective scale and `(fit)` when in fit mode.

The viewer height is derived from the image aspect ratio until you drag the splitter, so fit mode has
no letterbox dead space.
At 1:1 the image is clipped to its pane rather than painted over the headers.

## Keyboard

| Key | Action |
|---|---|
| `←` / `→` | Step one frame |
| `Shift` + `←` / `→` | Step ten frames |
| `Home` / `End` | First / last frame |
| `Space` | Play or pause (native frame rate) |
| `F` (hold) | Reveal the encode in Flicker mode |
| `Ctrl+O` / `Ctrl+Shift+O` | Choose the reference / add encodes |
| `Ctrl+V` | Paste files or paths |
| `Ctrl+R` | Re-score the selected encode |

Playback is clock-driven: each tick computes the target frame from elapsed wall time, so a slow
decode drops frames instead of playing in slow motion.

## Models

The model (in Settings, behind the model chip) defaults to **VMAF NEG** (`vmaf_v0.6.1neg`). NEG removes the enhancement gain
from the score, so a sharpened encode cannot buy points by looking crisper; it is the better default
for encoder comparisons. `vmaf_v0.6.1` (standard) and `vmaf_4k_v0.6.1` are selectable, and the
selected model is stored on every report, shown in the table's `MODEL` column and in the graph title
(`VMAF NEG per frame — CUDA`).

Scores from different models are not comparable. If a comparison mixes them, the status line warns and
the graph title is marked `MIXED MODELS`; re-score with one model. The same rule applies to mixing
backends.

## Scoring behaviour

- Backend **Auto** picks CUDA whenever `libvmaf_cuda` is present and an NVIDIA GPU is visible;
  otherwise CPU. Both can be forced in the toolbar. If an Auto CUDA run fails (for example an NVDEC
  profile the GPU cannot decode), it retries once on CPU and says so.
- Each report permanently records its backend and precision; converted 10-bit runs keep `·8b` in the
  table, the JSON export and the status line.
- Progress comes from `ffmpeg -progress pipe:1` and shows frame count and encoding speed. Cancel
  kills the ffmpeg process tree and discards the partial log.
- A 10-bit source scored on CUDA is converted to 8-bit by `scale_cuda` because `libvmaf_cuda`
  accepts `yuv420p` only; the status line says so. Select **CPU** when full 10-bit precision matters.
- Scores are paired by frame index (`ts_sync_mode=nearest`, `shortest=1`), not by wall-clock
  timestamp.

## CLI

```
vq probe  [--ffmpeg PATH]
    Resolve and describe the ffmpeg build: version, libvmaf, libvmaf_cuda, GPU, libav path, log path.

vq score <reference> <distorted> [--gpu|--cpu] [--model NAME] [--ffmpeg PATH]
                            [--csv out.csv] [--json out.json]
    Run VMAF. Default backend is Auto (CUDA when available). --csv writes frame,vmaf rows;
    --json writes the full report (backend, precision, model, stats, command, frames).

vq frame <video> [index] [--ppm out.ppm]
    Decode one frame through FFmpeg.AutoGen and optionally write a PPM.

vq diag  [--ffmpeg PATH]
    Print a session header: versions, OS, ffmpeg capabilities, libav path, log location.
```

`score` prints backend and precision, frame count, mean, harmonic mean, p1, min, max, standard
deviation, elapsed time and the exact ffmpeg command line. A converted 10-bit run prints
`CUDA·10b→8b` and a note.

The CLI resolves ffmpeg the same way the app does and prints the resolved path, so a
`probe`/`score` mismatch is impossible.
