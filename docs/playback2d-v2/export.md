# 2D playback — video export

Turns a stretch of a demo into a video file: the same scene the 2D tab draws, rendered at a fixed
timestep through the same layer stack, encoded to WebM, MP4 or GIF.

It is available in the app (2D Playback → Export) and headlessly through
[`dv2d export`](../dv2d.md). Both go through the same `SceneExportSession`, so a request the dialog
accepts is a request the CLI accepts, and neither can produce a file the other would refuse.

---

## What an export is

- **Its own replay.** An export never touches the playback clock. It builds a private
  `EntityTracker` over the parsed demo and walks it forward on a background thread, so you can keep
  watching (or scrubbing, or annotating) while it renders.
- **A fixed timestep.** Every frame advances by exactly `speed / fps` seconds, whatever the machine
  is doing. Two exports of the same request produce the same pixels — that is asserted, on frame
  hashes rather than on encoded bytes, because video encoders are not bit-reproducible.
- **The window's picture.** Levels, panes, radar art and the camera all come from the same code the
  on-screen host uses. A two-floor Nuke export has two bands, because the app draws two bands.

---

## Formats

| Format | Codec | Needs | Notes |
|---|---|---|---|
| **WebM** (default) | VP9 (`libvpx-vp9`, CRF) | ffmpeg | Present in **LGPL** ffmpeg builds, which is why it is the default |
| **MP4** | H.264 (`libx264`, CRF, faststart) | a **GPL** ffmpeg | Not in LGPL builds; the dialog says so rather than failing at encode time |
| **GIF** | palettegen / paletteuse, or ImageSharp | nothing | The only format that works with no ffmpeg at all |

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

## Speed

Measured on `assets/tour/sample-de_nuke.dem`, WebM/VP9, CPU rasteriser, the shipped layer set:

| Size | fps | Exported frames/s | vs realtime |
|---|---|---|---|
| 1280×720 | 60 | 109.8 | **1.83×** |
| 1920×1080 | 60 | 58.4 | 0.97× |
| 1920×1080 | 30 | 53.0 | **1.77×** |

**720p is the default preset** because it is the one that finishes faster than the clip plays on a
CPU. 1080p is one click away and perfectly usable — it just takes about as long as watching it.
A GPU backend (C2) is where 1080p60 gets its headroom.

Two levers if an export is slower than you want:

- **Vision cones are off by default.** Solving line of sight is the most expensive thing in a frame.
- **The radar is the next biggest cost.** It is a full-frame image composite; nothing to turn off,
  but it is why output size dominates the number above.

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
2. **An in-app download**, on Windows x64 only. It fetches a **pinned LGPL-2.1 build** from
   [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), verifies it against a pinned SHA-256,
   shows you the `LICENSE.txt` from inside the archive, and installs nothing until you accept. A 404,
   a checksum mismatch or a declined consent all leave the disk exactly as they found it.
   *macOS and Linux do not get this rung*: BtbN publishes those builds as `.tar.xz`, .NET has no xz
   decoder, and taking a compression dependency to unpack something every distribution already
   packages is the wrong trade.
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
reports `Cancelled`.

---

## Overlays

`Include` in the dialog, `--layers` / `--hud` on the command line.

| Overlay | Default | Notes |
|---|---|---|
| Radar, trails, area effects, markers, bomb, floor labels | on | The scene |
| Clock + kill feed (`hud.clock`, `hud.killfeed`) | on in the app, off in `dv2d` | Opt-in by name — an export never burns in a scoreboard by accident |
| Annotations (`playback2d.annotations`) | on | B2's layer |
| Vision cones (`playback2d.vision`) | **off** | The frame's biggest cost |

The exported kill feed and the on-screen one are windowed by the same function over the same rows, so
they cannot show different kills at the same tick.

---

## Command line

```bash
dv2d export --demo match.dem --from t12000 --to t20000 \
            --format webm --fps 60 --size 1920x1080 --out round-7.webm
```

`--from` / `--to` take a frame index, or a tick with a `t` prefix. `--no-encode` renders and reads
back every frame without encoding anything — the way to tell whether the renderer or the encoder is
the bottleneck. `--json` reports `frames_per_second` and `realtime_ratio`.

Ctrl+C cancels, with the same guarantees as the dialog's Cancel.
