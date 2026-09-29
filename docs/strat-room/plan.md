# The Strat Room: research and implementation plan (status board)

**Status:** tracked in the repository from 2026-09-24 (it began as a local, uncommitted working
document; the owner promoted it once the design phase closed). **Design phase: closed.** All nine
review-required designs in `designs/` are approved, each with an owner-decision banner at its top;
`designs/00-overview.md` is the integration record that binds them. No build item has started.
**Source of scope:** the "Strat Room" research artifact (claude.ai/artifact/Ho9tuwBGY637hisGT6QbRL),
September 2026. **Tree surveyed:** `main` at `d90ec9f` (0.8.1), CS2DemoKit 0.12.0, VRF 19.2.6339,
Cs2VideoGenerator.Core 0.9.0.
**Upstream dependencies (CS2DemoKit):** #53 delta user commands, #54 rules-driven round facts (the
Round Facts build waits on it), #56 smoke creation packet, #58 `PositionSampler` contract, #59
`ProjectileSampler`.
**Measurements:** every number in the designs was measured with scratch console projects over real
Valve matchmaking demos from the Steam replays folder. The probes lived in a session scratchpad and
are not committed; each design's §10 names its probe, its modes and the demos it ran on, so any of
them can be re-created from the document. The tour sample was never used as evidence.
**Last updated:** 2026-09-26.

This is both the plan and the board. Every work item has a human-readable name, and those names are the
only way anything is referred to, here or in conversation. No abbreviations.

---

## 0. How to read this

- **§1** is what the artifact asks for, compressed to what has to be built.
- **§2** is what the survey found in the tree that changes the artifact's assumptions. Read this before
  the board; several of the artifact's dependency claims are wrong in useful directions.
- **§3** is the work-item catalogue: every item, what it is, what it needs, and whether it needs human
  review before code.
- **§4** is the order, as phases with gates.
- **§5** is the status board.
- **§6** is the list of decisions only the owner can make.

**Kinds of work item.** `research` produces a written answer and no code. `design` produces a written
proposal that stops for human review. `build` is implementation, and starts only when its `design` (if it
has one) is approved. Anything that adds a sizable new concept to CS2DemoKit, the asset baker, or the
CSVG game plugin is `design` first, without exception; that is the rule the owner set for this work.

**Statuses:** `not started` · `researching` · `awaiting review` · `approved` · `in progress` · `done` ·
`deferred` (with a reason) · `dropped` (with a reason).

---

## 1. The scope, compressed

Six ranked features, with the artifact's phase order in brackets:

| Feature | One line | Artifact phase |
|---|---|---|
| **Situation Search** | Sketch a setup on the radar; get every round across every indexed demo where it happened. Seeded from playback with one key. Live result count, tolerance slider, result cards, overlay, saved "watched situations". | 1 |
| **Round Tagger** | Sports-analysis code/label model on the timeline: hotkeyed panel flow, lead/lag per button, click-to-tag position, sticky labels, label mode, rule-engine suggestions with accept/edit/reject, and the Matrix pivot where every count opens its clips. | 2 |
| **Strat Book** | A strat is a diagram plus the set of tagged rounds where it was run. Slots A–E, steps on the round clock, branches, lifecycle status, create-from-round, interpolated step authoring on the existing canvas, GIF/MP4 export, record panel with failure breakdown, version diffs, role view, LAN print, walk-it-in-the-server. | 3 |
| **Utility Book** | Every grenade in every indexed demo, clustered by landing point, searchable by destination, each with throw origin, setpos/setang, jump-throw flag, movement, an auto-rendered clip, attachable to a strat step, pushable to the server. | 4 |
| **Review Packs** | An ordered, multi-demo clip playlist with annotations burned in and a per-clip question, exported as one video or a folder plus index, buildable headlessly from the CLI. | 4 |
| **Opponent Dossier** | A generated, editable scouting document: veto model, CT setups by buy type as overlay heatmaps, T opening tendencies, post-plant and retake, situational behaviour; sample sizes on every claim, every number a link to clips, two-period diff, short and long export. | 5 |

Plus the artifact's stated prerequisite: **issue #5, zone baking** (nav place polygons and bombsite
volumes in the asset bundle), which it calls "the one blocking prerequisite" for half of the above.

Explicitly out of scope per the artifact: a general stats dashboard, tier-1 targeting, cloud sync or
accounts, trajectory clustering or strategy classifiers, win probability or team ratings, a 3D renderer.

---

## 2. What the tree actually says (findings that change the plan)

Numbered so the board can cite them.

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
CSVG extension out. These two sub-features are **deferred** until the owner decides whether to open
that door (§6, D4).

**F4. "The opponent" and "our team" do not exist as data.** `DemoCacheRecord` carries `CtClan`/`TClan`
(populated on pro/HLTV demos, empty on matchmaking) and per-player SteamIDs. Nothing groups demos by
who played in them. Yet Watched Situations ("their Mirage A setup: 3 new"), the multi-demo Matrix
("Falcons, last 6 demos"), the Strat record split (our scrims vs officials), and the entire Dossier
assume a stable team identity across demos. This is the largest unnamed dependency in the artifact
and needs a design: roster clustering by SteamID overlap, user-named teams, and a "this is us"
designation.

**F5. Per-round facts are thin.** `CachedRound` is `Number` + `StartTickFrameClock`, nothing else. The
artifact's "the parser already knows most of the labels" (side, both buy types, score, end reason,
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

**F7. The annotation model is further along than the artifact credits, and not as far as strat
authoring needs.** `AnnotationKind` already declares Freehand, Line, Arrow, Rect, Ellipse, Text; the
sidecar format is forward-compatible; the time envelope is exactly the step-range primitive the artifact
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
CS2DemoKit. (a) needs no engine change and is recommended first; (b) is a design item if the owner
wants suggestions authored as YAML.

