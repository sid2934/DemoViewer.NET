# Global Demo-Processing Queue

Shipped — merged to `main` 2026-07-20. The design of record for the queue
abstraction, the `HeavyJobGate` rework, the settings, the two consumer migrations, and the
UI. Correctness of the concurrency primitive is paramount — a race or a lost interactive-await is
worse than any missing UI polish. The default behaviour is byte-for-byte identical to today
(one heavy parse at a time; background yields to interactive and reel).

---

## 1. Problem & goals

Today three call sites each own an ad-hoc parse loop, all coordinating through the machine-wide
`HeavyJobGate` (`SemaphoreSlim(1,1)` — the 16 GB one-heavy-parse-at-a-time OOM invariant):

- **Library tier-2** (`DemoLibraryService.RescanAsync`): a `foreach` over `needFull`, each demo
  parsed under `AcquireBackgroundAsync`, newest-first, extracting players/duration/score and
  firing the `Tier2DemoParsed` piggyback.
- **Highlights backfill** (`HighlightScanService.BackfillLoopAsync`): a single loop draining the
  durable `HighlightsCacheStore` Pending rows under the same gate, newest-first, with forced
  (manual) paths draining regardless of the background-scan opt-in.
- **Interactive open** (`MainViewModel.LoadDemoFromBytesAsync`): parses the in-hand bytes under
  `AcquireInteractiveAsync` (preempts background) and returns the `ParsedDemo` in-process.

Problems this creates:

1. No single place to see/manage what background work is queued.
2. A **real double-parse risk**: Library tier-2 and the Highlights backfill can each parse the same
   demo. Today only the Library→Highlights direction is coalesced (the `Tier2DemoParsed` piggyback,
   and only when the background-scan opt-in is on); the reverse re-parses a multi-GB demo the other consumer is
   already handling.
3. Each consumer re-implements newest-first ordering, gate yielding, and lifecycle.

### Goals

- A **globally-accessible** demo-processing queue — the single source all background parse/analyse
  work is pulled from.
- **Priority** = how soon the user wants the results. **Opening a specific demo = HIGHEST** and must
  be **awaitable** (the caller gets that demo's `ParsedDemo` back, not fire-and-forget).
- Any **module** can add a demo and remove demos **it** added. The **user** (UI) can remove **any**
  item.
- **Settings**: max demos in the queue; max demos processed at once; pause/resume background; fully
  disable background.

### Hard constraints

- **Safety**: max-concurrent defaults to **1** and the queue must NEVER run more concurrent heavy
  parses than is memory-safe. Values > 1 are advanced/opt-in and risk RAM exhaustion (§9).
- Preserve **interactive-preemption** and **reel-exclusivity**.
- Don't touch the four protected parser files.

---

## 2. Architecture at a glance

```
                         ┌─────────────────────────────────────────────┐
  interactive open  ───► │  RequestForegroundAsync(path?, bytes)        │  awaitable → ParsedDemo
  (MainViewModel)        │    • parses in-hand bytes under the          │  (fast-path; NOT the pump)
                         │      INTERACTIVE gate slot (preempts bg,     │
                         │      refuses during reel)                    │
                         │    • best-effort coalesce onto an in-flight  │
                         │      item for the same path                  │
                         └─────────────────────────────────────────────┘
  Library tier-2   ──┐
  (module "library") │   SubmitBackground(request)   ┌───────────────────────────────┐
                     ├──────────────────────────────►│  DemoProcessingQueue           │
  Highlights backfill┘   coalesced BY PATH           │   • priority-ordered item set  │
  (module "highlights")  (one parse, N processors)   │   • up to maxConcurrency        │
                                                      │     background WORKER loops    │
                                                      │   • each worker acquires the   │
                                                      │     gate per demo (yields      │
                                                      │     between demos), picks the  │
                                                      │     highest-priority item      │
                                                      └───────────────┬───────────────┘
                                                                      │ acquires
                                                                      ▼
                                        ┌───────────────────────────────────────────────┐
                                        │  HeavyJobGate (resized, poll-based)            │
                                        │   _held < _maxConcurrency  (default 1)         │
                                        │   _interactivePending  → background yields     │
                                        │   _reelSessions        → all yield; reel drains│
                                        └───────────────────────────────────────────────┘
```

Two authorities, one live number:

- **The queue's pump** is the PRIMARY limiter (starts ≤ `maxConcurrency` worker loops, owns
  ordering/coalescing/size/pause). Removal and reprioritisation stay clean because it never
  over-commits.
