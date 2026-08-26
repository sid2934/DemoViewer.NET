# 2D playback — video export

Turns a stretch of a demo into a video file: the same scene the 2D tab draws, rendered at a fixed
timestep through the same layer stack, encoded to WebM, MP4 or GIF.

It is available in the app (2D Playback → Export) and headlessly through
[`dv2d export`](../dv2d.md). Both go through the same `SceneExportSession`, so a request the dialog
accepts is a request the CLI accepts, and neither can produce a file the other would refuse.

**That is a statement about the validator, not about the picture.** The two front ends agree on what a
legal request is; what they *default* to differs, and one thing the app can burn in the CLI cannot
build at all. The [Overlays](#overlays) table below is the authority on where they part.

---

## What an export is

- **Its own replay.** An export never touches the playback clock. It builds a private
  `EntityTracker` over the parsed demo and walks it forward on a background thread, so you can keep
  watching (or scrubbing, or annotating) while it renders.
- **A fixed timestep.** Every frame advances by exactly `speed / fps` seconds, whatever the machine
  is doing. Two exports of the same request produce the same pixels — that is asserted, on frame
  hashes rather than on encoded bytes, because video encoders are not bit-reproducible. That is
  doubly true of a hardware encoder — two runs on the same card, or the same card on two driver
  versions, can produce different bytes from identical frames — which is why the encoder that
  produced a file is recorded alongside it.
- **The window's picture.** Levels, panes, radar art and the camera all come from the same code the
  on-screen host uses. A two-floor Nuke export has two bands, because the app draws two bands.

---

## Formats

| Format | Codec | Needs | Notes |
|---|---|---|---|
| **WebM** (default) | AV1 on a GPU, else VP9 (`libvpx-vp9`) | ffmpeg | VP9 is present in **LGPL** ffmpeg builds, which is why WebM is the default |
| **MP4** | H.264 — on a GPU, else `libx264` | a **GPL** ffmpeg for `libx264` | Not in LGPL builds; the dialog says so rather than failing at encode time |
| **GIF** | palettegen / paletteuse, or ImageSharp | nothing | The only format that works with no ffmpeg at all |

**AV1 in a WebM is still a WebM.** The container has carried AV1 since 2018, so a hardware WebM
export keeps the same extension, the same saved default and the same players. It is also why the
hardware rung is AV1 and not HEVC — HEVC cannot go in a WebM at all.

**Frame rates.** Video accepts 24, 25, 30, 50, 60 and 64. GIF accepts **10, 20, 25 and 50** only: a
GIF frame delay is a whole number of centiseconds, so anything that does not divide 100 would be
exported at a rate you did not ask for. The dialog re-lists the rates when you change format and
keeps the closest one you had.

**Dimensions.** WebM and MP4 encode through `yuv420p`, which subsamples chroma 2×2 and therefore
needs an even width and height. Every preset is even; a custom size snaps down to even rather than
refusing.

**GIF caps.** 1800 frames and 1920 px wide. A GIF has no inter-frame compression and its palette is
chosen from the whole animation, so a longer one is an out-of-memory rather than a slow export. The
request is refused up front — you never render two thousand frames into a failure.

---

## Encoder and quality

Every video format has an ordered list of encoders, best first, and DemoViewer takes the first one
this machine can **actually run**:

| Format | Ladder |
|---|---|
| WebM | `av1_nvenc` → `av1_qsv` → `av1_amf` → `libvpx-vp9` |
| MP4 | `h264_nvenc` → `h264_qsv` → `h264_amf` → `libx264` |

"Actually run" means a two-frame test encode, not a menu entry. `ffmpeg -encoders` describes the
*build*; whether the machine has the silicon and a working driver is a different question, and they
disagree constantly — on one development box three of the six listed hardware encoders fail, and one
of them fails on the same GPU where its sibling works. The check costs about a second, once per
session, and is skipped entirely for the software encoders.

**A machine with no working hardware encoder is not a degraded machine.** It lands on tuned software,
which is a completely normal export — and is still substantially faster than DemoViewer used to be
(see below).

**Quality** is `draft`, `standard` (the default) or `best`. It is an intent, translated per encoder,
so `standard` means the same thing on a GPU and on a CPU even though the two share no settings.

`dv2d export` exposes both as `--encoder` and `--quality`; the app remembers your choice. Naming a
specific encoder is taken literally — if it does not work here the export is refused and says why,
rather than quietly using something else.

---

## Speed

Measured over a busy two-minute mid-match range at 1280×720, 60 fps, HUD on, CPU rasteriser, on an
RTX 4070 Ti SUPER — **with CS2 running and using most of the GPU**, which is the realistic case for
this app and which slows the hardware rows by a good margin:

| Encoder | Quality | Exported frames/s | vs realtime | 2-minute clip |
|---|---|---:|---:|---:|
| `libvpx-vp9`, *as it was before* | — | 68.6 | 1.10× | 2.64 MB |
| `libvpx-vp9` | standard | 120.7 | **2.00×** | 3.00 MB |
| `av1_nvenc` | standard | 177.0 | **2.80×** | 3.08 MB |
| `av1_nvenc` | draft | 205.4 | **3.20×** | 1.79 MB |
| `libx264` | standard | 124.4 | 2.00× | 1.45 MB |

A 45-minute match that used to take about 41 minutes to export takes about 16.

Two thirds of that is free: the software encoder was being run at libvpx's slowest setting by
accident, and simply telling it how fast to go is worth 1.76× on its own, with no GPU involved.

**720p is still the default preset.** 1080p is one click away.

Levers if an export is slower than you want, in order:

- **Try `draft`.** On a GPU it is the fastest and the *smallest*.
- **Vision cones are off by default.** Solving line of sight is expensive.
- **The radar is now the single biggest cost in a frame** — with a hardware encoder the encode is
  about 3 % of the work and the drawing is nearly all of it. Nothing to turn off; it is why output
  size dominates the numbers above.

---

## ffmpeg

DemoViewer **ships no ffmpeg** and links no ffmpeg code — it starts ffmpeg as a separate program and
pipes raw frames to it. See [`THIRD-PARTY-NOTICES.md`](../../THIRD-PARTY-NOTICES.md) §e for why that
distinction matters.

The ladder, in order:

1. **An ffmpeg already on your `PATH`.** Whatever you installed always wins.
   - Windows: `winget install Gyan.FFmpeg`, then restart DemoViewer.
   - macOS: `brew install ffmpeg`.
   - Linux: `apt install ffmpeg` (or your distribution's equivalent).
   - No PATH edits wanted? Drop `ffmpeg` and `ffprobe` into `<config>/tools/ffmpeg` and press
     **Re-check**. That folder is picked up immediately, and the highlight-reel feature uses the same
     one — one copy serves both.
2. **An in-app download**, on Windows x64 only, from the export pane's **Download ffmpeg (LGPL)**
   button. It fetches a **pinned LGPL-2.1 build** from
   [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), verifies it against a pinned SHA-256,
   shows you the `LICENSE.txt` from inside the archive, and installs nothing until you accept. A 404,
   a checksum mismatch, a cancelled transfer or a declined licence all leave the disk exactly as they
   found it.
   *This is a button you press, never something an export does on your behalf.* Consent can only be
   asked for **after** the transfer — that is what makes the licence you read the one inside the bytes
   whose checksum was just verified — so a background job that downloaded first and asked later would
   be asking permission for something it had already done.
   *macOS and Linux do not get this rung*: BtbN publishes those builds as `.tar.xz`, .NET has no xz
   decoder, and taking a compression dependency to unpack something every distribution already
   packages is the wrong trade. `dv2d` does not get it either — it is a CLI, and there is nobody there
   to show a licence to.
3. **The GIF floor.** With no ffmpeg and no download, GIF still exports, through ImageSharp.

Because the downloadable build is LGPL it has no H.264 encoder. MP4 needs a GPL ffmpeg you installed
yourself, and the dialog tells you that instead of failing three minutes into an encode.

---

## When export refuses

An export is a heavy job, and it says so rather than queueing silently:

- **A Live Sync session is running.** Export needs the CPU that CS2 is using. Disable Live Sync and
  try again. This is a *start-time* rule — a session that comes up mid-export does not abort it,
  because an export never touches the shared clock and cannot corrupt sync.
- **A highlight reel is rendering.** Symmetrically, starting a reel while an export runs is refused.
- **Background demo processing pauses** for the duration. Opening a demo does **not**: an export is
  CPU-bound, not multi-gigabyte-RAM-bound, so it never blocks your next load.

Cancelling is safe at any point. It kills ffmpeg, deletes the partial file, releases the gate and
reports `Cancelled`. Quitting DemoViewer while an export is running does the same thing on the way
out, so no orphaned ffmpeg and no half-written video survive the exit.

---

## While it runs

The pane hands off and closes; the export does **not** hold a modal. What it is doing appears on a
status-strip chip — the same shape the highlight-reel job uses — whose flyout carries:

- a determinate progress bar (the export contract counts frames, so this is a measurement);
- frames done of total, throughput in frames/s, elapsed, and an ETA;
- **Cancel**, at any point;
- on failure, the message plus an **Encoder log** section holding the chosen encoder rung and the tail
  of ffmpeg's stderr, and a Copy button that puts the whole diagnostic on the clipboard;
- on success, the output path and **Open folder**.

---

## Overlays

`Include` in the dialog; `--layers`, `--hud` and `--annotations` on the command line.

| Overlay | App default | `dv2d` default | Notes |
|---|---|---|---|
| Radar, trails, area effects, markers, bomb, floor labels (`playback2d.*`) | on | on | The scene |
| Score + clock (`hud.clock`) | on | off — `--hud` | Opt-in by name: an export never burns in a scoreboard by accident |
| Kill feed (`hud.killfeed`) | on | off — `--hud` | See the parity note below |
| Player cards (`hud.roster`) | on | off — `--hud` | D3b's cards down both edges |
| Annotations (`playback2d.annotations`) | on | off — `--annotations` | B2's ink. On the CLI this is the demo's own `.dvann.json` sidecar; with no sidecar the layer is not named at all |
| Vision cones (`playback2d.vision`) | **off** | **off** — and naming it does nothing here | The frame's biggest per-frame cost. In the app the layer reads a live `IVisionSolver` over the map's visibility engine. **`dv2d export` builds no engine**, and its frames come off `SceneFrameBuilder`, which fills no pre-solved geometry either — so `--layers …,playback2d.vision` on an export registers a layer with nothing to draw. `dv2d render`/`golden`/`bench` *do* draw it, because a scene fixture carries the solved cones (D6 round 3). Closing the export half means constructing a `VisibilityEngine` for the demo's map in `ExportCommand`; nobody has needed it. |

**Palette.** The app exports in the theme you are looking at. `dv2d` defaults to dark and takes
`--palette dark|light`.

**The kill feed is the one real gap, and it is the CLI's.** In the app the exported feed and the
on-screen one are windowed by the same function over the same rows, so those two cannot show different
kills at the same tick. `dv2d --hud` draws a true clock and true player cards — both read the frame
being drawn — over an **empty** feed: kill rows come from a parsed event timeline the app builds from
`AllGameEvents` and the CLI has no equivalent of. Inventing rows would be worse than the absence, so
until the CLI can build that timeline, `--hud` on the command line is a HUD with no kills in it.

---

## Command line

```bash
dv2d export --demo match.dem --from t12000 --to t20000 \
            --format webm --fps 60 --size 1920x1080 --out round-7.webm

# the app's shipped Include set, on the command line
dv2d export --demo match.dem --hud --annotations --palette dark --out round-7.webm
```

`--from` / `--to` take a frame index, or a tick with a `t` prefix. `--no-encode` renders and reads
back every frame without encoding anything — the way to tell whether the renderer or the encoder is
the bottleneck. `--json` reports `frames_per_second` and `realtime_ratio`, and its `layers` array is
the exact id set that was drawn.

`--annotations` is a flag, not a path: it burns in the demo's own `.dvann.json` sidecar — the file the
app writes beside the demo — and prints a line and adds no layer id when there is none. (The
similarly-spelled `fixture capture --annotations <path>` is a different thing entirely: it embeds a
raw JSON blob in a golden fixture.)

Ctrl+C cancels, with the same guarantees as the dialog's Cancel.
