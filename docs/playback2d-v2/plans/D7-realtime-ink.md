# D7: real-time ink replay

**Design authority:** this document · **Registry:** [`D0-ux-pass-overview.md`](D0-ux-pass-overview.md) ·
**Branch:** `feature/playback2d-v2` · **Status:** in flight · **Estimate:** 0.5 wk (phase 1).

A stroke authored in `RealTime` mode **replays at the cadence it was drawn at** and then dissolves
behind itself: the head advances at the original speed, including the pauses where the author
stopped to think, and each section runs the element's own fade trapezoid shifted by the offset at
which it was drawn.

For scale: B2, the entire annotation subsystem, was 2.5 wk. Four of the five things that look hard
here are already solved in the code.

---

## 1. The decision that shapes everything

**"The tick a section was drawn at" is not a well-defined quantity, and anchoring to it cannot work.**

`IToolServices.CurrentTick` is the playhead, `host.CurrentSceneFrame.Time.Tick`
(`SceneHostToolServices.cs:32`), and `CurrentFrame` is rebuilt only when playback pushes a frame or
a seek lands. **While paused it is frozen for the whole gesture**, which is when most annotation
happens: every sample of a paused stroke would share one tick and the replay would be instantaneous.
It is worse than that. Even while playing, at `--speed 0.5` the hand moves at 1× while the clock
moves at 0.5×.

So a sample's time is **elapsed authoring wall-clock, re-based at `Time.FromTick`**. The consequence
is real and is accepted: a stroke drawn during three seconds of paused thinking replays over three
seconds of *demo* time. In exchange the replay is a pure function of tick, which is what keeps the
export determinism gate green (§5).

`BannedApiTests` bans `DateTime` / `Stopwatch` / `Random` in Core. The clock therefore arrives
through `IToolServices`, which is exactly the seam that exists for this.

---

## 2. The shared model: **already landed, do not redesign**

In `Core/Annotations/AnnotationElement.cs`:

```csharp
public enum EnvelopeMode { Always, Fade, Custom, RealTime }          // RealTime is new

public readonly record struct TimingRun(int SampleIndex, int TickOffset);

public sealed record StrokeTiming(IReadOnlyList<TimingRun> Runs, int DurationTicks)
{
    public static readonly StrokeTiming Instant;
    public int TickOffsetForSample(int sampleIndex);            // interpolates inside a run
    public int RevealedCount(int elapsedTicks, int sampleCount); // monotone + continuous in tick
}

public sealed record AnnotationElement(..., string? Text, StrokeTiming? Timing = null);
```

**Why a sparse run table, not a stamp per point.** A boundary is emitted only where the authoring
speed changed, so a continuous stroke carries two entries and one with three pauses carries eight.
Measured on a 1200-world-unit stroke: **+0.9 %** of the persisted document, against **+26 %** for a
fourth float on every `InkPoint`. It is also the better encoding: what a viewer reads as "it is
replaying me" is the **pauses**, and speed variation inside one continuous motion is invisible at
64 Hz through a fading tail.

`Timing` is trailing and nullable so every existing positional construction site compiles, and so the
DTO's `WhenWritingNull` leaves the persisted v1 schema byte-identical. It is in `Equals` **and**
`GetHashCode` because that comparison is what persistence uses to prove a round trip. A cadence the
writer dropped would otherwise pass every save/load test silently.

---

## 3. Per-section fade is the existing trapezoid, shifted

The user's ask, *"each section fade after a set time (how fade currently works)"*, is exactly:

```
opacityOfSection(i, tick) = element.Time.OpacityAt(tick - Timing.TickOffsetForSample(i))
```

`TimeEnvelope.OpacityAt` (`AnnotationElement.cs`) is already pure, already scrub-safe, and already
overflow-guarded. So **`HoldTicks` keeps its meaning per section**: if the hold outlasts the draw the
whole stroke is visible at once and then dissolves from the start; if it does not, the stroke chases
its own tail. Both are useful and the same control produces them.

---

## 4. Rendering: measured, and affordable

The visible stroke is always a **contiguous window**: indices above the head are not drawn yet,
indices below the tail have fully faded, and the alpha ramp lives only at the older end. So the draw
is **one full-alpha body + k≈6–8 short tail-ramp draws, independent of stroke length**.

Measured on a 1920×1080 CPU surface, one stroke, antialiased fill:

| N samples | k=1 | k=8 | k=64 |
|---|---|---|---|
| 400 | 117 µs | **152 µs** | 316 µs |
| 1600 | 144 µs | 180 µs | 458 µs |

**+35 µs/frame** for the realistic case, against a current full-scene p99 of 2.75 ms and a design
baseline of 8 ms. **Allocation measured at 0 B/frame at k = 1, 8 and 64**. The `[Category("Budget")]`
zero-allocation gate is safe.