- **`HeavyJobGate` is the hard SAFETY BACKSTOP** — it cannot be exceeded even if the pump miscounts.
  Both read the same live `maxConcurrency`, so at equal values no started worker ever blocks in the
  gate beyond the intended interactive/reel yield; but a pump bug can never OOM the machine.

---

## 3. `HeavyJobGate` — the resized concurrency primitive

`HeavyJobGate` is **not** a protected file, so its internals are rewritten; its **public method
names are preserved** (`AcquireInteractiveAsync`, `AcquireBackgroundAsync`, `EnterReelSessionAsync`,
`IsReelActive`, `IsInteractivePending`) so `ReelJobService`, `UiCapture`, and existing tests are
untouched. Two additions: `int MaxConcurrency { get; set; }` and a `_held` counter.

### Why not a resizable semaphore

A `SemaphoreSlim` cannot change its permit count; the "release-to-grow / absorb-on-release-to-shrink"
accounting needed to fake it is exactly the hand-rolled-semaphore code that is more likely to hide a
race than the race it replaces. We don't need it: resize is **apply-forward** (a max change affects
newly-started parses, not ones already running). Given that, concurrency is a plain integer compared
under the one lock the gate already holds.

### Design (all-await; WASM-safe — no `SemaphoreSlim.Wait`, no `.Result`)

State, all guarded by one `_sync` lock: `int _maxConcurrency = 1`, `int _held`,
`int _interactivePending`, `int _reelSessions`. A 100 ms poll interval (unchanged from today).

- **`AcquireBackgroundAsync`**: loop — under the lock, if
  `_reelSessions == 0 && _interactivePending == 0 && _held < _maxConcurrency` then `_held++` and
  return the releaser; else `await Task.Delay(100, ct)` and retry.
- **`AcquireInteractiveAsync`**: under the lock, throw `ReelInProgressException` if a reel is active;
  else `_interactivePending++` (this is what makes background yield). Then loop — if a reel appeared,
  decrement pending and throw; if `_held < _maxConcurrency`, `_held++`, decrement pending, return the
  releaser. On cancellation, decrement pending. (Pending is held only while WAITING; once the slot is
  held, background may use *other* slots when `maxConcurrency > 1`.)
- **`EnterReelSessionAsync`**: `_reelSessions++`, then poll until `_held == 0` (drain every in-flight
  parse — CS2+OBS must never overlap a multi-GB parse). Releaser decrements `_reelSessions`.
- **Releaser**: `_held--` (idempotent; `Interlocked.Exchange` disposed-guard as today).
- **`MaxConcurrency` setter**: `Math.Clamp(n, 1, 8)` under the lock. Grow → the next background poll
  cycles admit more workers. Shrink while N run → they drain naturally; no new start until
  `_held < n`. Shrink while idle → immediate. No accounting, trivially correct.

At `maxConcurrency == 1` every path is behaviourally identical to today's `SemaphoreSlim(1,1)` gate:
one holder at a time; background yields to a pending interactive between demos; reel drains the one
holder and refuses interactive.

HardCap = 8 (nobody should run 8 concurrent multi-GB parses; it is headroom for a hypothetical big
machine, not a recommendation — see §9).

---

## 4. The queue abstraction

