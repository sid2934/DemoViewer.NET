# The Strat Room: research and implementation plan

**Status:** Written 2026-09-23 against `main` at `d90ec9f` (0.8.1), CS2DemoKit 0.12.0; the nine
review-required designs in this folder were approved 2026-09-24 and `00-overview.md` reconciles them.
**Source of scope:** the original Strat Room roadmap (September 2026). **Tree surveyed:** `main` at
`d90ec9f` (0.8.1), CS2DemoKit 0.12.0, VRF 19.2.6339, Cs2VideoGenerator.Core 0.9.0.
**Upstream dependencies (CS2DemoKit):** #53 delta user commands, #54 rules-driven round facts (the
Round Facts build waits on it), #56 smoke creation packet, #58 `PositionSampler` contract, #59
`ProjectileSampler`.
**Measurements:** every number in the designs was measured with scratch console projects over real
Valve matchmaking demos from the Steam replays folder. The probes were throwaway console projects and
are not committed; each design's §10 names its probe, its modes and the demos it ran on, so any of
them can be re-created from the document. The tour sample was never used as evidence.
Record as of 2026-09-26.

Every work item has a human-readable name, and those names are the
only way anything is referred to, here or in conversation. No abbreviations.

---

## 0. How to read this

- **§1** is what the roadmap asks for, compressed to what has to be built.
- **§2** is what the survey found in the tree that changes the roadmap's assumptions. Read this before
  the catalogue; several of the roadmap's dependency claims are wrong in useful directions.
- **§3** is the work-item catalogue: every item, what it is, what it needs, and whether it needs human
  review before code.
- **§4** is the order, as phases with gates.
- **§6** is the list of decisions.

The status board and the dated decision log that tracked the build are not part of this record.

**Kinds of work item.** `research` produces a written answer and no code. `design` produces a written
proposal that stops for human review. `build` is implementation, and starts only when its `design` (if it
has one) is approved. Anything that adds a sizable new concept to CS2DemoKit, the asset baker, or the
CSVG game plugin is `design` first, without exception; that is the rule for this work.

**Statuses:** `not started` · `researching` · `awaiting review` · `approved` · `in progress` · `done` ·
`deferred` (with a reason) · `dropped` (with a reason).

---

## 1. The scope, compressed

Six ranked features, with the roadmap's phase order in brackets:

| Feature | One line | Artifact phase |
|---|---|---|
| **Situation Search** | Sketch a setup on the radar; get every round across every indexed demo where it happened. Seeded from playback with one key. Live result count, tolerance slider, result cards, overlay, saved "watched situations". | 1 |
| **Round Tagger** | Sports-analysis code/label model on the timeline: hotkeyed panel flow, lead/lag per button, click-to-tag position, sticky labels, label mode, rule-engine suggestions with accept/edit/reject, and the Matrix pivot where every count opens its clips. | 2 |
| **Strat Book** | A strat is a diagram plus the set of tagged rounds where it was run. Slots A–E, steps on the round clock, branches, lifecycle status, create-from-round, interpolated step authoring on the existing canvas, GIF/MP4 export, record panel with failure breakdown, version diffs, role view, LAN print, walk-it-in-the-server. | 3 |
| **Utility Book** | Every grenade in every indexed demo, clustered by landing point, searchable by destination, each with throw origin, setpos/setang, jump-throw flag, movement, an auto-rendered clip, attachable to a strat step, pushable to the server. | 4 |
| **Review Packs** | An ordered, multi-demo clip playlist with annotations burned in and a per-clip question, exported as one video or a folder plus index, buildable headlessly from the CLI. | 4 |
| **Opponent Dossier** | A generated, editable scouting document: veto model, CT setups by buy type as overlay heatmaps, T opening tendencies, post-plant and retake, situational behaviour; sample sizes on every claim, every number a link to clips, two-period diff, short and long export. | 5 |

Plus the roadmap's stated prerequisite: **issue #5, zone baking** (nav place polygons and bombsite
volumes in the asset bundle), which it calls "the one blocking prerequisite" for half of the above.

Explicitly out of scope per the roadmap: a general stats dashboard, tier-1 targeting, cloud sync or
accounts, trajectory clustering or strategy classifiers, win probability or team ratings, a 3D renderer.

---

## 2. What the tree actually says (findings that change the plan)

Numbered so the catalogue can cite them.

