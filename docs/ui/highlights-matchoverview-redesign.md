# Match Overview as cache render · Highlights as reel dashboard · one unified demo cache

Shipped in v0.5.3 (2026-07-29) — all eight forks resolved and implemented. Kept as the design record
the Highlights/Match Overview code cites.
**Starting point:** Match Overview becomes a *cache render*; the demo caches
unify; Match Overview is explicitly open to redesign; cross-demo reels are a confirmed requirement.
**Companion docs:** [`design-system.md`](./design-system.md) (§4 layout patterns, §5 visibility matrix),
[`../csvg-integration/ux-design.md`](../csvg-integration/ux-design.md) (§1 platform, §2 gating, §6 Verify,
§7 Highlights, §8 reel dialog, §9 interlock), [`theme-token-catalog.md`](./theme-token-catalog.md).

---

## Decisions — these override any hedging below

All eight forks in §10 are resolved. Where the text below still reads as an open question, **this section
wins**.

| Fork | Resolution |
|---|---|
| **1 · cache storage shape** | **(a) Thin `index.json` + per-demo sidecars.** Not a monolith, not SQLite. |
| **2 · how T3 coverage grows** | **(a)+(c) Extend T2, retarget T3.** Tier-2 grows to carry rosters-with-teams, bot flags, steamIds, tick rate, tick count and rounds. The rules pass becomes a per-demo `Compute full stats` action; the library-wide background opt-in is retained. **Explicitly NOT (b)** — the rules pass does not become automatic. |
| **3 · MO body layout** | **(a) Two-column body at ≥1000px**, collapsing to one column below. |
| **4 · hero band** | **(a) Merged hero band** — identity + score + completeness chip + progress rail in one fixed-height slot. |
| **5 · tab header** | **(a) "Reels."** `TabId "highlights.browser"` and feature id `"tab.highlights"` are persisted keys and do **not** change. |
| **6 · single-click feedback** | **(a) `Overview ▸` chip** on the selected Library card. No `WorkspaceTabDescriptor` contract change. |
| **7 · WASM Highlights registration** | **(a) Keep registered + degraded**, rewritten copy. |
| **8 · tray persistence** | **(a) Persist** via `SnapshotState` as `HighlightKey`s, re-resolved on restore. |

**Scope: the full program ships in v0.5.3.** The unified cache, the T2 extension and the migration land
*together with* the UI work — not as a follow-on. §9.1's hand-off caveat ("§1 is a recommendation, not a
claim") is therefore **superseded**: the data-layer work is in scope for this release. §9.2's ordering is
revised accordingly in §9.3 below.

**Accepted consequence of fork 2.** Until a demo has been through `Compute full stats`, its Match Overview
shows map, server, rosters-with-teams, bot tags, duration, rounds, player and spectator counts, tick rate and
final score — and an **empty scoreboard and empty highlights section**, each carrying the one button that
fills it. On today's library that is ~99% of demos at first run. This was chosen with eyes open over making
the rules pass ambient.

**One correction to §1.5.** The monolith write-traffic figure is overstated: a 719-demo sweep against a
growing monolith is on the order of **~1 GB**, not 2.4 GB (the average file size during the sweep is about
half the final size). The argument for splitting storage is unaffected — a full deserialize on every app
start, and a full rewrite after every demo, are the real costs.

---

## 0. Recommendation in one page

| Move | Recommendation |
|---|---|
| **Unified demo cache** | One `DemoCacheStore` service replacing `library.json` + `highlights.json`, written as a **thin index + per-demo sidecars** and filled in **four tiers** (T0 identity → T1 header → T2 parse → T3 analysis) by the evaluators that already share one parse via `DemoEvaluationCoordinator.FanOutParsed`. Library card, Match Overview, and the Reels dashboard all **project** from it; none of them persists anything. |
| **Fill strategy** | **Extend T2 (the cheap pass, 80% real-world coverage) — do not promote T3 to automatic.** Roster-with-teams, bot flags, steamIds, tick rate, tick count and round boundaries are already sitting in `ParsedDemo` at T2 time and cost ~+0.5 KB/demo. Retarget T3 (the rules run) from a library-wide sweep to a **per-demo `Compute full stats` action** on Match Overview, with the existing background opt-in retained. |
| **Match Overview** | **Redesigned, not bolted onto.** Hero band merging identity + score + a **completeness chip**; a facts strip; a **two-column body** (the match: rosters + scoreboard · the moments: highlights + enrichments). Skeleton-first survives as a *principle* (reserved slots, nothing appears on load state) with new section metrics. |
| **Partial fill** | The completeness chip is the answer: `LIVE` / `FULL` / `INDEXED` / `NOT INDEXED` / `FAILED`, each with the one action that advances it. Missing slots carry a short honest message plus that same action — never a wall of `—`. |
| **Highlights tab** | Card grid dropped. The tab becomes: **clip tray** (multi-demo, ordered, provenance-bearing) + the **promoted reel-config body** + inline job status + an **enrichment slot**. Cross-demo assembly gets two explicit entry points. |
| **Library** | Not redesigned. One additive change: single-click selection in the card grid. |

**The load-bearing invariant, stated once:** *Match Overview never paints data belonging to a demo other than
its current subject.* Every fill path — live push and cached read alike — is keyed by demo identity and drops
mismatches. `SetAnalysis` already documents this bug class in its own comment; a second data source multiplies
it.

---

## 1. The unified demo cache

### 1.1 Measured baseline (the reference library, 2026-07)

| File | Size | Rows | Bytes/row | Fill |
|---|---|---|---|---|
| `library.json` | 561 KB | **719 demos** | ~780 (indented) | `FullyIndexed` **575/719 (80%)**; `ScoreComputed` 575/719 |
| `highlights.json` | 260 KB | **348 rows** | ~747 | `ScanState = Pending` for **346/348**. 2 rows have players, **0** have rounds. 267 events total, **226 of them in one demo**. |

**Read that second row carefully — it corrects an assumption this design could easily have been built on.**
Highlight scanning is opt-in (the "Scan my library" CTA) and has effectively never been run, so
`highlights.json` today is ~260 KB of *identity stubs*: path/size/mtime/sha/map with empty payloads. It covers
**0.6%** of the library while the Library tier-2 pass covers **80%**. Any design that sources Match Overview
fields from the highlights pass would produce a page that is empty for 99% of the user's library.

### 1.2 What each pass actually computes

| Pass | Work | Produces | Coverage today |
|---|---|---|---|
| **T1 header** | ~256 KB head read (`DownstreamUtilities.TryReadQuickInfo`) | map, server, demo version | high (cheap) |
| **T2 parse** (Library tier 2) | full parse + entity replay; score from `CCSTeam.m_iScore`. **No analysis-engine run.** | duration, players, final score, clans | **80%** |
| **T3 analysis** (Highlights scan) | `RulesHighlightHarvester.RunBareAnalysis(parsed)` — a full rules-engine run | highlights, and the same source `StatsTab.GameTable` uses: per-player K/D/A, ADR, rating, per-side split | **~0%** |

**Crucially, T2 and T3 already share one parse.** Both `HighlightScanService` and the Library indexer are
`IDemoEvaluator`s driven by `DemoEvaluationCoordinator.FanOutParsed`, and `IDemoEvaluator.Evaluate(path,
parsed)` runs *inside the queue's gate slot with the `ParsedDemo` still in memory* (its own doc comment). So
**"cache the Match Overview fields during the initial pass" requires no new parse — it rides a shared one.**
That is the strongest technical argument for the cache-render plan and it should be stated in the
implementation brief.

### 1.3 The finding that shapes everything: most of Match Overview is T2-cheap

Today's `DemoLibraryCacheEntry.Players` is `List<string>` — **names only**. That is a choice, not a cost.

**Verified against the parser, not inferred.** `PlayerInfo` (`src/Parser/DemoViewer.NET.Parser/PlayerInfo.cs`)
is `record PlayerInfo(int Slot, string Name, ulong SteamId64, int UserId, int Team, bool IsBot)` plus
`bool IsHltv` — i.e. **every field the T2 roster extension needs is already on the record the pass is
holding**, including the steam id and the userid that maps to game-event actors. `ParsedDemo` exposes
`TickCount` (`:163`) and derives `TickRate` (`:175`) and `Duration` (`:101`) from `TickInterval`. These are
the exact fields `MatchOverviewTabViewModel.SetSummary` already reads to build rosters, the player count and
the spectator count (`:349-395`).

**Consequence:** with a modest T2 extension, a cached Match Overview can render *everything except the
scoreboard and the per-side split* — rosters split by team, bot tags, correct player and spectator counts,
tick rate, duration, rounds, final score, clans — **for the 80% of the library that is already indexed.**
That is a dramatically better story than "cached mode has no rosters", and it is why the recommendation is to
extend T2 rather than to promote T3.

### 1.4 The unified record (design altitude — field list, not an implementation)

```
DemoCacheRecord
├─ T0  identity        path · size · modifiedTicks · sha256?            (free: stat + dedup hash)
├─ T1  header          map · server · demoVersion                       (~256 KB head read)
│      computedAt · schema
├─ T2  parse           duration · tickRate · tickCount · serverStartTick
│      ├─ players[]    slot · name · steamId64 · team · isBot           ← NEW (names-only today)
│      ├─ rounds[]     number · startTickFrameClock                     ← NEW here; today only in highlights.json
│      ├─ score        ctScore · tScore · ctClan · tClan
│      └─ computedAt · schema
└─ T3  analysis        (stamped with the rules fingerprint)
       ├─ scoreboard[] slot · kills · deaths · assists · adr · rating · ctw · tw
       ├─ sideSplit    ctSideWins · tSideWins · roundCount
       ├─ highlights[] rulesetId · highlightId · frameIndex · tick · playerSlot · roundNumber · renderedTitle
       ├─ profileName · configFingerprint · highlightHashes{}
       └─ computedAt · schema · state{Pending|Indexed|Failed}
```

Notes that matter:

- **Players are stored once, at T2, and referenced by `slot` from T3.** Today `CachedHighlight` re-stores
  `PlayerName` + `SteamId64` on every event row — 267 events × ~40 bytes of redundancy in a file that is
  mostly empty. Normalising on slot removes that and makes rename/team-swap handling coherent.
  (`RenderedTitle` still embeds the raw name; that is fine, it is a rendering artifact captured at emission.)
- **`rounds[]` moves to T2.** It is a parse product, the highlights pass needs it for clip lead-in flooring
  (`ClipWindows.RoundStartFor`), and Match Overview wants round count. Today it lives only in the highlights
  cache and is empty in 348/348 rows.
- **Player names stay RAW** (the CSVG `spec_player` currency) — `DisplayText.Sanitize` at the render
  boundary only. Unchanged rule, restated because every new surface below renders names.
- **Nothing per-tick, per-frame or per-event beyond highlights.** See §1.7.

### 1.5 Byte budget

Disk is not the constraint — say so plainly, so the discussion lands on the constraint that is.

| Addition | Per demo | Over 719 demos |
|---|---|---|
| T2 roster extension (10 × slot/steamId/team/isBot) | ~+500 B | +0.35 MB |
| T2 tickRate/tickCount/serverStartTick | ~+40 B | +0.03 MB |
| T2 rounds (24 × number+startTick) | ~+1.2 KB | +0.86 MB |
| T3 scoreboard (10 × K/D/A/ADR/rating/CTW/TW) | ~+700 B | +0.5 MB |
| T3 highlights (varies; typical 10–30 events) | ~+1.5 KB | ~+1.1 MB |
| **Fully-populated record** | **~4.7 KB** indented | **~3.4 MB** at 719 demos (~1.8 MB minified) |