Lives in `src/App/DemoViewer.NET/Services/DemoProcessing/` (shared App project, so it must COMPILE
for WASM; it never assumes ASP.NET or a physical file, and never blocks a thread). UI mutations are
marshalled through an injected `Action<Action> post` delegate (the dispatcher in-app, inline in
tests) exactly like `DemoLibraryService`/`HighlightScanService`.

### Item model

```csharp
public enum DemoJobPriority { Background = 0, UserRequested = 1, Foreground = 2 } // higher = sooner

public sealed record DemoProcessingRequest(
    string Path,                    // coalescing key + file to read
    string OwnerTag,                // module identity: per-owner removal + display ("library"/"highlights")
    DemoJobPriority Priority,       // Background (auto), UserRequested (manual/forced)
    long OrderHint,                 // within-tier ordering, higher = sooner (mtime ticks ⇒ newest-first)
    Action<ParsedDemo> OnParsed,    // runs INSIDE the gate slot after a successful parse
    Action<Exception>? OnFailed = null,
    string? DisplayName = null);
```

`OnParsed` runs while the ParsedDemo is still held and the gate slot is still owned — this is the
memory-safety contract: the heavy post-processing (Library's entity-replay score extraction,
Highlights' bare analysis eval) must not run while another parse could start. Multiple coalesced
owners' `OnParsed` handlers run sequentially on the one `ParsedDemo`.

### Queue state (per item, observable to the UI)

`DemoQueueItem`: `Id (Guid)`, `Path`, `DisplayName`, `Owners (set)`, `Priority`, `OrderHint`,
`State ∈ {Queued, Running, Completed, Failed, Cancelled, Rejected}`, `error?`. Exposed as
`ReadOnlyObservableCollection<DemoQueueItem>` (`Items`) mutated on the post thread.

### API

```csharp
public interface IDemoProcessingQueue
{
    // FOREGROUND — awaitable, highest priority, bypasses pause/disable/size-cap BY CONSTRUCTION.
    // Parses `bytes` (the caller's in-hand bytes — NOT a re-read) under the interactive gate slot.
    // Best-effort: if a non-null `path` matches an in-flight item, awaits THAT parse instead.
    Task<ParsedDemo> RequestForegroundAsync(string? path, ReadOnlyMemory<byte> bytes, CancellationToken ct = default);

    // BACKGROUND — fire-and-forget, coalesced by path across owners. Rejected (handle.State==Rejected)
    // only when the background tier is at MaxQueueSize AND this path is not already queued/running.
    IDemoQueueHandle SubmitBackground(DemoProcessingRequest request);

    IReadOnlyList<DemoQueueItem> Items { get; }            // + INotifyCollectionChanged for binding
    event Action? Changed;                                  // any state change (posted)
    event Action? CapacityAvailable;                        // background tier dropped below cap (posted)

    void RemoveByUser(Guid itemId);                         // UI removes ANY item
    void CancelOwned(string ownerTag, string path);         // module cancels ITS OWN submission

    int MaxConcurrency { get; set; }                        // forwards to the gate
    int MaxQueueSize { get; set; }                          // background tier only
    bool BackgroundEnabled { get; set; }                    // master disable (persisted)
    bool IsPaused { get; }                                  // transient
    void Pause();
    void Resume();
}
```

`IDemoQueueHandle`: `Guid Id`, `DemoQueueItemState State`, `Task Completion`, `void Cancel()`.

---

## 5. Priority & ordering

Items are ordered by `(Priority desc, OrderHint desc, EnqueueSeq asc)`:

- **Foreground** never enters the background pump — it is the `RequestForegroundAsync` fast-path
  (interactive gate slot). It is "highest priority" by construction: the interactive slot preempts
  background between demos, and (best-effort) coalesces onto an in-flight parse of the same path.
- **UserRequested** (a manual/forced Highlights rescan click) outranks Background — a retry on one
  demo is never starved behind the auto-scan backlog. It also **bypasses the size cap** (a user
  action is never rejected because auto-scan filled the queue).
- **Background** (Library tier-2, Highlights opt-in backfill): `OrderHint = file mtime ticks` gives
  the existing **newest-first** drain — the demos a user most likely cares about first.

Coalescing bumps an item's `Priority`/`OrderHint` to the max seen across its owners, so a later
higher-priority request reprioritises an already-queued item.

---

## 6. Coalescing / dedup (fixes the double-parse bug)

The queue keys work items on **path**. `SubmitBackground(X)` when an item for `X` already exists
(Queued *or* Running) attaches the new request's `OnParsed`/`OnFailed`/`OwnerTag` to that item and
bumps its priority/order — one parse, every owner's post-processing. This **dissolves the stated
double-parse risk**: Library tier-2 and the Highlights backfill submitting the same demo now share a
single parse, in both directions (today's `Tier2DemoParsed` piggyback only covered
Library→Highlights, opt-in on).

**Assumption (explicit decision):** coalescing keys on path ⇒ *same path = same content*. The shared
background parse reads the **file** at that path. The interactive fast-path instead parses the
caller's **in-hand bytes** (the picker already copied them from storage; the library reads the local
path) — identical content for the same file, and it avoids a wasteful multi-GB re-read. If a
non-null path matches an in-flight item, the fast-path reuses that item's result (best-effort); the
rare mismatch (a file replaced in place between submit and parse) is no worse than today, where the
same stale-vs-fresh race already exists per consumer.