**F1. The round-index token does not need issue #5.**
`CS2DemoKit.Parser.EntityTracking.PositionSampler.Walk(demo, from, to)` streams every player's world
position per frame **with `Place` = the pawn's `m_szLastPlaceName`** ("BombsiteA", "Ramp", …), the
exact nav-place token ggViz discretises onto. `rules/highlights_position.rules.yaml` already consumes
it as `player.place`. Measured by the package: 1.6M samples in 3.6 s on a 223k-frame match, lazy. So
Situation Search's index can be built today from the parser alone. What #5 *does* gate: turning a
click on the query canvas into a place, turning a grenade landing point into a place, drawing place
outlines on the canvas, and resolving "inside bombsite volume" for arbitrary points. That moves #5 off
the critical path for the flagship and onto the critical path for the query canvas polish and the
Utility Book.

**F2. The baker already parses the nav mesh.** `tools/DemoViewer.NET.AssetBaker/NavFloors.cs` reads
`maps/<map>.nav` through VRF's `NavMeshFile` to derive floor bands. VRF 19.2 also ships
`ResourceTypes.EntityLump` (for `func_bomb_target` / bombsite volumes). Issue #5 is therefore an
extension of existing baker code, not new territory. Open question: whether VRF's `NavMeshArea`
exposes the per-area place-name index (the `.nav` format carries a place-name table). That is the
first thing Zone Baking has to establish.

**F3. LiveSync cannot place a player.** `ISyncClient` is the entire outbound surface: load, close,
pause, resume, seek (acked and unacked), timescale, spectate-by-name. All of it goes through
`Cs2VideoGenerator.Core` (an external package, 0.9.0) and its CSVG game plugin, and all of it drives
**demo playback**. "Walk it in the server" and "push a lineup to the server" need a running practice
server with `sv_cheats` and a `setpos`/`setang` command path, which is a different CS2 mode and a
plugin+protocol change in an external project. `docs/plugins/plugin-system-design.md` explicitly scopes
CSVG extension out. These two sub-features are **deferred** until it is decided whether to open
that door (§6, D4).

**F4. "The opponent" and "our team" do not exist as data.** `DemoCacheRecord` carries `CtClan`/`TClan`
(populated on pro/HLTV demos, empty on matchmaking) and per-player SteamIDs. Nothing groups demos by
who played in them. Yet Watched Situations ("their Mirage A setup: 3 new"), the multi-demo Matrix
("Falcons, last 6 demos"), the Strat record split (our scrims vs officials), and the entire Dossier
assume a stable team identity across demos. This is the largest unnamed dependency in the roadmap
and needs a design: roster clustering by SteamID overlap, user-named teams, and a "this is us"
designation.

**F5. Per-round facts are thin.** `CachedRound` is `Number` + `StartTickFrameClock`, nothing else. The
roadmap's "the parser already knows most of the labels" (side, both buy types, score, end reason,
plant site and time, man-count at each kill) is true of the demo, not of anything cached or exposed.
The engine has freeze-end economy sums (`round.team.equipment`, `PlayerEconomyFreezeEndEdge`) but no
buy-type classification (eco / force / full / pistol thresholds) and no team-scoped per-round output
that the app reads today. Round Facts is its own foundation item.

**F6. Grenades: partial in the engine; inputs are on the wire but mostly unreadable today.**
*(Corrected 2026-09-23. The first draft said "GOTV demos carry no input buttons"; that was wrong.)*
The analysis layer indexes smoke and molotov projectile slots (`ProjectileSlotIndex`) for the
visibility solver only; `GrenadeThrown` is bound in the HLTV profile and not the GOTV one; detonation
events exist for all types. There is no "every grenade with origin, release angle, landing point"
walk.

On inputs, measured with a scratch probe over CS2DemoKit 0.12.0 against untrimmed `GotvMatchmaking`
demos in the Steam replays folder (never the tour sample, whose trimmer strips `svc_UserCmds` by
design):

| Demo | Build | Commands (30k frames) | Full `data` | `delta_data` | Decoded buttons |
|---|---|---|---|---|---|
| Jan 2025, de_nuke | 10231 | 300,030 | 300,030 | 0 | attack 7,377 · jump 1,534 · duck 7,816 · mouse 89,293 · sub-tick jump presses 138 |
| Aug 2025, de_ancient | 10499 | 300,020 | 300,020 | 0 | attack 7,804 · jump 1,401 · duck 4,859 · sub-tick moves 291,117 |
| Sep 2026, de_dust2 | 10896 | 300,000 | **505** | **299,495** | attack 2 · jump 23 (keyframes only) |
| Sep 2026, de_inferno | 10896 | 300,000 | **813** | **299,187** | attack 33 · jump 0 (keyframes only) |