**The real constraints are (a) monolithic-JSON load/rewrite cost and (b) T3 CPU.** `HighlightsCacheStore`
already writes atomically *after every demo* during a backfill; at a 3.4 MB monolith that is ~2.4 GB of write
traffic for one library sweep, and a full deserialize on every app start regardless of which demo the user
cares about. That is the argument for splitting storage, not disk space.

### 1.6 Storage shape — fork 1 (cache storage shape), see §10

**Recommendation: thin index + per-demo sidecars.**

```
<config>/cache/
  index.json                 ← T0 + T1 + a Library-card projection (map, players count, score, duration,
                                 fill flags). ~250–400 B/demo → ~200–290 KB at 719. Loaded at startup.
  demos/<sha256|pathhash>.json  ← the full T2 + T3 record for ONE demo. Loaded lazily.
```

Why: the Library grid needs a small, always-loaded projection; Match Overview needs the *fat* record for
**exactly one** demo at a time — which is precisely what "Match Overview is a cache render" means. A sidecar
write during a backfill touches one small file, not a growing monolith. It also makes per-demo invalidation
and hand-repair (delete one file) trivial. WASM degrades exactly as today (`AppPaths.ConfigRoot` null →
in-memory, nothing loaded, nothing written).

Alternatives are enumerated in fork 1 (cache storage shape), including SQLite (best scaling, but a new dependency, a WASM problem,
and a migration this project does not need yet).

### 1.7 What stays parse-on-open (deliberately NOT cached)

- **Per-round timelines / round event streams** — grow with round count *and* event density; the Stats tab
  and the round export already compute them from a live parse, and they are the wrong granularity for a
  glance page.
- **Anything per-tick or per-frame** — positions, entity snapshots, the 2D playback stream. Orders of
  magnitude larger than the whole cache and useless without the parse anyway.
- **Kill feeds / damage matrices** — plausible-looking but they scale with events (a 30-round match is
  hundreds of kills, thousands of damage records) and no glance surface consumes them today. If a future
  enrichment needs one, it belongs in a T4 sidecar of its own, not in the record everything loads.
- **Rendered/derived display strings** — cache values, render strings. The existing MO placeholder rules live
  in the VM and must stay the single formatting site.

### 1.8 Ownership, projection, invalidation, migration