---

## 7. Concurrency model — the pump

The queue runs **up to `maxConcurrency` background worker loops** (default 1 ⇒ a single loop,
identical to today). Each worker:

```
while (not disposed):
    using (await gate.AcquireBackgroundAsync(ct)):     # poll-yields to interactive/reel; between demos
        item = under-lock: pick highest-priority Queued item, honoring pause/BackgroundEnabled; mark Running
        if item is null: break                          # no work → worker exits (respawned on next submit)
        try:    parsed = DemoParser.Parse(File.ReadAllBytes(item.Path));  run item.OnParsed handlers
        catch:  run item.OnFailed handlers; mark Failed
        under-lock: mark Completed/Cancelled; fire Changed + CapacityAvailable
    # loop re-acquires the gate for the NEXT demo → yields between demos, exactly like today
```

- **Acquire-then-pick**: the worker selects the item only after it owns the slot, so priority
  ordering is always current (a higher-priority item that arrived while the worker was blocked wins).
- **Worker count** = `maxConcurrency`. The pump (re-run on submit / completion / resume /
  max-increase) tops up workers to `min(pendingBackgroundCount, maxConcurrency)`. This is the proven
  `EnsureBackfillRunning` respawn pattern generalised to N workers.
- **Pause / disable**: a worker that finds `IsPaused || !BackgroundEnabled` picks nothing and exits;
  the pump won't respawn until resumed/enabled. In-flight parses finish (never abort a multi-GB parse
  mid-way). **Foreground is unaffected** — it doesn't go through the pump.
- **Reel**: `AcquireBackgroundAsync` yields while a reel session is active; the worker simply waits.

### Foreground fast-path (never coupled to pump health)

```
RequestForegroundAsync(path, bytes, ct):
    if path != null and an item for path is currently Running:
        attach a foreground waiter (TCS) to it; return its parse result (reuse)   # best-effort
    using (await gate.AcquireInteractiveAsync(ct)):        # preempts background; throws ReelInProgress during reel
        return await Task.Run(() => DemoParser.Parse(bytes.Span/Memory))
```

The discriminating guarantee: **with Pause AND Disable both on and the background tier full,
`RequestForegroundAsync(X)` still returns X's `ParsedDemo` promptly** (nothing is running ⇒ the
interactive slot is free ⇒ direct parse). Foreground correctness never depends on the pump.

---

## 8. Max queue size