Rejected, with reasons: a **gradient shader** is linear in space, not arc length, so a stroke that
doubles back fades wrongly. **Per-vertex colours** are the right answer in the abstract but need a
paired left/right ribbon, which `FreehandOutline` cannot give: `left` and `right` are filtered
independently by the min-distance test so they have different lengths, and
`FreehandOutlineTests.Matches_ReferenceVector` pins exact vertex counts against upstream v1.2.2. That
is phase 2, as a *second* emitter beside `GetStrokeOutline`, never a change to it. **Splitting into
sub-elements** multiplies erase hits, timeline markers, undo entries and document size by N.

Caching costs nothing: a time-anchored element already skips `RecordDry`
(`AnnotationLayer.cs:182`) and the layer is `LayerCacheHint.Dynamic`, so a `Fade` element is outside
every cache today.

---

## 5. Export determinism: free, with two ways to lose it

The export iterates source frames 1:1 and `fps`/`speed` change only *which* ticks are sampled
(`ticksPerOutputFrame = speed * rate / fps`). **Any `f(tick)` is therefore correct by construction**:
same demo tick at 30/60/64 fps, and `--speed 2` replays twice as fast, which is what the flag means.

Two ways to break it, both forbidden here:

1. **Accumulating `DeltaSeconds`** instead of reading `Tick`. 30 fps and 64 fps then diverge. The
   layer holds no replay state.
2. **A discrete one-tick pulse.** At 30 fps `ticksPerOutputFrame ≈ 2.13`, so ticks are *skipped* and
   a section visible for exactly one tick can be missed entirely. `RevealedCount` is monotone and
   continuous in the tick for this reason.

Do **not** phase on `SceneTime.DemoSeconds`: it is computed relative to the export's own start frame,
so it is not a stable absolute.

---

## 6. Workstreams

Three parallel workstreams, file-disjoint, all against the model in §2 which is already committed.

| | Owns | Delivers |
|---|---|---|
| **D7a** capture | `Core/Input/**`, `Core/Annotations/AnnotationSession.cs`, `Scene2DHost.cs`, `Annotations/SceneHostToolServices.cs` | A monotonic clock on `IToolServices`; pointer-event timestamps plumbed onto `ToolPointerEvent`; `DrawTool` accumulating a run table and committing `StrokeTiming` |
| **D7b** replay | `Core/Layers/AnnotationLayer.cs`, `SceneStage.cs`, `BudgetTests.cs` | `RevealCount` generalised to a run-table lookup; per-section alpha rendering; **the ink budget fixture** |
| **D7c** persistence + UI | `Pipeline/Annotations/**`, `AnnotationsPanelViewModel.cs`, `AnnotationToolbar.axaml`, `AnnotationSessionController.cs`, `Configuration/**`, `annotations-format.md` | v2 DTO field; `EnvelopeMode.RealTime` in the toolbar; the settings key; the format spec |

---

## 7. The gap this closes on the way past

**The 8 ms render budget has never seen ink.** `BudgetTests` builds `SceneStage` with no `extra`
layers and `SceneStage`'s fixed seven do not include the annotation layer: it takes a session, so it
can only arrive via `extra`. `AnnotationLayerTests.SteadyState_ZeroAllocations` measures allocation
only, on **3-sample** strokes.

So the timing gate has zero annotation coverage today. D7b adds a real-stroke ink fixture to
`BudgetTests` regardless of what the rest of this plan does.

---

## 8. What must stay green

| Gate | Why it is at risk |
|---|---|
| `AnnotationSchemaSnapshotTests.V1Schema_MatchesCheckedInSample` | Green **iff** the DTO field is nullable; `WhenWritingNull` then emits nothing for a non-RealTime element |
| `AnnotationLayerTests.SteadyState_ZeroAllocations` | Measured safe at every k; do not allocate per section |
| `ExportDeterminismTests`, `SceneDeterminismTests`, CLI `RenderDeterminismTests` | Safe **iff** the reveal is `f(Tick)` with no accumulated state |
| `Playback2DSettingsConsumptionTests` | Every new settings key needs a production reader **and** writer: real UI, not a field |
| `FreehandOutlineTests.Matches_ReferenceVector` | Untouched **provided** phase 2's ribbon emitter is a new method |
| `AnnotationStoreTests` unknown-field preservation | The v2 field must survive a v1 build's re-save, which `[JsonExtensionData]` already gives |

---

## 9. Deferred to phase 2 (~1 wk), not in scope now

Per-vertex-colour ribbon emitter; timeline **spans** for a real-time stroke via the already-wired,
currently-empty `AnnotationTrack.BuildBands` seam; erase clamped to the revealed prefix (today a
half-drawn stroke is fully erasable, matching `RevealOnFadeIn`'s existing behaviour).

`RevealOnFadeIn` **survives**. It is a different feature (a linear sweep across the fade-in ramp),
it is in the published schema and the pinned sample, and removing it is a format break for no gain.
D7b *generalises* the code path it shares rather than adding a parallel one beside it.