Three things follow. **(a) Every player's `CSGOUserCmdPB` is in a Valve matchmaking demo**: ten
players, `buttonstate1` bits for attack/jump/duck/movement, mouse deltas, `subtick_moves` with a
`when` fraction, `input_history`. Jump-throw detection from inputs is therefore the right primary
method, with the thrower's ground flag as the fallback for sources that lack inputs. **(b) Since
roughly July 2026 the server writes `CMsgServerUserCmd.delta_data` instead of `data`** (demoparser
issue #340 reports the same date for FACEIT). `CS2DemoKit.Parser` 0.12.0 contains no reference to
`DeltaData` (the packaged `CS2OpenDev.Protos` 0.9.0 does carry the field), so on any current demo the
engine decodes only the ~0.2% full keyframes and says nothing about the rest: `SubTickExtractor`,
`AimCapabilityProbe` and every input consumer are silently starved. demoinfocs-golang reconstructs a
command "from a `CMsgServerUserCmd` full payload or `delta_data`", so a reference decoder exists.
**(c) Whether HLTV broadcast recordings carry `svc_UserCmds` at all is unverified.** The corpus lost
on 2026-09-20 held three; none survive locally. Until one is probed, treat pro demos as
input-less and design every input feature with a fallback.

**F7. The annotation model is further along than the roadmap credits, and not as far as strat
authoring needs.** `AnnotationKind` already declares Freehand, Line, Arrow, Rect, Ellipse, Text; the
sidecar format is forward-compatible; the time envelope is exactly the step-range primitive the roadmap
wants. But only Freehand is written today. Step authoring needs Arrow and Text as real tools, and a
token-position keyframe the envelope does not model. Shape Tools is a prerequisite build item.

**F8. The clip tray is already cross-demo.** `HighlightsModels.StagedClipState` / `ClipTrayKeys` and
`HighlightReelDialogViewModel` stage clips from several demos, and `DemoCacheStore`'s class doc says the
tray is "cross-demo by definition". Review Packs is a generalisation of this tray (any `(demo, tick
range, note)` source, ordering, title cards), not a new subsystem. Two encoders exist: the in-game
CSVG reel (`ReelJobService`) and the 2D `SceneExportSession`. The 2D one is single-demo per session;
packs need a concatenation step (ffmpeg concat, or one session per clip then stitch). Research item.

**F9. Persistence has a home.** `DemoCacheStore`: `index.json` plus one lazily-read sidecar per demo
under `demos/`, tiers Header/Parse/Analysis with stamps and a `ConfigFingerprint`, atomic writes,
in-memory on WASM. Tags, the round index, and grenade rows fit as new tiers or sibling sidecars keyed
by the same `StableKey`. Caveat: the cache keys by **path**, and computes `Sha256` only for files that
share a byte size with another file. Tags and strats must survive a moved or re-parsed demo, so they
need a content hash for every demo (the annotation sidecar already hashes every demo it touches).

**F10. The timeline and the keymap are ready for tags.** `ITimelineTrack` (Core) takes point markers
and range bands with a `Custom` marker kind; tag instances are bands. `Playback2DKeymap` exists (A1)
and is where tagger hotkeys and "find rounds like this" register; collisions must be checked there.

**F11. Rules v2 cannot express an execute detector today.** The language has `player.place`,
`pos_x/y/z`, and team *count* aggregates (`round.team.alive`), but no "N teammates in place X within
T seconds" primitive, and the closed function set is deliberate. Suggested Tags therefore either (a)
computes proposals app-side over `PositionSampler` output, or (b) adds a team-presence provider to
CS2DemoKit. (a) needs no engine change and is recommended first; (b) is a design item if suggestions
should be authored as YAML.

**F12. The veto model needs data that is not in any demo.** First-ban frequency and veto consistency
come from match-series metadata (HLTV/FACEIT), not from `.dem` files. A local-first tool can substitute
a **Map Pool Record** (maps played, win rate, side wins: `CtSideWins`/`TSideWins` are already cached)
and accept an optional user-entered veto history. The roadmap's veto section is otherwise dropped.

**F13. Demo provenance is partly derivable.** `DemoSourceClassifier` / `DemoSourceKind` distinguish
GOTV / HLTV / POV. "Official / scrim / our scrim" is a user label with a heuristic default (clan tags
present → official; server name patterns → scrim). Small item, but four features filter on it.

**F14. The rules engine is out of scope as a UI host.** New features are first-party
`IWorkspaceModule`s inside `DemoViewer.NET.dll`, following `Playback2DModule` / `HighlightsModule`
(folder under `Modules/`, tab descriptors, feature-gate id). The add-on loader in the plugin design doc
is not a dependency.

**F15. Two clocks, one convention.** Everything positional in the app runs on the frame clock
(`ClipRound.StartTickFrameClock`, the `.dvann.json` `clock` block). The tag store, the round index and
the strat evidence list must use frame-clock ticks with the same `clock` header the annotation sidecar
writes, or they will drift from the timeline on re-parse. (CS2DemoKit:
`GameTick` *is* the frame clock; do not "correct" it.)

**F16. Engine changes go upstream, against the DLLs.** The local `C:\dev\CS2DemoKit` checkout is
stale against the 0.12.0 pin; API questions are answered by reflecting over the packaged DLLs and XML
docs, and engine work lands in `CS2OpenDev/CS2DemoKit` (open there today: #49 clip sign bug, #50 rule
graph consumers). Nothing upstream is in flight for nav, grenades, or per-round facts.

**F17. WASM degrades everything here.** No filesystem on the browser host means the tag store, the
round index, the strat book and watched situations are desktop features; the browser gets session-only
or nothing, recorded per capability in `docs/playback2d-v2/wasm-matrix.md` as the house convention.

---

## 3. Work-item catalogue

Grouped by phase. Each entry: **kind** · **needs human review?** · **depends on** · what it is · what
"done" means. Names are final; use them verbatim.

### Phase 0: Foundations

**Place Names From The Pawn** · research · no review · depends on nothing.
Establish that `m_szLastPlaceName` is populated across every demo source the library sees (Valve
GOTV, FACEIT, HLTV, POV) on every shipped map, and how often it is null (spawn, before first network,
maps without named areas). Method: `PositionSampler.Walk` over the corpus in `demos/` (via
`DEMO_PATH`, never a link), tabulate null rate per (source, map). Also list the distinct place names
per map; that list is the canonical vocabulary for callout aliases. **Done:** a table in this folder
and a go/no-go on F1.

**Team Identity** · design · **review required** · depends on nothing.
Group demos by roster: cluster on SteamID overlap (three or more of five shared across the same side),
let the user name a cluster, and mark one cluster as "us". Clan tags seed names where present. Decide
storage (a `teams.json` beside `index.json`), how a roster change splits or merges a team, and how
"opponent" is derived per demo (the cluster that is not us). **Done:** an approved two-page design.
Unblocks Watched Situations, the multi-demo Matrix, the Strat record split, and the Dossier.

**Round Facts** · research then design · **review required if it touches the engine** · depends on
nothing.
Per round, per side: buy type (with the classifier thresholds written down and sourced), freeze-end
equipment sum, score before the round, end reason, plant site and plant tick, defuse tick, first-contact
tick, man-count timeline at each kill, round phase boundaries (freeze / opening / mid / post-plant /
retake, with the definitions written down). Research question one: can a shipped YAML ruleset emit a
team-scoped per-round table the app can read, or does this need an engine API or an app-side walk?
Research question two: which buy-type thresholds to adopt (HLTV, Leetify, CS Demo Manager each publish
one). **Done:** an answer to both questions and, if engine work is needed, a design note for review.
Stored as a new `DemoCacheStore` tier (F9). Unblocks Round Tagger's free labels, Situation Search's
filters, and the Dossier's stratification.

**Content Identity** · build · no review · depends on nothing.
Hash every library demo (SHA-256, the same the annotation sidecar uses), cache it in the record, and
give every new store a `demo.sha256` join key with the `.dvann.json` clock header (F9, F15). Small.
Unblocks the Tag Store and the Strat Book surviving a moved or re-parsed demo.

**Demo Provenance Labels** · build · no review · depends on Team Identity (for the "our scrim" default).
A per-demo `official | scrim | our scrim` label with a heuristic default and a user override (F13).

**Inputs Per Demo Source** · research · no review · depends on nothing.
Run the user-command probe (today a throwaway console project, `UserCmdProbe`; promote it under `tools/` if
it earns its keep) over one demo per source and era: Valve matchmaking 2025 and 2026, FACEIT, HLTV
broadcast, POV. Record per source the full-vs-delta share, distinct players, button bits seen, and
sub-tick yield. Needs one untrimmed HLTV demo (re-download one of the three `demos/CORPUS.md` names).
**Done:** a table in this folder that the Grenade Walk and Delta User Commands designs cite.

**Delta User Commands** · design · **review required** (a CS2DemoKit engine change, landed upstream)
· depends on Inputs Per Demo Source.
Decode `CMsgServerUserCmd.delta_data` (Valve's delta encoding against the previous command for the
same player slot) so `SubTickExtractor`, `AimCapabilityProbe` and every input consumer see current
demos instead of one command in six hundred (F6). Reference implementation: demoinfocs-golang's
`UserCmd` reconstruction. Also make `AimCapabilityProbe` report the delta share, so the loss becomes a
stated verdict rather than a silence. **Done:** an upstream issue carrying the measurement and a
proposal, approved before any code.

**Zone Baking** (issue #5) · research then design then build · **review required** (baker output
schema is a contract) · depends on nothing; runs in parallel with everything in Phase 1.
Research: does VRF's `NavMeshArea` carry the place-name index; can `EntityLump` yield `func_bomb_target`
volumes from the per-map vpk. Design: the bundle schema addition (place polygons per floor, bombsite
volumes, a version bump), and the app-side lookup (`PlaceResolver`: world point → place, and →
bombsite). Build: the baker mode and the resolver. **Done:** every shipped map resolves an arbitrary
point to a place with a documented miss rate. Unblocks the query canvas click-to-place, the Utility
Book landing-place index, and zone outlines.

### Phase 1: Situation Search

**The Round Index** · design then build · **review required** (a new derived store and a new indexing
job in the library) · depends on Place Names From The Pawn, Content Identity, Round Facts.
One row per (demo, round, sampled tick): the place-count token per side (string, exact-match), plus a
foreign key into Round Facts. Sampling cadence is a decision (once per second is the proposal; the
roadmap's own build note says "sample at a fixed cadence rather than every tick"). Built by a new
library evaluator alongside tier 2 (F9), one demo at a time under the same gate. Design covers token
encoding, storage (columnar sidecar vs SQLite: a decision, §6 D2), size per demo, and rebuild triggers.
**Done:** a corpus indexes at a stated rate, and a token lookup over it returns in under a second.

**Query Canvas** · build · no review · depends on The Round Index.
The existing Skia scene and level model with a ten-slot token rail (five per side), drag to place,
partial queries as the normal case. Without Zone Baking the dropped token snaps to the nearest sampled
place centroid from the index itself (the index knows where each place's samples cluster); with Zone
Baking it resolves exactly. **Done:** a placed query produces a token identical to what the index
stores.

**Find Rounds Like This** · build · no review · depends on Query Canvas.
One key in playback snapshots the current tick's positions onto the query canvas. Register in
`Playback2DKeymap`; the roadmap proposes `F`, collision check required (F10). **Done:** the key works
from any tick and the resulting query round-trips.

**Search Filters And Live Count** · build · no review · depends on Round Facts, Demo Provenance
Labels, Team Identity.
The filter rail (side, each team's buy type, phase, clock band, man-count, score, map, opponent, date,
source) and the count that updates as tokens move. **Done:** the count matches the result set every
time.

**Tolerance Slider** · research then build · no review · depends on Zone Baking for the adjacency
graph (the `.nav` file has area connections; without it, tolerance is "same place or any place" only).
Exact at one end; adjacent-area match at the other. **Done:** the count changes monotonically as the
slider loosens.

**Result Cards And Walking** · build · no review · depends on The Round Index, Round Facts.
Cards with mini-radar thumbnail, match, round, score, both buys, end reason icon, clock; click seeks
playback to the matched tick minus ten seconds; `J`/`K` walk the set. Thumbnails come from the
existing headless renderer (`dv2d render`'s code path) at a small size, cached. **Done:** forty results
walk in under two minutes of user time.

**Overlay View** · build · no review · depends on Result Cards And Walking.
Every matching state stacked onto one map as a heatmap layer (new `ISceneLayer`). **Done:** a default
is visible on a known corpus.

**Watched Situations** · build · no review · depends on Team Identity, The Round Index.
A saved query that re-evaluates when new demos index, with a "N new" badge. **Done:** indexing a new
demo updates the badge without a restart.

### Phase 2: Round Tagger

**Tag Store** · design then build · **review required** (a new persisted schema that other tools will
read) · depends on Content Identity.
Codes create instances (`demo.sha256`, frame-clock from/to, code id); labels attach key/value metadata
to an instance; a separate namespace for parser-derived labels so a re-parse can refresh those without
touching human ones. XML export in the sports-analysis interchange shape. **Done:** the schema is
documented like `annotations-format.md`, round-trip pinned by a snapshot test.

**Tag Track** · build · no review · depends on Tag Store.
An `ITimelineTrack` whose bands are instances (F10). **Done:** instances appear on the timeline and
seek on click.

**Tag Palette** · build · no review · depends on Tag Store, Tag Track.
Dockable palette, hotkeyed, panel flow (a code reveals only the panel that follows), per-button lead
and lag, sticky session labels, free-text note per instance. Palette definitions are data (JSON in the
user config dir, like themes) so a team can author its own vocabulary. **Done:** a demo is tagged
without touching the mouse.

**Click To Tag Position** · build · no review · depends on Tag Palette; better with Zone Baking.
While tagging, a click on the map attaches coordinates (and the resolved place) to the instance; two
clicks make a movement. **Done:** the coordinates are queryable from Search Filters.

**Label Mode** · build · no review · depends on Tag Palette.
Second-pass mode that adds labels to existing instances without creating new ones. **Done:** a
first-pass tag set can be enriched without duplicating anything.

**Free Labels From Round Facts** · build · no review · depends on Round Facts, Tag Store.
Every instance inherits its round's facts in the parser namespace automatically. **Done:** a fresh
instance already carries side, both buys, score, end reason, plant site.

**The Matrix** · build · no review · depends on Tag Store, Free Labels From Round Facts, Team Identity.
Pivot over instances: codes as rows, labels as columns, swappable axes, every cell a count that opens
its clips in the Review Queue; multi-demo mode over one opponent and a dynamic mode restricted to a
slice. **Done:** the screen mock in the roadmap is reproducible on a tagged corpus.

**Suggested Tags** · research then design · **review required if the engine route is chosen** ·
depends on The Round Index, Tag Store.
Proposals with a confidence and accept/edit/reject: a five-player site commit inside four seconds, a
three-detonation opener inside two seconds. Route (a), app-side over `PositionSampler` and the
detonation events, needs no engine change; route (b), a team-presence provider in CS2DemoKit so rules
can author suggestions, is a design item (F11). **Done:** the route is chosen and, for (b), designed.

### Phase 3: Strat Book

**Shape Tools** · build · no review · depends on nothing (Playback2D work).
Make Arrow, Line, Rect, Ellipse and Text real tools in the annotation toolbar and the sidecar writer
(F7). Independently valuable for Review Packs. **Done:** all six kinds round-trip through
`.dvann.json` and render in export.

**Strat Model** · design · **review required** · depends on Team Identity, Content Identity.
Strat: name, map, side, type (the pro tactics directory vocabulary), target site, economy class, tempo,
trigger, lifecycle status. Slots A–E, not names. Steps on the round clock (at 1:30, at 1:15) with actor
slot, verb, from-callout, to-callout, utility reference. Branches as first-class objects (condition +
pointer). Evidence list = the set of instances tagged with this strat. Version history as an append-only
log of diffs. **Done:** an approved schema document.

**Callout Aliases** · build · no review · depends on Place Names From The Pawn.
Nav place is canonical; a team defines its own words over it; the UI shows the team's words. Ships with
an empty alias table per map plus the Valve names. **Done:** "popdog" resolves to a nav place in every
query.

**Create Strat From Round** · build · no review · depends on Strat Model, Round Facts, Shape Tools.
Right-click in playback: positions, timings and utility pre-populated from the demo; the user edits.
**Done:** a strat created this way needs no manual re-entry to be playable.

**Step Authoring** · design then build · **review required** (this is a new interpolated keyframe
model over the annotation document, not a stroke) · depends on Strat Model, Shape Tools.
Add step → drag tokens → add arrows and text for that step; the engine interpolates and the scrubber
plays it. Reuses the Skia canvas and the envelope model for per-step strokes; adds a token-position
keyframe track. **Done:** a five-step strat plays back and scrubs.

**Strat Export** · build · no review · depends on Step Authoring.
GIF or MP4 through `SceneExportSession` with a synthetic frame source (no demo: the strat *is* the
scene). **Done:** a GIF drops into Discord.

**Strat Record Panel** · build · no review · depends on Tag Store, Strat Model, Demo Provenance Labels.
Run / won / aborted, split scrim vs official, small-sample caution under eight, failure breakdown from
the tagged rounds, every number a clip. **Done:** the panel is computed live from the Tag Store.

**Strat Version History** · build · no review · depends on Strat Model.
Diff view ("molotov moved from 1:22 to 1:16") with the record split either side of each change.
**Done:** every save is a diff and the record splits.

**Role View And LAN Print** · build · no review · depends on Strat Model.
One slot's parts only; a printable page. **Done:** a strat prints to one sheet per slot.

**Walk It In The Server** · design · **review required, and blocked on D4** · depends on a CSVG
plugin and protocol change (F3).
Push step positions as setpos/setang to a running practice server. **Deferred** until it is decided
whether to extend CSVG or add a second, simpler practice-server bridge.

### Phase 4: Utility Book and Review Packs

**Grenade Walk** · research then design · **review required if upstreamed** · depends on Inputs Per
Demo Source; better with Delta User Commands.
Walk every projectile entity (smoke, molotov/incendiary, HE, flash, decoy) through the `EntityTracker`
the way `PositionSampler` walks pawns: thrower, release tick, release position and view angle,
trajectory samples, landing/detonation point, air time. Jump-throw from inputs (a sub-tick jump press
followed by the attack release, with its `when` fraction) wherever the demo's commands decode, and
from the thrower's ground flag at release otherwise (F6). App-side first, mirroring
`DemoLibraryService`'s `StoreClassFilter` replay; propose upstreaming to CS2DemoKit as a sibling of
`PositionSampler` afterwards. **Done:** a design note with measured cost per demo and the fallback
rate per source.

**Grenade Index** · build · no review · depends on Grenade Walk, Content Identity; better with Zone
Baking (landing place).
One row per grenade, clustered on a coarse spatial grid, deduplicated by rounded origin. **Done:** "every
smoke that landed on Mirage CT in these nine demos" returns clustered origins.

**Lineup Cards** · build · no review · depends on Grenade Index.
The CS2UTIL field set: map, type, jump-throw flag, air time, movement, setpos/setang string, landing
point on the radar. **Done:** each card is copy-pasteable into a console.

**Lineup Clip Render** · build · no review · depends on Lineup Cards, Review Queue.
Auto-render a short GIF of the throw from the thrower's position with the existing encoder. **Done:**
the setpos + video pair is produced without user input.

**Lineup On A Strat Step** · build · no review · depends on Lineup Cards, Strat Model.
A step references a lineup by id. **Done:** "Mirage A exec, four lineups, in execution order" is one
object.

**Push Lineup To Server** · deferred with Walk It In The Server (F3, D4).

**Review Queue** · build · no review · depends on nothing (generalises the Highlights clip tray, F8).
Any `(demo, from, to, note)` from any surface: search results, a Matrix cell, a strat's failure list, a
manual pick. Ordering, sections with title cards, a one-line question per clip. **Done:** the Highlights
tray is a client of the queue rather than its own thing.

**Pack Export** · research then build · no review · depends on Review Queue, Shape Tools.
One video from many demos with annotations burned in: either one `SceneExportSession` per clip stitched
by ffmpeg concat, or a multi-source session. The research decides which; the GIF path needs the frame
cap respected per clip. **Done:** a pack of clips from three demos exports as one file that plays on a
phone.

**Headless Packs** · build · no review · depends on Pack Export.
`dv2d pack` from a queue file, so "every scrim from last night, tagged rounds only, rendered by
morning" is a scheduled command. **Done:** documented in `dv2d.md`, covered by the CLI tests.

### Phase 5: Opponent Dossier

**Map Pool Record** · build · no review · depends on Team Identity.
The demo-derivable substitute for the veto model (F12): maps played, win rate, side wins, decider
record where inferable, plus an optional user-entered veto history. **Done:** the section renders with
sample sizes.

**Setup Heatmaps By Buy** · build · no review · depends on The Round Index, Round Facts, Overlay View.
For each map and economy state, their defensive positions overlaid; fixed vs rotating positions.
**Done:** every heatmap opens its rounds.

**Opening Tendencies** · build · no review · depends on Grenade Index, Round Facts.
First utility location and timing as a clock histogram, first-contact distribution, entry player by
site, site split, lurk timing. **Done:** every number is a link.

**Post-Plant And Retake** · build · no review · depends on Round Facts, The Round Index.
Plant clusters, post-plant holds, retake grouping. **Done:** as above.

**Situational Behaviour** · build · no review · depends on Round Facts.
Pistol patterns, anti-eco setups, man-advantage handling, save discipline. **Done:** as above.

**Period Diff** · build · no review · depends on all Dossier sections.
Last N vs previous N. **Done:** a roster change shows up as a diff.

**Dossier Editing And Export** · build · no review · depends on all Dossier sections.
Starred findings assemble the one-pager; long form is the working doc; everything editable; no rating,
no grade, no win probability. **Done:** the short form is the default export.

---

## 4. Order, and the gates between phases

The roadmap's order survives, with one substantive change: **Zone Baking leaves the critical path for
Situation Search (F1) and moves to a parallel track**, and **Team Identity and Round Facts join Phase 0
as the real gates**, because four features assume them and nothing provides them.

```
Phase 0  Place Names From The Pawn ─┐
         Content Identity ───────────┼─→ The Round Index ─→ Query Canvas ─→ Find Rounds Like This
         Round Facts (design) ───────┘         │                  │
         Team Identity (design) ──────────┐    │                  └─→ Search Filters And Live Count
         Demo Provenance Labels ──────────┤    └─→ Result Cards ─→ Overlay View ─→ Watched Situations
         Zone Baking (parallel, #5) ──────┼──→ (Tolerance Slider, click-to-place, landing place)
         Inputs Per Demo Source ─→ Delta User Commands (engine) ─→ Grenade Walk (Phase 4)
                                          │
Phase 2  Tag Store (design) ─→ Tag Track ─→ Tag Palette ─→ Label Mode ─→ The Matrix
                                          └─→ Free Labels From Round Facts    Suggested Tags (research)
Phase 3  Shape Tools ─→ Strat Model (design) ─→ Create From Round ─→ Step Authoring (design) ─→ Export
                                              └─→ Record Panel · Version History · Role View
Phase 4  Grenade Walk (design) ─→ Grenade Index ─→ Lineup Cards ─→ Lineup Clip Render
         Review Queue ─→ Pack Export ─→ Headless Packs
Phase 5  Map Pool Record · Setup Heatmaps · Opening Tendencies · Post-Plant · Situational ─→ Diff ─→ Export
```

**Gate 0 → 1:** Place Names From The Pawn says go; Team Identity and Round Facts designs approved;
Content Identity merged.
**Gate 1 → 2:** Situation Search demonstrable in a thirty-second clip on the corpus (the roadmap's own
bar).
**Gate 2 → 3:** a real demo tagged end to end by hotkey; the Matrix opens clips.
**Gate 3 → 4:** a strat created from a round exports as a GIF and reports a record.
**Gate 4 → 5:** the Utility Book returns clustered lineups; a pack exports from three demos.

Research runs ahead of the phase that needs it, and never more than one phase ahead: the point of
gating is that a later design can absorb what an earlier build taught.

**Research order, first sitting:** Place Names From The Pawn → Round Facts (question one, the engine
route) → Zone Baking (the VRF place-name question) → Team Identity design → Tag Store design.

---

## 6. Decisions

**D1. Does Team Identity ship before Situation Search?** *Decided 2026-09-23.* Design it in Phase 0,
build it in Phase 1 after Result Cards And Walking. (The flagship works over "all demos" until then;
Watched Situations and every "their" in the UI wait for the build.)

**D2. Storage for the derived stores** (round index, tag store, grenade index): JSON sidecars per demo
in the existing `DemoCacheStore` shape, or a single SQLite file. *Decided 2026-09-23: JSON sidecars
plus a per-session in-memory index*, on the condition of a measurement over a **real**
hundred-demo corpus (`round-index.md` §2.7: 100 real sidecars from the replays folder,
162k rows; postings build 204 ms and lookup 3.7 µs, versus a 141 to 267 ms scan per query and a
17.9 MiB SQLite file). The revisit trigger is a startup load over 5 s, about 2,500 demos at the
measured 2 ms per demo. This closes D2 for all five derived stores (integrator §4.1).

**D3. Round Facts: engine or app?** *Decided 2026-09-23: rules-driven.* The research
(`round-facts.md` §2) showed a YAML ruleset cannot emit a team-scoped per-round table on
0.12.0; rather than walk around that in C#, it was decided to have the engine grow the six pieces a
shipped `round_facts` ruleset needs (CS2DemoKit #54), so users can edit the buy-type classification
(HLTV bands as `params:` defaults) and extend the record without a build. Ownership: CS2DemoKit owns
the semantics and ships the ruleset; the app caches its rows in the Analysis tier keyed by the
ruleset's resolved identity. No app-side extractor exists or is planned.

**D4. Open the practice-server door?** Walk It In The Server and Push Lineup To Server need a
`setpos`/`setang` path into a live CS2 server, which CSVG does not provide and the plugin design doc
scopes out. Options: extend the CSVG plugin (external project), build a second minimal bridge (an RCON
or console-log channel), or drop both sub-features. Recommendation: defer past Phase 4 and revisit with
the Utility Book in hand, since that is where the value concentrates. *Decided 2026-09-23 as
recommended:* deferred past Phase 4; Walk It In The Server and Push Lineup To Server stay `deferred`
and are revisited with the Utility Book in hand.

**D5. Is the veto section worth an import path?** *Decided 2026-09-23:* manual entry only, in
Phase 5. Map Pool Record covers what demos know; no scraping.

**D6. Suggested Tags route.** *Decided 2026-09-23:* app-side first (route (a) in
`suggested-tags.md`); revisit route (b), the CS2DemoKit team-presence provider, only if the
palette authors want to write their own detectors as YAML.

**D7. Hotkey for Find Rounds Like This.** *Decided 2026-09-23.* `F` is taken (`Playback2DKeymap.cs:333`,
follow next player). Use an unused default, preferably `Ctrl+F` (the common search/find chord; verified
unbound in `Playback2DKeymap` on 2026-09-23), and let users change it through the existing hotkey
configuration layer rather than hard-coding it. The Round Index and Suggested Tags designs proposed `S`;
that proposal is superseded by this decision.

**D8. Fund the delta user-command decoder upstream now?** *Resolved 2026-09-23.* Classed as a
CS2DemoKit bug (the parser must surface the data; the per-slot reconstruction state belongs
in EntityTracking), so it is filed as CS2DemoKit issue #53 with the measurement, the failing code
path (`SubTickExtractor` reads `Data` only), and the reference format from demoinfocs-golang. The
plan proceeds on the expectation that it lands upstream; Grenade Walk designs against the
reconstructed command and keeps the ground-flag fallback.

---
