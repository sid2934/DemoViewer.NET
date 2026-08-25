# P2 — export throughput (encoder ladder, quality presets, input path)

**Design authority:** [`../design.md`](../design.md) §5.7, §6 · **Registry:** [`00-overview.md`](00-overview.md) §3
**Measurement authority:** [`P1-perf-instrumentation.md`](P1-perf-instrumentation.md) §7
**Branch:** `feature/playback2d-v2` · **Status:** design fixed before implementation; kept true afterwards.

P1 answered *why* an export runs at 1.1× realtime and refused to fix anything. This phase spends that
answer. It changes no pixels the renderer draws; it changes what happens to them afterwards.

---

## 1. What P1 left on the table

The inferno two-minute range at 720p60 with `--hud`, CPU raster, libvpx-vp9 at CRF 30:

| stage | p50 ms | share |
|---|---:|---:|
| encode (blocked on the sink's bounded channel) | 7.28 | **54.3 %** |
| render | 3.74 | 28.1 % |
| readback | 2.08 | 15.3 % |
| source (tracker decode + `SceneFrameBuilder`) | 0.29 | 2.1 % |
| advance | 0.01 | 0.1 % |

Two facts from §7 shape everything below.

1. **The encoder is the frame.** Over half of it, and the same range with `--no-encode` runs at 2.5×
   realtime on both demos. There is no content problem and no decode problem to fix.
2. **ffmpeg does not merely follow the renderer, it competes with it.** The same frames raster at
   2.785 ms p50 with no encoder and 4.143 ms with libvpx running beside them — **+49 %**. libvpx-vp9
   with `-row-mt 1` takes every core it can reach, and the render loop is one of the things it takes
   them from.

Fact 2 is the whole argument for a hardware encoder over "a faster software preset". A faster
software preset shortens stage 1 and lengthens stage 2 by taking the cores back. NVENC is a fixed
function block on the die: it shortens stage 1 and gives stage 2 its cores back at the same time.

And one thing P1 recorded that is a defect rather than a trade-off: **today's libvpx invocation sets
no `-deadline` and no `-cpu-used`**, so the software path runs at libvpx's *slowest* setting on a
codec whose speed control is that pair of flags. That is fixed here whether or not a GPU exists.

---

## 2. Product goal, in the user's words

> "decent bitrate but quick encoding"

and, longer term,

> "a single exporting node responsible for producing as many as possible as quickly as possible".

The first sentence is this phase. The second is **not built here** — but every seam it needs is,
and §7 records the shape so the phase that builds it does not have to re-cut this one.

---

## 3. Decisions

### D1 — Probe, do not trust the list. Two frames, once, cached.

`ffmpeg -encoders` is a **build** manifest, not a **machine** manifest. On the development box for
this phase, a 2-frame test encode at 256×256 says:

| encoder | listed by `-encoders` | 2-frame test encode |
|---|---|---|
| `av1_nvenc` | yes | **ok** (678 ms) |
| `h264_nvenc` | yes | **ok** (598 ms) |
| `av1_qsv` | yes | **fails** — `Error creating a MFX session: -9` (no Intel device) |
| `h264_qsv` | yes | **fails** — same |
| `av1_amf` | yes | **fails** — `CreateComponent(AMFVideoEncoderHW_AV1) failed with error 30` |
| `h264_amf` | yes | **ok** (618 ms) |

Four of six listed hardware encoders are wrong about themselves on one machine, and two of them are
wrong in *both* directions on the same vendor's silicon (`h264_amf` works, `av1_amf` does not: the
Radeon iGPU has no AV1 encode block). A ladder built on the listing alone would pick `av1_qsv` on
this box and fail an hour into a full-match export.

So the ladder verifies. The verification is:

- **a real encode**, of two 256×256 frames of `yuv420p` fed on **stdin as `rawvideo`**, to `-f null -`.
  No `lavfi`, no filter graph, no container, no temp file — the probe must not be able to fail for a
  reason that is not the encoder. 256×256 clears every hardware minimum (AV1 NVENC's is 160×128).
- **hardware rungs only.** A software rung that `-encoders` lists is trusted from the listing.
  The failure mode the probe exists for — "listed, initialises, then dies on a missing device" — is a
  driver fact. `libvpx-vp9` present in the build and broken at runtime is not a thing that happens,
  and paying 600 ms per export on a GPU-less CI runner to re-learn it would be a tax on the one lane
  that can never benefit.
- **cached** in an `EncoderProbeCache` keyed by (ffmpeg directory, encoder name). One instance is
  shared for the life of an app session; `dv2d` builds one per invocation. Concurrency-safe by
  construction (`ConcurrentDictionary`), because §7's export node will probe from several sessions.
- **bounded**: a probe that has not exited in 20 s is killed and reported as a failure. A hung
  probe must not become a hung export.

The cache holds *environment* facts, so `EncoderProbeCache.Shared` exists as a convenience. **The
selection is not cached and not global**: `EncoderSelection` is a value handed to one sink, which is
what D5 needs.

### D2 — The ladders

Per output format, best-first. `--encoder auto` (the default) walks it and takes the first rung that
verifies.

**WebM** — `av1_nvenc` → `av1_qsv` → `av1_amf` → `libvpx-vp9`

AV1 in a `.webm` is legal: the WebM project added AV1 to the container in 2018, and `ffprobe` on our
own output reads `format_name=matroska,webm  codec_name=av1`. **The container does not change** — a
`.webm` stays a `.webm`, which is what keeps the format id, the file extension, the dialog and every
persisted default untouched. It is also the reason AV1 is preferred over HEVC on this rung: HEVC
cannot go in a WebM at all.

**MP4** — `h264_nvenc` → `h264_qsv` → `h264_amf` → `libx264`

H.264 rather than HEVC/AV1 for the MP4 rung on compatibility grounds: an `.mp4` is the format a user
picks when they are about to hand the file to somebody else.

**GIF** — unchanged. The palettegen/paletteuse chain is not an encoder choice and has no ladder;
it reports as the pseudo-rung `gif` so the JSON has one shape.

**Ordering, and the QSV/AMF rungs.** Vendor order is NVENC, then QSV, then AMF. It is not a quality
claim — it is that on a machine with both a discrete NVIDIA card and an integrated GPU, the discrete
card is the one that is not also drawing the desktop. On a machine with only an iGPU the first rung
fails its probe in milliseconds (not listed) or in ~600 ms (listed, no device) and the ladder moves
on. **`av1_qsv`, `h264_qsv` and `av1_amf` are shipped unverified on this hardware** — the probe is
exactly what makes shipping them safe.

### D3 — Quality presets, and what "decent bitrate" resolves to

Three ids, `draft | standard | best`, mapped **per encoder**, default `standard`.

Measured on 900 frames of a real 1280×720 60 fps export of the inferno range (`nv12` source file, so
the numbers are encoder-only and not pipeline numbers). SSIM is against that same source.

| encoder | quality | arguments | fps | kbps | SSIM |
|---|---|---|---:|---:|---:|
| `av1_nvenc` | draft | `-preset p1 -rc vbr -cq 40 -b:v 0` | 657 | 100 | 0.99531 |
| | **standard** | `-preset p4 -rc vbr -cq 34 -b:v 0 -bf 3 -rc-lookahead 8` | 527 | 156 | 0.99862 |
| | best | `-preset p6 -rc vbr -cq 28 -b:v 0 -bf 3 -rc-lookahead 20` | 490 | 204 | 0.99891 |
| `h264_nvenc` | draft | `-preset p1 -rc vbr -cq 32 -b:v 0` | 795 | 114 | 0.99607 |
| | **standard** | `-preset p4 -rc vbr -cq 26 -b:v 0 -bf 3 -rc-lookahead 8` | 571 | 194 | 0.99924 |
| | best | `-preset p6 -rc vbr -cq 21 -b:v 0 -bf 3 -rc-lookahead 20` | 472 | 280 | 0.99964 |
| `libvpx-vp9` | draft | `-crf 36 -deadline realtime -cpu-used 8` | 588 | 157 | 0.99860 |
| | **standard** | `-crf 32 -deadline realtime -cpu-used 5` | 526 | 164 | 0.99934 |
| | best | `-crf 30 -deadline good -cpu-used 2` | 142 | 146 | 0.99986 |
| `libx264` | draft | `-preset superfast -crf 26` | 913 | 117 | 0.99838 |
| | **standard** | `-preset veryfast -crf 21` | 911 | 82 | 0.99854 |
| | best | `-preset medium -crf 18` | 767 | 132 | 0.99951 |
| — | *today* | `libvpx-vp9 -crf 30` (no deadline, no cpu-used) | **97** | 143 | 0.99987 |
| — | *today* | `libx264 -preset medium -crf 30` | 765 | 56 | 0.99662 |

Everything carries `-b:v 0` where the codec needs it (without it `-crf`/`-cq` is ignored and the
encoder goes CBR), `-row-mt 1` for VP9, `-pix_fmt yuv420p`, and `-movflags faststart` for MP4.

Four things this table decides.

- **The software VP9 default was leaving 5× on the floor.** `-deadline realtime -cpu-used 5` at
  CRF 32 is 5.4× the throughput of today's invocation for 15 % more bits and an SSIM still above
  0.999. That is the *software* default now; the GPU is a separate win on top of it.
- **`best` is not "today, but slower".** VP9 `best` is today's quality (0.99986 vs 0.99987) at 1.5×
  today's speed, because `-deadline good -cpu-used 2` is still faster than libvpx's unflagged default.
- **`draft` is a real rung, not a joke.** Even `av1_nvenc` draft holds 0.995 SSIM at 100 kbps.
- **`libx264 -preset medium -crf 30` — today's MP4 default — is beaten on both axes.** `veryfast`
  at CRF 21 is faster *and* scores higher; today's setting spends its time on a rate target so low
  the quality is thrown away before the preset can help.

Bitrates are all inside 82–280 kbps at 720p60 because the content is a 2D radar: flat fills, sharp
text, a mostly static background. "Decent bitrate" for this content means *not visibly quantising the
text and the player dots*, which is what the `cq`/`crf` values were chosen against — not a target
number of megabits.

### D4 — `--encoder` and `--quality` are honest, and an explicit request is never silently substituted

`--encoder <name|auto|software>` on `dv2d export`, plus `Playback2D:ExportEncoder` (default `auto`)
and `Playback2D:ExportQuality` (default `standard`) in settings.

- `auto` — walk the ladder.
- `software` — skip every hardware rung. The reproducible, machine-independent answer; what a
  bisect or a golden-adjacent comparison wants.
- a rung's name — take that rung. **If it does not verify, the export is refused** with the probe's
  own message, rather than falling through to something else. A user who asked for `h264_nvenc` and
  silently got `libx264` has been told a lie about what their file is. `auto` is the default
  precisely so this refusal is something you opt into.
- anything not on that format's ladder — a usage error that lists the valid names.

The chosen encoder, why it was chosen, and every rung that was tried and rejected go into
`export --json` (additive keys on the existing `schema_version: 1` payload) and into the human line.

### D5 — The selection is per session. Nothing about it is process-global.

`EncoderSelection` is a value: `(VideoEncoder Encoder, ExportQuality Quality, string Reason,
IReadOnlyList<EncoderProbeResult> Attempts)`. It is resolved by the caller, handed to
`FfmpegSinkOptions`, and lives exactly as long as that sink. Two exports in one process may hold two
different selections at the same time; nothing in the ladder, the selector or the sink reads or
writes shared mutable state. The probe **cache** is shared, and it is a `ConcurrentDictionary` of
facts about the machine — safe to share precisely because nothing about a session can change what it
holds.

This is the same argument that already keeps `GlobalFFOptions` out of the sink (B4: "a CLI export and
an in-app export must be able to disagree"), applied to the encoder.

### D6 — The determinism contract does not move (plan D13)

The determinism gate hashes **pre-encode RGBA frames** through `HashingFrameSink`. Nothing in this
phase touches the render loop, the readback, or that sink, so the gate is encoder-independent by
construction and stays exactly as green as it was.

**Hardware encoders are not bit-reproducible.** Two runs of `av1_nvenc` over identical input can
differ, and the same input on two different NVIDIA driver versions certainly can. That is accepted,
it is why D13 hashes raw frames in the first place, and it is why the chosen encoder is recorded in
`export --json`: a file's bytes are a function of the machine, so the machine is written down.

### D7 — Input path: measure before converting

With a fast encoder the pipe (raw RGBA, 221 MB/s at 720p60) and ffmpeg's own `swscale` become
candidates. Converting BGRA→NV12 on our side with `System.Runtime.Intrinsics` would cut pipe traffic
by 62.5 % and remove `swscale` from the graph.

**It is implemented only if `--perf` shows an end-to-end win with the new encoders.** The pre-measurement
says it probably will not: feeding the same 450 frames as `rgba` versus as `nv12` costs
`av1_nvenc` 35 ms and `h264_nvenc` 23 ms over the whole run — 0.05–0.08 ms per frame — while the raw
read-floor difference alone is 238 ms. `swscale` is overlapped with the encode and is not on the
critical path. Our side would pay ~0.5–1.0 ms per frame of *render-loop* time to save it, which is
the wrong direction. The measurement and the decision are recorded in §8 either way; the same
discipline applies to double-buffering the read-back.

---

## 4. Where the code goes

All new types are Pipeline. **Core is untouched** — `ExportRequest`, `IFrameSink` and
`SceneExportSession` do not learn what an encoder is, which is what keeps registry §3.8's signatures
and design §5.7 intact.

| File | Namespace | What |
|---|---|---|
| `Ffmpeg/ExportQuality.cs` | `…Pipeline.Ffmpeg` | `ExportQuality` enum + `ExportQualities.TryParse`/`ToId`/`All` |
| `Ffmpeg/VideoEncoder.cs` | `…Pipeline.Ffmpeg` | `VideoEncoder` record (name, codec, acceleration, three argument strings), `EncoderAcceleration` |
| `Ffmpeg/EncoderLadder.cs` | `…Pipeline.Ffmpeg` | the per-format rung lists and the named-rung lookup |
| `Ffmpeg/EncoderProbe.cs` | `…Pipeline.Ffmpeg` | `IEncoderProbe`, `EncoderProbeResult`, `FfmpegEncoderProbe`, `EncoderProbeCache` |
| `Ffmpeg/EncoderSelector.cs` | `…Pipeline.Ffmpeg` | `EncoderSelection` + the ladder walk |
| `Export/FfmpegFrameSink.cs` | `…Pipeline.Export` | `FfmpegSinkOptions` swaps `Crf`/`H264Preset` for `Encoder`/`Quality`; `Configure` emits the rung's arguments |

`FfmpegSinkOptions.Crf` and `.H264Preset` are **removed**, not deprecated: with a ladder they are two
ways to say the same thing and the losing one would drift. Three call sites and one test class move.

App and CLI additions:

| Surface | Addition |
|---|---|
| `dv2d export` | `--encoder <name\|auto\|software>`, `--quality <draft\|standard\|best>` |
| `export --json` | additive `video_encoder`, `video_encoder_kind`, `encoder_reason`, `quality`, `encoder_attempts` |
| `Playback2DSettings` | `ExportEncoder` (default `auto`), `ExportQuality` (default `standard`) — both flattened into `SettingsService.WriteInMemory`, per registry §3.10 |
| export dialog | a quality picker, settings-backed; the encoder override rides the setting |
| `Scene2DExportRequest` | trailing `EncoderOverride`/`Quality` params, defaulted |

---

## 5. Degrading honestly on a GPU-less runner

CI has no GPU. Every path here must reach tuned software without a special case, and the tests must
prove it without one either.

- `EncoderLadder` is data. `EncoderSelector` takes an `IEncoderProbe`. A test supplies a fake probe
  that fails the hardware rungs and asserts the selection lands on `libvpx-vp9` / `libx264` with a
  `Reason` that names what was tried — **no process, no PATH, no GPU**, so the assertion is identical
  on a workstation and on a hosted runner.
- `FfmpegEncoderProbe`'s own behaviour (spawning ffmpeg) is covered by cases that skip cleanly when
  `FfmpegLocator` finds nothing, the way `FfmpegAcquisitionTests` and `ExportFailureTests` already do.
- The one thing a GPU-less runner must never see is a *refusal*: `auto` on a machine with no working
  hardware encoder is a completely normal, fully supported export.

---

## 6. Test plan

| Suite | Case |
|---|---|
| Pipeline | ladder order per format; every rung's name is a real ffmpeg encoder id |
| Pipeline | fake probe fails all hardware → software rung chosen, `Reason` names the failures, `Attempts` lists them |
| Pipeline | fake probe fails rung 1, passes rung 2 → rung 2 chosen |
| Pipeline | `software` skips hardware rungs without probing them at all |
| Pipeline | an explicit rung that fails to verify **throws**, and the message carries the probe detail |
| Pipeline | an unknown `--encoder` name throws and lists the valid rungs |
| Pipeline | software rungs are selected from the listing without a test encode (probe call count == 0) |
| Pipeline | preset mapping: each (encoder, quality) pair emits its documented arguments |
| Pipeline | the built ffmpeg argument line carries the selected rung's `-c:v` and its quality arguments |
| Pipeline | GIF is untouched by the ladder — still one input, still palettegen/paletteuse |
| Pipeline | the probe cache calls the underlying probe once per (directory, encoder) |
| CLI | `--encoder` / `--quality` parse; bad values are usage errors |
| CLI | `export --json` carries the additive keys and stays `schema_version: 1`, snake_case |
| App | settings round-trip for `ExportEncoder`/`ExportQuality`, including `WriteInMemory` |
| App | the dialog persists the chosen quality |
| unchanged | the determinism gate, the budget lanes, `HashingFrameSink` |

---

## 7. The export-node seams (design only — NOT built here)

The long-term shape is "one node, many exports, as fast as the box allows". This phase does not build
it. What it does is make sure nothing here has to be undone first.

```
ExportJobService (today: single-flight)          →   ExportQueue (later)
        │                                                │
        │ one Scene2DExportRequest                       │ N requests, priority + admission
        ▼                                                ▼
  SceneExportSession  ×1                          SceneExportSession × N   (already independent:
        │                                                │                  private tracker, private
        │ IFrameSink                                     │                  compositor, private surface)
        ▼                                                ▼
  FfmpegFrameSink ── EncoderSelection ──┐         FfmpegFrameSink × N ── EncoderSelection × N
                                        │                                        │
                              EncoderProbeCache (shared, concurrent) ────────────┘
```

Three constraints the builder of that node will hit, and what P2 already did about them.

1. **NVENC session limits are real.** Consumer NVIDIA drivers cap concurrent NVENC sessions (3 on
   older drivers, 5 on current ones, 8 on Ada; the professional drivers are unlimited). A queue that
   starts eight hardware exports will have the ninth fail *at encoder init*, not at admission. The
   node therefore needs a **semaphore over hardware sessions, not over exports** — and the ladder is
   already the place that knows whether a session is a hardware one (`VideoEncoder.IsHardware`).
   The natural shape is: acquire a hardware permit → if the wait would be long, or the permit pool is
   exhausted, resolve the *same* request against `software` instead and keep going. That is a
   selection-time decision, which is exactly why D5 made the selection a per-session value.
2. **The probe must not be re-run N times.** `EncoderProbeCache` is concurrent and shareable for this
   reason and no other. One instance on the node; every session reads it.
3. **`HeavyJobGate` currently admits one export.** Widening it is the node's change, not this one.
   Nothing in P2 assumes single-flight: `EncoderSelector` has no state, `EncoderLadder` is
   `static readonly` data, and `FfmpegSinkOptions` is a record.

What is deliberately **not** designed here: queue persistence, priority, per-job output naming,
progress aggregation, and whether the node is in-process or a service. Those are product questions,
and answering them early would be guessing.

---

## 8. What it measured

*(filled in after implementation — see the commit that lands the numbers)*

---

## 9. Deviations from the surrounding plans

*(filled in as they occur)*