**Ownership.** One `DemoCacheStore` in `Services/DemoCache/` — the single writer, thread-safe, atomic writes
(the `HighlightsCacheStore` pattern, which is already the better of the two: *"a crash mid-write must never
destroy an hour of scan progress"*), with `BeginBatch` coalescing retained. It absorbs `HighlightsCacheStore`
and the Library's cache persistence. `HighlightScanService` and the Library indexer stay `IDemoEvaluator`s;
they write **tiers of one record** instead of rows in two files.

**Projection — nobody else persists.**

| Surface | Reads | Shape |
|---|---|---|
| Library card grid | `index.json` only | today's `DemoEntry`, unchanged in spirit |
| Match Overview | one sidecar (lazy) | the full record for the subject demo |
| Reels dashboard — tray | sidecars for staged clips' demos | provenance + `ClipWindows` inputs |
| Reels dashboard — Add-clips picker | **highlight rows across sidecars** | needs a cross-demo highlight list without loading 719 sidecars → the index carries a per-demo `highlightCount` + the picker loads sidecars on demand as the user filters. **Flagged as the one place the split storage costs something** (fork 1 records it). |

**Invalidation.**

- **File identity** — `(path, size, modifiedTicks)` invalidates the whole record, as today; `sha256` remains
  the content-dedup key.
- **T3 validity** — the existing `HighlightConfigFingerprint` (`Analysis/RulesetsV2/Compile/`) already
  produces a combined config fingerprint plus per-highlight hashes, and is already `(tickRate, profile)`-
  dependent. **Extend it to stamp the whole T3 block**, since the scoreboard comes from the same rules run;
  a fingerprint mismatch marks T3 stale (the existing `IsStale` concept) while leaving T0–T2 valid. This is
  the point of tiering: a rules edit must not invalidate 719 demos' rosters.
- **Per-tier `schema` + `computedAt`** — a schema bump invalidates only its own tier. Today's single
  `DemoLibraryCacheEntry.CurrentSchema` invalidates everything, which is why the cache has been conservative
  about adding fields; per-tier versioning is what makes the record safe to grow.

**Migration.** One-shot, on first run of the new store: read `library.json` + `highlights.json`, project into
records, write index + sidecars, rename the legacy files to `.bak` only after a clean write. **Migration risk
here is unusually low** — `highlights.json` is 99.4% empty stubs, so exactly one demo (the 226-event row) has
a payload worth preserving, and `library.json`'s fields map 1:1 into T1/T2 with `Players` widening from
`string` to a record (name-only entries migrate with `team = unknown`, which the completeness model already
handles). Anything that fails to migrate is simply re-indexed — this is a rebuildable cache, never a source
of truth.

---

## 2. Partial fill is the normal state — designing for it

Even with T2 extended, the library will be a mix for a long time: some demos header-only, most T2, few T3.
**This is the question the redesign has to answer honestly**, and the answer is one component plus one rule.

### 2.1 The completeness chip (the answer)

A single inline state chip in the hero band, reusing the shared `StatusChip` dot-plus-label idiom (dot =
redundant colour cue, **word carries the state** — the established contrast rule). Five states, each with the
one action that advances it:

| State | Means | Dot | Action offered |
|---|---|---|---|
| `LIVE · analysing…` | this demo is open and the pipeline is running | `stateWorking` + pulse | — (the progress bar is right there) |
| `FULL` | T3 present and fingerprint-current | `stateGood` | — |
| `INDEXED · stats not computed` | T2 present, T3 absent or stale | `stateGood` **hollow** (the "inferred/partial" ring already in the vocabulary) | **`[ Compute full stats ]`** |
| `NOT INDEXED` | T1 only | `off` (`TextDim`) | **`[ Index this demo ]`** |
| `INDEX FAILED` | last pass threw | `stateError` | **`[ Retry ]`** |

Why this works: the user is never asked to infer *why* a section is empty. The page says which tier it has,
and the empty sections all point at the same button. It also reuses an existing component and adds **zero
tokens** — including the hollow-ring treatment, which already exists precisely to mean "partial/inferred".

### 2.2 The rule for a slot with no data

> A slot whose tier is missing shows **one short sentence naming the tier plus the same action** — never a
> grid of `—`, never a hidden section.

- Scoreboard, no T3 → *"Player stats need a full analysis pass. `[ Compute full stats ]`"*
- Highlights, no T3 → *"Highlights need a full analysis pass. `[ Compute full stats ]`"*
- Rosters, T2 with name-only players (migrated rows) → *"Team split needs a re-index. `[ Index this demo ]`"*
- Per-side split, no T3 → the existing suppressed-slot copy, unchanged.

`—` remains correct for a **single value inside an otherwise-populated card** (tick rate on a migrated row),
because there the surrounding context already tells the user the tier is present.

### 2.3 `Compute full stats` and the one-heavy-parse invariant

The action **enqueues this demo on the existing `IDemoProcessingQueue` at interactive priority**. It does not
open the demo, and it does not introduce any new machinery:

- the queue already routes every parse through `Services/HeavyJobGate` (one heavy parse machine-wide);
- it already surfaces in the processing-queue status chip (pause/resume/remove, `chrome.processingQueue`,
  default-visible to every category);
- `DemoEvaluationCoordinator.FanOutParsed` already fans one parse to every interested evaluator — so a single
  click fills **T2 gaps, T3 stats, and highlights together**, and the Library card, Match Overview and the
  Reels picker all improve at once.

While it runs, the completeness chip shows `LIVE · analysing…` for *that* demo even though it is not open —
which is honest, and is exactly the affordance the current opt-in "scan my whole library" CTA fails to
provide (all-or-nothing, 30 minutes, no per-demo entry).

### 2.4 The product fork this creates — extend T2, retarget T3 (fork 2)

- **(a) Extend T2** — roster teams/bots/steamIds, tickRate, tickCount, rounds. ~+0.5 KB/demo, rides the
  pass that already has 80% coverage, no new CPU. **Do this.**
- **(b) Promote T3 to automatic** — rejected: full stats everywhere, but the opt-in exists for a real reason
  (~30 min per 200 demos of rules-engine work) and making it ambient contradicts the app's own
  "heavyweight actions are explicit" principle.
- **(c) Retarget T3's trigger** — keep the background opt-in for users who want the sweep, and add the
  **per-demo `Compute full stats`** action as the primary path. This is what makes T3 coverage actually grow:
  users compute the demos they look at, which is the correct prior.

**Recommendation: (a) + (c).** Explicitly *not* (b).

---

## 3. Match Overview, redesigned

The page was explicitly opened to redesign. Below is a real layout for its three jobs — (i) live landing during
parse, (ii) full cached render of any indexed demo, (iii) per-game highlight exploration — not the current
page with a section bolted on.

### 3.1 What survives from the current page, and what changes

**Survives (as principle):** no layout jump; nothing `IsVisible`-gated on **load state**; every section
present from the first frame with a reserved slot and a placeholder; values swap placeholder → real. The file
comment's warning stands verbatim — *"a page that sprouts new cards mid-load reads as the UI misbehaving even
when it is fast."*

**Changes (as structure):**

*(Note on the facts strip: there is deliberately **no** RECORDED / date tile. No tier carries a recording
timestamp — `DownstreamUtilities.DemoQuickInfo` is `(MapName, ServerName, ClientName, DemoVersion)` with no
date, and the nearest available value, `ModifiedTicks`, is file mtime, which changes when a demo is copied and
is therefore not the recorded date. Showing mtime labelled RECORDED would be exactly the defect §11 item 8
forbids. If a demo-header timestamp is later found, it is a T1 field and the tile can be added then.)*

| Today | Proposed | Why |
|---|---|---|
| Identity hero card, then a separate `FINAL SCORE` card | **One hero band**: identity left, score plate right, completeness chip + progress under both | The two things a user opens a demo to see are the map and the score; putting them in one band saves ~120px and puts both above the fold |
| 104px stage-strip card | **A thin progress rail inside the hero band** (2px bar + inline stage words) | Still a permanent slot, one sixth the height; the completeness chip now carries the state word |
| Single 920px centred column, everything stacked | **Two-column body at ≥1000px** (`1.3*` the match · `*` the moments), collapsing to one column below | With highlights added, a single column becomes a very long scroll; two columns put the scoreboard and the highlights side by side, which is how the page is actually read |
| Rosters 188px, scoreboard 268px reserved | Re-proposed metrics per the new columns (rosters ~176px each, scoreboard ~268px unchanged, highlights ~300px) | `MatchOverviewLandingTests` measures these against real rendered layout — new numbers must be re-measured, see §3.5 |
| CTAs at the very bottom | **Primary CTA in the hero band**, secondary CTAs stay at the bottom of the left column | An action reachable only after scrolling past the whole page is not a primary action |

### 3.2 Wireframe — Match Overview, cached render (`INDEXED`, T3 absent)

```
┌─ Match Overview ──────────────────────────────────────────────────────────────────────────────────┐
│ ┌─ HERO BAND (fixed height ~150px; identical in every mode) ───────────────────────────────────┐  │
│ │ MATCH OVERVIEW                                                                                │  │
│ │ Dust II                                    ● NAVI  13   –   11  FAZE ●                        │  │  ← score plate
│ │ ▬▬▬▬                                          ENDED CT        ENDED T                          │  │    (was its own card)
│ │ FACEIT Server EU #4021                                                                        │  │
│ │ faceit_2025-06-14_dust2.dem                                                                   │  │
│ │ ──────────────────────────────────────────────────────────────────────────────────────────── │  │
│ │ ◌ INDEXED · stats not computed   [ Compute full stats ]        [ Open this demo ] (.primary)  │  │  ← completeness chip
│ │ ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁  ○ Parsing  ○ Enriching  ○ Analysing                  │  │    + progress rail
│ └───────────────────────────────────────────────────────────────────────────────────────────────┘  │
│ ┌─ FACTS STRIP ────────────────────────────────────────────────────────────────────────────────┐  │
│ │  DURATION 38:12 │ ROUNDS 24 │ PLAYERS 10 │ SPECTATORS 3 │ TICK RATE 64                        │  │  ← all T2 now
│ └───────────────────────────────────────────────────────────────────────────────────────────────┘  │
│                                                                                                    │
│ ┌─ THE MATCH  (1.3*) ─────────────────────────────┐ ┌─ THE MOMENTS  (*) ──────────────────────┐  │
│ │ ┌ Counter-Terrorists            [5] ●─────────┐ │ │ HIGHLIGHTS            ⚠ outdated·rescan │  │
│ │ │  s1mple      b1t      jL                     │ │ │ ┌─────────────────────────────────────┐ │  │
│ │ │  iM          w0nderful                       │ │ │ │                                     │ │  │
│ │ └──────────────────────────────────────────────┘ │ │ │  Highlights need a full analysis    │ │  │  ← §2.2 rule
│ │ ┌ Terrorists                    [5] ●─────────┐ │ │ │  pass.                              │ │  │
│ │ │  broky       rain     frozen                 │ │ │ │  [ Compute full stats ]             │ │  │
│ │ │  karrigan    ropz          [BOT]             │ │ │ │                                     │ │  │
│ │ └──────────────────────────────────────────────┘ │ │ └─────────────────────────────────────┘ │  │
│ │                                                   │ │                                          │  │
│ │ PLAYER STATS                                      │ │ ENRICHMENTS                              │  │  ← §7 slot,
│ │ ┌───────────────────────────────────────────────┐ │ │ (empty → zero height)                    │  │    zero height
│ │ │ PLAYER        K    D    A    ADR   RATING     │ │ │                                          │  │
│ │ │                                               │ │ └──────────────────────────────────────────┘  │
│ │ │  Player stats need a full analysis pass.      │ │                                               │
│ │ │  [ Compute full stats ]                       │ │                                               │
│ │ └───────────────────────────────────────────────┘ │                                               │
│ │                                                   │                                               │
│ │ ROUNDS BY SIDE — unavailable without full stats   │                                               │
│ │ [ View full stats ] (disabled)  [ Watch in 2D ] (disabled)                                        │
│ └───────────────────────────────────────────────────┘                                               │
└────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### 3.3 The same page, `FULL` (T3 present), demo not open

```
│ │ ● FULL                                                        [ Open this demo ] (.primary)   │ │
│ │ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓  ● Parsing  ● Enriching  ● Analysing     (all done)   │ │
   …
│ │ PLAYER STATS                                   │ │ HIGHLIGHTS                    7 · 3 staged │
│ │  s1mple      24  14   5   94.1   1.31          │ │  ▾ s1mple           (T)  4    [ + Add all ]│
│ │  b1t         19  16   7   81.0   1.12          │ │    2 kills after plant  r7  ~20s  [+][Vfy] │
│ │  …                                             │ │    ace                  r12 ~22s  [+][Vfy] │
│ │ ROUNDS BY SIDE   ● CT 15  ·  ● T 9             │ │  ▸ ZywOo            (CT) 2    [ + Add all ]│
│ │ [ View full stats ]  [ Watch in 2D ]  (enabled)│ │  ▸ b1t              (T)  1    [ + Add all ]│
```

### 3.4 Live mode is the same page

`LIVE` differs only in that the completeness chip pulses, the progress rail moves, the stage words light, and
values arrive into slots that are already reserved. **The cached render *is* the skeleton** — so opening a
demo you were previewing produces no visual discontinuity at all: the placeholders you are looking at fill in.
That property is the strongest argument for making Match Overview a cache render, and it should be protected
by whatever tests replace the current landing gates.

### 3.5 Identity discipline and the test gates

**Identity.** The VM gains `string? SubjectKey` (demo path) + `OverviewMode { Empty, Live, Cached }`. Every
existing push setter (`SetStage`, `SetSummary`, `BeginAnalysis`, `SetAnalysis`, `SetTeamNames`,
`SetTeamScores`, `Fail`) upgrades its `if (!HasContent) return;` guard to an identity check and takes the
demo key the shell already has in scope at every call site. A late push for a demo that is no longer the
subject is **dropped, not painted** — closing the hazard `SetAnalysis`'s own comment describes, and making the
preview-B-while-A-is-open case safe by construction rather than by care.

**Preview does not route through `BeginOpening`/`ResetValues`** (they clear the collections). Cached render is
its own entry point. When another demo is live, the hero band carries `[ ◀ Back to <open demo> ]` and the live
payload is stashed — a handful of strings and three collections, trivially cheap.

**Two honesty defects the current markup would carry into cached mode, both must be fixed:**

1. The roster count badges bind `CounterTerrorists.Count` / `Terrorists.Count` (`.axaml:477`, `:507`) →
   they render literal **`0`** when the split is unknown, asserting there are zero CTs. They need display
   strings yielding `—`.
2. The stage strip must **not** show all three steps `done` in cached mode — nothing ran. Cached = all steps
   pending, `Progress = 0`, with the completeness chip carrying the real state.

**Test gates in the blast radius (acknowledged, not mine to fix):**
`src/App/DemoViewer.NET.App.Tests/MatchOverviewLandingTests.cs` (reserved heights / no-jump — will need
re-measuring against the new sections),
`MatchOverviewScoreSourceTests.cs` (score derivation + reconcile — `SetTeamScores` gains a key parameter),
`MatchOverviewSpectatorTests.cs` (player/spectator counting — the counting rule moves to T2 cache time and
must produce identical results).

### 3.6 How Match Overview is reached — and what single-click actually does

| Gesture | Result | Parses? |
|---|---|---|
| **Single click** a Library card/row | Sets `SelectedEntry`; the shell pushes the cached record into Match Overview; **the card grows an `Overview ▸` chip** and takes the selection ring. **You stay on the Library tab.** | **No.** Reads the index + one sidecar. |
| `Overview ▸` on the card, or clicking the Match Overview tab | Switches to Match Overview, already rendered | No |
| **Double click** (unchanged) | The existing shared load funnel → live mode → shell switches to Match Overview | Yes, via `HeavyJobGate` |
| `Open this demo` in the hero band | identical to double-click | Yes |

**The `Overview ▸` chip is the answer to "what visible feedback does single-click give?"** — without it,
single-click populates a tab you are not looking at and appears to do nothing, which would leave the whole new
capability undiscoverable. The chip costs nothing (a `.chip` revealed on the selected card) and needs no
contract change.

*Rejected: echoing the demo name into the Match Overview tab header.* Nicer, but
`WorkspaceTabDescriptor.Header` is `required string … { get; init; }` and `MainView.axaml:222` binds it
directly, so a live header would need the descriptor to become observable — a `Modules.Abstractions` contract
change for cosmetics. Enumerated as fork 6 (single-click feedback).
*Rejected: single-click switches tabs.* It makes arrow-key browsing of the library unusable.

**Preview performs zero parsing.** It touches no parser, no `HeavyJobGate`, no queue, no coordinator. Not even
a header read — `TryReadQuickInfo`'s 256 KB read belongs to the open path (`MainViewModel.cs:2999-3005`) and
must not be borrowed. If the demo has no record, the page renders `NOT INDEXED` with `[ Index this demo ]`;
it never opportunistically indexes.

**Push, not pull — and there is an architectural reason.** `BuiltInTabsModule.cs:73-81` registers Match
Overview with **`DataContext`**, not `ViewModelFactory`, so — per that module's own comment and
`HighlightsModule`'s — **it never receives `OnActivated`** (`MatchOverviewTabViewModel.OnActivated` is an
empty body, `:289-291`). A pull design ("MO fetches the Library selection when activated") requires flipping
the app's most-visited tab to a different lifecycle. Push costs one delegate call from the shell, which
already owns every other MO fill call.

---

## 4. The Highlights tab → the Reels dashboard

### 4.1 Cross-demo reels — confirmed requirement, concrete design

Reels are multi-demo by construction: `_selection` is never cleared when the demo changes, and
`ClipWindows.Coalesce` groups by `(DemoPath, SteamId64, RoundNumber)` while `Recompute` iterates a
`demoFacts` dictionary keyed by demo path. The card grid was the cross-demo entry point; removing it without
a replacement would delete the capability. **Two entry points replace it, both feeding one tray:**

1. **Per-game** — Match Overview's highlight section: `[ + ]` per row, `[ + Add all ]` per player group.
   This is the brief's "explore the highlights per game", and it is also how a user naturally builds a reel:
   look at a match, take the good bits, move on.
2. **Cross-demo** — the dashboard's **`Add clips…`** picker: a flat, virtualized, filterable **highlight-row**
   list spanning every demo with a T3 record. This is where the four orphaned filters land, and it is the
   deliberate "assemble across matches" path.

### 4.2 The clip tray — contract

**The tray is not a new data model.** It is today's `_selection`
(`Dictionary<HighlightKey, HighlightSelection>`, keyed `HighlightKey(FilePath, RulesetId, HighlightId, Tick,
PlayerSlot)` — already cross-demo-stable, already carrying the owning row) promoted to a **visible, ordered,
provenance-bearing collection** plus an O(1) `IsStaged` lookup that Match Overview's `[ + ]` buttons read.

- **Provenance is mandatory and always visible** — map accent dot + map, demo file name, round, player, tick,
  estimated window. A 12-clip cross-demo tray is unreadable without it.
- **Reorder** — `▲ ▼` (keyboard-reachable) plus drag. ⚠ **Reorder must affect output sequence only.**
  `ClipWindows.Coalesce` groups by `(DemoPath, SteamId64, RoundNumber)` and is order-independent; wiring
  reorder into `Coalesce` would make merge behaviour position-dependent and non-deterministic.
- **Remove** — per clip, and per `(player · demo)` group.
- **Live coalescing feedback** — the tray groups by `(player · demo)` exactly as `ReelClipGroupViewModel`
  already does, with the bracketed contributors and the `→ merged clip: ticks A–B (~Ns)` line, and the header
  reads `CLIPS (7 staged · 5 after merge)`. **This is the single strongest argument for promoting the modal:**
  coalescing feedback currently visible only after you commit to rendering becomes visible while you build.
- **Per-clip pre-flight inline** — `⚠ demo moved` (the existing `_movedCount` path) surfaces at staging time,
  not at Generate time.
- **Persistence** — VM-held (survives tab switches) **and** persisted in `SnapshotState` as a list of
  `HighlightKey`s re-resolved on restore, dropping vanished keys with a one-line note. Today's selection is
  *not* persisted; a half-built cross-demo reel evaporating on restart is exactly the loss this tab now exists
  to prevent.

### 4.3 Wireframe — the Reels dashboard

```
┌─ Highlights ─────────────────────────────────────────────────────────────────────────────────┐
│ REELS                                                    [ + Add clips… ]   [ ⟳ Rescan all ] │
├──────────────────────────────────────────┬──┬──────────────────────────────────────────────────┤
│  CLIPS (7 staged · 5 after merge)        │▓▓│  PADDING                                         │
│  ┌────────────────────────────────────┐  │▓▓│    Lead-in [ 15 ]s  Lead-out [ 5 ]s              │
│  │ ● s1mple · Dust II                 │  │▓▓│    ☐ Don't cross round start                     │
│  │   faceit_2025-06-14_dust2.dem      │  │▓▓│    Highlights fire at the end of the action, so   │
│  │  ┌ 2 kills after the plant   r7    │  │▓▓│    lead-in covers the build-up.                   │
│  │  │   ticks 54,105–54,650   ~8.5s   │  │▓▓│                                                   │
│  │  ┕ ace                      r7     │  │▓▓│  DISPLAY    ( ● Default )  ( ○ No-HUD )           │
│  │      ticks 54,400–54,980   ~9.1s   │  │▓▓│                                                   │
│  │    → merged clip: 54,105–54,980    │  │▓▓│  OUTPUT                                           │
│  │                          ~13.7s    │  │▓▓│    Folder [ …/Reels          ] [ Browse ]         │
│  │                        ▲ ▼  ✕      │  │▓▓│    Name [ dust2_s1mple ]  Format ( mp4 ▾ )        │
│  │ ● ZywOo · Nuke                     │  │▓▓│    FPS ( 60 ▾ )  ☑ Concatenate  ☑ Capture audio   │
│  │   esl_2025-06-02_nuke.dem          │  │▓▓│                                                   │
│  │  · 3k retake              r4       │  │▓▓│  ▾ ENCODING          (power/dev default-open)     │
│  │      ticks 29,900–30,500  ~9.4s    │  │▓▓│      ( ● CRF [ 20 ] )  ( ○ Bitrate [   ] kbps )   │
│  │      ⚠ demo moved                  │  │▓▓│                                                   │
│  │                        ▲ ▼  ✕      │  │▓▓│  ─────────────────────────────────────────────    │
│  │ ● b1t · Mirage                     │  │▓▓│  ENRICHMENTS                                      │
│  │   pug_2025-05-30_mirage.dem        │  │▓▓│    (empty → zero height)                          │
│  │  · clutch 1v3            r19       │  │▓▓│                                                   │
│  │      ticks 88,010–88,900  ~14s     │  │▓▓│                                                   │
│  │                        ▲ ▼  ✕      │  │▓▓│                                                   │
│  └────────────────────────────────────┘  │▓▓│                                                   │
│              Total ~55s across 5 clips   │▓▓│                                                   │
├──────────────────────────────────────────┴──┴──────────────────────────────────────────────────┤
│  ⚠ 1 clip has a problem (demo moved). Fix or remove it to continue.                            │
│  ● Reel · 2 of 5 — s1mple — ace     [▓▓▓▓▓▓░░░░░░░░]              [ Cancel ]                    │  ← inline job strip
│  ─────────────────────────────────────────────────────────────────────────────────────────────  │
│  ⚠ Developer/testing — walks the clip plan without recording.  [ Clear tray ] [ Dry run (mock) ]│
└────────────────────────────────────────────────────────────────────────────────────────────────┘
                                          └ GridSplitter — collapses to one column below ~760px
```

- **Layout:** the shipped master-detail pattern (`*,Auto,1.4*` + `GridSplitter`, design-system §4), with the
  weights **inverted** — the tray is the content-dense pane here. Same responsive collapse below ~760px, same
  VM-held column-span mechanics, same persisted star weights. **No new layout pattern.**
- **Job status remains a status-strip chip too** (a background job must be visible from any tab); the inline
  strip is a second view of the same `ReelJobStatusViewModel`, shown only while a job exists.
- **The modal is retired.** `HighlightReelDialogWindow`/`View` become the config pane;
  `IWindowService.ShowHighlightReelDialog` and the `RequestCreateReel` event (`App.axaml.cs:675-694`) go away;
  the ux-design §9 single-CS2 interlock strip transplants unchanged (it is already an inline `Border`, not a
  nested dialog). `ClipWindows` and every
  computation are untouched.

### 4.4 The `Add clips…` picker — where the filters live

**Recommendation: the four filters live in the picker, not over the tray.** The tray gets at most one
free-text box, and only once trays routinely exceed ~10 clips. The multi-selects were **discovery**
affordances over a library-wide corpus; a staged tray is small by construction, and filtering ten things you
deliberately chose — with a player multi-select carrying counts — is machinery without a job.

```
┌─ Add clips ────────────────────────────────────────────────────────────────────────┐
│ [ 🔍 search map, player, file… ] [ Players ▾ (2) ] [ Types ▾ ] [ Maps ▾ ] [ Clear ] │  ← the four orphans, verbatim
│ ────────────────────────────────────────────────────────────────────────────────── │
│  ● Dust II   faceit_2025-06-14   s1mple    2 kills after the plant   r7   ~20s  [+] │
│  ● Dust II   faceit_2025-06-14   s1mple    ace                       r12  ~22s  [✓] │
│  ● Nuke      esl_2025-06-02      ZywOo     3k retake                 r4   ~18s  [+] │
│  … (VirtualizingStackPanel over highlight ROWS, not demo cards)                      │
│                                                                                      │
│  No highlights match these filters.   [ Clear filters ]                              │
│ ────────────────────────────────────────────────────────────────────────────────── │
│  <N> highlights across <M> analysed demos  ⓘ Only demos with full stats appear here. │  ← honest about T3 coverage
│                                          [ ⟳ Rescan all ]        [ Add 3 selected ]  │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

A flat row list is the right shape — the unit of work is a **clip**, not a demo — and it virtualizes trivially,
where the chunked-`CardRow` machinery existed only because `WrapPanel` has no virtualizing counterpart, a
constraint that disappears with the grid.

The footer line is deliberately honest about T3 coverage, and the counts are left as `<N>`/`<M>` rather than
guessed: the measured data has 346/348 rows `Pending` yet 267 events present, which means events exist on
`Pending` rows (today's `stale = Pending && Events.Count > 0` case in `ApplyFilter`), so "demos with usable
highlights" and "demos with `ScanState == Indexed`" are **not** the same set. Whichever the picker counts, it
must count one of them deliberately. The point of the line stands either way: with today's coverage this
picker shows a handful of demos out of 719, and the user needs to understand *why* rather than conclude the
feature is broken.

> **RESOLVED as built (step 9): a demo appears when it has EVENTS**, i.e. `Events.Count > 0`, regardless of
> `ScanState`. Counting `Indexed` rows against the measured data would print *"0 analysed demos"* above a list
> of 267 visible highlights — a page contradicting what it is showing, which is the same class of defect the
> step-5 capture review caught on Match Overview. `DemosWithHighlights` is that count; `LibraryRowCount` is
> every cached row, and the *"M of N cached demos have been"* clause is emitted **only** when `N > M` (before
> the first `RefreshStaleness` pass — and in every test/capture host — the cache holds nothing but analysed
> rows, and "2 of 2" reads as a bug).
>
> ⚠ **The wireframe's caveat copy is therefore wrong and was NOT used.** *"Only demos with full stats appear
> here"* is false under this definition — a re-queued `Pending` row with a previous harvest does appear. The
> shipped copy is *"Only demos that have been analysed for highlights appear here."* Choosing the definition
> and keeping the wireframe's sentence would have reintroduced the contradiction one line lower.

---

## 5. Every orphaned function, placed

Dropping the card grid orphans eleven functions. **No row reads "dropped" without a named replacement.**

| # | Function today | Lands in | Mechanism |
|---|---|---|---|
| 1 | **⟳ Rescan all** | **Status-strip chip flyout** + mirrored in the Add-clips picker footer | Existing `RescanAllCommand` |
| 2 | **Scan-progress chip** (`ScanQueueSummary`, `ShowScanChip`) + per-card scan animation | **A 4th `StatusChip`** (`Highlights · 12 queued`, `stateWorking` + pulse) | The design system says verbatim that three consumers now share it and *"a fourth should extend it, not fork."* Flyout: queue depth, currently-scanning demo name, stale count, failed count, `[Retry all failed]`, `[⟳ Rescan all]` |
| 3 | **Staleness badge** `⚠ highlights outdated — rescan` (`IsStale`) + tap | **Match Overview's highlight-section header** (it is per-game) + aggregate count in the chip flyout | Existing `RescanDemoCommand` |
| 4 | **`✕ Failed — retry`** (`IsFailed`) + tap | **Match Overview's completeness chip** (`INDEX FAILED · [ Retry ]`) + aggregate `[Retry all failed]` in the chip flyout | Same command |
| 5 | **"Scan my library" hero** + `ShowScanCta` | **Dashboard empty state (secondary line)** + Settings → Highlights | ⚠ **The trap:** "no clips staged" and "library not indexed" are different emptinesses. Primary copy is always about the tray; the *"Your library isn't indexed — [ Scan my library ]"* line appears **only** when no demo has a T3 record |
| 6 | **Four filters** + **Clear** | **The Add-clips picker** (§4.4) | Same `PlayerFilterItem`/`HighlightTypeFilterItem`/`MapFilterItem` collections and flyout markup, re-pointed at highlight rows |
| 7 | **Per-highlight "Verify live"** | **Match Overview highlight rows** | Same `VerifyMomentAsync`, same two-level gate. **Falls out correctly with no new logic** — `canVerify(rowDemoPath)` basename-compares against the *open* demo (`App.axaml.cs:652-666`), so Verify is disabled in a cached render and enabled in live mode. Do not "fix" this |
| 8 | **"Open in workspace"** | **Match Overview's `Open this demo`** | Match Overview *is* the demo detail now; a second button beside it would violate "exactly one prominent Open-Demo per state" |
| 9 | **Reel footer** (`SelectionSummary`, `Create Highlight Reel`) | **Dashboard tray header + footer primary** | The modal's footer becomes the tab's footer |
| 10 | **No-filter-match empty state** | **Inside the Add-clips picker** | Same copy + `Clear filters` |
| 11 | **WASM degraded note** | **Dashboard body, rewritten** (§6.2) | Copy must change — the tab's purpose is now authoring, wholly absent on WASM |

**The one accepted reduction:** the per-card scanning animation showed *which* demo was scanning through
spatial motion. With cards gone it becomes a name in the chip flyout (`◐ scanning faceit_…dust2.dem`). Its
value was ambient reassurance, which a pulsing status chip delivers at lower cost — but it is a real loss and
is recorded as one.

---

## 6. Gating and WASM

### 6.1 ⚠ A premise correction

The brief states *"Highlights is absent on the browser host."* **It is not.** `HighlightsTabViewModel` carries
`IsBrowser`/`CanScan`/`ShowWasmNote`/`ShowBrowseSurface`; `App.BuildRegistry` says *"Registered on both hosts
(WASM degrades…)"*; ux-design §1 rules **"Registered, degraded"** with the rationale that *"an unregistered
tab that silently vanishes on one host is a worse mental model."*

What *does* change is the strength of that rationale: today's justification is a **browsing** payoff, and
after this redesign the tab is an **authoring** surface that is 100% unavailable on WASM.
**Recommendation: keep it registered-and-degraded with rewritten copy** — because (a) the browsing payoff
genuinely relocates to Match Overview, which is present on the browser host, so a browser user is *better* off
after this change, and (b) host-dependent tab sets remain the worse mental model. Fork 7 (WASM Highlights
registration) enumerates unregistering.

### 6.2 WASM specifics

- **Reels dashboard, browser body:** *"Reels need the desktop app. Building a highlight reel records clips
  from a live CS2 game and writes video files — neither is possible in a browser. You can still see a match's
  highlights on Match Overview."*
- **Match Overview on WASM:** the cache needs a filesystem (`AppPaths.ConfigRoot` is null), so **guard the
  entry point, not the tree** — the Library never pushes a cached record and the page never enters cached
  mode. **No `IsBrowser` branch anywhere in the Match Overview view.** The highlight section stays in the tree
  and renders either the open demo's in-memory harvest or *"Highlights aren't indexed in the browser build."*

### 6.3 Per-category defaults

| Feature / surface | Scope | Consumer | Power | Dev | Platform | Change |
|---|---|:-:|:-:|:-:|---|---|
| `tab.highlights` (now the Reels dashboard) | Tab | ● | ● | ● | Desktop full; WASM degraded | **Unchanged.** ux-design §2.2 already reasoned that gating reel generation power+ *"would hide the feature's headline payoff from the audience most excited by it"* |
| `tab.matchoverview` | Tab | ● | ● | ● | Both hosts | Unchanged |
| MO highlight section | — | ● | ● | ● | cached render desktop-only | **Bind the existing `tab.highlights` gate** — do not mint a new id; disabling Highlights should coherently remove both surfaces |
| **`highlights.encoding`** (NEW SubFeature) | SubFeature | ○ | ● | ● | Desktop | **New.** `ParentId: "tab.highlights"`, `Defaults(false, true, true)`. CRF/bitrate/FPS/container are OBS-encoder knobs a consumer cannot reason about — the textbook "hidden but enableable" tier. Consumer face: tray + Default/No-HUD + folder/name + Generate |
| `Compute full stats` action | (ungated) | ● | ● | ● | Desktop | **New, ungated.** It is one queued parse through machinery every category already sees (`chrome.processingQueue` is `Defaults(true,true,true)`) and it is how a consumer gets their scoreboard |
| Highlights background scan | setting | ○ | ○ | ○ | Desktop | Unchanged (opt-in for all) |
| Create Reel / Generate | (ungated) | ● | ● | ● | Real Win/Linux · dry-run macOS · absent WASM | Unchanged |
| Live Sync / Verify live | Chrome | ○ | ○ | ● | Desktop | Unchanged (`chrome.livesync`, ux-design §2.3 fork) |

**Gate axis vs load axis — put this in the code comment or a reviewer will read the gated section as a
skeleton-first violation.** Skeleton-first forbids `IsVisible`-toggling on **load state** (a section appearing
because a parse finished). A **feature gate** is a different axis: user-initiated, reconciled live via
`FeatureGate.Changed`, stable for the whole load. Gating the highlight section on `tab.highlights` is
legitimate; gating it on `HasSummary` would not be.

### 6.4 Catalog hygiene

1. **Rewrite `tab.highlights`'s description** — it currently describes a surface that will not exist.
   Proposed: *"Build and customise highlight reels — stage clips from any match and render them to video.
   Explore a match's highlights on Match Overview."*
2. ⚠ **The catalog contradicts itself about whether descriptions are persisted.** `FeatureCatalog.cs:55` says
   *"The description string is a persisted-key sibling; keep it verbatim"*; `:154` says *"Only the ID is a
   persisted key; the description is display-only help text."* **`:154` is correct** —
   `FeatureDescriptor.Description` is documented as *"One-line explanation of the feature for the settings
   UI"* and overrides are keyed by `Id` alone. Fix the stale comment at `:55`.
3. **`TabId "highlights.browser"` and feature id `"tab.highlights"` must not change** even if the tab header
   is renamed — they are persisted keys. A header rename is fork 5 (tab header), with that constraint
   attached.

---

## 7. Future enrichments — the extension shape

One named slot, one existing mechanism, no speculative features.

Both surfaces end with an `ENRICHMENTS` region backed by `ObservableCollection<object> EnrichmentSections`
rendered by an `ItemsControl` whose item template is the app's `ViewLocator` — the same VM→View resolution
the `StatusChip` flyouts already use (`FlyoutContent = this` → `Views/Highlights/ReelJobStatusView`). An
enrichment is **a VM plus a View appended to the collection at composition — zero edits to either view.** One
constraint: `ViewLocator.Match` requires `data is ViewModelBase`, so enrichment VMs derive from it.

On the dashboard, an enrichment sees the tray through a narrow read-only contract (staged clips with
provenance, the computed plan, and the `ReelRequest` before hand-off) so it can add configuration — an
overlay, a transition, a caption source, a per-clip annotation — without reaching into reel internals. On
Match Overview it sees the `DemoCacheRecord`, which is exactly why the unified cache makes enrichments cheap:
a new per-demo insight is **a new tier or sidecar plus a section VM**, not a new pipeline.

**One rule that keeps §7 from breaking §3:** enrichments register **at composition, not mid-load**. The slot is
in the tree from frame one and renders zero height when empty. An enrichment that appears mid-run would
reintroduce exactly the layout jump the page exists to prevent. This slot is also where the module
section-contribution contract would plug in if that ever becomes real (§8).

---

## 8. Cache ownership vs the module boundary

`HighlightsCacheStore` currently lives in `Modules/Highlights/` and is injected at the composition root.
Under this proposal it is **absorbed into `Services/DemoCache/`** along with `HighlightsCacheModels` and
`ClipWindows`, leaving `Modules/Highlights/HighlightsModule.cs` owning only the tab. That resolves the
"built-in depends on a module" question by dissolving it: Match Overview depends on a **service**, exactly as
it would depend on `DemoEvaluationCoordinator` — itself a composition-root service fanning out to
`[library, highlights]` evaluators (`App.axaml.cs:487-505`). There is precedent; this makes it explicit.

The alternative — a module-contributed *section* on another tab — **has no contract today**:
`IWorkspaceModule`'s entire contribution surface is `CreateTabs(IModuleHost)`. Inventing a section-contribution
contract, a host-side slot registry and an ordering model is a much larger lift than this feature justifies.
Adopt it only if Match Overview ever needs N module-contributed sections; §7's enrichment slot is where it
would land.

---

## 9. Ownership and sequencing

### 9.1 Who owns what — §1 is a recommendation, not a claim

**§1 (the unified demo cache) is a recommendation to the data/services workstream.** It is not UI
work and this proposal does not claim it: it means a new `DemoCacheStore`, widening
`DemoLibraryCacheEntry.Players` from `List<string>` to a record, absorbing `HighlightsCacheStore`, tiered
schema versioning, and a one-shot migration. The UI scope here is `Styles/`, `Controls/`, per-view
`.axaml`, `docs/ui/design-system.md`, and view-models — separate systems coordinate, they don't
collide. §1 is written at design altitude (field lists, storage shape, invalidation,
migration story) precisely so the owning workstream can take or amend it; the byte budgets and the T2/T3
coverage argument are the inputs that decision needs.

**Everything in §2–§7 is UI-side and is this proposal's own work**, with one exception noted below.

| Work | Owner | Depends on §1? |
|---|---|---|
| MO identity guards (`SubjectKey`, `OverviewMode`, keyed setters), roster-badge `—` fix, stage-strip cached state | UI | **No** — hardens the existing live path on its own |
| Library single-click selection + `Overview ▸` chip | UI (one additive Library change, not a redesign) | No |
| MO redesign (hero band, facts strip, two-column body, completeness chip) | UI | Only for *which* tiers exist; the layout is built against whatever record is available |
| Dashboard: tray + promoted config body + inline job strip; modal retirement | UI | No |
| Add-clips picker + orphan re-homing (incl. the 4th `StatusChip`) | UI | Reads the cache; works against today's two stores if §1 slips |
| `highlights.encoding` SubFeature, `tab.highlights` description rewrite, stale-comment fix | UI proposes → gating/settings system owns the catalog edit | No |
| Unified cache, T2 extension, migration | **data/services layer** | — |

### 9.2 Suggested order

1. **MO identity + mode model.** Ships value alone: it closes the bug class `SetAnalysis`'s own comment
   describes, independent of everything else.
2. **T2 extension + cache decision** (fork 1 storage shape, fork 2 fill strategy) — the data-layer workstream
   starts here because §3's cached render is only as good as the record.
3. **MO redesign** against whatever record exists (it degrades correctly via the completeness chip, so it does
   not have to wait for full T2/T3).
4. **Library single-click → cached render.** Steps 1 + 3 + 4 together deliver move 1.
5. **Dashboard promotion**: tray + config body, fed by today's `_selection`, `Generate` on the existing job
   service. Modal retired. Delivers move 2 without waiting on the picker.
6. **`[ + ]` staging from MO → tray**, tray persistence.
7. **Add-clips picker**, the scan `StatusChip`, catalog changes.

Steps 1, 3–4, and 5 are each independently shippable. Nothing here touches a protected parser file, and no
new parser hook is needed — every value consumed already exists on `ParsedDemo` / `PlayerInfo` or in the
current caches.

### 9.3 Revised order for the full-program scope (supersedes §9.2)

The whole program was scoped into v0.5.3, so the data layer is no longer a follow-on. The ordering
below front-loads the record — everything downstream renders better against a real one — while keeping each
step independently committable.

| # | Step | Why here | Gates |
|---|---|---|---|
| 1 | **MO identity + mode model** — `SubjectKey`, `OverviewMode {Empty,Live,Cached}`, keyed setters, roster-badge `—` fix, cached stage-strip state | Closes the existing clobber bug class on its own, and every later step pushes into these setters | `MatchOverviewScoreSourceTests`, `MatchOverviewSpectatorTests` |
| 2 | **`DemoCacheStore`** — tiered record, index + sidecars, atomic writes, `BeginBatch`, per-tier `schema`/`computedAt` | The record everything else projects from | new tests |
| 3 | **T2 extension** — `Players` widens to a record (slot/name/steamId64/team/isBot), + tickRate/tickCount/serverStartTick/rounds | ~+0.5 KB/demo of data already held at T2 time; unlocks cached rosters for the 80% already indexed | Library indexer tests |
| 4 | **Migration + absorption** (done). The one-shot `library.json`+`highlights.json` → index+sidecars migration landed first; the absorption followed in three commits — readers, then the writer + derived backlog, then the deletion of `HighlightsCacheStore` and its models. `highlights.json` is read exactly once, by `LegacyCacheMigration`, through the migration-only DTOs in `Services/DemoCache/LegacyHighlightsModels.cs`. See §9.7. | Must land with 2–3 or two writers race the same demos | migration tests + the full 659-test App suite |
| 5 | **MO redesign** (done): merged hero band, facts strip, two-column body, completeness chip, cached render, highlight section. `Compute full stats` is an injected delegate; the **queue wiring is owed by the shell pass** (§9.4). | Now has a real record to render | `MatchOverviewLandingTests` re-measured: 24 tests green, content **898 wide / 1216 narrow**, identical across all 3 load states × all 3 cached tiers × both widths |
| 6 | **Library single-click → cached render** + `Overview ▸` chip | Completes move 1 | Library tests |
| 7 | **Dashboard promotion** (done): ordered clip tray, promoted config pane, inline job strip, header → "Reels", enrichment slot, tray persistence. The modal is retired from the TAB; deleting its window + `IWindowService` entry point is **owed by the shell pass** (§9.5). | Completes move 2; independent of 2–6 and can run in parallel | `HighlightsTabViewModelTests` 16 green, `HighlightReelDialogViewModelTests` 17 green; rendered across dark / light / high-contrast / e-girl × populated / empty / bare-library / demo-moved / running-job / narrow |
| 8 | **`[ + ]` staging from MO → tray**, tray persistence via `SnapshotState` | Needs 5 and 7 | |
| 9 | **Add-clips picker**, 4th `StatusChip`, `highlights.encoding` SubFeature, catalog description rewrite + `:55` stale-comment fix | The orphan re-homing tail | `HighlightsTabViewModelTests` |
| 10 | **`design-system.md` updates** (done) — §5 matrix rows (incl. `highlights.encoding`), §4 master-detail note (weights inverted for the tray), `StatusChip` fourth-consumer entry. The three sections landed alongside steps 7/9; the tail was the stale TYPE references the absorption left behind (`HighlightsCacheStore`/`HighlightsCacheRow`/`ScanState` → `DemoCacheStore`/`DemoCacheRecord`/`AnalysisState`) plus the now-shipped scan-chip shell registration. | Owed by §9.1; land it with the code it documents | — |

> All ten steps were code-complete as of 2026-07-29, and so was the half-score repair. Two sections written
> after this table live at the END of the document, below §10 and §11:
> [§9.7](#97-tier-3-producers) records how the absorption landed and what tier 3's two producers are, and
> [§9.8](#98-half-score-repair) records the half-score repair, which ships as an explicit action.

**Open question — RESOLVED at step 5 (MO redesign).** The question was whether *restoring* a stashed live
payload needs its own guard, given the identity guard only drops mismatched *pushes*.

**Resolved by removing the stash entirely.** Match Overview holds no snapshot of the live demo. The live page
is a projection of pipeline state the view-model cannot re-derive, so restoring it is the SHELL's job:
`◀ Back to <demo>` invokes an injected `returnToLive` delegate, and the shell re-renders the open demo the
same way it rendered it the first time. That makes the stale-stash race impossible rather than guarded — there
is nothing to go stale. Anyone tempted to add a stash later should read this first; the question is closed, not
open.

<a id="94-shell-wiring-owed"></a>
### 9.4 Shell wiring owed by the step-6 pass (deliberately NOT done in step 5)

Step 5 left every shell-bound behaviour as a null-safe injected delegate, so the wiring lands in one place.
`MatchOverviewTabViewModel`'s constructor now takes, after `viewStats` / `viewPlayback`:

| Delegate | Must do |
|---|---|
| `Action<string> computeFullStats` | Enqueue that demo path on `IDemoProcessingQueue` at interactive priority. It routes through `HeavyJobGate`, shows in the processing-queue chip, and fans out to every evaluator — so one press fills parse gaps, scoreboard and highlights together. **Must not open the demo.** |
| `Action<string> openDemo` | The normal load funnel for that path (what a Library double-click does). |
| `Action returnToLive` | Re-render the demo that is actually open. See the resolved open question above — the shell owns this because only it can re-derive live pipeline state. |
| `Func<int,string?,CancellationToken,Task<bool>> verifyMoment` | `LiveSync.VerifyMomentAsync(tick, spectateName, ct)`. Tick is frame clock, passed AS-IS. |
| `Func<bool> isVerifyPresent` | `MainViewModel.IsLiveSyncEnabled` (the `chrome.livesync` gate). |

Plus two contracts the wiring pass must honour:

1. **`LiveDemoName` is shell-owned and is NOT cleared by `ResetValues()`.** Set it when a preview is shown
   while a demo is open; **null it on `BeginOpening`**, or a later preview can offer "◀ Back to \<the demo
   before last\>". Harmless today (`CanReturnToLive` also requires `Mode == Cached`), but it is a real trap.
2. **Single-click preview must call `SetCachedRecord`, never `BeginOpening`.** The latter means "a load is
   starting" and would light the stage strip for a demo nothing is doing anything to.

<a id="95-shell-wiring-owed"></a>
### 9.5 Shell wiring owed by the step-7 pass (deliberately NOT done in the Reels-dashboard step)

The modal is gone from the tab, but it cannot be *deleted* by the step that removed it:
`App.axaml.cs:695-715` still constructs a `HighlightReelDialogViewModel` and calls
`IWindowService.ShowHighlightReelDialog`, and `IWindowService.cs` still references
`HighlightReelDialogWindow`. Neither file is in step 7's ownership, and the build gate includes both. So step
7 left **null-safe seams** and this table, exactly as §9.4 did for step 5.

**Everything below is optional / null-safe.** The dashboard is fully functional un-wired except that the
reel service, the interlock probe, the platform mode, the inline job strip and the Add-clips picker are
absent — each degrades to a disabled control with an honest tip, never a dead one.

#### A. New constructor parameters on `HighlightsTabViewModel` (all optional, all appended AFTER the existing four delegates)

| Parameter | Type | Must be | Today, un-wired |
|---|---|---|---|
| `reelJob` | `IReelJobService?` | `MainViewModel.ReelJob`, resolved lazily like the other Highlights delegates. Null on Browser. | Generate hands off to nothing (`_reelJob?.Start`) |
| `isLiveSyncSessionActive` | `Func<bool>?` | `() => shell.LiveSync?.State.IsSessionActive ?? false` — the §9 single-CS2 interlock probe | Interlock strip never appears; reels start immediately |
| `dryRunOnly` | `bool` | `OperatingSystem.IsMacOS()` (§8.9) | Primary reads "Generate reel" on every platform |
| `requestAddClips` | `Action?` | Opens the §4.4 cross-demo picker — **step 9** | `+ Add clips…` renders DISABLED with "Coming next…" tip |
| `fileExists` | `Func<string,bool>?` | leave null (defaults to `File.Exists`) | tests/captures inject it |

⚠ **The existing positional order is unchanged on purpose.** `openInWorkspace` / `isVerifyPresent` /
`canVerify` / `verifyMoment` are still parameters 5-8 and are now **accepted and discarded** (their surfaces
re-homed to Match Overview, §5 rows 7/8). The wiring pass should **delete those four arguments and
parameters together** — removing them before `App.axaml.cs` is rewired breaks the build.

#### B. Settable properties the shell assigns after construction

| Property | Type | Must be |
|---|---|---|
| `JobStatus` | `ReelJobStatusViewModel?` | ⚠ **The SAME instance the status-strip chip is bound to** (§4.3: the inline strip is a second VIEW of one job, never a second job model). The tab subscribes to its `PropertyChanged` and **never disposes it** — the shell owns its lifetime |
| `EnrichmentSections` | `ObservableCollection<object>` | Append enrichment VMs **at composition only** (§7). They must derive from `ViewModelBase` or `ViewLocator.Match` refuses them. Call `NotifyEnrichmentsChanged()` after the last add |
| `ReelConfig.IsEncodingVisible` | `bool` | Bind to the `highlights.encoding` SubFeature once it is minted (**step 9**, §6.3 `Defaults(false, true, true)`). Defaults `true` |

#### C. Deletions the wiring pass performs

| Delete | Where | Note |
|---|---|---|
| `tabVm.RequestCreateReel += …` block | `App.axaml.cs:695-715` | Then delete the event itself. It survives as an **explicit-accessor no-op shim** on the tab VM (`add {} remove {}`) purely so this subscription keeps compiling — a never-raised field-like event is CS0067, which this repo treats as an error |
| `IWindowService.ShowHighlightReelDialog` | `Services/IWindowService.cs:43-48, 176, 265` | Both implementations |
| `Views/Highlights/HighlightReelDialogWindow.axaml{,.cs}` | — | The last reference is `IWindowService.cs:180` |
| args 5-8 of the `HighlightsTabViewModel` ctor | `App.axaml.cs` + the VM | See the ⚠ above — one commit, both sides |

`HighlightReelDialogViewModel` / `HighlightReelDialogView` **stay** (they are the config pane).
`HighlightReelDialogViewModelTests` guards the ViewLocator name mapping, so the `ReelConfig*` rename is
tracked as owed debt in `design-system.md`, not done here.

#### D. ⚠ Tray persistence is INERT until the shell wires it (fork 8)

`SnapshotState()` / `RestoreState()` are implemented and tested (ordered `HighlightKey`s as a plain DTO
list, re-resolved against the live cache on restore, vanished keys dropped with a one-line note). **But
`IWorkspaceTabViewModel.SnapshotState()` has a default returning `null` and ZERO call sites outside tests** —
the module framework declares the contract and nothing invokes it. Module tab state is not written to disk
today. So the tray survives tab switches (it lives in the retained VM) and **does not survive a restart**.
Fork 8's stated goal — "a half-built cross-demo reel evaporating on restart is exactly the loss this tab now
exists to prevent" — is therefore **not met by step 7**. The shell owes: call `SnapshotState()` on each
module tab at shutdown, persist it, and hand it back through `RestoreState()` on the next run.

#### C2. ⚠ Live copy mismatch as of the step-7 commit

`FeatureCatalog.cs:58-60` still describes `tab.highlights` as *"Browse analysis-generated highlights across
your library and build highlight reels. Viewing surface."* — half of which no longer exists, on a tab now
headed "Reels". §6.4 assigns the rewrite to step 9; noting here that the mismatch is **live now**, not
pending. Proposed replacement is in §6.4. (`IWorkspaceModule.DisplayName` was also changed to "Reels";
grepped — it has **zero consumers** in the app and is not a persisted key, so it is display-only metadata.)

#### D2. Ordering reaches the finished video — verified, not assumed

The tray's ▲▼ would be a lie if order stopped at our boundary. It does not. `_plan` (sorted by group
first-appearance, then `StartTick`) → `ReelRequest.Clips` → `ReelJobService` (`DryRunAsync` walks it by
index; `BuildCompilation` maps it 1:1 with `request.Clips.Select(...)`) → `Cs2Compilation.Clips`, with **no
re-sort anywhere in this repo**. The shipped `Cs2VideoGenerator.Core` XML docs close the last hop:
`Cs2Compilation.Clips` is *"Ordered list of clips to capture. **Processed sequentially**"* and
`Cs2CompilationSettings.ConcatenateClips` *"uses FFmpeg to **combine clips in order**."*
`ClipWindows.Coalesce` was **not** touched and remains order-independent.

#### E. Staging seam for step 8 (Match Overview `[ + ]` → tray)

`MatchOverviewTabViewModel` already builds `OverviewHighlightRow`s with an `onStage` callback and an
`IsStaged` flag (step 5 stubbed it deliberately). Wire it to:

```csharp
// stage:   tab.StageFromCache(demoPath, rulesetId, highlightId, tick, playerSlot) -> bool
// unstage: tab.Unstage(new HighlightKey(demoPath, rulesetId, highlightId, tick, playerSlot))
// initial: tab.IsStaged(key)          // O(1) dictionary lookup, safe per row build
```

`StageFromCache` resolves the `DemoCacheRecord` from the store itself, because Match Overview renders
from the demo cache and holds no record of its own — and the tray needs that record's tickRate / tickCount /
rounds to compute a window at all. It returns `false` when the demo or the highlight is no longer cached.

<a id="96-shell-wiring-owed"></a>
### 9.6 Shell wiring owed by the step-9 pass (the Add-clips picker + the scan chip)

Same shape as §9.4 and §9.5: everything below is **optional and null-safe**. The picker is fully functional
un-wired — it is tab-owned, needs nothing from the shell, and never touched `MainViewModel.cs`. What the
shell owes is the **status-strip chip** and (optionally) the **feature gate**.

#### A. The scan `StatusChip` — the FOURTH consumer (§5 row 2)

`ViewModels/Highlights/HighlightScanStatusViewModel` + `Views/Highlights/HighlightScanStatusView`
(ViewLocator-resolved flyout body). It owns a `StatusChipViewModel Chip` whose `FlyoutContent` is itself —
the Live Sync / Reel-job / processing-queue pattern, extended rather than forked.

```csharp
// Composition (App.BuildRegistry already resolves both singletons):
HighlightScanStatusViewModel scanStatus = new(hlScanner, hlStore);   // (scanner, store)
// Shell:
tabVm.ScanStatus = scanStatus;              // §9.5-B pattern: settable, shell-owned, never disposed by the tab
ReconcileHighlightScanChip();               // add/remove scanStatus.Chip from Chips on scanStatus.IsRelevant
```

| Contract | Detail |
|---|---|
| **Presence rule** | Add `Chip` to `MainViewModel.Chips` while `IsRelevant` (`QueueDepth > 0 \|\| IsScanning \|\| FailedCount > 0`), mirroring `ReconcileQueueChip`. An idle, fully-indexed library must add no strip clutter. The VM raises `PropertyChanged` for `IsRelevant` on every scanner/store change, so subscribe to that rather than polling. |
| **Also AND `!OperatingSystem.IsBrowser()`** | Scanning needs a filesystem. Same shim the processing-queue chip uses. |
| **Gate** | ⚠ **None.** Deliberately not gated: it appears only when work is happening, and `chrome.processingQueue` already establishes that "background work on the user's behalf" is visible to every category. If a gate is later wanted, reuse `chrome.processingQueue` rather than minting an id. |
| **Lifetime** | `IDisposable` (it subscribes to `HighlightScanService.ScanProgressChanged` **and** `DemoCacheStore.Changed`). The shell disposes it; the tab never does. |
| **⚠ Duplication is SAFE here — unlike `JobStatus`** | §4.3 forbids a second reel-job model because that VM holds job state. This one holds **none**: every property is derived from the scanner + the cache rows. So the shell may construct its own instance at composition (so the chip exists **before** the lazily-built Reels tab is first activated) and still assign the same or a different instance to `tabVm.ScanStatus` — they cannot drift. Prefer one instance anyway; just don't contort the composition order to get it. |

**Deletion trigger (do this in the same commit).** `HighlightsTabView.axaml`'s header still carries the
ad-hoc scan badge, marked TRANSITIONAL in the markup. It mirrors `ScanStatus.Chip.Label` when the shell has
assigned one and falls back to the tab's own `ScanQueueSummary` when it has not, so scan progress is never
invisible. Its visibility is `ShowScanBadge`, which follows **whichever source it is showing**
(`ScanStatus.IsRelevant` when assigned, else `ShowScanChip`) — `ShowScanChip` alone is queue/scanning only, so
with failures and an idle queue the badge would have hidden while displaying a "3 failed" label, which is the
one state it exists to cover. **Once the chip is registered in `Chips`, delete that `Border Classes="badge"`
block** and, if nothing else uses them, `ScanQueueSummary` / `ShowScanChip` / `ShowScanBadge` /
`HasScanStatus` on the tab VM.

**One more contract you inherit for free.** `HighlightsTabViewModel.RestoreState` now accepts BOTH the DTO and
the `JsonElement` your session round-trip produces, and reads it **case-insensitively**. Today your writer sets
no naming policy and `SettingsService`'s section options set none either, so both sides say `StagedClips` — but
that agreement is incidental. If a `JsonSerializerDefaults.Web` is ever added to either path, a case-sensitive
read would bind nothing and the tray would restore empty with no error raised anywhere. The read is immune;
the test `Snapshot_Survives_A_Real_JsonRoundTrip` pins it with a camelCase payload.

#### B. `highlights.encoding` — minted, and self-wiring

The descriptor exists in `FeatureCatalog.cs` (`ParentId "tab.highlights"`, `Defaults(false, true, true)`, no
`GroupId`, so the parserDeepDive / graphDebug leader-lock ordering is undisturbed). The tab VM takes an
optional **final** ctor parameter `IFeatureGate? featureGate` and applies it to `ReelConfig.IsEncodingVisible`
**on construction and again on `IFeatureGate.Changed`**.

- **Preferred:** pass the DI-resolved `IFeatureGate` as the last argument in `App.BuildRegistry`. One line,
  and it re-reconciles when the user toggles the SubFeature in Settings.
- **Also fine:** keep passing nothing and assign `tabVm.ReelConfig.IsEncodingVisible` from the shell — but
  then the shell owns re-applying on `FeatureGate.Changed`. ⚠ A one-shot assignment is the failure mode: the
  section stays wrong until the tab is rebuilt.
- Null gate (tests, UiCapture) ⇒ visible. A missing gate must never silently remove a section.

#### C. Deletions the wiring pass performs (adds to §9.5-C)

| Delete | Where | Note |
|---|---|---|
| `requestAddClips` arg + parameter | `App.axaml.cs` (8th positional arg, currently `null`) + `HighlightsTabViewModel` | The picker is tab-owned; the parameter is **accepted and discarded**, exactly like args 5-8. One commit, both sides. |

#### D. What step 9 did NOT need from the shell

The picker is an **overlay inside the tab**, not a window: a window would need `IWindowService` — the surface
§9.5-C is stripping — and would be unreachable on the browser host. It is therefore reachable, testable and
capturable today with zero shell involvement. `MainViewModel.cs`, `App.axaml.cs` and the session plumbing were
not touched.

---

## 10. Open forks (the pick marked per fork; all resolved in the decisions table at the top)

**Fork 1 — cache storage shape.**
- (a) **Thin `index.json` + per-demo sidecars** — the pick. Small always-loaded index for the Library grid; the fat
  record loads lazily for exactly the demo Match Overview is rendering; a backfill writes one small file per
  demo instead of rewriting a growing monolith (~2.4 GB of write traffic over a 719-demo sweep at 3.4 MB).
  **Cost:** the Add-clips picker wants highlight rows across all demos, so it must load sidecars on demand —
  mitigated by carrying `highlightCount` in the index and loading lazily as the user filters.
- (b) One unified monolithic JSON — simplest migration, one file to reason about; but it keeps both problems
  the split solves and gets worse as the library grows.
- (c) SQLite — best scaling and query story; but a new dependency, a WASM problem, and a migration this
  project does not need at 719 demos.

**Fork 2 — how T3 coverage grows** (the product decision that had to be made explicitly).
- (a) **Extend T2 + retarget T3 to a per-demo `Compute full stats` action, keeping the background opt-in**
  — the pick. The cheap pass already covers 80%, the extension is ~+0.5 KB/demo of data already in hand, and per-demo
  computation grows T3 along the demos users actually look at.
- (b) Promote T3 to automatic background — full stats everywhere eventually, but ~30 min per 200 demos of
  rules-engine work made ambient, contradicting the "heavyweight actions are explicit" principle.
- (c) Extend T2 only, leave T3 exactly as it is — cheapest, but leaves the scoreboard and highlights sections
  perpetually empty for ~99% of the library with no per-demo escape hatch.

**Fork 3 — Match Overview body layout.**
- (a) **Two columns at ≥1000px** (`1.3*` match · `*` moments), single column below — the pick. Puts scoreboard and
  highlights side by side, halves the scroll, and the collapse mechanics are the shipped responsive pattern.
- (b) Keep the single 920px centred column with highlights appended — zero layout risk and preserves the
  existing test metrics, but the page becomes a long scroll and highlights land below the fold on every demo.

**Fork 4 — the hero band's mode affordance.**
- (a) **Merge identity + score + completeness chip + progress rail into one fixed-height hero band** — the
  pick. One slot, no mode-dependent height, both headline facts above the fold. Cost: a denser band that must be
  rendered and contrast-checked across all four themes before it ships.
- (b) Keep today's separate identity hero / stage-strip card / score card and add the completeness chip to the
  stage strip — smaller diff, preserves the existing landing-test metrics, but keeps ~220px of chrome above
  the first real content.

**Fork 5 — tab header.** The tab is no longer a highlights browser.
- (a) **"Reels"** — the pick. Names what the tab does and makes Match Overview unambiguously the highlights surface.
- (b) Keep "Highlights" — zero churn, but the name now describes a section on another tab.
- (c) "Highlights & Reels" — hedges; re-blurs the split the redesign exists to create.
**Either way `TabId "highlights.browser"` and `"tab.highlights"` are persisted keys and must not change.**

**Fork 6 — single-click feedback.**
- (a) **`Overview ▸` chip on the selected Library card** — the pick. Zero contract change, discoverable in place.
- (b) Echo the demo name into the Match Overview tab header — nicer, but `WorkspaceTabDescriptor.Header` is
  `required … { get; init; }` and is bound directly in `MainView.axaml:222`, so it needs the descriptor to
  become observable: a `Modules.Abstractions` contract change for cosmetics.
- (c) Accept silence — cheapest, and leaves the new capability undiscoverable.

**Fork 7 — WASM Highlights registration.**
- (a) **Keep registered + degraded, rewritten copy** — the pick. Preserves the reasoned ux-design §1 decision; the
  browsing payoff genuinely relocates to Match Overview, which is present on the browser host.
- (b) Unregister on browser now that the tab's whole purpose is desktop-only — honest, but reverses a decision
  made for a good reason and makes tab sets host-dependent.

**Fork 8 — does the tray persist across app restarts?**
- (a) **Persist** — the pick (`SnapshotState` list of `HighlightKey`s, vanished keys dropped with a note). A
  half-built cross-demo reel evaporating on restart contradicts the tab's new purpose as a workspace.
- (b) Session-only — simpler, no stale-key handling.

---

## 11. What I'd cut

1. **SQLite / any database for the cache.** At 719 demos and ~3.4 MB, split JSON is sufficient, debuggable and
   hand-repairable. Revisit at library sizes or query patterns that JSON genuinely cannot serve.
2. **Caching kill feeds, damage matrices, or per-round timelines.** They scale with events rather than with
   demos, no glance surface consumes them, and they would dominate the record. If an enrichment needs one, it
   is a T4 sidecar of its own.
3. **A module section-contribution contract** (§8). Real architecture built for an audience of one. The
   shared-service route plus the §7 enrichment slot leaves the door open at zero cost.
4. **Promoting `StagedClipRow` to a shared control up front.** It appears in the tray and the picker (≥2×),
   but staged and stageable rows differ in affordances. Build both, validate, then promote — pre-abstracting
   buys a parameterised control nobody has used.
5. **Filters over the staged tray.** Machinery without a job (§4.4). A single free-text box, and only once
   trays routinely exceed ~10 clips.
6. **Reproducing the per-card scan animation.** Ambient reassurance tied to a spatial card; the status chip's
   pulse plus a demo name delivers the same information at a fraction of the surface (§5).
7. **An "index this demo" affordance anywhere on the *cached* path other than the completeness chip.** Cached
   render's credibility rests on "this page starts no work unless you press the one button that says it
   will." Scattering rescan buttons across sections erodes exactly that.
8. **Any heuristic reconstruction of missing tiers.** No inferring team splits from name order, no estimating
   ratings from a score. This page's own comments repeatedly choose a missing value over a wrong one
   (`ComputeSideWins` returns nulls when teammates disagree; the side split suppresses itself unless it
   reconciles). The completeness chip exists so that missing data is *explained*, not *faked*.

---

<a id="97-tier-3-producers"></a>
## 9.7 Tier 3's producers, and how the absorption landed

Steps 5–9 built every tier-3 *reader* against a record that, for a while, nothing wrote. Step 4 landed its
migration half before its absorption half, so `HighlightScanService` went on writing `highlights.json`
alone. From the moment the migration ran until the writer moved, a scanned demo had highlights the Reels tab
could see and Match Overview could not — and with ~346/348 legacy rows still `Pending` at migration time,
effectively no demo had tier 3 at all. The section whose entire purpose was showing highlights read "needs a
full analysis pass" forever, on every demo, no matter what the user did. That is fixed: there is one store.

### Tier 3 has two producers, because its halves have different costs

| Half | Producer | Why it cannot be the other one |
|---|---|---|
| **Highlights** | `HighlightScanService.MirrorToDemoCache` — mirrors its row on every completed scan and on failure | The scan runs `CaptureSnapshots = false`. That is what makes a library-wide sweep affordable at all |
| **Scoreboard** | `MainViewModel.WriteTier3ScoreboardToCache` — stores the interactive run's own `MetricTable` | Per-player stats are projected from the final snapshot vector, which bare mode never produces. Only a real open runs snapshot mode |

Consequences worth knowing before changing any of it:

- **`FULL` requires the scoreboard, not merely a stamped analysis tier.** Reading both halves off one flag
  rendered a `FULL` chip directly above *"Analysis produced no per-player stats for this demo."* — one
  screen, one demo, two contradictory claims — and retired the `Compute full stats` button that fixes it.
- **Opening a demo is what fills the scoreboard**, and it costs nothing extra: the table already exists by
  the time `SetAnalysis` is called. A demo you have opened once renders `FULL` from then on.
- **A forced scan runs snapshot mode; the sweep does not.** `_forcedPaths` is what distinguishes them. Two
  consequences that are easy to undo by accident: the "row is no longer `Pending`, skip it" guard at the top
  of `Evaluate` **must not apply to a forced path** — both owners coalesce onto one parse, so the Library's
  fan-out can mark the row `Indexed` with a bare run first and silently consume the user's press — and
  `RunFullAnalysis` must stay defaulted on the interface so test fakes keep compiling.
- **The scoreboard ROWS are route-independent** (both producers call `DemoCacheAnalysisProjector`), but
  `AnalysisRoundCount` is not: the shell passes `StatsTab.Rounds.Count`, the scanner the scan row's
  `Rounds.Count` (derived from `round_freeze_end`). They usually agree; they are not the same source.
- **The flip to `Pending` is deliberately NOT mirrored.** Pending is scan-queue state, not cache-tier state;
  mirroring it would mean a rules save (which marks every row `Pending`) instantly blanked the highlight
  section of every demo in the library. Staleness already has an honest signal in `IsAnalysisCurrent`.

### The mtime trap

The library indexer stamps `FileInfo.LastWriteTime` (**local**); the scanner stamps `LastWriteTimeUtc`.
Routing a tier-3 fill through the identity-asserting `DemoCacheStore.Update` hands a UTC tick count to a
locally-stamped record, `MatchesFile` fails for every user not on UTC, and the "identity drift discards
everything" rule throws away the tier-2 roster and score **on every scan** — which would present as the
library spontaneously forgetting demos it had already indexed. `UpdateExisting` exists for this: a later
tier fill keeps the identity its establishing writer set, in that writer's units.

Unifying the two conventions is *not* a safe cleanup: every record on disk was written in the library's,
so changing it invalidates the whole cache and forces a full re-index.

### How the absorption landed

It went in three commits, readers before writer, so that at no point did a surface read a store nothing was
writing:

1. **The readers.** Every tier-3 reader (clip tray, Add-clips picker, reel config pane, `ClipWindows`) moved
   onto `DemoCacheStore` / `DemoCacheRecord`. The clip-window math whose tick rules are load-bearing for CS2
   sync (`docs/csvg-integration/implementation-plan.md` — disturb them and you get subtly wrong sync) was
   re-homed here, against the same tick semantics: `CachedHighlightEvent.Tick` is
   frame clock exactly as `CachedHighlight.Tick` was, and the lead-in floor still looks rounds up BY TICK.
2. **The writer.** `HighlightScanService` stopped touching `highlights.json` and the scan backlog
   became derived.
3. **The deletion.** `HighlightsCacheStore` and `HighlightsCacheModels` are gone. What survives is the DTO set
   in `Services/DemoCache/LegacyHighlightsModels.cs` — `LegacyHighlightsRow` and friends, prefixed because
   `CachedRound` and `CachedPlayer` would otherwise collide with the live types in that namespace, and named
   to make it obvious they are read once by `LegacyCacheMigration` and never at runtime. `AppPaths.
   HighlightsCacheFile` survives for the same reason. That file is now the only surviving description of the
   retired on-disk format, including its PascalCase convention, which is why it keeps fields the migration
   does not even read.

**The backlog is derived, never stored.** `HighlightScanService.BacklogNewestFirst` walks the index and takes
every row where `NeedsAnalysis(fingerprint)` holds — not current under the live rules fingerprint, with
`Failed` excluded — plus any library path the cache has never seen. `RefreshStalenessCore` consequently
**writes nothing at all**: "refreshing staleness" is re-deriving the list and asking the coordinator to
reconsider.

Two properties fall out of that, and both are load-bearing:

- **A rules save no longer blanks the library.** The old persisted `Pending` flag meant one field for two
  different facts — "queued" and "has no tier-3 data" — so marking every row `Pending` on a config change
  also erased every demo's highlight section until it was rescanned. Deriving separates them: the previous
  harvest stays on screen while its demo waits, and the page says it is stale.
- **The scanner must never prune.** `RefreshStaleness` used to drop rows for demos outside the current
  library view, which was safe when it owned a highlights-only store. Against the shared cache the same call
  deletes whole records, taking the Library's tier-2 roster, score and rounds with them — and a configured
  folder on a detached volume enumerates zero files, so it would fire exactly when the demos are fine.
  Pruning belongs to the Library (`PruneStaleCacheRows`, with its reached-roots guard). There is a test
  naming this.

### The mtime divergence, resolved

The trap described above is not fixed by unifying the two conventions — that would rewrite what every record
on disk means and force a full re-index. It is fixed by **giving identity exactly one owner**. The Library
establishes `Size`/`ModifiedTicks` in its own units; every tier-3 write goes through
`DemoCacheStore.UpdateExisting`, which preserves whatever the record already carries and re-states nothing.
The divergence stops mattering because only one writer ever speaks.

One read-side residue outlived the writer move and was fixed with the deletion: `SafeFileIdentity` — the
scanner's freshness probe, used only by the `OnParsedOpportunistically` early-out — still returned
`LastWriteTimeUtc.Ticks` and compared it against a locally-stamped record. Nothing was corrupted, because the
value is only ever compared and never written, but for every user off UTC the compare could not match, so the
early-out never fired and a perfectly current demo was re-harvested on every hand-off. It reads local ticks
now. **Aligning a comparison is safe in a way that aligning a stored value is not** — it invalidates nothing.

<a id="98-half-score-repair"></a>
## 9.8 The half-score repair — shipped 2026-07-29 as an explicit action

### The problem

On the reference library 555 rows carry a CT score and 3 carry the T score — states `ExtractFinalScore`
cannot emit, left behind when `TScore`/`TClan` were renamed to `Score`/`Clan` and every
already-written row silently stopped deserializing its T side. `ScoreComputed = true` then guaranteed nothing
would ever recompute them, and `HasScore` needs both sides, so essentially the whole library lost its score
badge permanently.

### The decision

The repair used to run **automatically at hydrate**: clear the half data, drop `ScoreComputed`, let the row
rejoin the tier-2 backlog. Correct in the small, and at scale that is **342 demos / ~100 GB of background
re-parsing on the first launch after upgrade**, on a library that looks unchanged. The call: it ships
as an explicit action.

### What shipped

**Nothing is repaired in place. The half score is refused at the READ boundary.**

`ApplyCache` tests each hydrated row with `IsScoreResultCoherent`; an incoherent one has its score withheld
from the `DemoEntry` and sets `ScoreRepairPending`. The cache row itself is never touched.

| Piece | Where |
|---|---|
| Detect + withhold | `DemoLibraryService.ApplyCache` (`HasIncoherentScore` is the pure predicate) |
| Count offered | `DemoLibraryService.ScoreRepairPendingCount` — over `Entries`, **not** the cache |
| The action | `DemoLibraryService.RepairPendingScoresAsync` |
| Card state | `DemoEntry.NeedsScoreRepair` → the `score ?` badge |
| Toolbar action | `LibraryTabViewModel.ScoreRepairLabel` / `RepairScoresCommand` |

### Three things that are load-bearing

**1. Refusing at read, rather than repairing the row, is what makes the state unloseable.** The obvious
design — clear the row at hydrate and set a persisted marker — is a trap. Cleared-to-all-nulls reads as
COHERENT to `IsScoreResultCoherent`, so the marker has to be persisted to survive a restart; and because
`UpsertCache` mutates the very row that was cleared, **any** later `Save` (a tier-1 map write will do)
persists the cleared row. Lose the marker in that window and the demo is silently scoreless forever with
nothing left on disk to detect it. Leaving the original half data on the row makes it permanent evidence,
re-derived correctly on every launch. It also means **no new persisted field and no schema bump**.

**2. The action must enlist directly, not by rescanning.** `Reconcile` only evaluates NEWLY-discovered files
— an entry already in `Entries` hits its `continue` and is never re-tested for the backlog. So on a populated
library (i.e. always, in the real app) routing the action through `RescanAsync` flips `ScoreComputed` on disk
and enlists **nothing**: a button that mutates the cache, reports success, and parses nothing. `_pendingFull`
IS the Library evaluator's `Wants` gate, so populating it is the enlistment. This was caught by a test, and
only because that test drove a service that had already scanned — a fresh-service test passes either way,
since every entry is new there.

**3. The count is over `Entries`, not over cache rows.** 552 rows were repairable but only 342 had a file
still on disk; the rest were under a folder the user had removed and can never be re-derived. A button
offering 552 and doing 342 lies about the work it is proposing.

### Termination, and the one automatic path

A re-derived row satisfies the contract whatever the extractor returned — including the honest all-null
answer — so it is never flagged again. Termination is structural, not bookkept.

The single way flagged rows re-enter the **automatic** backlog: an interrupted repair. `RepairPendingScoresAsync`
persists `ScoreComputed = false`, so if the user quits mid-run the remaining rows are picked up by the next
launch's ordinary backfill. That is intended — they asked for the work and quitting is not a retraction — but
it is worth knowing when reading the "never automatic" rule above. Rows never pressed stay out of it entirely.

### Tests

`DemoLibraryCacheRepairTests` (16). The two that changed meaning: `HalfScore_IsWithheldFromTheCard_ButNeverReIndexesOnItsOwn`
(was `..._ReIndexedOnce_AndNeverAgain`, and demanded the opposite) and `ClanWithoutScore_IsRepaired_Once`.
`RepairPendingScores_ReDerivesOnce_ThenClearsTheFlag` covers the action end to end. The parse COUNT per
launch is the assertion throughout — the queue's injected parser throws on any unexpected parse, so an
accidental return to automatic sweeping fails loudly rather than being counted.