`MaxQueueSize` bounds the **background tier only** (Queued+Running background/UserRequested-that-came-
from-auto items). Overflow policy: **reject** the new background submit (`handle.State == Rejected`);
the durable backlogs (library.json cache, `HighlightsCacheStore`) remain the source of truth. When
the tier drops below cap (a completion or a user removal), the queue fires `CapacityAvailable`; each
consumer's "enqueue pending" pass is **idempotent** (coalescing makes an already-queued path a no-op)
and re-runs on that event to top the queue back up. Foreground and UserRequested **bypass** the cap.

Correctness test: `MaxQueueSize = 2`, submit 5 background ⇒ all 5 eventually process,
`gate._held` never exceeds `maxConcurrency`, `CapacityAvailable` drives the refeed.

Default `MaxQueueSize = 200` (comfortably above a typical library working set; the cap is a
resource/clutter guard, not a routine limiter).

---

## 9. Settings

New `ProcessingQueueSettings` section on `AppSettings`:

```csharp
public sealed class ProcessingQueueSettings
{
    public bool BackgroundProcessingEnabled { get; set; } = true; // master disable
    public int  MaxQueueSize   { get; set; } = 200;               // background tier cap
    public int  MaxConcurrency { get; set; } = 1;                 // DEFAULT 1 — SAFETY (see below)
}
```

- **`MaxConcurrency` defaults to 1** and the UI clearly warns that > 1 can exhaust RAM: two
  concurrent multi-GB parses OOM a 16 GB machine (the whole reason `HeavyJobGate` exists). The gate
  clamps to `[1, 8]` and is the hard backstop. This is the one open decision (§14):
  keep > 1 developer-only / effectively unavailable until validated on larger hardware.
- **Pause** is a transient runtime toggle (a Pause/Resume button), NOT persisted — the app always
  starts un-paused. **Disable** (`BackgroundProcessingEnabled = false`) is the persisted master off.
- **WASM**: `ProcessingQueueSettings` is flattened in `SettingsService.WriteInMemory` (three scalar
  keys), because the Settings surface is WASM-reachable — an unflattened section would silently drop
  a browser write on reload. (Contrast `LiveSync`/`Highlights`, excluded there because no browser
  path writes them.)

`SettingsService.Write` merges the new preference keys through its existing JSON-node merge (Session
/ Recents preserved). No change to the write contract.

---

## 10. Consumer migrations

Both consumers **stop owning their drain loop** and instead submit work items; the queue owns the
workers. Externally-observable behaviour (newest-first, forced/manual scans, opt-in gating,
staleness, the piggyback) is preserved.

### Library tier-2 (`DemoLibraryService`)

- `RescanAsync` no longer parses in a `foreach`; for each `needFull` entry it calls
  `queue.SubmitBackground(new DemoProcessingRequest(path, "library", Background, mtimeTicks,
  OnParsed: parsed => IndexTier2Core(entry, parsed), OnFailed: _ => MarkTier2Failed(entry)))`.
- `IndexTier2` is split: the parse moves into the queue; `IndexTier2Core(entry, parsed)` keeps the
  extraction + `_post` UI updates + `ExtractFinalScore` + the `Tier2DemoParsed` piggyback + cache
  upsert, all running inside the slot (unchanged behaviour, minus the now-queue-owned parse).
- On `CapacityAvailable`, re-submit any not-yet-Indexed `needFull` (idempotent via coalescing).
- The `HeavyJobGate` dependency is replaced by the queue; the null-gate legacy/test path becomes a
  null-queue path (parse inline, exactly as the null-gate path does today).

### Highlights backfill (`HighlightScanService`)

- `BackfillLoopAsync` is deleted. `EnsureBackfillRunning` becomes "enqueue Pending rows":
  each Pending row → `SubmitBackground(path, "highlights", Background | UserRequested, mtimeTicks,
  OnParsed: parsed => UpsertRow(ProcessParsed(path, parsed)), OnFailed: _ => MarkFailed(path))`.
