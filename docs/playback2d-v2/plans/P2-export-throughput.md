# P2: export throughput (encoder ladder, quality presets, input path)

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
   2.785 ms p50 with no encoder and 4.143 ms with libvpx running beside them, **+49 %**. libvpx-vp9
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

The first sentence is this phase. The second is **not built here**, but every seam it needs is,
and §7 records the shape so the phase that builds it does not have to re-cut this one.

---

## 3. Decisions

### D1: Probe, do not trust the list. Two frames, once, cached.

`ffmpeg -encoders` is a **build** manifest, not a **machine** manifest. On the development box for
this phase, a 2-frame test encode at 256×256 says:

| encoder | listed by `-encoders` | 2-frame test encode |
|---|---|---|
| `av1_nvenc` | yes | **ok** (678 ms) |
| `h264_nvenc` | yes | **ok** (598 ms) |
| `av1_qsv` | yes | **fails**: `Error creating a MFX session: -9` (no Intel device) |
| `h264_qsv` | yes | **fails**: same |
| `av1_amf` | yes | **fails**: `CreateComponent(AMFVideoEncoderHW_AV1) failed with error 30` |
| `h264_amf` | yes | **ok** (618 ms) |

Four of six listed hardware encoders are wrong about themselves on one machine, and two of them are
wrong in *both* directions on the same vendor's silicon (`h264_amf` works, `av1_amf` does not: the
Radeon iGPU has no AV1 encode block). A ladder built on the listing alone would pick `av1_qsv` on
this box and fail an hour into a full-match export.

So the ladder verifies. The verification is:

- **a real encode**, of two 256×256 frames of `yuv420p` fed on **stdin as `rawvideo`**, to `-f null -`.
  No `lavfi`, no filter graph, no container, no temp file: the probe must not be able to fail for a
  reason that is not the encoder. 256×256 clears every hardware minimum (AV1 NVENC's is 160×128).
- **hardware rungs only.** A software rung that `-encoders` lists is trusted from the listing.
  The failure mode the probe exists for ("listed, initialises, then dies on a missing device") is a
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

**Only an answer is remembered. A cancellation is not one** (found in review). `EncoderProbeCache`
already declined to memoise a cancelled *result*; the `-encoders` listing underneath it did not, and
an empty listing is indistinguishable from "this build carries no encoders". Cancelling an export
while the ladder was being walked therefore poisoned the one cache an app session has: every later
export was told every rung was "not built into this ffmpeg" and dropped to the software floor,
silently, permanently, until the user happened to press Re-check. Two rules now:

- **An empty listing is never memoised.** A listing that named something is a fact about a build; an
  empty one is a fact about a moment.
- **Cancellation kills the child, and is never handed to the stream reads.** Handing the token to
  `ReadToEndAsync` abandoned ffmpeg's pipes while it was still writing, so the child blocked on a
  full buffer and the "cancelled" probe sat out the entire 20 s timeout. The token now terminates the
  process and the reads end with it; the probe throws `OperationCanceledException` rather than
  reporting a machine fact it never learned.

Both are covered by `FfmpegEncoderProbeTests.ACancelledListing_IsNotRemembered` and
`…ACancelledProbe_DoesNotSitOutTheTimeout`.

### D2: The ladders

Per output format, best-first. `--encoder auto` (the default) walks it and takes the first rung that
verifies.

**WebM**: `av1_nvenc` → `av1_qsv` → `av1_amf` → `libvpx-vp9`

AV1 in a `.webm` is legal: the WebM project added AV1 to the container in 2018, and `ffprobe` on our
own output reads `format_name=matroska,webm  codec_name=av1`. **The container does not change**: a
`.webm` stays a `.webm`, which is what keeps the format id, the file extension, the dialog and every
persisted default untouched. It is also the reason AV1 is preferred over HEVC on this rung: HEVC
cannot go in a WebM at all.

**MP4**: `h264_nvenc` → `h264_qsv` → `h264_amf` → `libx264`

H.264 rather than HEVC/AV1 for the MP4 rung on compatibility grounds: an `.mp4` is the format a user
picks when they are about to hand the file to somebody else.

**GIF**: unchanged. The palettegen/paletteuse chain is not an encoder choice and has no ladder;
it reports as the pseudo-rung `gif` so the JSON has one shape.

**Ordering, and the QSV/AMF rungs.** Vendor order is NVENC, then QSV, then AMF. It is not a quality
claim: it is that on a machine with both a discrete NVIDIA card and an integrated GPU, the discrete
card is the one that is not also drawing the desktop. On a machine with only an iGPU the first rung
fails its probe in milliseconds (not listed) or in ~600 ms (listed, no device) and the ladder moves
on. **`av1_qsv`, `h264_qsv` and `av1_amf` are shipped unverified on this hardware**: the probe is
exactly what makes shipping them safe.

### D3: Quality presets, and what "decent bitrate" resolves to

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
- **`libx264 -preset medium -crf 30`, today's MP4 default, is beaten on both axes.** `veryfast`
  at CRF 21 is faster *and* scores higher; today's setting spends its time on a rate target so low
  the quality is thrown away before the preset can help.

Bitrates are all inside 82–280 kbps at 720p60 because the content is a 2D radar: flat fills, sharp
text, a mostly static background. "Decent bitrate" for this content means *not visibly quantising the
text and the player dots*, which is what the `cq`/`crf` values were chosen against, not a target
number of megabits.

### D4: `--encoder` and `--quality` are honest, and an explicit request is never silently substituted

`--encoder <name|auto|software>` on `dv2d export`, plus `Playback2D:ExportEncoder` (default `auto`)
and `Playback2D:ExportQuality` (default `standard`) in settings.

- `auto`: walk the ladder.
- `software`: skip every hardware rung. The reproducible, machine-independent answer; what a
  bisect or a golden-adjacent comparison wants.
- a rung's name: take that rung. **If it does not verify, the export is refused** with the probe's
  own message, rather than falling through to something else. A user who asked for `h264_nvenc` and
  silently got `libx264` has been told a lie about what their file is. `auto` is the default
  precisely so this refusal is something you opt into.
- anything not on that format's ladder: a usage error that lists the valid names.

The chosen encoder, why it was chosen, and every rung that was tried and rejected go into
`export --json` (additive keys on the existing `schema_version: 1` payload) and into the human line.

### D5: The selection is per session. Nothing about it is process-global.

`EncoderSelection` is a value: `(VideoEncoder Encoder, ExportQuality Quality, string Reason,
IReadOnlyList<EncoderProbeResult> Attempts)`. It is resolved by the caller, handed to
`FfmpegSinkOptions`, and lives exactly as long as that sink. Two exports in one process may hold two
different selections at the same time; nothing in the ladder, the selector or the sink reads or
writes shared mutable state. The probe **cache** is shared, and it is a `ConcurrentDictionary` of
facts about the machine, safe to share precisely because nothing about a session can change what it
holds.

This is the same argument that already keeps `GlobalFFOptions` out of the sink (B4: "a CLI export and
an in-app export must be able to disagree"), applied to the encoder.

### D6: The determinism contract does not move (plan D13)

The determinism gate hashes **pre-encode RGBA frames** through `HashingFrameSink`. Nothing in this
phase touches the render loop, the readback, or that sink, so the gate is encoder-independent by
construction and stays exactly as green as it was.

**Hardware encoders are not bit-reproducible.** Two runs of `av1_nvenc` over identical input can
differ, and the same input on two different NVIDIA driver versions certainly can. That is accepted,
it is why D13 hashes raw frames in the first place, and it is why the chosen encoder is recorded in
`export --json`: a file's bytes are a function of the machine, so the machine is written down.

### D7: Input path: measure before converting

With a fast encoder the pipe (raw RGBA, 221 MB/s at 720p60) and ffmpeg's own `swscale` become
candidates. Converting BGRA→NV12 on our side with `System.Runtime.Intrinsics` would cut pipe traffic
by 62.5 % and remove `swscale` from the graph.

**It is implemented only if `--perf` shows an end-to-end win with the new encoders.** The pre-measurement
says it probably will not: feeding the same 450 frames as `rgba` versus as `nv12` costs
`av1_nvenc` 35 ms and `h264_nvenc` 23 ms over the whole run (0.05–0.08 ms per frame) while the raw
read-floor difference alone is 238 ms. `swscale` is overlapped with the encode and is not on the
critical path. Our side would pay ~0.5–1.0 ms per frame of *render-loop* time to save it, which is
the wrong direction. The measurement and the decision are recorded in §8 either way; the same
discipline applies to double-buffering the read-back.

---

## 4. Where the code goes

All new types are Pipeline. **Core is untouched**: `ExportRequest`, `IFrameSink` and
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
| `Playback2DSettings` | `ExportEncoder` (default `auto`), `ExportQuality` (default `standard`), both flattened into `SettingsService.WriteInMemory`, per registry §3.10 |
| export dialog | a quality picker and an encoder picker, both settings-backed, beside Format and Frame rate |
| `Scene2DExportRequest` | trailing `EncoderOverride`/`Quality` params, defaulted |

---

## 5. Degrading honestly on a GPU-less runner

CI has no GPU. Every path here must reach tuned software without a special case, and the tests must
prove it without one either.

- `EncoderLadder` is data. `EncoderSelector` takes an `IEncoderProbe`. A test supplies a fake probe
  that fails the hardware rungs and asserts the selection lands on `libvpx-vp9` / `libx264` with a
  `Reason` that names what was tried (**no process, no PATH, no GPU**), so the assertion is identical
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
| Pipeline | GIF is untouched by the ladder: still one input, still palettegen/paletteuse |
| Pipeline | the probe cache calls the underlying probe once per (directory, encoder) |
| CLI | `--encoder` / `--quality` parse; bad values are usage errors |
| CLI | `export --json` carries the additive keys and stays `schema_version: 1`, snake_case |
| App | settings round-trip for `ExportEncoder`/`ExportQuality`, including `WriteInMemory` |
| App | the dialog persists the chosen quality |
| unchanged | the determinism gate, the budget lanes, `HashingFrameSink` |

---

## 7. The export-node seams (design only, NOT built here)

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
   node therefore needs a **semaphore over hardware sessions, not over exports**, and the ladder is
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

Same box as P1 §7, same range, same everything: `export --demo match730_…117.dem --from 72000
--to 79680 --size 1280x720 --fps 60 --hud --perf`, CPU raster, a two-minute mid-match range. The
`old-default` row is the **pre-P2 binary** (`78cd116`) built from a throwaway worktree and run
back-to-back with the rest, so it is an A/B on one machine state rather than a number remembered from
an earlier session.

> **Map label corrected in review.** This range was described as de_inferno here and in P1 §7.
> `dv2d render --demo match730_…117.dem --frame 72000 --json` reports `"map": "de_mirage"`, and so
> does the other demo in that folder (`…_408.dem`). The range and the numbers are unaffected; only
> the label was wrong. §8.7 re-measures it and says which of the numbers survive.

### 8.1 The condition, and why it is the interesting one

**A copy of CS2 was running on this machine for every row below, holding 70–99 % of the GPU and
about 3.7 CPU cores.** That was discovered mid-campaign (`nvidia-smi` said 100 % / 250 W while our
own export was the only thing we had started; the Windows GPU-engine counter named `cs2.exe` at
85 %), and the first instinct was to throw the numbers away.

That would have been the wrong call, because **this is what the product does.** DemoViewer is a CS2
demo viewer; a user exporting a clip has, very often, just come out of the game. So the contended
machine is recorded deliberately, and the honest caveats are recorded with it:

- **NVENC is not immune to 3D load.** It is separate silicon, and `utilization.encoder` stayed at
  0 % for CS2, but frame submission goes through the same GPU scheduler, and it starves. The
  isolated 900-frame `av1_nvenc` bench that ran at **561–657 fps** on the quiet machine ran at
  **52, 110, 53, 88, 96 and 305 fps** on the busy one. Same command, same file, six runs.
- **Run-to-run spread is therefore wide.** `h264_nvenc` at standard measured 86.1, 146.5 and
  166.4 fps across three runs; `av1_nvenc` at standard measured 177.0, 140.7 and 143.4. The table
  quotes the first run of each; treat the hardware rows as ±25 % rather than as three significant
  figures.
- **The ranking is stable even so**, and the ranking is what the phase is about.

### 8.2 The ladder table

Stage columns are p50 ms. SSIM is each output against `sw-best` over all 7 201 frames, a *relative*
figure, anchored on the rung measured at 0.99986 against its true source in §D3, not an absolute
one. Bitrate is `ffprobe`'s.

| run | encoder | quality | fps | ×realtime | frame p50 | source | render | readback | encode | encode p99 | size | bitrate | SSIM |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| **old default** | `libvpx-vp9` *untuned* | — | 68.6 | 1.10× | 13.99 | 0.33 | 4.08 | 2.09 | **7.49** | 12.6 | 2.64 MB | 176 kbps | 0.99983 |
| sw draft | `libvpx-vp9` | draft | 144.9 | 2.40× | 6.39 | 0.32 | 3.45 | 1.75 | 0.79 | 2.0 | 2.85 MB | 190 kbps | 0.99839 |
| **sw standard** | `libvpx-vp9` | standard | **120.7** | **2.00×** | 8.15 | 0.33 | 3.48 | 1.75 | 2.84 | 3.1 | 3.00 MB | 200 kbps | 0.99914 |
| sw best | `libvpx-vp9` | best | 86.2 | 1.40× | 10.92 | 0.36 | 3.83 | 2.00 | 4.60 | 10.2 | 2.72 MB | 181 kbps | *(ref)* |
| av1 draft | `av1_nvenc` | draft | **205.4** | **3.20×** | 4.34 | 0.26 | 2.61 | 1.29 | 0.15 | 0.2 | 1.79 MB | 119 kbps | 0.99531 |
| **av1 standard** | `av1_nvenc` | standard | **177.0** | **2.80×** | 5.15 | 0.31 | 2.99 | 1.65 | **0.16** | 0.2 | 3.08 MB | 205 kbps | 0.99857 |
| av1 best | `av1_nvenc` | best | 160.9 | 2.60× | 5.40 | 0.32 | 3.04 | 1.72 | 0.17 | 3.0 | 4.76 MB | 317 kbps | 0.99914 |
| x264 standard | `libx264` | standard | 124.4 | 2.00× | 8.07 | 0.34 | 3.42 | 1.73 | 2.82 | 3.3 | 1.45 MB | **97 kbps** | 0.99841 |
| h264 standard | `h264_nvenc` | standard | 86–166 | 1.4–2.7× | 7.23 | 0.33 | 3.17 | 1.77 | 1.31 | 170.9 | 3.98 MB | 265 kbps | — |
| h264 best | `h264_nvenc` | best | 129.4 | 2.00× | 7.02 | 0.33 | 3.21 | 1.76 | 1.58 | 3.2 | 5.88 MB | 392 kbps | 0.99925 |
| `--no-encode` | `HashingFrameSink` | — | 145.9 | 2.40× | 6.49 | 0.23 | 3.01 | 1.69 | 1.53 | 1.7 | — | — | — |

### 8.3 What the table says

**The headline: 1.10× → 2.80× realtime**, on the same range, same size, same layers, same machine
state. A 45-minute match that took 41 minutes to export takes about 16.

> **Scope, added in review (§8.7).** That multiple holds *while the encoder is the frame*, which is
> what P1 measured and what every row above was taken in, with the radar layer costing ~1.5 ms. It is
> not a promise about every machine: an independent re-run found the same range rendering its radar at
> **11.8 ms** per frame, and at that price the encoder is 4 % of the frame and none of the rungs below
> move the total at all. **What P2 changes is the encoder's share of the frame; whether that shows up
> as end-to-end throughput depends on the renderer.** §8.7 has both regimes measured.

**The software default alone is worth 1.76×** (68.6 → 120.7 fps) and needs no GPU at all. That is the
`-deadline`/`-cpu-used` defect being paid back, and it is the number every CI runner and every
GPU-less laptop gets. It costs 14 % more bits (176 → 200 kbps) and 0.0007 of relative SSIM.

**`av1_nvenc` makes the encoder disappear from the frame.** The encode stage (time the render loop
sits blocked on the sink) goes from 7.49 ms (54 % of the frame, P1's headline) to **0.16 ms, 3 %**.
It is now *cheaper than SHA-256'ing the frame*: the `--no-encode` row's `HashingFrameSink` costs
1.53 ms, ten times what handing the same frame to NVENC costs. "No encoder" is no longer the fast
path, which is a sentence P1 could not have written.

**P1's second-order finding is confirmed and then fixed.** P1 measured the same frames rastering
49 % slower with libvpx beside them. Here: render p50 **4.08 ms under untuned libvpx → 2.99 ms under
`av1_nvenc`** (−27 %), and readback 2.09 → 1.65 ms (−21 %). Nothing about the renderer changed. The
encoder simply stopped taking its cores, and it gave back more than its own stage was worth.

**The bottleneck has moved.** The frame is now render 58 % + readback 32 % + source 6 %; encode is
3 %. Any further export work is renderer work (P1's radar blit and the `ReadPixels`), not encoder
work. That is a different phase.

**Hardware costs bits, and that is the trade.** `av1_nvenc` at standard spends 205 kbps for 0.99857;
`libx264` at standard spends **97 kbps for 0.99841**: half the bits at the same quality, because a
fixed-function encoder is less efficient than a software one that can search. Both are tiny at 720p60
(a 2-minute clip is 1.5–3 MB), so the ladder still prefers speed. A user who wants the smallest file
rather than the fastest export has `--encoder software` and always will.

### 8.4 Visual spot-check

The same frame (t = 60 s) decoded out of five outputs and looked at: `av1_nvenc` standard,
`av1_nvenc` draft, `h264_nvenc` best, `libvpx-vp9` standard and `libx264` standard. All five render
the HUD line "Round 11  T 6 : 4 CT" crisply, both player clusters with their four-character name
labels legible, and the map geometry sharp. `av1_nvenc` draft (119 kbps, the cheapest rung on the
board) shows faint blocking in the large flat dark areas of the map interior and is otherwise
indistinguishable; nothing a coach would be reading is degraded on any rung.

Full-file decode verification: `ffmpeg -v error -i … -f null -` over the complete
`av1_nvenc` and `h264_nvenc` outputs reports **no errors on any of the 7 201 frames**, and `ffprobe`
reads `codec_name=av1 profile=Main pix_fmt=yuv420p 1280x720 60/1` inside `format_name=matroska,webm`
and `codec_name=h264 profile=Main nb_frames=7201` inside `mov,mp4`. AV1-in-WebM is a real WebM.

### 8.5 D7 resolved: the NV12 conversion is **not** implemented

The pre-measurement said `swscale` was not on the critical path; the post-measurement says our side
of the pipe is not either, and a conversion would make things worse. Three numbers:

| thing | cost per 720p frame |
|---|---|
| what we pay today: one 3.5 MB memcpy into the sink's pooled buffer | **0.123 ms** (29.9 GB/s) |
| BGRA→NV12, scalar | 1.835 ms |
| BGRA→NV12, 128-bit SIMD luma + scalar chroma | 1.871 ms |
| what it would save ffmpeg (`rgba` vs `nv12` input, same 450 frames) | 0.05–0.08 ms, **already overlapped** |

The memcpy figure is corroborated in-pipeline: with `av1_nvenc` the whole `encode` stage (that copy
plus the channel hand-off) measures 0.15–0.18 ms p50, which is the micro-benchmark plus change.

So converting on our side would put **+1.7 ms of new work on the render thread** (a thread whose
whole frame is 4.3–5.4 ms) to remove 0.06 ms from a process that is not the bottleneck: an export
roughly 30 % slower. Even a properly de-interleaved SIMD version four times faster than the one
benchmarked would still cost ~0.45 ms against the 0.123 ms it replaces. **Not implemented, and the
measurement is why.**

The same discipline settles read-back double-buffering: the read-back is already overlapped with
ffmpeg by the capacity-4 bounded channel, and what a second buffer would remove is the same 0.123 ms
copy. It would need an `IFrameSink` rent/commit API, a Core contract change (registry §3.8), to buy
2 % of a frame. Not implemented.

### 8.6 The shipped default, with no flags at all

Everything above names its rung explicitly. This is what a user actually gets: `dv2d export --demo …
--from 72000 --to 79680 --size 1280x720 --fps 60 --hud`, nothing else:

```
video_encoder      av1_nvenc (nvenc, av1) at standard
encoder_reason     the best rung verified first time
encoder_arguments  -preset p4 -rc vbr -cq 34 -b:v 0 -bf 3 -rc-lookahead 8
encoder_probe_ms   1331.9
                   157.2 fps · 2.50× realtime · frame p50 5.47 ms
                   source 0.30 · render 3.16 · readback 1.76 · encode 0.17
```

**2.50× against the old default's 1.10×**, with no flag, no configuration and no reading, in the
regime §8.3's scope note describes, and measured under the CS2 contention §8.1 records.

### 8.7 Independent re-measurement (adversarial review)

Re-run on the same box against the same demo and range, on a **quiet machine** (CS2 had exited;
`nvidia-smi` 0 %, CPU 2–7 %), with the `78cd116` binary rebuilt in a fresh worktree and interleaved
first *and* last so drift would show. Two regimes, because the review found they disagree.

**Regime A: the shipped default, radar on.** 1 714 frames, `--hud`, CPU raster.

| case | encoder | fps | ×rt | render p50 | encode p50 |
|---|---|---:|---:|---:|---:|
| old default | `libvpx-vp9` untuned | 62.3 | 1.04× | 12.60 | 0.16 |
| sw standard | `libvpx-vp9` | 60.2 | 1.00× | 12.21 | 2.34 |
| **auto** | **`av1_nvenc`** | **64.8** | **1.08×** | 12.14 | 0.67 |
| av1 standard | `av1_nvenc` | 62.0 | 1.03× | 12.25 | 0.72 |
| old default (again) | `libvpx-vp9` untuned | 59.8 | 1.00× | 12.60 | 1.00 |

**Every row is 1.0×, before and after.** The radar layer alone measures **11.84 ms p50** (picture
cache `hit_rate 1.00`, 1 713 replays: the cache is working; the replay is simply that expensive on
a CPU raster), which is 84 % of the frame. The encode column is not an encoder cost in this regime at
all: it is back-pressure on the capacity-4 channel, and against a 12 ms renderer no encoder here ever
fills it, which is why the column is non-monotonic noise that ranks *untuned* libvpx fastest.

**Regime B: `--no-radar`, where the encoder is the frame again.** 6 843 frames, full range. This
reproduces P1 §7's condition: `--no-encode --no-radar` render was 1.311 ms there and is 1.5–1.7 ms
here, so the non-radar pipeline agrees closely with what P1 measured.

| case | encoder | fps | ×rt | frame p50 | render | readback | encode | kbps |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| **old default** | `libvpx-vp9` untuned | 99.8 | 1.66× | 9.54 | 1.74 | 1.82 | **5.75** | 131 |
| sw draft | `libvpx-vp9` | 205.3 | 3.42× | 5.64 | 1.63 | 1.24 | 2.70 | 192 |
| **sw standard** | `libvpx-vp9` | **184.0** | **3.07×** | 5.82 | 1.62 | 1.31 | 2.84 | 186 |
| sw best | `libvpx-vp9` | 124.1 | 2.07× | 7.48 | 1.87 | 1.82 | 3.40 | 134 |
| av1 draft | `av1_nvenc` | 201.6 | 3.36× | 5.50 | 1.52 | 1.22 | 2.72 | 105 |
| **av1 standard** | `av1_nvenc` | **199.7** | **3.33×** | 5.55 | 1.57 | 1.21 | 2.73 | 147 |
| av1 best | `av1_nvenc` | 201.3 | 3.36× | 5.55 | 1.53 | 1.22 | 2.77 | 223 |
| x264 standard | `libx264` | 194.4 | 3.24× | 5.70 | 1.62 | 1.23 | 2.79 | **93** |
| **h264 standard** | `h264_nvenc` | **224.5** | **3.74×** | 3.11 | 1.55 | 1.21 | **0.13** | 184 |
| old default (again) | `libvpx-vp9` untuned | 99.9 | 1.66× | 9.54 | 1.73 | 1.86 | 5.65 | 131 |

The two `old-default` runs agree to 0.1 fps, so the spread here is real signal.

**What survives.**

- **The VP9 defect and its repair are exactly as claimed.** 99.8 → 184.0 fps, **1.84×**, on the CPU
  alone (the plan claimed 1.76×). No GPU-less user is slower than before; every one of them is nearly
  twice as fast, and even `best` (124.1) beats the old default.
- **`av1_nvenc` is 2.00× the old default** (99.8 → 199.7), and `h264_nvenc` is **2.25×**. The plan's
  2.5–2.8× is the same finding measured from a lower base.
- **"ffmpeg steals from the renderer" reproduces**, smaller: render 1.74 → 1.57 (−10 %) and readback
  1.82 → 1.21 (−33 %) moving from untuned libvpx to `av1_nvenc`.
- **Hardware costs bits**, as claimed: `libx264` standard spends 93 kbps where `av1_nvenc` spends 147
  for a comparable picture.

**What does not.**

- **The absolute end-to-end multiple is a property of the renderer, not of this phase.** With the
  radar drawing, P2 moves nothing; the "45 min → 16 min" arithmetic needs the radar blit fixed first.
  That is renderer work, which §8.3 already identifies as the next phase: it is simply a
  *precondition* for the headline rather than a consequence of it.
- **`av1_nvenc` standard leaves ~12 % on the table against `h264_nvenc` standard** (199.7 vs 224.5
  fps) and shows a 2.73 ms encode stage where h264 shows 0.13. `-rc-lookahead 8 -bf 3` makes NVENC
  hold frames before it emits any, and a capacity-4 channel is narrower than that look-ahead, so the
  render loop waits on the reorder delay. Not a defect (the file is smaller and cleaner for it), but
  a capacity worth revisiting if the renderer ever stops being the bottleneck.

**Visual check, independently.** Frames pulled at t = 20 s from `av1_nvenc` draft/standard/best and
`libvpx-vp9` standard, cropped to the marker cluster and magnified 4× nearest-neighbour: `RS`, `BA`,
`YU`, `NE`, `ÅN` and the `♥B` bomb-carrier glyph are legible on all four, diacritic included. Draft
shows mild ringing around the marker discs; standard and best are clean. **`standard` meets the
"decent bitrate" bar.**

**Container check, independently.** `ffmpeg -v error -f null -` over the complete outputs: zero
errors. `ffprobe`: `av1 / Main / yuv420p / 1280x720 / 60-1` in `matroska,webm`; `vp9 / Profile 0` in
`matroska,webm`; `h264 / Main / nb_frames=6843` in `mov,mp4`. AV1-in-WebM is a real WebM.

### 8.7 How much the CS2 contention actually cost

One calibration point makes the §8.1 caveat quantitative. The `old-default` run exists twice: once on
the quiet machine before CS2 was launched, and once inside the matrix with it running.

| | quiet | with CS2 | delta |
|---|---:|---:|---:|
| old default (`libvpx-vp9`, all CPU) | 71.4 fps / 1.20× | 68.6 fps / 1.10× | **−3.9 %** |

So **the software rows are barely affected**: CS2 takes about 3.7 of 32 logical cores and the export
does not miss them. Every software comparison in §8.2, including the 1.76× that the
`-deadline`/`-cpu-used` fix is worth, is therefore solid.

The hardware rows are the uncertain ones, and the uncertainty has a floor rather than being open-ended:
this pipeline needs roughly **180 encoded frames per second**, and a *contended* NVENC still delivered
that (the encode stage measured 0.15–0.18 ms p50 throughout). The export is render-bound in every
hardware row (render + readback are 84 % of the frame), so a quiet GPU cannot move the total by much
more than the render and readback stages themselves would gain from having CS2's cores back. The
honest statement is: **2.5–2.8× realtime measured under contention, and a quiet machine is not slower
than that.**

### 8.8 The probe's own cost

`encoder_probe_ms` measures the whole ladder walk. `--encoder auto` on this machine: **1 303 ms** the
first time (one `ffmpeg -encoders` listing plus one two-frame test encode, both cached afterwards);
`--encoder software`: **1.6 ms**, because it probes nothing. Against a 40-second export that is 3 %
paid once, and against the several-minute full-match export it is meant for, nothing. On a GPU-less
runner the walk is one listing plus zero test encodes, because software rungs are trusted from it.

---

## 9. Deviations from the surrounding plans

1. **`EncoderProbeResult` carries no duration.** The design sketched one. `BannedApiTests` bans
   `System.Diagnostics.Stopwatch` in Pipeline outside `…Benchmarking` and `…Export`, and the right
   response to a determinism gate catching a diagnostic clock is to remove the clock, not to widen the
   exemption. Its own doc comment says the exemptions are narrow on purpose. The whole ladder walk is
   timed by the front end instead and reported as `encoder_probe_ms`, which is the number a user wants
   anyway.
2. **`FfmpegSinkOptions.Crf` and `.H264Preset` are removed rather than deprecated.** With a ladder they
   are a second way to say the same thing, and the losing one would drift. Three call sites moved.
3. **The plan said "`av1_qsv` → `av1_amf`" for WebM; both shipped, neither verified on this hardware.**
   `av1_qsv` fails MFX session creation (no Intel device) and `av1_amf` fails component creation (the
   Radeon iGPU has no AV1 block). `h264_amf` *does* verify here, so the AMF rung is exercised on the
   MP4 ladder at least. This is the case the probe exists for, and shipping the rungs behind it is
   safe by construction.
4. **`EncoderUnavailableException` maps to exit 6, not 3.** It joins `--gpu` and `--layout single`:
   nothing about the request is wrong, the machine cannot answer it. A CI lane treating exit 3 as
   "the change is broken" must not see a missing driver as one.
5. **Measured under GPU contention.** See §8.1. The pre-P2 baseline binary was built from a detached
   `git worktree` under the scratchpad purely so the A/B ran back-to-back on one machine state; the
   worktree is removed afterwards and nothing was developed in it.