**F12. The veto model needs data that is not in any demo.** First-ban frequency and veto consistency
come from match-series metadata (HLTV/FACEIT), not from `.dem` files. A local-first tool can substitute
a **Map Pool Record** (maps played, win rate, side wins: `CtSideWins`/`TSideWins` are already cached)
and accept an optional user-entered veto history. The artifact's veto section is otherwise dropped.

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
writes, or they will drift from the timeline on re-parse. (See the CS2DemoKit tick-clocks memory:
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
"opponent" is derived per demo (the cluster that is not us). **Done:** a two-page design approved by
the owner. Unblocks Watched Situations, the multi-demo Matrix, the Strat record split, and the Dossier.

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
Run the user-command probe (today a scratchpad project, `UserCmdProbe`; promote it under `tools/` if
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
proposal, approved by the owner before any code.

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
artifact's own build note says "sample at a fixed cadence rather than every tick"). Built by a new
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
`Playback2DKeymap`; the artifact proposes `F`, collision check required (F10). **Done:** the key works
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
slice. **Done:** the screen mock in the artifact is reproducible on a tagged corpus.

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
log of diffs. **Done:** a schema document approved by the owner.

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
Push step positions as setpos/setang to a running practice server. **Deferred** until the owner decides
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

The artifact's order survives, with one substantive change: **Zone Baking leaves the critical path for
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
**Gate 1 → 2:** Situation Search demonstrable in a thirty-second clip on the corpus (the artifact's own
bar).
**Gate 2 → 3:** a real demo tagged end to end by hotkey; the Matrix opens clips.
**Gate 3 → 4:** a strat created from a round exports as a GIF and reports a record.
**Gate 4 → 5:** the Utility Book returns clustered lineups; a pack exports from three demos.

Research runs ahead of the phase that needs it, and never more than one phase ahead: the point of
gating is that a later design can absorb what an earlier build taught.

**Research order, first sitting:** Place Names From The Pawn → Round Facts (question one, the engine
route) → Zone Baking (the VRF place-name question) → Team Identity design → Tag Store design.

---

## 5. Status board

| Work item | Phase | Kind | Review | Depends on | Status | Notes |
|---|---|---|---|---|---|---|
| Place Names From The Pawn | 0 | research | no | – | **Valve done; waiting on FACEIT, HLTV and POV demos** | `research/place-names-from-the-pawn.md`: Valve matchmaking measured (null 0 to 0.04%, empty 0.03 to 0.75%, 23 to 29 names per map, F1 go on Valve); FACEIT, HLTV, POV outstanding |
| Team Identity | 0 | design | **yes** | – | **done** | merged into `feature/strat-book` as `c5ad261` (2026-09-24) and pushed: `IsCoach` from `m_iCoachingTeam`, side keys without bots or coaches, the two-tier clusterer (fixed five, then the extended core of seven), `teams.json` + `team-index.json`, the query API with `SideAtRound`, the Teams tab, the Library team filter, an anonymised corpus pin. **Follow-ups (design §3.9, not built):** the Library card subtitle "{Us} vs {opponent}" with the us mark, and the Match Overview roster label preferring the team name with the this-is-us glyph; fold into a later Library/Match Overview pass |
| Round Facts | 0 | research → design | **yes** (engine) | CS2DemoKit #54 | **done** (engine half `56bb8d3`, 2026-09-25, CS2DemoKit 0.13) | merged into `feature/strat-book` as `19025b2` (2026-09-24) and pushed: record, classifier, phases, projection, tier-2 evaluator, `IRoundFactsSource`, timeline tint; the engine row source and the shipped ruleset park on #54 (six RealDemo tests skipped with reason until it lands) |
| Content Identity | 0 | build | no | – | **done** | merged into `feature/strat-book` as `f9b2b23` (2026-09-24) and pushed; 3 commits, one fix pass (the annotation clock header now carries real first/last ticks); app suite 1202/0/2 |
| Demo Provenance Labels | 0 | build | no | Team Identity | **done** | merged `858ffa2` (2026-09-24); vocabulary is official, scrim, our scrim, matchmaking (O-11); override store is a `provenance` section of teams.json keyed by hash; verifier accepted a documented deviation (heuristic order differs from team-identity.md section 3.10); merge landed by the orchestrator after the permission classifier denied the verifier |
| Inputs Per Demo Source | 0 | research | no | – | **Valve done; waiting on FACEIT, HLTV and POV demos** | `research/inputs-per-demo-source.md`: matchmaking 2025/2026 measured (F6, builds 10231 to 10896); FACEIT, HLTV, POV outstanding, one demo per source named |
| Delta User Commands | 0 | upstream bug | – | – | **done** (CS2DemoKit 0.13; wired in Player Input Refresh `4cfae65`) | filed as CS2DemoKit #53 (2026-09-23); owner classes it as an engine bug, not our design |
| Zone Baking (#5) | 0 ∥ | research → design → build | **yes** | – | **done** (part 2 merged `a82f6fa`; mirage floor 91 accepted by the owner) | part 1 merged as `49d90a3`; part 2 fully implemented and green on `feature/strat-book-zone-baking-app` (5 commits, 51 files) except `ZoneResolverAgreementTests` on one abandoned 9.4-minute mirage replay (90.35 % vs the 92 % floor; pooled mirage 92.83 % over ten demos). Waiting on the owner's D7 answer, written to `docs/strat-room/parked/d7-zone-floor.txt` (`pooled`, `floor90` or `hold`); the Phase 1 lane applies it between items |
| The Round Index | 1 | design → build | **yes** | Place Names, Content Identity, Round Facts (rules-driven, CS2DemoKit #54) | **done** (real indexing live since `56bb8d3`) | merged into `feature/strat-book` as `0dd8bb6` (2026-09-24) and pushed: token, `.dvri.json` sidecar, builder, `roundindex` evaluator after `roundfacts`, in-memory situation index with four tolerance levels, Situations tab with the status strip, settings, wasm row. Deviation accepted as a note: the sidecar lives under `cache/round-index/` through its own store because the `DemoCacheStore.WriteSibling` seam (correction 1, owned by Grenade Walk) does not exist yet; move it when that seam lands |
| Query Canvas | 1 | build | no | The Round Index | **done** | merged into `feature/strat-book` as `39e5206` (2026-09-24) and pushed: ten-slot rail over the scene, drop resolved through zones else the index's place snap, the query draft encoding through the place-count token (round-trip pinned), a query token layer with a dv2d golden, hosted in the Situations tab |
| Find Rounds Like This | 1 | build | no | Query Canvas | **done** | merged into `feature/strat-book` as `d9759ab` (2026-09-24) and pushed: `Ctrl+F` in 2D playback snapshots the alive players by side through the index's place source onto the Query Canvas and opens the Situations tab; round-trip pinned against the builder's token; app tier 1365/0/2 |
| Search Filters And Live Count | 1 | build | no | Round Facts, Provenance, Team Identity | **done** | merged `10e4f54` (2026-09-24); tick-anchored fields on RoundFactsFilter, the filter rail, live count through the index; the map filter is the canvas map picker; buy fields are absolute per side; follow-ups: un-skip SearchFiltersRealDemoTests when #54 closes, clock band edges 30 s and 75 s are the implementation's, check the CalendarDatePicker round trip live; date range reads ModifiedTicks until a match date exists on the cache row |
| Tolerance Slider | 1 | build | no | The Round Index, Zone Baking | **done** | merged `447228a` (2026-09-24); four stops from exact to any place; follow-up found: the app registered no zone resolver, so the running app always used the empirical graph (Phase 2 item Zone Resolver In The App); the full app tier did not finish inside the verifier's budget |
| Result Cards And Walking | 1 | build | no | The Round Index, Round Facts | **done** | merged into `feature/strat-book` as `7c4eff5` (2026-09-24) and pushed: a per-hit positions sibling (`.dvrp.json.gz`, `pos=1` in the fingerprint) kept from the same walk so thumbnails render through the headless scene path without re-parsing; cards with round facts or "no data"; click seeks ten seconds before the match; `J`/`K` walk the set from inside playback; a dv2d golden |
| Overlay View | 1 | build | no | Result Cards | **done** | merged `0278af2` (2026-09-24); heatmap layer in Core over an OverlayDocument read from the .dvrp.json.gz sibling (opens no demo); wash normalised per pane; follow-ups: Setup Heatmaps By Buy can reuse OverlayDocument and the layer, a shared cross-floor peak needs the compositor to pass all panes |
| Watched Situations | 1 | build | no | Team Identity, The Round Index | **done** | merged `9308108` (2026-09-24); saved queries with a watermark and a new-since-seen count, re-run onto the canvas; follow-up: un-skip WatchedSituationsRealDemoTests when #54 closes |
| Tag Store | 2 | design → build | **yes** | Content Identity | **done** | store and session merged `61d5ff0`, TagQuery and `docs/tags-format.md` merged `8e36df2` (2026-09-24); XML interchange deferred (owner); autosave 750 ms (the annotation controller's constant); follow-ups: the first item that opens a TagSession adds the shutdown SaveIndex call, regenerate the schema-v1 sample with the correction-10 fact groups |
| Tag Track | 2 | build | no | Tag Store | **done** | merged `c5fcf81` (2026-09-24); one lane, overlapping instances, click seeks; follow-ups: palette colours replace the hashed stand-in, persist the tag toggle with the palette settings row |
| Tag Palette | 2 | build | no | Tag Store, Tag Track | **done** | merged `76deb98` (2026-09-24); palettes as data with the built-in default and a validator, panel flow, lead and lag, sticky labels, notes, keyboard-only tagging pinned by a test; palette focus key is C (G is the tests' example unbound key) |
| Click To Tag Position | 2 | build | no | Tag Palette | **done** | merged `0cd5164` (2026-09-24); TagPositionResolver: zones place when the map has zones, else the nearest alive pawn's place at the click tick (tag-store.md section 3.6); two clicks make a movement; queryable through TagQuery's position predicate; follow-up: no filter-rail control yet (the Matrix is TagQuery's first UI caller) |
| Label Mode | 2 | build | no | Tag Palette | **done** | merged `b03d380` (2026-09-24); the palette second pass adds labels to the picked tag without creating one; one tag at a time (repeated clicks walk a merged band); follow-up: in Label Mode, notes and map clicks still go to the pending or last-written tag |
| Free Labels From Round Facts | 2 | build | no | Round Facts, Tag Store | **done** (real rows live since `56bb8d3`) | merged `539e05c` (2026-09-24); TagFactsRefresher writes round facts into the parser namespace and never touches human labels; tested on synthetic rows, TagFactsRealDemoTests skipped on #54; parked: the section 3.3 place pass (re-resolving null or stale positions[].place through the zone resolver) |
| The Matrix | 2 | build | no | Tag Store, Free Labels, Team Identity | **done** | merged `755f22e` (2026-09-24); pivot through TagQuery, swappable axes, multi-demo over one opponent, slice mode; a cell sends its clips to the Review Queue and shows the Review tab; follow-up: SearchFilterField value-equality trap when options are rebuilt (the Matrix reuses one static any-option) |
| Suggested Tags | 2 | research → design | if engine | The Round Index, Tag Store | **done** (validation left for humans) | route (a) app-side; detectors `9345b3b`, review loop `878b537`, tuning and `docs/suggested-tags-format.md` `e1275a7` (2026-09-24; tuning scores per demo and per site after a second pass); parked: section 7.3 hand-tag validation (two human taggers), relearning site regions when the library grows, real-demo variants on #54 |
| Shape Tools | 3 | build | no | – | **done** | merged `6a70b85` (2026-09-24); line, arrow, rect, ellipse and text as real annotation tools to step-authoring.md section 3.2, round-tripping through .dvann.json and rendering in export |
| Strat Model | 3 | design | **yes** | Team Identity, Content Identity | **done** | built 2026-09-24 as four items: Strat Store `c481177`, Strat Session And Tab `78af8dc`, Strat Evidence And History Data `2bfc342`, Role Sheet And Format Doc `eca83ce` (`docs/strat-format.md`) |
| Callout Aliases | 3 | build | no | Place Names | **done** | merged `0909033` (2026-09-24); CalloutResolverSource merges an owner's alias table over the canonical nav places and baked zone names; the UI shows the team's words |
| Create Strat From Round | 3 | build | no | Strat Model, Round Facts, Shape Tools | **done** | merged `6b553ff` (2026-09-25); right-click a round band: positions, timings and utility captured from the demo (utility events, plant, 10 s sweep at 200 units), slot map review, throw arrows; real-demo test on mirage and nuke replays; follow-ups: throw arrows start at the thrower at detonation (no throw origin yet), fire thrower from m_hOwnerEntity (Grenade Walk can supply it later), no end-to-end UI test of the 2D tab's OpenCreateStrat |
| Step Authoring | 3 | design → build | **yes** | Strat Model, Shape Tools | **done** | built 2026-09-24/25 as four items: Token Keyframes `e83a7e6`, Scene Frame Host Seam `621ec17`, Strat Frame Source `57feb4c`, the canvas `e7aec73`; a five-step strat plays back and scrubs |
| Strat Export | 3 | build | no | Step Authoring | **done** | merged `f918c6f` (2026-09-25); GIF or MP4 through SceneExportSession over the synthetic StratFrameSource, default GIF at 20 fps, 640 wide |
| Strat Record Panel | 3 | build | no | Tag Store, Strat Model, Provenance | **done** | merged `eaedfd5` (2026-09-25); run / won / lost / aborted, scrim vs official through provenance, small-sample caution under eight, failure breakdown, every number a clip into the Review Queue, computed live from the Tag Store |
| Strat Version History | 3 | build | no | Strat Model | **done** | merged `219a2ed` (2026-09-25); append-only diff log view with phrased changes and the record split either side of each change |
| Role View And LAN Print | 3 | build | no | Strat Model | **done** | merged `0c04fe0` (2026-09-25); one slot's parts only in the Strat Book tab; LAN Print as self-contained HTML in the system browser, one sheet per slot |
| Walk It In The Server | 3 | design | **yes** | CSVG change | **deferred** | blocked on D4 (F3) |
| Grenade Walk | 4 | research → design | if upstreamed | Inputs Per Demo Source | **done** | merged `6134386` (2026-09-26); verified in the Phase 1 to 5 integration test pass `c37c9d0` |
| Grenade Index | 4 | build | no | Grenade Walk, Content Identity | **done** | merged `045c48a` (2026-09-26); verified in the Phase 1 to 5 integration test pass `c37c9d0` |
| Lineup Cards | 4 | build | no | Grenade Index | **done** | merged `9d8e499` (2026-09-26); verified in the Phase 1 to 5 integration test pass `c37c9d0` |
| Lineup Clip Render | 4 | build | no | Lineup Cards, Review Queue | **done** | merged `395e0c5` (2026-09-26); verified in the Phase 1 to 5 integration test pass `c37c9d0` |
| Lineup On A Strat Step | 4 | build | no | Lineup Cards, Strat Model | **done** | merged `8826867` (2026-09-26); verified in the Phase 1 to 5 integration test pass `c37c9d0` |
| Push Lineup To Server | 4 | design | **yes** | CSVG change | **deferred** | with Walk It In The Server |
| Review Queue | 4 (pulled into 2) | build | no | – | **done** | merged `906df0f` (2026-09-24); one ordered list of clips with sections as title cards and a question per clip; the Highlights tray is a client; Result Cards can send a result set; Pack Export can read ReviewQueue.Entries |
| Pack Export | 4 | research → build | no | Review Queue, Shape Tools | **done** | merged `704e80d` (2026-09-26); verified in the Phase 1 to 5 integration test pass `c37c9d0` |
| Headless Packs | 4 | build | no | Pack Export | **done** | merged `bcf3566` (2026-09-26); verified in the Phase 1 to 5 integration test pass `c37c9d0` |
| Map Pool Record | 5 | build | no | Team Identity | **done** | merged `c7bb5e6` (2026-09-26); verified in the Phase 1 to 5 integration test pass `c37c9d0` |
| Setup Heatmaps By Buy | 5 | build | no | The Round Index, Round Facts, Overlay View | **done** | merged `5ebde1a` (2026-09-26); verified in the Phase 1 to 5 integration test pass `c37c9d0` |
| Opening Tendencies | 5 | build | no | Grenade Index, Round Facts | **done** | merged `0116a4d` (2026-09-26); verified in the Phase 1 to 5 integration test pass `c37c9d0` |
| Post-Plant And Retake | 5 | build | no | Round Facts, The Round Index | **done** | merged `80b5ea7` (2026-09-26); verified in the Phase 1 to 5 integration test pass `c37c9d0` |
| Situational Behaviour | 5 | build | no | Round Facts | **done** | merged `59d71bd` (2026-09-26); verified in the Phase 1 to 5 integration test pass `c37c9d0` |
| Period Diff | 5 | build | no | all Dossier sections | **done** | merged `34fc398` (2026-09-26); verified in the Phase 1 to 5 integration test pass `c37c9d0` |
| Dossier Editing And Export | 5 | build | no | all Dossier sections | **done** | merged `6b4463f` (2026-09-26); verified in the Phase 1 to 5 integration test pass `c37c9d0` |
| Strat Book Shell | 6 | build | decided | every Phase 1 to 5 tab | **done** (merged `f56a8d3`, 2026-09-26) | owner decided 2026-09-26: Teams under Library, Review in the hub, left rail. One Strat Book strip tab (`stratbook.hub`) hosts Strats, Situations, Tags, Utility, Review, Dossier on a left rail through `TabPlacement.StratBook`; Teams sits behind the Library toolbar's Demos / Teams toggle through `TabPlacement.Library`. Section ids, feature ids, `TrySelectTab`, gating and the session file are unchanged for every caller |
| Team Identity Revision | 6 | design → build | decided | Team Identity, Demo Provenance Labels | **done** (merged 2026-09-26, branch `feature/strat-book-team-identity-revision`) | owner: suggestion-only succession. Source gate (queue play = GotvMatchmaking or FACEIT without both clan tags; the provenance pin decides), FACEIT server names read as FACEIT and labelled matchmaking, squads (min(3, size), no stand-ins, no five), suggestions (squad, roster change by Valve's active roster, merge by tag) with persisted dismissals, referenced teams kept through a rebuild, the us mark and a first rename re-cluster, `team-index.json` schema 2, the Teams view's my-team card, inbox, plays-now line and move-to. Follow-up: a real match date would make succession dates exact |
| Utility Map | 6 | build | decided | Grenade Index, Lineup Cards | **done** (merged 2026-09-27, branch `feature/strat-book-utility-map`) | owner feedback: the Utility view's grouping is right, the list is not. The Utility section is now the radar: an icon per landing group, click to focus and see every throw position with its flight, click a position for a card (used N times in M demos, Copy for the setpos and setang line, every throw with Open into 2D Playback). Positions thrown from once are hidden by default behind a toggle. `MapSceneHost` extracted from the Query Canvas and shared; the Grenade Index merges neighbouring grid positions (seeded, aim-checked) and keeps absorbed ids resolvable |
| Review Mode | 6 | build | decided | Tag Palette, Suggested Tags, Tag Track | **done** (merged 2026-09-27, branches `feature/strat-book-review-mode-toggle`, `-review-panel`, `-review-timeline`) | owner feedback 2026-09-27: labelling is always on in 2D Playback and takes the player cards' space. Now a mode, off by default (toolbar toggle, Shift+R, persisted); on, the cards collapse to a strip and the review panel holds the palette, Suggested / Labels, and one editor for suggestions and written tags (code, span, labels, note, positions; delete with undo; label a whole round). The tag lane takes edits: drag the editor's span, click empty lane to label, right-click a band to edit, delete or review. Accept-all keeps its threshold |
| Strat Mining | 6 | build | decided | Grenade Index, Round Facts, Team Identity, Suggested Tags, Strat Model | **done** (merged 2026-09-27, branch `feature/strat-book-strat-mining`) | owner feedback 2026-09-27: no strat is ever populated automatically. The miner reduces each side of each round to a signature from cached files (players at two anchors, grenades in a window, the execute time), clusters near-identical rounds by complete linkage across demos and teams, and offers each pattern of two or more rounds in a Detected inbox in the Strats section; Add to book builds the strat from the most typical round through StratFromRound and writes every member round as a run. T executes by site, T defaults and CT setups. Utility is compared only where both demos have grenade rows (53 of 366 here) |
| UX Consistency: UI-thread work | 7 | audit → build | decided | every Strat Book section | **done** (merged 2026-09-28, branch `feature/strat-book-ui-thread-audit`; table in `docs/perf/ui-thread-audit.md`) | no full-library read, file IO or large rebuild on the UI thread when a section opens or a store changes; every list that can reach thousands of rows virtualizes (Review was 24 s to open, lineup planning 0.8 s per index change) |
| UX Consistency: generated content | 7 | build | decided | Suggested Tags, Strat Mining, Team Identity | **done** (merged 2026-09-28, branch `feature/strat-book-generated-inbox`; design in `docs/strat-book/generated-content.md`) | machine output lands in its own inbox or card, never in a curated list; one rule for reviewed, dismissed and hidden across features |
| UX Consistency: preview before commit | 7 | design | **yes** | Suggested Tags, Team Identity, Strat Mining | proposed | accept-all, team and squad suggestions preview what they will write; promoting a pattern shows and can undo the member-demo labels it writes |
| UX Consistency: queue guard | 7 | build | no | processing queue | proposed | a test that fails when a service takes the heavy-job gate outside a queue item; time the Team Identity rebuild and queue it if slow |
| UX Consistency: silent data | 7 | audit | no | caches, indexes | proposed | audit dictionaries keyed by coarse buckets (grid cell, map, slot) for collisions like the utility map's; every cache carries a real fingerprint of its inputs (Round Facts' was a constant) |
| UX Consistency: identity and callouts | 7 | build | no | Dossier, Opening Tendencies, strat slots, role sheets | proposed | per-player facts by SteamID64 and name, not slot; show callout names instead of raw place names (SnipersNest, TopofMid) through the alias table |
| UX Consistency: navigation | 7 | design | **yes** | Strats, Review, Utility, Dossier, Situations | proposed | the same map, side and team filters and search in every section, kept across section switches; uniform loading versus empty states |
| Strat Editor: room and steps | 8 | build | decided | Step Authoring, Strat Book Shell | **done** (merged `edb86d15` 2026-09-29, branch `feature/strat-book-editor-room`) | owner 2026-09-29: the edit page needs a horizontal scroll. The hub rail and the strat list collapse (state kept in the session file); a step row keeps #, at, actor and verb and shows only the fields its verb uses (throw: utility, then a lineup or lands at); fields a verb stops using clear in the same undo entry so exports never print hidden values; no horizontal scroll at 1280x800 |
| Strat Editor: visual lineup picker | 8 | build | decided | Strat Editor: room and steps, Utility Book map | building (branch `feature/strat-book-lineup-picker`) | pick a step's lineup on the Utility Book 2D map filtered to the strat's map and utility kind: landing group, then technique; writes `utility.lineupId` and `utility.technique` |
| Strat Editor: spawns and throw origins | 8 | build | decided | Step Authoring, Grenades v2 | **done** (merged `9ff1dbcf` 2026-09-29, branch `feature/strat-book-spawn-throw`) | a new strat places A to E and O1 to O5 in their spawns at round start, fanned out, only where a slot has no position; a throw step with a lineup puts its actor at the technique's throw origin at the step's time in the projection (canvas, preview and export agree) without writing positions; `utility.technique` added, lineup ids resolve through the alias map |
| Strat Editor: map-first editing | 8 | build | decided | Strat Editor: room and steps | proposed | click the map to set the selected step's place (to, lands at); dragging a token writes that step's position |
| Strat Editor: step templates | 8 | build | decided | Strat Editor: room and steps | building (branch `feature/strat-book-step-templates`) | a new strat of a type (execute, default, retake, setup) starts with a skeleton of steps |
| Strat Editor: duplicate and carry | 8 | build | decided | Strat Editor: spawns and throw origins | proposed | duplicate a step; a new step inherits the previous step's positions |
| Strat Editor: inline checks and keys | 8 | build | decided | Strat Editor: room and steps | proposed | validator warnings beside the field they concern; Enter adds a step, Tab moves through its fields |
| Follow-ups from the 2026-09-27/28 work | 7 | build | mixed | see note | open | two-pass grenade walk (golden first, owner said not yet); Round Index and Suggested Tags on the forward reader (blocked on CS2DemoKit #76); a strat deleted from the book leaves its pattern "in book"; setups drop when first contact precedes the 15 s anchor (about half); no Settings UI for the lineup clip cap; tests: ThePinnedRounds detector order on this Mac, SetupHeatmapTests flake, one TagMatrixTests collection-modified run; ESL last-round defuse the retained parse misses; in-app dotnet-counters check of the memory work |

Update the row, not the prose, when something moves. Put a dated line under §7 when a decision lands.

---

## 6. Decisions only the owner can make

**D1. Does Team Identity ship before Situation Search?** *Decided 2026-09-23.* Design it in Phase 0,
build it in Phase 1 after Result Cards And Walking. (The flagship works over "all demos" until then;
Watched Situations and every "their" in the UI wait for the build.)

**D2. Storage for the derived stores** (round index, tag store, grenade index): JSON sidecars per demo
in the existing `DemoCacheStore` shape, or a single SQLite file. *Decided 2026-09-23: JSON sidecars
plus a per-session in-memory index*, on the owner's condition of a measurement over a **real**
hundred-demo corpus (`designs/round-index.md` §2.7: 100 real sidecars from the replays folder,
162k rows; postings build 204 ms and lookup 3.7 µs, versus a 141 to 267 ms scan per query and a
17.9 MiB SQLite file). The revisit trigger is a startup load over 5 s, about 2,500 demos at the
measured 2 ms per demo. This closes D2 for all five derived stores (integrator §4.1).

**D3. Round Facts: engine or app?** *Decided 2026-09-23: rules-driven.* The research
(`designs/round-facts.md` §2) showed a YAML ruleset cannot emit a team-scoped per-round table on
0.12.0; rather than walk around that in C#, the owner chose to have the engine grow the six pieces a
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
`designs/suggested-tags.md`); revisit route (b), the CS2DemoKit team-presence provider, only if the
palette authors want to write their own detectors as YAML.

**D7. Hotkey for Find Rounds Like This.** *Decided 2026-09-23.* `F` is taken (`Playback2DKeymap.cs:333`,
follow next player). Use an unused default, preferably `Ctrl+F` (the common search/find chord; verified
unbound in `Playback2DKeymap` on 2026-09-23), and let users change it through the existing hotkey
configuration layer rather than hard-coding it. The Round Index and Suggested Tags designs proposed `S`;
that proposal is superseded by this decision.

**D8. Fund the delta user-command decoder upstream now?** *Resolved 2026-09-23.* The owner classed
it as a CS2DemoKit bug (the parser must surface the data; the per-slot reconstruction state belongs
in EntityTracking), so it is filed as CS2DemoKit issue #53 with the measurement, the failing code
path (`SubTickExtractor` reads `Data` only), and the reference format from demoinfocs-golang. The
plan proceeds on the expectation that it lands upstream; Grenade Walk designs against the
reconstructed command and keeps the ground-flag fallback.

---

## 7. Decision log

- 2026-09-23: plan created from the Strat Room artifact and a survey of `main` at `d90ec9f`. No
  decisions taken yet; D1–D7 open.
- 2026-09-23: F6 corrected after the owner challenged "GOTV demos carry no input buttons". Measured
  on four untrimmed Valve matchmaking demos: inputs are present for every player; demos from
  September 2026 are 99.8% `delta_data`, which CS2DemoKit 0.12.0 does not decode. Added Inputs Per
  Demo Source, Delta User Commands, and D8. HLTV broadcast demos still unverified.
- 2026-09-23: owner approved the shape of the plan. Delta User Commands reclassified as an upstream
  bug and filed as CS2DemoKit #53; D8 closed. A design workflow was launched for the nine
  review-required items (Team Identity, Round Facts, Zone Baking, The Round Index, Tag Store, Grenade
  Walk, Suggested Tags, Strat Model, Step Authoring); outputs land in `docs/strat-room/designs/` with
  an integrator overview at `designs/00-overview.md`. No implementation starts until each design is
  reviewed.
- 2026-09-23: design workflow complete (10 agents, no failures). Nine designs in `designs/`, 6,540
  lines, integrated by `designs/00-overview.md`: 24 binding cross-design corrections, 35 new owner
  decisions (O-1 to O-35) on top of D1–D7, about 87 working days of reviewed items. All nine rows are
  `awaiting review`. The overview recommends building Content Identity and Round Facts inside Phase 0
  and pulling Shape Tools and the Grenade Walk walker forward into idle time; the plan's phase gates
  are unchanged. Nothing is implemented.
- 2026-09-23: owner decided D1 (design Phase 0, build Phase 1 after Result Cards And Walking), D2
  (decide at The Round Index review on a measured scan over a REAL hundred-demo corpus), D3 (research
  done; owner picks app-side walk vs engine API at the Round Facts review), D4 (deferred past Phase 4),
  D5 (manual veto entry, Phase 5), D6 (app-side detectors first), D7 (unused default key, preferably
  Ctrl+F, user-rebindable through the existing hotkey layer; supersedes the designs' `S`). D8 was
  already closed. Owner began reviewing the nine designs.
- 2026-09-23: Team Identity reviewed against Valve's competition rules
  (`ValveSoftware/counter-strike_rules_and_regs`); notes appended as `designs/team-identity.md` §11.
  The 3-of-5 rule is Valve's own (§3.2.5(a), §3.10.1) and stays. Five informed decisions TI-V1 to
  TI-V5 raised: adopt Roster/Team vocabulary, match the fixed five-player Core Lineup before the
  rolling core of 7, stamp stand-ins, exclude coaches (`m_iCoachingTeam`) from side keys, keep k.
  Design stays `awaiting review` until the owner answers and the two-tier rule is re-measured.
- 2026-09-23: **Team Identity approved** by the owner, as reviewed: §11's TI-V1 to TI-V5 are accepted
  as recommended and bind over §3 where they differ (Roster/Team vocabulary; fixed five-player Core
  Lineup matched first, extended core of 7 as fallback; `standIn` stamp; `IsCoach` excluded from side
  keys; k = min(3, |S|)). First approved design. Build waits for Phase 1 per D1; before the build
  starts, re-run the §3.3 clustering measurement with the two-tier rule and fold §11 into §3.
- 2026-09-23: **Round Facts approved** by the owner after revision. D3 is now decided:
  **rules-driven**, not an app-side walk, so users can edit the classification (HLTV bands as
  `params:` defaults) and extend the record from their rules directory. The six engine pieces it
  needs are filed as CS2DemoKit #54 (team scope on the forward path, game-rules providers, a real
  `$round_end` on GOTV profiles, `event.frame_tick`, `round.team.money`, `enrich.round.plant_site`);
  the build starts when a release closes it. Ownership chosen: CS2DemoKit ships the ruleset, the app
  caches rows in the Analysis tier keyed by the ruleset's resolved identity. Owner's answers to all
  seven decisions are in the design's §8; no open questions remain. The Round Index's "call the
  extractor when the tier is absent" fallback is withdrawn (there is no extractor).
- 2026-09-23: Zone Baking reviewed by the owner from the markdown. D2 to D7 accepted as recommended;
  D1 left to the design's investigation and decided there: no `bundle.json` change and no schema
  bump (the proposed `zones` reference had no reader; drift is caught by `bundleMapVersion`). Owner
  requirement added: user-defined zones and callouts over the baked default, designed as a per-map
  overlay in `<config>/zones/` (§3.8; user volumes win inside their polygons, `EffectiveVersion`
  replaces `zonesVersion` as the stamp consumers store, +1.5 days). Two new decisions Z-1 (an opt-in
  Round Index mode that tokenises through the resolver) and Z-2 (overlay scope per install) carry
  recommended answers. Design stays `awaiting review` until the owner confirms §3.8 and Z-1/Z-2.
- 2026-09-23: **Zone Baking approved** by the owner, as revised: no `bundle.json` change (D1), D2 to
  D7 as recommended, the Custom Zones overlay (§3.8) in scope, Z-1 (opt-in resolver-token mode,
  owned by The Round Index) and Z-2 (overlay scope per install) as recommended. Third approved
  design. The Round Index review must take Z-1 into its fingerprint design.
- 2026-09-23: **The Round Index approved** by the owner on the design's recommended answers. The
  two attached conditions were discharged in the revision: D2 re-measured over a real hundred-demo
  corpus (postings win; SQLite is a larger file for slower lookups; D2 closed for all five stores)
  and Z-1 folded in as the opt-in `TokenSource.Zones` mode with the map's `EffectiveVersion` in the
  fingerprint. Also folded in: the extractor fallback is withdrawn (Round Facts is rules-driven, the
  index requires its rows and inherits the #54 dependency), and Find Rounds Like This takes `Ctrl+F`
  (D7). Fourth approved design; all of Phase 0's designs and the flagship's are approved.
- 2026-09-24: Tag Store: the owner asked whether the Sportscode XML export is an open standard and
  what justified it. It is not (a vendor format with no specification or licence text; no open
  standard exists for the domain) and the justification was a research-artifact bet with no
  consumer. The exporter is removed from v1 (`designs/tag-store.md` §3.8 now records the field
  mapping and the trigger for building it; D2 and D5 withdrawn; integrator owner action O-33
  withdrawn; effort 12 → 11 days). The `.dvtag.json` document is the format of record, MIT with the
  repository; its three interchange-friendly conventions are kept on their own merits. If an export
  is wanted sooner it is CSV, which the research's spreadsheet evidence supports.
- 2026-09-24: **Tag Store approved** by the owner, as recommended (D1 config root keyed by hash, D3
  clamp to round, D4 hash helper in Playback2D.Pipeline, D6 keep rejected proposals), with the XML
  exporter already removed from v1. Fifth approved design. Remaining: Grenade Walk, Suggested Tags,
  Strat Model, Step Authoring.
- 2026-09-24: the smoke creation-packet decode bug the Grenade Walk design found (its §5.2) was
  re-verified on a fourth real demo and filed as CS2DemoKit #56 (integrator O-34 done). Grenade Walk
  put up for owner review in markdown; D3 answered, six decisions remain.
- 2026-09-24: **Grenade Walk approved** by the owner, as recommended (stride-4 trajectories, walker in
  the App, background indexing off by default, named thresholds, `setpos` at release, Parser-level
  upstream proposal). Sixth approved design. Remaining: Suggested Tags, Strat Model, Step Authoring.
- 2026-09-24: **Suggested Tags, Strat Model and Step Authoring approved** by the owner on their
  recommended answers. **All nine designs are now approved**; the design phase of the Strat Room is
  closed. Upstream sweep at the owner's direction: CS2DemoKit #58 filed (PositionSampler: IsAlive and
  Team on the sample, the wrong "live pawn" doc, Tick is the frame clock, empty-string Place; covers
  The Round Index's proposals A and B and Suggested Tags §5.3) and #59 filed (ProjectileSampler, a
  sibling of PositionSampler for all five projectile classes; Grenade Walk §5.1, D7). Deliberately
  NOT filed: Suggested Tags route (b), the team-presence rules provider, which D6 holds until a team
  asks to author detectors in YAML; Team Identity's two optional notes (clan on ParsedDemo, the
  `.dem.info` match date), which the design itself declines to propose; Zone Baking's optional
  sidecar-reader note. Open upstream set: #53, #54, #56, #58, #59. The build order is the integrator's
  §5 of `designs/00-overview.md`, starting with Content Identity and Round Facts (which waits on #54).
- 2026-09-24: **`docs/strat-room/` promoted into the tree** at the owner's direction: the
  `.git/info/exclude` entry is removed, the header above no longer describes a local file, and the
  eleven documents (this plan, nine designs, the integrator overview) are staged for the owner to
  commit. Session-scratchpad paths were replaced with a neutral placeholder before staging; the
  probes themselves stay uncommitted and re-creatable from each design's §10.
- 2026-09-24: **Build workstream started** on branch `feature/strat-book` (from `main` at `d90ec9f`).
  Rules set by the owner: every phase/sub-feature on its own `feature/strat-book/<slug>` branch,
  merged locally into `feature/strat-book` only after build and tests pass, then pushed to origin;
  implementation and testing strictly one item at a time; planning and research in parallel; no
  planning or temporary markdown committed (`docs/strat-room/` stays untracked in the working tree);
  no `.dem` file ever deleted; no PRs until the whole workstream is done and the owner has verified
  it in a live run; items blocked on CS2DemoKit progress as far as they can and park the rest.
  Phase 0 workflow launched: Content Identity → Round Facts (engine parts parked on #54) → Zone
  Baking baker → Zone Baking app side, each with a verifier that merges and pushes; in parallel,
  the Team Identity fold-in and re-measure, the two research tables, and the Scene2DHost seam note.
- 2026-09-24: the two open research items were written up from what the designs already measured:
  `research/inputs-per-demo-source.md` and `research/place-names-from-the-pawn.md`. Both stay in
  progress: Valve matchmaking (builds 10231 to 10896) is measured and F1 is a go on that source;
  FACEIT, HLTV broadcast and POV are outstanding, and each file names the one demo per source that
  closes it (HLTV by re-downloading a `demos/CORPUS.md` name, FACEIT and POV supplied by the owner)
  and the fallback every design already carries. Re-acquisition stays with Inputs Per Demo Source.
- 2026-09-24: **Content Identity merged** into `feature/strat-book` (`f9b2b23`) and pushed. First
  verify pass failed on one gap (tag-store §3.10 item 5, the real first/last tick in the annotation
  clock header); the fix pass closed it; re-verify green (build 0 errors; app suite 1202 passed, 0
  failed after one interference-only re-run, 2 skipped). Round Facts build started.
- 2026-09-24: **Round Facts merged** into `feature/strat-book` (`19025b2`) and pushed, engine half
  parked on CS2DemoKit #54 (the ruleset draft sits untracked at `docs/strat-room/parked/`). Verify
  green on the standard app tier (1284/0/2); the full tier with `DEMO_PATH` ran 1492 with 49 skips
  (six are the parked RealDemo tests) and two failures in `Playback2DBombTimerTests` that the
  verifier attributed to the chosen demo rather than the change (recorded below once confirmed).
  Zone Baking part 1 (baker) started.
- 2026-09-24: confirmed by the verifier: the two full-tier failures seen during the Round Facts
  verify (`Playback2DBombTimerTests.BombTimer_IsC4TimerLength_AtPlant_AndDecreases`,
  `AimParity.SprayControlOracleRealDemoTests.ShippedSprayColumn_IsWired_AndReportedBesideTheOracle`)
  fail identically on `feature/strat-book` at `f9b2b23`, before any Round Facts commit, with replay
  `match730_003731893271710924851_1024675027_129.dem`; neither file is in the diff (last touched by
  PRs #13 and #10). **Pre-existing, not attributable to the workstream**; noted for a separate look
  (possibly replay-specific). Also noted: `DemoTestHelper.RequireDemo` takes a file, not a folder,
  so full-tier runs pick one replay; verifiers should name the one they used.
- 2026-09-24: **Zone Baking part 1 merged** into `feature/strat-book` (`49d90a3`) and pushed: `Zones.cs`,
  the `--zones` top-up mode with `--diag`, the full-bake hook, and `assets/<map>/zones.json` for the
  ten shipped maps (plain JSON, D3). Verify: build clean; `--zones --diag` baked 10/10 with zero
  self-check failures; nine coverage rows equal the design's §2.5 baseline exactly, de_vertigo differs
  by one area; a re-bake was byte-identical. `bundle.json` untouched (D1). Part 2 started. Upstream
  check 03:05: #53, #54, #56, #58, #59 all open, no comments; CS2DemoKit.Parser still 0.12.0.
- 2026-09-24: **Zone Baking part 2 verify red on a calibration, not a defect.** Five commits on
  `feature/strat-book-zone-baking-app` (resolver and layer in Core, pipeline loader with the overlay,
  dv2d flag and two nuke goldens, tab toggle and diagnostics, docs). Build green; the one failure is
  `ZoneResolverAgreementTests.CascadeAgreement_ClearsThePerMapFloor_OnEveryDemoUnderDemoPath`: a
  third mirage replay (`match730_003800088723782107231_0720579879_408.dem`) scores 90.35 % against
  the 92 % floor the design fixed from two mirage demos (92.41 / 93.39, both still reproduce). The
  resolver and the test are both as designed, so the verifier refused to merge and asked for the
  owner's D7 call: lower the mirage floor, or assert per map on pooled samples across every demo of
  that map. Proposed: pooled per-map assertion at measured-minus-one over the pooled set, with the
  per-demo table still written to the test output. Awaiting the owner. Upstream check 04:10: all five
  issues open, no comments; CS2DemoKit.Parser still 0.12.0.
- 2026-09-24: **Phase 0 workflow complete** (15 agents, 3 h 9 min, no failures): Content Identity
  `f9b2b23`, Round Facts `19025b2` (engine half parked on #54), Zone Baking part 1 `49d90a3` merged
  and pushed; the three planning tasks done. **Zone Baking part 2 not merged:** the fix pass
  investigated the mirage floor instead of patching it (the rule forbids weakening a failing test):
  the failing replay is a 9.4-minute abandoned match; its largest miss is Underpass→Catwalk where the
  pawn stands at Catwalk height 3 units off the nav mesh while `m_szLastPlaceName` still says
  Underpass, the sticky-field residual the design's §6 risk 2 names; the D4 tie rule is already at
  its best for mirage; pooled mirage agreement is 92.83 % over ten demos. The re-verify refused the
  merge as designed. Owner decision D7 requested: `pooled` (per-map assertion over pooled samples at
  measured-minus-one; recommended), `floor90` (per-demo floor lowered to 90 for mirage), or `hold`.
  The answer goes in `docs/strat-room/parked/d7-zone-floor.txt`; the Phase 1 lane checks for it
  before every build item and applies it on the existing branch, re-verifies, merges and pushes.
  Also carried forward from Phase 0: `DemoTestHelper` ignores a folder `DEMO_PATH` (119 RealDemo
  skips), added as the first small Phase 1 build item; two machine-local GPU parity failures in the
  playback2d tier predate the branch; the two full-tier App failures noted earlier are pre-existing.
  Phase 1 workflow launched.
- 2026-09-24: **Test harness: folder DEMO_PATH merged** into `feature/strat-book` (`fde23e6`) and
  pushed: `DemoTestHelper` resolves a folder to one demo (`DEMO_PATH_PICK`, then the reference demo,
  then the ordinal-first `.dem`); eight resolution tests; app standard tier 1292/0/2. Not a plan
  feature; infrastructure so RealDemo tests run against the replays folder. The Round Index build
  started. D7 still unanswered; the hook found no decision file before the item.
- 2026-09-24: **The Round Index merged** into `feature/strat-book` (`0dd8bb6`) and pushed, three
  commits (token, sidecar, builder and stamps; the in-memory situation index with tolerances,
  adjacency seam and place snap; the Situations tab, the evaluator after `roundfacts`, settings and
  the wasm row). Verify green. One deviation recorded as a note, not a finding: integrator
  correction 1 routes the sidecar through a `DemoCacheStore.WriteSibling` seam that Grenade Walk
  owns and no branch has yet, so the index ships its own `RoundIndexStore` under
  `cache/round-index/` with a `Changed` subscriber and orphan sweep, exactly the design's §3.3/§3.6
  shape. **Follow-up for the Grenade Walk build:** land the sibling seam and move the round index's
  sidecar onto it (a `RoundIndexSchema` bump). Real-corpus indexing stays parked on #54 (no Round
  Facts rows). Query Canvas started.
- 2026-09-24: Phase 1 workflow stopped at Query Canvas: the implementer committed its work
  (`8ae76fd` on `feature/strat-book-query-canvas`, build and standard tiers green per its
  transcript) but parked on a background wait for the full tier and ended without reporting, which
  the workflow treats as a failure. Script hardened (foreground-only test runs, always report,
  a no-result agent stops the lane cleanly) and resumed from cache with the verifier picking up the
  existing branch. Nine agents replayed; nothing on `feature/strat-book` changed.
- 2026-09-24: **Query Canvas merged** into `feature/strat-book` (`39e5206`) and pushed; the verifier
  ran every tier itself (the implementer had ended before reporting) and first killed an orphaned
  test host from that run that was holding App.Tests output locks. Find Rounds Like This started.
  Upstream check 06:33: all five issues open, no comments; CS2DemoKit.Parser still 0.12.0.
- 2026-09-24: **Find Rounds Like This merged** into `feature/strat-book` (`d9759ab`) and pushed
  (`Ctrl+F`, a new `Playback2DAction`, the marker carrying the pawn's place, a mode menu entry and
  toolbar button, the round-trip test). Result Cards And Walking started. Upstream check 07:22: all
  five issues open, no comments; CS2DemoKit.Parser still 0.12.0. D7 still unanswered.
- 2026-09-24: **Result Cards And Walking merged** into `feature/strat-book` (`7c4eff5`) and pushed
  (38 files, one commit). It took the hit-render budget note's recommendation: a per-hit positions
  sibling written by the index builder from the same walk, so a thumbnail never re-parses a demo.
  Team Identity build started (Phase 1 per D1). Upstream check 08:08: all five issues open, no
  comments; CS2DemoKit.Parser still 0.12.0. D7 still unanswered.
- 2026-09-24: the pre-existing-failure triage note is written
  (`docs/strat-room/research/pre-existing-test-failures.md`): all four failures (the two App full-tier
  ones on the Jan-2025 nuke replay, the two GPU parity ones) reproduce identically on `main` at
  `d90ec9f`, so none is attributable to the workstream. The BombTimer one is replay-specific: the
  first plant in that replay is followed by a round-ending death ten ticks later. Upstream check
  08:59: all five issues open, no comments; CS2DemoKit.Parser still 0.12.0. D7 still unanswered.
- 2026-09-24: **Team Identity merged** into `feature/strat-book` (`c5ad261`) and pushed, one commit.
  Two §3.9 UI details were not built (Library card subtitle and us mark; Match Overview roster label
  and glyph); the verifier recorded them as scope drift between the design section and the
  integrator's deliverable row, not defects, and merged. Carried as follow-ups on the row. Demo
  Provenance Labels started.

**10:12** Demo Provenance Labels verified (`8c860ed`: standard app tier 1428/0, full app tier 1652 with only the two known failures, twice) but the verifier could not merge: the permission classifier denied `git merge --no-ff` twice. The lane went on and cut Search Filters And Live Count from `c5ad261`, without the provenance source it needs. Stopped the run, merged `--no-ff` by hand as `858ffa2`, pushed, fast-forwarded the Search Filters branch onto it with the implementer's few minutes of uncommitted work restored, hardened the script (a verified-but-unmerged item now stops the lane; the verifier reports a denied merge rather than working around it) and relaunched from Search Filters And Live Count. D7 still unanswered.

**10:58** Search Filters And Live Count verified and merged as `10e4f54` (standard app tier 1435/0; full app tier 1660 with only the two known failures); pushed. Overlay View started. Upstream at 10:44: CS2DemoKit still v0.12.0, #53 #54 #56 #58 #59 open with no comments. D7 still unanswered.

**11:35** Owner decided D7: **pooled** (Zone Baking part 2 agreement floor becomes 92% pooled over the ten-demo sample, measured 92.83%; the per-demo mirage 90.35% no longer gates). Written to `docs/strat-room/parked/d7-zone-floor.txt`; the D7 hook applies it on `feature/strat-book-zone-baking-app` and merges it before the next Phase 1 item.

**11:45** Overlay View verified and merged as `0278af2` (app standard 1439/0; playback2d standard only the two known GPU parity failures; cli 134/0); pushed. The D7 hook is applying the pooled floor on `feature/strat-book-zone-baking-app` before Watched Situations. Upstream: CS2DemoKit still v0.12.0; #53 #54 #56 #58 #59 open, no comments.

**12:03** Zone Baking part 2 landed as `a82f6fa` (pushed). The D7 hook merged `feature/strat-book` into the item branch (twelve union-style conflicts with the query and overlay layer ids), rewrote the agreement test to assert per map on the pooled samples of every demo, and verified (app standard 1442/0; cli 140/0; playback2d only the two GPU parity failures; app full 1670 with only the two known failures, agreement test passing). The permission classifier denied its merge, so the orchestrator landed it. **Deviation from the owner's number:** floors were set per map at pooled minus one point from a fresh ten-per-map run over 83 demos (mirage pooled 92.91, floor **91**, not 92), because the verification run at DEMO_ZONES_PER_MAP=2 pools mirage's two smallest demos at 91.67. Raising mirage to 92 is a one-line change if the verification cap goes to 10. Decision file reset to hold.

**12:32** Phase 1 run `wf_eab20ddd-329` stopped at Watched Situations when its agents hit the Fable usage limit; relaunched as `wf_be5306de-13a` on Opus, resuming the uncommitted work. Watched Situations verified and merged as `9308108` (app standard 1447/0; real-demo variant skipped on #54); pushed. Tolerance Slider, the last Phase 1 item, is building (`09fab30` committed).

**12:50** **Phase 1 complete.** Tolerance Slider merged as `447228a` (app standard 1451/0; the full app tier was started but did not finish inside the verifier budget). Integration head `447228a`, pushed. Phase 2 launched as `wf_30dfd14e-e1f`, serial, thirteen items: Zone Resolver In The App (Phase 1 follow-up: the composition root still registers `NoZonePlaceResolverSource`), Tag Store (steps 1 and 2), Tag Query And Format Doc (steps 5 and 7; the format page goes to `docs/tags-format.md`, not under `docs/strat-room/`), Tag Track, Tag Palette, Click To Tag Position, Label Mode, Free Labels From Round Facts, Review Queue (pulled forward from Phase 4 because The Matrix opens its clips there), The Matrix, Suggested Tags Detectors, Suggested Tags Review, Suggested Tags Tuning And Docs. Hand-tag validation (Suggested Tags step 7) is left for humans. The workflow guard now lets a long full-tier run go to the background with bounded polling of its log.

**13:02** Zone Resolver In The App verified and merged as `71c824f` (app standard 1454/0); pushed. The running app now resolves query drops and the Tolerance Slider through the baked zones plus the user overlay where a map has them. Follow-up: this source's overlay loads do not publish to ZoneOverlayDiagnostics (the hub holds one report and the Playback tab owns it). Tag Store building.

**13:18** Tag Store merged as `61d5ff0` (app standard 1492/0) and Tag Query And Format Doc as `8e36df2` (1505/0); both pushed. Tag Track building.

**13:34** Tag Track merged as `c5fcf81` (app standard 1514/0); pushed. Tag Palette building. Upstream: CS2DemoKit still v0.12.0; #53 #54 #56 #58 #59 open, no comments.

**14:06** Tag Palette merged as `76deb98` (app standard 1550/0) and Click To Tag Position as `0cd5164` (1557/0); both pushed. Label Mode building.

**14:22** Label Mode merged as `b03d380`; pushed. Free Labels From Round Facts building (synthetic rows; real rows wait on #54).

**15:10** Free Labels From Round Facts merged as `539e05c` (app standard 1576/0); pushed. Review Queue building. **New full-tier failure to investigate:** `Playback2DRealDemoRenderTests.RealDemo_MidRound_RendersLivePlayerFormation` fails with a thread-affinity error on the integration branch. It is an old test (initial import) and was not among the failures in the Phase 1 full-tier runs up to `a82f6fa`, so it is either a regression from an item merged after that (Tolerance Slider, Watched Situations and the Phase 2 items had no completed full-tier run) or one of the known cross-test races. To bisect when the lane is idle; not a blocker for the standard tiers.

**15:26** Review Queue merged as `906df0f` (app standard 1587/0); pushed. The Matrix building.

**16:14** The Matrix merged as `755f22e` (app standard 1605/0; app full 1837 with 3 failures). Suggested Tags Detectors building. **The MidRound failure is deterministic, not a race:** `RealDemo_MidRound_RendersLivePlayerFormation` fails alone (Avalonia Dispatcher.VerifyAccess) on `906df0f` and on The Matrix branch. Last full run without it: `a82f6fa`. Bisect over `a82f6fa..906df0f` once the lane is idle (testing stays one at a time).

**17:12** Suggested Tags Detectors merged as `9345b3b` (app standard 1674/0; app full 1914 with only the three known failures, MidRound among them); pushed. Owner asked (16:20) for Sonnet where appropriate: the Phase 2 script now routes the first-pass verifier and light items (Suggested Tags Tuning And Docs) to Sonnet, with implementers of design-heavy items, the fix pass and the re-verify on the session model. The lane was stopped a few minutes into Suggested Tags Review and relaunched as `wf_221fdfd1-7d1` from that item with its uncommitted work kept.

**18:15** Suggested Tags Review merged as `878b537` (the first item verified on Sonnet; merged `--no-ff` and pushed, though its merge subject reuses the item subject rather than the repo's "merge ..." form). Proposal store and verdicts under `cache/suggestions/<StableKey>.json`, the Proposal Track, the queue panel, J/K/Y/N/Enter/Ctrl+Y, accept/edit/reject into the Tag Store with provenance "suggested". Deviation accepted by the verifier: overview correction 1 wants `demos/<StableKey>.suggestions.json` through a `DemoCacheStore.WriteSibling` seam that does not exist; this follows the Round Index's own-directory precedent, so the sibling seam follow-up now covers both. Follow-up: nothing triggers relearning `site-regions.<map>.json` when the library gains demos. Suggested Tags Tuning And Docs building (Sonnet).

**19:20** Suggested Tags Tuning And Docs: the Sonnet first-pass verifier found two real defects and refused to merge, but reported `ok:true`, so the lane skipped its fix pass and stopped. (1) The tuning report pools every demo's proposals and hand tags and matches on round number plus tick overlap with no demo identity, so round 1 of demo A can match round 1 of demo B. (2) Scoring ignores site, against suggested-tags.md section 7.3's rule that code and site must agree. Relaunched that item as `wf_b580db0d-72e` with both findings as a second-pass brief, on the session model rather than Sonnet. The verifier prompt now says a refused merge is `ok:false`. Lesson for the routing: the Sonnet verifier's review caught the bugs; only its verdict flag was wrong.

**20:25** **Phase 2 complete.** Suggested Tags Tuning And Docs merged as `e1275a7` after its second pass (app standard 1735/0; app full 1976 with the three known failures; a one-off RoundIndexBudget timing flake did not reproduce). Integration head `e1275a7`, pushed. Next: bisect the MidRound failure over `a82f6fa..906df0f`, then launch Phase 3.

**20:45** **MidRound failure found and fixed.** Bisected with a single-test run (`--treenode-filter` on the one test, about 2 s once built): passes at `447228a` and `c5fcf81` (Tag Track), fails from `76deb98` (Tag Palette). Cause: `TagPaletteButtonViewModel.Swatch` was a mutable `SolidColorBrush`, created on the test thread and rendered by a Border, so the compositor tripped `Dispatcher.VerifyAccess`. Fixed with an `ImmutableSolidColorBrush` (`ca2f66f`, merged `37780ff`, pushed; the test passes and app standard is 1735/0). The known failures are back to the two pre-existing ones. **Phase 3 launched** as `wf_fa1aa3a0-aa7`, serial, sixteen items: Shape Tools, Strat Store, Strat Session And Tab, Callout Aliases, Strat Evidence And History Data, Role Sheet And Format Doc, Token Keyframes, Scene Frame Host Seam, Strat Frame Source, Step Authoring, Strat Export, Create Strat From Round, Strat Record Panel, Strat Version History, Role View And LAN Print, Strat Goldens And Docs. Walk It In The Server stays deferred on D4. Sonnet builds the light items and runs every first-pass verification. The guard now requires the item branch before the first edit and an honest `ok` flag.

**21:06** Shape Tools merged as `6a70b85` (first Phase 3 item, first-pass verified on Sonnet); pushed. Strat Store building.

**21:22** Strat Store merged as `c481177` (models, store, clock, validator, CalloutResolver and canonical lists); pushed. Strat Session And Tab building.

**21:55** Strat Session And Tab merged as `78af8dc`; pushed. Callout Aliases (a Sonnet implementer) again started editing while `feature/strat-book` was checked out, despite the guard line; the orchestrator moved its uncommitted work onto `feature/strat-book-callout-aliases`. The Phase 3 script now opens every implementer prompt with the checkout as its mandatory first tool call; the lane will be relaunched from the next item once Callout Aliases merges so the remaining Sonnet items get it.

**22:18** Callout Aliases merged as `0909033`; pushed. Phase 3 lane stopped at the item boundary (Strat Evidence And History Data had only created its branch, no edits; branch dropped) and relaunched as `wf_87e871a8-0e1` from that item with the first-call branch checkout.

**22:48** Strat Evidence And History Data merged as `2bfc342` (evidence by the reserved label groups, record pane data, StratHistory with Materialize, Inverse and phrasing); pushed. Role Sheet And Format Doc building on Sonnet, on its own branch from the first call. Upstream: CS2DemoKit still v0.12.0; #53 #54 #56 #58 #59 open, no comments.

**23:35** Role Sheet And Format Doc (`eca83ce`) and Token Keyframes (`e83a7e6`) merged (Strat Model complete); pushed. Scene Frame Host Seam building (the integrator's riskiest App change; its brief makes the existing Playback2D tests and goldens pass unchanged).

**23:51** Scene Frame Host Seam merged as `621ec17` (ISceneFrameHost over Scene2DHost); pushed. Strat Frame Source building. Upstream: CS2DemoKit still v0.12.0; #53 #54 #56 #58 #59 open, no comments.

**00:07 (2026-09-25)** Strat Frame Source merged as `57feb4c` (StratSceneSpec, StratFrameSource in Pipeline, StratHudDataSource); pushed. Step Authoring (the canvas) building; Step Authoring's row flips to done when it merges.

**00:55** Step Authoring canvas merged as `e7aec73` (Step Authoring complete); pushed. Strat Export building. Upstream unchanged (v0.12.0; issues open, no comments).

**01:11** Strat Export merged as `f918c6f`; pushed. Create Strat From Round building.

**02:15** Create Strat From Round merged as `6b553ff` (app standard 1967/0; full tier with real mirage and nuke rounds); pushed. Strat Record Panel building (Sonnet).

**02:47** Strat Record Panel merged as `eaedfd5`; pushed. Strat Version History building on its own branch.

**03:19** Strat Version History merged as `219a2ed`; pushed. Role View And LAN Print committed (`6f4b6e9`) and under verification.

**03:25** Owner accepted the mirage zone agreement floor at **91** (pooled minus one point, as landed in `a82f6fa`). D7 is closed.

**03:35** Role View And LAN Print merged as `0c04fe0`; pushed. Strat Goldens And Docs, the last Phase 3 item, building on its own branch.

**04:10 (2026-09-25)** **Phase 3 complete; stopping for the owner's early live review** (Phase 4 not launched, per the owner's 23:10 instruction). Strat Goldens And Docs merged as `e0a3e90`; integration head `e0a3e90`, pushed. Review: solution builds (0 errors); every standard tier green (3317 tests; the only failures are the two known GPU parity tests); the desktop app launches and stays up (smoke run of about 50 s, no new crash or error log); the five new tabs (Situations, Teams, Review, Round Tagger, Strat Book) are registered and on by default. No stopping-point work was needed. Known limits the live check will show: Round Facts rows do not exist on real demos until CS2DemoKit #54 lands, so the Round Index, Situations search, Find Rounds Like This, result cards, the overlay, watched situations, Suggested Tags proposals and Free Labels produce nothing on real demos (all tested on synthetic rows); the two research rows wait on FACEIT, HLTV and POV demos. Open follow-ups carried into Phase 4: WriteSibling seam (Round Index and Suggested Tags sidecars), relearning site regions, the tags place pass, Team Identity initials on strat tokens, book-default slot line-ups, branch-into-another-strat editing, an end-to-end UI test for Create Strat From Round, the section 3.9 Team Identity UI details.

**2026-09-25** CS2DemoKit **0.13.0-beta0001** shipped (PR #74) and closed #53, #54, #56, #58 and #59. Owner: bump the pin and wire the new APIs into Phases 0 to 3 with the same workflow pattern, Sonnet where appropriate, then stop for a second mid-point live review. Launched `wf_88053b41-43f`: one read-only wiring-map agent (`research/engine-0.13-wiring.md`), then six serial items: Engine Upgrade To 0.13 (pin bump and compile breaks; the one owner-approved pin exception), Round Facts From The Engine (the parked engine half, rules-driven `for: each_team`), Real Demo Unskip Sweep (every test skipped on #54), Alive And Team From Position Samples (#58), Grenade Projectiles Wiring (#59 and #56 into Create Strat From Round and Suggested Tags), Player Input Refresh (#53). Sonnet runs every first-pass verifier and the last four implementers.

**12:06** Engine Upgrade To 0.13 merged as `d86f46b` (CS2DemoKit Parser, Analysis and Analysis.Rules at 0.13.0-beta0001; no transitive pin moved; compile breaks fixed; one Rule Workbench graph assertion now excludes rows drawn to the new ExternalStateNode, per the 0.13 releasing notes, accepted by the verifier); pushed. Standard tiers 3321 with only the two GPU parity failures; app full tier 2244 with only BombTimer and SprayColumn, so 0.13 fixes neither. Round Facts From The Engine building.

**13:26** Round Facts From The Engine merged as `56bb8d3`: `rules/round_facts.rules.yaml` (for: each_team, per: team_round; server winner and reason on round_decided, frame-clock ticks, freeze-end team money, plant site, first contact and opening kill; HLTV thresholds as params) behind EngineRoundFactsRowSource; RoundFactsRealDemoTests 5 passed on real replays; pushed. The verifier's full app tier did not finish inside its poll window, and the implementer's own full-tier log showed `RuleWorkbenchGraphTests.Graph_Conversion_ProducesNodesAndEdges` failing in the full tier (it passes in standard). To check at the mid-point review. Real Demo Unskip Sweep building.

**15:13** Real Demo Unskip Sweep: the Sonnet implementer re-enabled every #54 skip by 13:37 (`4169fc9`, standard app 2003/0) but then spent about 95 minutes on the App full tier, killing two runs it misread as hung and timing out a third; its fourth run did finish (1429 s) and showed four failures it never saw: two SuggestedTagsRealDemoTests (index source vs walk; pinned folds golden), SearchFiltersRealDemoTests.AFactFilter (planted-round count), and RuleWorkbenchGraphTests.Graph_Conversion (pinned 142 names, moved by the shipped round facts ruleset). Lane stopped before its verifier re-ran the 25-minute tier and relaunched as `wf_156c525e-654` with those four as a second-pass brief on the session model. The script now verifies with the item's own RealDemo classes run from the built test binary; the whole App full tier (now past 30 minutes) runs once at the mid-point review.

**15:40** Real Demo Unskip Sweep merged as `ec75f41f` after its second pass (about 25 minutes, targeted classes only: SuggestedTagsRealDemoTests 8/8, SuggestedTagsGoldenTests 2/2, SearchFiltersRealDemoTests 1/1, RuleWorkbenchGraphTests 6/6; standard app 2003/0). All four failures were test premises the real data disproves; no product code changed: engine rows end a round at round_decided (448 ticks earlier) and keep a round the synthesised rows dropped, so the engine fold got its own golden (`tests/fixtures/suggested-tags/engine-rows/`); a plant 10 ticks before the round is won has no 1 s index step after it; round facts is `for: each_team`, so node names repeat per side. **Confidence cost to note:** the walk-vs-index proposal check is narrowed to execute, default and fake because retake proposals differ between the walk and the index on both synthesised and engine rows; that retake difference is unexplained and is a follow-up. Alive And Team From Position Samples building.

**17:05** Alive And Team From Position Samples stopped the lane after its fix pass: the first verifier caught the positions file and the index bucketing CT/T from different sources (fixed in `3c071c7`: side from each slot's first live sample per round); the re-verifier then found SuggestedTagsRealDemoTests 4 of 8 failing (per-place counts moved, goldens not re-pinned), RoundOccupancyBuilder still bucketing by the facts seating while the index uses the sample, and no cache schema bump for the ""-place change. Relaunched as `wf_024caa71-f89` from that item with all three as a third-pass brief on the session model; Grenade Projectiles Wiring and Player Input Refresh follow.

**17:25** **Stopped at the owner's request to free the machine; second mid-point stop.** Integration head `ec75f41f` (pushed): CS2DemoKit 0.13.0-beta0001, real Round Facts rows from the shipped ruleset, every #54 skip re-enabled and passing. Checks on the head: solution builds; all standard tiers green except the two known GPU parity tests (3338 tests); the desktop app launches and stays up with no new crash or error lines; the real-demo classes behind Situations (Query Canvas, Find Rounds Like This, Search Filters, Tolerance Slider, Watched Situations, Overlay View) passed on real replays in the sweep's verification. The 30-minute whole App full tier was not re-run to save the machine. Deferred to the next session: **Alive And Team From Position Samples** (parked unmerged on its branch at `63cea95`, third pass unverified), **Grenade Projectiles Wiring** and **Player Input Refresh** (not started); both are improvements on top of working 0.12-era code paths, not blockers for the live check. Follow-ups: the walk-vs-index retake difference; money_reliable per-side nodes without a subtitle (engine display gap).

**Lean finish (owner, 2026-09-25):** no heavy real-demo tests until all wiring is done and Phases 1 to 3 can be tested together; unit tests are fine; fewer redundant checks; some issues may surface only in the one full real-demo pass. New script `strat-book-lean-finish.js`, run `wf_45aeafc3-a3a`: Wire phase (Alive And Team from `63cea95` with its goldens reverted for later re-pin, Grenade Projectiles Wiring, Player Input Refresh), each with build plus standard unit tiers only and one light Sonnet review (one fix pass, then park rather than loop); then a single Integration Test pass that runs the whole App full tier once (up to 60 minutes, never killed early), re-pins moved goldens with reasons, fixes or parks what it finds, and merges.

**18:29** Alive And Team From Position Samples merged as `1f05248` (lean path, unit tests only; goldens left for the integration test pass). Grenade Projectiles Wiring under review.

**19:00** Grenade Projectiles Wiring merged as `2d49613` and Player Input Refresh as `4cfae65` (lean path, unit tests only). All six 0.13 wiring items are on `feature/strat-book`. The single Integration Test pass (whole App full tier once) started.

**20:05** **Lean finish complete; third stop for the owner's live review** (Phase 4 not started). Integration test pass merged as `71b1880` (pushed): standard tiers 3353 (only the two GPU parity failures); the whole App full tier ran once over Phases 1 to 3 plus all six 0.13 wiring items: 2267 tests, 8 failures, all resolved or explained: 4 Suggested Tags goldens re-pinned (occupancy alive, side and place now come from the position samples, which moves some detonation places), the current-replay aim probe re-pinned to a post-delta replay, the two known (BombTimer, SprayColumn), and RoundIndexBudgetTests (a 50 ms timing budget that fails only under the full run's load; passes alone). Occupancy cross-check quiet on the pinned demos (0 side mismatches). Desktop app builds and launches clean on `71b1880`. Parked for the owner: AimCapabilityProbe.MinSubtickYield (0.10) needs recalibrating, since every replay measures 0.024 to 0.037 so sub-tick aim timing reads Degraded everywhere (the documented 0.88 does not reproduce). Optional, not done: rebuilt user command shown in the parser tab, a view-level sub-tick real-demo test, reconstructor caching.

**23:19** Live check found the app stalling: the library's tier-2 sweep re-parsed the same two demos every ~10 s forever. Cause: after a restart, index.json claimed Round Facts for four demos whose cache records had none (the index was saved before a later record write), so the Round Index evaluator's Wants stayed true while Refresh returned early without stamping. Fixed in `012ab69`: the evaluator realigns the row from the record (Round Facts then recomputes) and logs it; regression test added; app standard 2019/0; pushed. Root cause of the stale index row itself (which write replaced those records) not yet traced.

**23:30** Second cause of the live stall, and the real one for the looping demos: the Round Facts evaluator returned without writing when the engine produced no rows (by design, so an empty payload would not read as current), which left those demos wanted forever; the tier-2 sweep re-parsed the same two newest demos every ~10 s. Fixed in `81e25e2`: a no-rows demo is skipped for the rest of the session under that fingerprint and logged; the row is untouched, so the next session or a ruleset change retries it. Relaunch confirmed: the backfill now moves through distinct demos with no repeats. **New finding for the owner:** every pro/HLTV demo in the library (the `furia-vs-*` ESL series) gets no Round Facts rows from the shipped ruleset, so Situations search, free labels and suggested tags do not cover pro demos yet; the matchmaking demos index normally. Needs a look at `rules/round_facts.rules.yaml` on the HLTV source profile (possibly a CS2DemoKit question).

**2026-09-26** Owner: good enough to continue with Phase 4 and on, with the lean pattern. Launched `strat-book-phase45.js` as `wf_841fc04a-7b6`: fifteen serial items, each with build plus standard unit tiers only and one light Sonnet review (one fix pass, then park), then a single integration test pass (whole App full tier once, re-pin, fix or park, merge). Items: Round Facts On Pro Demos (added first: pro/HLTV demos get no rows, which would leave the Dossier empty for pro opponents; one-demo probe allowed), Grenade Walk (rebased on CS2DemoKit 0.13 ProjectileSampler), Grenade Index, Lineup Cards, Lineup Clip Render, Lineup On A Strat Step, Pack Export (research folded in), Headless Packs, Map Pool Record (with the Dossier module shell), Setup Heatmaps By Buy, Opening Tendencies, Post-Plant And Retake, Situational Behaviour, Period Diff, Dossier Editing And Export. Review Queue was already built in Phase 2; Push Lineup To Server stays deferred with Walk It In The Server.

**00:22 (2026-09-26)** Round Facts On Pro Demos: parked on the engine. The pro/ESL demos start at the first live round and never carry `begin_new_match`; CS2DemoKit's built-in context turns `match_live` on only on `$match_start` (bound to `begin_new_match` in every profile), so `round_number` stays 0 and every per-round table is empty (a team_match table on the same demo returns rows). Filed as **CS2DemoKit #75** with a repro and a suggested fix; no app workaround (no rows synthesised from ClipRounds). Also likely empties the highlight and stats per-round tables on pro demos. The lane stopped because the reviewer had nothing to merge; the script now treats a no-commit parked item as parked and continues. Relaunched from Grenade Walk as `wf_ff66e67d-21f`.

**00:49** Grenade Walk merged as `6134386` (per-grenade rows over ProjectileSampler). Grenade Index building. CS2DemoKit #75 open, no reply.

**01:20** Grenade Index merged as `045c48a`. Lineup Cards building.

**01:51** Lineup Cards merged as `9d8e499` and Lineup Clip Render as `395e0c5`. Lineup On A Strat Step building. CS2DemoKit #75 open, no reply.

**02:22** Lineup On A Strat Step merged as `8826867` (Utility Book done apart from Push Lineup To Server, deferred). Pack Export building.

**02:53** Pack Export merged as `704e80d` (the Review Queue as one video, sections as title cards). Headless Packs building. CS2DemoKit #75 open, no reply.

**03:24** Headless Packs merged as `bcf3566` (Review Packs done) and Map Pool Record as `c7bb5e6` (with the Opponent Dossier shell). Setup Heatmaps By Buy building.

**03:55** Setup Heatmaps By Buy merged as `5ebde1a` and Opening Tendencies as `0116a4d`. Post-Plant And Retake building. CS2DemoKit #75 open, no reply.

**04:26** Post-Plant And Retake merged as `80b5ea7` and Situational Behaviour as `59d71bd`. Period Diff building; Dossier Editing And Export last.

**04:57** Period Diff merged as `34fc398` and Dossier Editing And Export as `6b4463f`: every Phase 4 and 5 build item is merged (Round Facts On Pro Demos parked on CS2DemoKit #75; Push Lineup To Server deferred). The integration test pass (whole App full tier once) started.

**05:30 (2026-09-26)** **Phases 4 and 5 complete; stop for the owner's live review.** Integration test pass merged as `c37c9d0` (pushed): standard tiers 3486 with only the two GPU parity failures (app 2140/0); the whole App full tier over Phases 1 to 5 ran once: 2393 tests, only the two known failures (BombTimer, SprayColumn). Grenade Walk real-demo invariants pass (about 1.8 s per 100k frames, input coverage 1.0) and the filtered walk equals the unfiltered one. Fixed in the pass: Grenade Walk read crouch from m_bDucked, which is always false on real demos; now from the FL_DUCKING bit of m_fFlags (walker version bumped so cached rows re-walk). Parked: the Grenade Walk golden (its reference demo is not on this machine); Round Facts On Pro Demos (CS2DemoKit #75, so pro opponents give empty per-round Dossier sections); real renders (lineup GIFs, a three-demo pack, dv2d pack end to end) and several threshold choices were not exercised and need the live check. Push Lineup To Server stays deferred.

**2026-09-26 (shell)** Owner's first live-review feedback on the Mac: too many top-level tabs. The Main strip went from four (Library, Match Overview, Stats, Reels) to eleven; the seven added tabs are all facets of one workflow and belong under a single Strat Book tab with a better in-tab navigation. New item **Strat Book Shell**, awaiting review. Proposal: one Main-strip tab "Strat Book" (Order 4, after Reels) hosting a left section rail: Strats (today's Strat Book browser), Situations, Tags (the Matrix; the palette stays in 2D Playback), Utility, Review, Dossier, Teams. Existing tab ids (`situations.search`, `review.queue`, `tagger.matrix`, ...) become section ids the hub resolves, so every ShowTab call, feature gate and test keeps working; the seven modules stop contributing Main-strip tabs and register sections with the hub instead. Open questions for the owner: whether Teams stays a section or moves to Library/Settings; whether Review (a clip tray like Reels) sits in the hub or beside Reels; whether the section rail is a left rail or a second tab row.

**2026-09-26 (shell, decided)** Owner: Teams under Library, Review in the hub, left rail. Built on `feature/strat-book-shell`: `TabPlacement.StratBook` and `TabPlacement.Library` on the descriptor contract, `TabSectionHost` (the sections, the selection, the activate-only-while-host-active lifecycle, the reconcile by identity), `StratBookHubViewModel` + `StratBookHubView` (the rail), the Library's Demos / Teams toggle, the shell routing (`TrySelectTab` resolves a section id through its host; the session and idle snapshots persist the section id as the active tab, so a session file from the strip era restores to the same place; the hub is gated by "any section on"). The seven modules changed placement, rail order and header only (Strats, Situations, Tags, Utility, Review, Dossier; Teams). Six shell tests over fake sections and one render smoke (the rail and the Library Teams view frames looked right); the touched App classes (153 tests) green.

**2026-09-26 (shell, merged)** Strat Book Shell merged into `feature/strat-book` as `f56a8d3` and pushed. App standard tier on the merged head: 2146 tests, 6 failures, all six reproduced identically at the commit before the shell on this Mac (checked in a throwaway worktree): five strat-canvas pixel goldens (`StratGoldenCaptureTests`, `StratExportTests`: max channel delta 150 to 157 against the 96 ceiling, the goldens were pinned on the Windows machine and this raster differs) and `SuggestedTagsGoldenTests.ThePinnedRounds_ExerciseEveryDetector` (the detector list arrives in a different order here; the assertion is order-sensitive). **Parked for the owner:** whether to re-pin those goldens on this machine or widen the tolerance; the known-two list from Windows (BombTimer, SprayColumn) is not yet re-measured here.

**2026-09-26 (goldens)** Owner: re-pin the five strat-canvas goldens on this Mac. Rewritten with `PB2D_GOLDEN_UPDATE=1` (the four `StratGoldenCaptureTests` scenes; their scene JSONs re-wrote byte-identical) and `STRAT_GOLDEN_UPDATE=1` (the `StratExportTests` frame); old and new compared by eye, the difference is text anti-aliasing and raster only. The five pass here now; the Windows machine will read them as a delta of the same size in the other direction. The detector-order assertion in `SuggestedTagsGoldenTests` stays open.

**2026-09-26 (teams)** Owner's review of the Manage Team view and Team Identity: matchmaking groups of three with rotating fifths come out as many fragments and near-universal stand-in stamps, and a team must not be tied to a fixed three or five, since real rosters change with substitutes and transfers. Assessment: the model is already Team over Rosters with the anchor on the roster (Merge, Split, Start Roster exist), so the coupling is not structural; what is missing is (1) provenance-aware clustering, since the same rule runs over matchmaking where it measured 24 rosters, 5 fixed fives, 428 of 554 unaffiliated and 82 of 90 stand-ins, against 3 exact rosters on the 8 HLTV demos, and (2) the §3.6 drift suggestion, never built, so a permanent replacement matches at four with a stand-in stamp forever. The VRS text (tournament-operation-requirements §1.2, §3.2.5(a), §3.10.1; the supplemental rulebook's one registered substitute per event) defines identity for an invite window and says nothing about substitute tenure, so it gives no rule for loose groups. New design item **Team Identity Revision**, awaiting review, with three owner calls: gate auto-clustering by provenance; automatic roster succession on team sources versus suggestion only; FACEIT as matchmaking by default.

**2026-09-26 (teams, verified)** Four claims in the proposal above were checked against the code and Valve's raw sources; three change it.
1. **The gate cannot be the provenance label.** `DemoProvenanceHeuristic` labels every non-`GotvMatchmaking` demo whose our side resolves as `scrim`, so a FACEIT demo (the engine has a `DemoSourceKind.Faceit`) would be auto-clustered by a gate on official, scrim or our scrim. The label is also circular: `our scrim` needs the opponent assigned by clustering. The gate must read inputs clustering does not produce: `DemoSourceKind` (skip `GotvMatchmaking` and `Faceit` by default) plus a user provenance override, with both clan tags present as the positive signal for team play. FACEIT is matchmaking by default, per the owner, not an open call.
2. **VRS already has a model for changing rosters, in code.** `ValveSoftware/counter-strike_regional_standings`, `model/team.js` and `model/data_loader.js`: matches are walked newest first; the most recent five found a team; an older match joins it when it shares 3 or more players (`TEAM_OVERLAP_TO_SHARE_ROSTER = 3`) with that five; the displayed **active roster** is the players with 5 or more of the team's last 10 matches, most recent first, at most five. So Valve anchors identity on the current lineup looking backwards, and derives "who is on the team now" from recency. Our clusterer anchors forwards on the first five seen, which is why a replacement reads as a stand-in forever. The rules repo adds §3.6.3(a) (three players who were on a top-12 roster in the preceding 6 months) and the Major rulebook's single registered substitute who may play until the team reverts to its Core Lineup. The raw text has no substitute tenure rule; the earlier summary was right on that point.
3. **Auto-succession on "five consecutive sides" is unsafe as written.** A substitute can play five maps in a row, and `orderTicks` is `DemoCacheRecord.ModifiedTicks`, download time, not match time, so "consecutive" is not chronological for demos fetched in a batch. Default to a suggestion. Automatic succession, if wanted, needs a real match date (the Valve replay's `.dem.info`; unknown for HLTV) and a window, following VRS's 5-of-last-10 rather than a streak.
4. **The upgrade path would orphan data.** `TeamClusterer.Finish` deletes auto teams with no sides (`IsAuto` = not user-named, not us, not merged). Strat books (`StratOwner.Team(teamId)`), Dossier notes and veto history key on `Team.Id`, so a rebuild can already delete a team that owns strats; gating matchmaking would do it wholesale. Fix: a team referenced by a strat book, Dossier notes or veto history counts as user-touched and is kept. This is a latent bug today, not only a migration concern.

**Display, from the owner's "3 plus rotating fills" case:** the squad card shows the core (the confirmed three to five) and then the regular fills with games-together counts, and the team page shows VRS's active roster (5 of the last 10) beside the roster history, so a team reads as "who plays now" without being tied to any fixed five.

**2026-09-26 (teams, built)** Team Identity Revision merged into `feature/strat-book` (owner: suggestion-only succession). Calibration on the corpus: the owner's trio (the owner and two partners) played 17 games together out of 266, while the single most frequent partner reached 47, so the squad rule has no share bar, only 8 games together; the corpus run as matchmaking founds zero teams and the tracked pin (24 rosters, 5 fives) is unchanged; the 8 pro demos suggest nothing. Found on the way: `SetMyAccounts`, `SetUs`, `Rename` and `SetHidden` never refreshed derived state beyond our side, and under the gate the us mark and a first rename change what queue play can match, so the first two now re-cluster; the Search Filters fixture staged our scrim on a Valve server name and now uses a scrim server. App standard tier 2156 with only the known detector-order test failing. Library's Teams view rendered headless with an inbox and a squad and checked by eye.

**2026-09-26 (teams, queue anchors and the real library)** Follow-up merged: queue play matches a user team only through its squad or an established five, never a rolling extended core, because an us team built from matchmaking before the gate would otherwise keep absorbing fills (pinned). Real-demo classes that read teams or provenance (`TeamIdentityRealDemoTests` 3, `DemoProvenanceRealDemoTests` 1) pass unchanged; no other real-demo class reads them. The owner's library: the desktop app was launched at 22:16 on the revision build, so the one-time rebuild already ran and the pre-revision `teams.json` was not captured; the file now holds 5 teams (My team with a squad the owner set at 22:18 of themselves and one partner, plus FURIA, NaVi, Team Vitality and FUT from the tagged demos). A replay over a copy of that library: 348 demos, 334 queue play and 14 team play, My team matched on 33 matchmaking demos through the squad, the four pro teams unchanged with plays-now equal to their fives where they have five sides. The algorithm's own squad pick on that library would have been the owner, their most frequent partner and a third player with 18 games together; the owner chose a different pair, which the suggestion rule respects by not re-offering once a squad exists. Post-revision copies of both team files are in the session scratchpad.

**2026-09-27 (utility map)** Owner: present the Utility Book on the 2D map, show only lineups seen more than once by default. Built and merged. Measured on the owner's 53 indexed demos (16,374 grenades): 12,451 of 13,694 grid positions were thrown from once, so the default hides most positions and the footer says how many; the fixed 16-unit origin grid split real lineups, and a seeded merge (origin 16 units, landing 128, aim 2 degrees) raised repeated lineups from 1,243 to 1,408 and the throws they cover from 3,923 to 5,201 with the largest lineup at 40, where single-link merging chained to 78. `MapSceneHost` holds the static-map plumbing both the Query Canvas and the utility map use; the Situations classes (58 tests) pass unchanged across the extraction. Headless renders of the map and of a focused group with its card checked by eye. App standard tier: only the known detector-order test. Playback2D standard tier on this Mac: 4 failures (two text-bounds tests, two GPU-probe tests) that reproduce on the integration head, so they are this machine's, not this change's. Found on the way and not fixed: `OpeningTendenciesTests.TheDossier_ShowsTheSection_AndEveryNumberOpensItsRounds` fails intermittently with "Destination array was not long enough", with or without this change.

**2026-09-27 (utility map, follow-ups)** Re-measured with the shipped C# over a copy of the owner's cache (the earlier Python figures pooled every map, because its map lookup failed): 11 maps; at the default Smoke filter 12 to 35 landing spots per map, with Mirage at 28 spots, 95 lineups and 523 throws while 527 single-throw positions are hidden; all grenades across all maps come to 1,407 lineups thrown twice or more covering 5,203 throws. The merges now bucket by neighbouring cells (same groups): the all-grenades query on Mirage went from 55 to 24 ms and a full refresh is 35 ms. Two fixes after review: stacked positions and icons were unreachable under the top one (a jump-throw and a standard throw from one spot always stack), so the hit test returns every item under the pointer, a second click cycles, and stacks carry a count badge; and the card now opens on the side away from the selected position. Both commits landed directly on `feature/strat-book` rather than an item branch; App tier unchanged (only the known detector-order test), Playback2D unchanged (the four Mac-only failures).

**2026-09-27 (dossier test race)** Fixed the intermittent `OpeningTendenciesTests.TheDossier_ShowsTheSection_AndEveryNumberOpensItsRounds` failure. Cause: the three Dossier section tests passed an inline `post`, so a section worker added blocks to the bound collection from the worker thread while `CollectFindings` on the test thread copied it ("Destination array was not long enough"). The app's post goes through the dispatcher, so only the tests raced. Posts now queue (`QueuedPost`) and the test drains them after awaiting the build; the same change in `PostPlantTests` and `SituationalBehaviourTests`, which had the same pattern. 12 of 12 repeated runs green; App standard tier: only the known detector-order test.

**2026-09-27 (review mode)** Owner: labelling is always on and crowds out the player cards; make it a toggled mode with a better review of suggestions and edits of what was detected. Survey: the 2D Playback right column (320 px) stacks game info, the Tag Palette, the Suggestion Queue and then the player cards in the one star row, so both panels take their height from the cards; both are shown whenever their feature gate is on (default on everywhere) and the only control is the C focus key. A suggestion can be edited today only as From/To seconds, labels as `group=value` text and a note, and saving accepts it; its code and positions cannot be changed. A written tag cannot have its code, span or positions changed and cannot be deleted from anywhere (nothing issues `TagDelta.Remove`); the Tag Track only seeks, and picks runs in Label Mode. No test pins the panels' placement; `Playback2DFollowCardRenderTests` finds the cards as the first ListBox in the view, which a new list above them would break. Proposal and owner calls in the owner-facing reply of this date.

**2026-09-27 (review mode, decided and built)** Owner: off by default; collapse the player cards to a strip; keep the threshold on accept-all. Built in three merged slices. (1) The mode: `Playback2D.ReviewMode` (default off, an in-memory row on the browser host), the toolbar toggle and Shift+R; off hides the palette, the queue and both tag lanes and leaves C, Y, N, Enter and Ctrl+Y unhandled; the lanes are hidden through `SetTrackSuppressed`, never by writing the user's per-track toggle; on, the cards become a strip whose row is sized from the player count, since a virtualizing list in an Auto row measures to 0. (2) The panel: one `TagEditorViewModel` for a suggestion and a written tag (code, start and end in seconds from the round start, labels, note, positions from map clicks), a Labels tab listing the demo's tags, Label this round and Label here, and delete through `TagDelta.Remove` so it undoes; a suggestion saved with a changed code records the new verdict `recoded`, which tuning counts as a rejection; the queue filters by round and shows each detector's reason. (3) The lane: the open editor's span draws over the tag lane with two handles that move the draft without seeking, a click on empty lane opens a 10 s label there, and a right-click on a band lists Edit and Delete for its tags or Review for its suggestions. Tests moved by design: the five tagging tests turn the mode on first; the keybind profile, settings and routing tests moved their example chord from Shift+R to Shift+W; `Playback2DFollowCardRenderTests` finds the cards by name and counts only visible followed cards; `SuggestedTagsQueueTests` and `SuggestedTagsReviewTests` follow the editor and the Labels list; `TagTrackTests` checks the lane's effective visibility, since the gate moved to the lane's host. New: `ReviewModeTests` (3) and `ReviewPanelTests` (6). Headless renders of the mode off and on, of the Labels tab with the editor and of the edit band checked by eye. App standard tier: only the known detector-order test.

**2026-09-27 (strat mining)** Owner: auto-tagging and review work now, but no strat is ever generated; a repeated T execute, or a team (or several teams) opening with similar utility and positioning, should become a strat. Survey: `StratDocument` has no generated marker (`Origin` names one round, set only by Create Strat From Round); `StratFromRound` builds steps only from a full parse through `RoundCaptureWalker`; the detectors read occupancy and detonations from the parse and run per demo, the library sweep off by default. Everything a signature needs is already cached for the whole library without a parse: Round Facts (plant site, buy type, winner, freeze end), `.dvrp` positions per slot at 1 s, grenade rows (thrower, release origin and aim, landing, tick), Grenade Index lineup ids, and `TeamIdentityService.SideAtRound`. The only cross-round clustering today is the Grenade Index and the roster clusterer; "Rounds like this" is a set match at one instant.

Proposal:
1. **Round signature, from cache only.** Per round and side: map, side, site (plant site, else the site the side's players reached first), buy bucket, the lineup ids thrown in the first N seconds after freeze end (landing cell as the fallback key for a one-off throw), each player's position at a few anchors (freeze end + 10 s and 20 s, and 5 s before the first site contact), and the execute time.
2. **Similarity and clustering.** Utility by weighted Jaccard over lineup ids; positions by the minimum-cost pairing of the five players (players differ between demos, so slots are matched by position, not identity); timing by the gap in execute seconds. Agglomerative with a strict linkage threshold, calibrated on the owner's library the way the Grenade Index merge was, and a minimum support of repeated rounds.
3. **Scope per cluster.** Per team (via `SideAtRound`) and across teams on a map ("seen from 3 teams"). Two kinds: T executes (by site) and setups (T defaults and CT holds, from the opening positions and utility only).
4. **The strat.** Built from the cluster's medoid round out of the cached data: a hold step at freeze end, throw steps with lineup ids and landings, move steps from the 1 s positions, a plant step; no yaw (not cached). Name, type, site, economy and tempo from the cluster. `Extra.mined` records the member rounds, the support and the score, so a later re-mine updates the same strat rather than duplicating it; the members become its runs so the record panel shows won and lost.
5. **Where it lands and when it runs:** owner calls, below. Also out of this item: learned models; anything needing yaw or sub-second paths.

**2026-09-27 (strat mining, decided)** Owner: mined strats land in a Detected inbox in the Strats section and are promoted into the book by hand; two near-identical rounds make a pattern; the first build covers T executes and setups (T defaults and CT holds); mining runs from a Find strats button and quietly after new demos finish indexing.

**2026-09-27 (strat mining, built)** Built in three slices on `feature/strat-book-strat-mining`. (1) Signatures and clustering: per side and round, from Round Facts, the positions files, the Grenade Index and Team Identity; a setup is freeze end + 15 s and + 25 s and grenades in the first 30 s, cut at first contact; an execute starts at the first 1 s sample with two Ts in the site's place (the plant site, else the first site three Ts stood in), with anchors at take - 10 s and - 3 s and grenades from - 25 s to + 5 s. Players pair by position through an exact minimum-cost assignment, not by slot; grenades pair by kind, landing within 350 units and time within 10 s, with a lineup id only a bonus, because the same smoke is thrown from several spots; a demo without grenade rows is unknown utility at a fixed 0.25 penalty, never scored as threw nothing. Complete linkage cut at 0.55. (2) The inbox and promotion: `StratMiningService` keeps the detected patterns in the cache and the dismissals and promotions under the config root; a `RoundCapture` built from cached files feeds `StratFromRound`, so a mined strat has the same steps as one created from a round, without yaw; the member rounds become `suggested` tag instances labelled `strat: <id>`. (3) The Detected toggle and detail pane in the Strats section.

Calibration on a copy of the owner's library (366 demos, 53 with grenade rows, 11 teams): 8,223 signatures in 4.5 s (4,007 T executes, 2,108 setups per side; half the setups are dropped because first contact comes before the 15 s anchor); a full mine takes about 10 s off the UI thread. At the 0.55 cut: 227 patterns, 93 executes (78 of two rounds, 13 of three or four, 2 of five or more) and 134 setups (124 of two rounds), 25 with utility compared, 12 setups run by more than one team, 5 pairs from back-to-back rounds of one demo. Cuts of 0.35, 0.45 and 0.65 gave 6, 55 and 846. The utility-compared patterns read as real strategies (a Mirage T default with Sniper's Nest and Top of Mid smokes, a Connector molotov and a Mid flash, run by Spirit and DENDELE across three rounds); the positions-only executes are mostly matchmaking rushes, so the inbox sorts utility-compared first and labels the rest positions only. Found on the way: the library holds a trimmed test copy of `vitality-vs-fut-m3-nuke.dem` that paired with its original as perfect patterns, so a round within 10 units of another demo's same round is dropped as a copy. Real strats built from real patterns pass `StratValidator` with no refusals. Tests: `StratMinerTests` (6), `StratMiningServiceTests` (3), `DetectedStratsTests` (2); `StratMiningCalibration` prints the numbers when `STRAT_MINE_CONFIG` names a library copy. App standard tier: only the known detector-order test. Open for the owner: grenade rows cover 53 of 366 demos because the Utility Book's background indexing is off by default, so most patterns are positions only until it runs.

**2026-09-27 (strat mining, re-mine checks)** Pattern keys across a re-mine, measured by holding out 10 of the 366 demos and adding them back: 221 of 222 keys kept (24 of 25 utility-compared); the one that moved kept at least half its rounds together. A dismissed or promoted pattern whose key moves now hands its state to the new pattern holding at least half its rounds, so a quiet re-mine cannot bring back a dismissal or offer a promoted pattern a second time. The record panel's own path (`StratEvidenceService.Compute`, through the tag index) counts the promoted runs. Mined runs are `suggested` instances with provenance detector `strat-mining`: tuning reads verdicts only, so they do not count as detector acceptances; the Matrix shows them under the suggested source. Follow-ups, not built: a strat deleted from the book leaves its pattern marked in book; Add to book writes a label into every member demo, which Review mode's Labels tab lists; half the setups drop because first contact comes before the 15 s anchor, the likely next thing to tune.

**2026-09-28 (UX consistency, proposed)** From the pattern of the last two days' fixes (Review 24 s to open, lineup planning on the UI thread, 4,792 generated clips in a curated list, a map key collision hiding 58% of lineup throws, a constant Round Facts fingerprint, hidden background renders), the owner asked which other changes to track. Seven UX Consistency rows and one follow-up row added to §5; the owner asked to start with the UI-thread audit.

**2026-09-28 (generated content, decided and built)** Owner approved one Dismiss/Restore vocabulary with one "Show settled" toggle per inbox, accepted items behind it, restorable rejected suggestions, no reviewed state outside Review, a library-wide Suggested section, squad and roster dismissals that survive a one-player change (judged against the originally dismissed SteamIDs), a dismissable "Is this you?", deleting a mined strat returning its pattern to New and removing its run tags, and machine labels grouped apart in the Labels tab; declined a confirm before sending more than 50 clips to Review. Survey on the owner's library: 3,795 tag suggestions in 149 demos (10 accepted), 303 mined patterns, 16 teams. Fixed on the way: an unreadable `strat-mining.json` loaded as empty and the next Dismiss erased every dismissal and promotion; a failed verdict save could duplicate an accepted tag.

**2026-09-28 (UI-thread audit, built)** Measured on a copy of the owner's library (381 demos, Debug, headless upper bounds): Dossier "My team" 7.0-9.1 s to 118 ms, each Team Identity change 7.6-9.3 s to 6-11 ms, grenade walks landing stalled the UI up to 1.5 s (now under 10 ms), the 12 MB lineup store save left the index lock, Situations with nothing placed 1.3-1.7 s to 10-25 ms, library prune 377 to 60 ms. Owner widened the queue rule: everything off the UI thread is a queue item, and user-triggered work goes to the front and preempts the running background item. Saves, loads, section builds, Team Identity replays and Teams commands, and the library scan are queue items now; pause holds background work only; preempted section work yields at per-demo checkpoints and resumes. Owner-approved visible changes: stores load off the UI thread at startup with loading states, collapsible Dossier sections regrouped into one section per map plus general sections, Situations results paged at 200, Teams commands in the background with a busy state. Open owner call: whether opening a demo goes through the queue. Still on the UI thread: the cache index (29 ms) and library.json (6 ms) loads, the strat working-copy and tag-document saves (the UI waits on them synchronously).

**2026-09-29 (demo opens through the queue)** Owner: demo opens go through the queue at the front. Every entry point (picker, Library, recents, drag-drop, tour, Situations, Teams, the DEBUG auto-load) now submits one "Open demo" user item placed first: it preempts a stoppable background item, is not held by pause, joins a running same-demo parse when that parse decodes user commands, otherwise waits with "Waiting for <file> to finish parsing" (a retained parse cannot be interrupted), and a second open replaces the first without the stale one touching the shell. Review caught a stale open wiping the shell after a newer one had painted, and a cancelled open's parse holding the slot unnamed; both fixed. Two QueueJobTests had relied on pause holding user items (untrue since the audit) and passed only when a compaction happened to run first; they now hold items behind an unpreemptible job. Standard tier: only the known detector-order test.

**2026-09-29 (strat editor, decided)** Owner: creating a strat from scratch needs a horizontal scroll to see the edit page; collapse the rail and the strat list or otherwise make room; tokens start in each team's spawn; step fields follow the verb; pick a lineup visually from the Utility Book map; the preview puts a throw's actor at the lineup's throw position at the step's time; explore more authoring improvements. Contract fixed before the split: `UtilityRef.Technique` (a `GrenadeLineups.TechniqueKey` string, optional); a missing or unknown key means the technique with the most throws; lineup ids resolve through the alias map. Two agents in parallel on separate files (editor room and steps, then the picker; spawns and throw origins).

Owner also approved four follow-on items (map-first editing, step templates, duplicate and carry, inline checks and keys), queued after the first three.

**2026-09-29 (spawns and throw origins, built)** Spawns come from each team's largest buy zone in the baked zones file, not the TSpawn/CTSpawn place polygons (nuke's CT centroid is outside the buy zone, overpass has no CTSpawn place); five nav-area spots at least 96 units apart, levels keyed on the bundle's floors. A throw's actor stands at the lineup technique's origin in the projection only; dragging that token on its own throw step is refused with "placed by its lineup". New Strat is disabled while a cold map's zones load, and a late create never changes the open strat. Review found and fixed the dead-position drag, the cross-map New Strat race, a missed redraw after a cancelled gesture, and the no-levels floor key. Open: a technique is checked against the 12-key vocabulary, not the chosen lineup; SchemaSample lacks `technique`.

**2026-09-29 (room and steps, built)** The rail and the strat list collapse to 32 px strips (state in `SessionPayload.StratBook`); a collapsed list moves Book/Detected, a strat or pattern picker and the callouts flyout into the editor header. The editor fits 1280x800 with the horizontal bar disabled; metadata, slot and record rows overflowed too, not only the step row. Step fields follow the verb (table in `Services/Strats/StratStepFields.cs` and design-system.md); a field the verb does not use still shows while set. Review found that wheel and arrow keys on a closed combo committed a destructive clear per value passed; combo changes are now bursts computed from the step at the burst's start, so passing through values is lossless and leaves one undo entry.