- **Forced/manual** paths (`RequestScan`, `RescanAll`) submit at `UserRequested` priority (outrank
  auto, bypass the size cap) and drain **regardless of the background-scan opt-in** — the auto rows are submitted
  only while `backgroundScanEnabled()` holds, preserving "opt-in off ⇒ only forced paths drain".
- `ProcessDemo` splits into the parse (queue-owned) + `ProcessParsed(path, parsed)` (SHA + bare eval
  + row build). The gate-wait re-check ("did the piggyback refresh this row while we waited?") moves
  into `OnParsed` as a cheap "still Pending?" guard.
- The `Tier2DemoParsed` piggyback stays as-is (Library still fires it inside `OnParsed`); it remains
  the zero-extra-parse fast path when the Library already holds the demo.

The **durable stores stay the source of truth** for the full backlog; the queue holds the bounded
working set and is topped up via `CapacityAvailable`.

---

## 11. Removal & cancellation

- **User (UI) `RemoveByUser(id)`**: Queued → removed. Running → marked `Cancelling`; the parse
  completes (not abortable), its result is discarded, no `OnParsed` runs, state → `Cancelled`, slot
  freed. Any item, any owner.
- **Module `CancelOwned(owner, path)`**: drops that owner's attachment. If attachments remain (a
  coalesced co-owner still wants it), the item survives. If none remain and it is Queued → removed;
  Running → best-effort (let it finish; skip the cancelled owner's `OnParsed`).
- A completed/failed item lingers briefly in `Items` for UI feedback, then is pruned (a capped
  history, oldest-terminal first).

---

## 12. UI

A queue-management surface (feature-gated via `FeatureCatalog`, not ad-hoc `IsVisible`):

- A **list of items**: display name / map, owner chip(s), priority, state (Queued/Running/…), a
  per-item **remove** (✕) button (calls `RemoveByUser`).
- **Pause/Resume** toggle (transient) and a **Disable background processing** switch (persisted).
- **Settings**: max queue size, max concurrent (with the RAM-risk warning on > 1), surfaced through
  the Settings screen and/or the queue surface.
- Bound to `Items` (`ReadOnlyObservableCollection`) + a status line (running / queued counts,
  paused/disabled). Placement (Diagnostics vs a dedicated surface), gating defaults, and visual
  design are the UI design's call, not this doc's.

---

## 13. Test plan (core, before any UI)

**Gate (`HeavyJobGateTests`) — green FIRST:** max=1 identical-to-today (one holder; background
yields to a pending interactive; reel drains the holder + refuses interactive); resize up (grow
admits more) and down (shrink drains, live and idle); reel drain waits for all N holders;
cancellation decrements pending.

**Queue (`DemoProcessingQueueTests`):** priority ordering (Foreground > UserRequested > Background;
newest-first within Background); max-queue-size reject + `CapacityAvailable` refeed (cap=2, submit 5
⇒ all process, concurrency never exceeded); max-concurrency honoured (max=1 never 2 in flight; and a
sanity max=2 case); pause/resume (in-flight finishes, no new starts, resume continues); disable
(no background, foreground still runs); per-owner removal (co-owner survives coalescing); user
removal of any item; **dedup/coalesce** (two owners, one parse, both `OnParsed` run);
**interactive-await returns the actual `ParsedDemo`** and the discriminating pause+disable+full
guarantee; cancellation.

**Consumers:** the existing `HighlightScanServiceTests` / `DemoLibraryServiceTests` adapted only
where behaviour genuinely moved onto the queue; newest-first, forced-only-when-opt-in-off, and
failure-marks-only-that-row preserved.

---

## 14. Open decision

**Allowing `MaxConcurrency > 1.**` It is implemented and clamped `[1,8]`, defaults to 1, and is
warned as risky. On a 16 GB machine it is effectively unsafe (two multi-GB parses OOM). Recommend
keeping it developer-gated / effectively unavailable until validated on larger hardware. Everything
else keeps today's behaviour exactly.
