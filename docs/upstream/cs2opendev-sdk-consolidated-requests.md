# CS2OpenDev — upstream ledger

**Between:** DemoViewer.NET / Cs2DemoKit (`sid2934/DemoViewer.NET`) and `CS2OpenDev-SDK` /
`CS2OpenDev-SchemaTracker` / `CS2OpenDev-Docs`
**Last updated:** 2026-08-15

Standing record of what has been asked, what has shipped, and what either side still owes. Started
as a request document; most of it has now been delivered, so it is kept as a ledger rather than
rewritten into a fresh ask each round.

**Correspondence** (the upstream side keeps its copies in `CS2OpenDev-SDK/docs/upstream/`):

| Date | Document | Direction |
|---|---|---|
| 2026-08-07 | `2026-08-07-upstream-requests.md` | ours → upstream (17 asks) |
| 2026-08-07 | `2026-08-07-demoviewer-response.md` | upstream → ours (docs-canonicality answered; 18-file subset; corrections) |
| 2026-08-10 | `2026-08-10-demoviewer-shipped.md` | upstream → ours (protos, versioning, GameEvents decoder/registry/envelope, licence — shipped) |

Deep detail lived in companion documents retired to git history in the 2026-08-16 docs cleanup:
the proto build review, the GameEvents gap asks, the Schema Lens handoff, the SDK#6 proposal and
emitter seed, the SDK#25 stage-2/3 reports, and the cutover-readiness inventory. Backticked
`docs/…` paths inside the dated rows below refer to those retired copies; the full text is in git
history, and the GitHub issue links remain the live record.
`docs/upstream/sdk6-adapter-findings.md` is kept — it documents the live seam contract.

---

## Delivered

| Ask | Shipped as | Verified here? |
|---|---|---|
| Protobuf message types | `CS2OpenDev.Protos` 2.0.1 — 18-file collision-free closure, `CS2OpenSchema.Protos` namespace | adopted 2026-08-10 — see the report below |
| Version on the proto clock | own `version.json` scoped to `protos/`; `CS2BuildId` assembly metadata | — |
| Documented subset + collision domains | in the shipped package README | — |
| `Google.Protobuf` floor policy | `>= 3.27.0`, stated as policy | yes — compatible with our 3.29.5 pin; no conflict with `Cs2VideoGenerator.Core`'s 3.29.5+ floor |
| Game-event decoder + registry | `CS2OpenDev.Sdk.GameEvents` 2.0.11 | adopted 2026-08-10/11 (3.0.3, then 4.0.1) — the generated record layer is deleted; decode + registry are the SDK's |
| — | SDK#2 naming fixes + SDK#3 curated event records | adopted 2026-08-11 — `Sdk`/`Sdk.GameEvents` **4.0.1**, `Protos` 3.0.2; our `GameEventSupplementary.cs` deleted; value-parity verified (goldens byte-identical pre/post bump) |
| — | pawn wire keys ([SchemaTracker#6](https://github.com/CS2OpenDev/CS2OpenDev-SchemaTracker/issues/6)) | adopted 2026-08-12 — [SDK PR#8](https://github.com/CS2OpenDev/CS2OpenDev-SDK/pull/8) root-caused our 62-key measurement as a generator read rule (wire keys derive from the declared TYPE tag), released as `Sdk`/`Sdk.GameEvents` **4.1.3** + `Protos` 3.0.6: 11 `player_pawn` properties retype `int`→`uint` pawn handle, 59 `<Name>Pawn` companions added. Our side re-mapped on adoption: `GameEventSemantics` classifies pawn keys as entity handles (tag + name suffix per SDK `docs/MIGRATION-4.1.md`), `IsPlayerSlotField` gained the type-aware overload (a `uint` field is a handle, never a slot) at every `.entity.`-read gate, catalog regenerated (+30 companion fields on the authoring surface) |
| Per-fire transport context | `GameEventEnvelope<T>` (`readonly record struct`, implicit conversion to `T`) | — |
| Permissive licence | both repos MIT | yes (earlier round) |
| Which docs repo is canonical | answered: one docs repo; protos now sourced from **SchemaTracker** | — |
| Keep `CS2OpenDev.Sdk` dependency-free | held; CI fails the build if its nuspec grows a `<dependency>` | yes — 1.0.5 nuspec had an empty dependency group |
| — | GPL-3.0 → MIT; event records made `partial`; build-machine paths removed | yes (earlier round) |

---

## Verification of the 2026-08-10 note

Checked against the local `CS2OpenDev-SDK` checkout at `8481564e`, which is already regenerated to
schema `24537688`.

### SDK 2.0's namespace migration does not affect us

This was the one item with real breakage potential — 297 types moved namespace and 40 were removed.
**It costs us nothing.**

`SchemaNames` is a flat table of 2,762 nested static classes under the root `CS2OpenSchema`
namespace. It is not partitioned by module or project, so moving a *type* between
`CS2OpenSchema.Client` and `.Server` does not move its `SchemaNames` entry. Every
`SchemaNames.X.Y` reference we hold resolves against the regenerated file:

| Consumer | Distinct refs | Broken |
|---|---|---|
| `Cs2DemoKit.Analysis` + `DemoViewer.NET` (production) | 14 | **0** |
| `Cs2DemoKit.Analysis.Tests` | 13 | **0** |

(The nine classes we touch — `CBaseEntity`, `CBasePlayerPawn`, `CBasePlayerWeapon`, `CCSGameRules`,
`CCSGameRulesProxy`, `CCSPlayerController`, `CCSPlayerItemServices`, `CCSPlayerPawn`,
`CPlayerWeaponServices` — all survive, with the members we use intact. A scan flags one
`SchemaNames.X.Y`; that is a placeholder in a doc comment and our own csproj comment, not code.)

We consume the SDK for `SchemaNames` string constants only — no schema *types* — which is why the
migration passes us by. Anyone reading `MIGRATION-2.0.md` and bracing for work can stop.

### `CS2OpenDev.Protos` 1.0.7 → 2.0.x really is renumbering only

Confirmed from commit `b18dbce5`: it touches `version.json`, the csproj, two READMEs and two docs
files. **No generated descriptor content changes.** Their "read it as a renumbering, not a release"
framing holds, and adopting across the major costs nothing.

### The `player_death` warning does not apply to us

They flagged `player_death` going 18 → 22 fields as the silent-truncation risk most worth checking,
and asked to hear our number rather than discover it themselves. Ours:

**Our schema already declares 22 fields** — `assistedflash, assister, attacker, attackerblind,
attackerinair, distance, dmg_armor, dmg_health, dominated, headshot, hitgroup, noreplay, noscope,
penetrated, revenge, thrusmoke, userid, weapon, weapon_fauxitemid, weapon_itemid,
weapon_originalowner_xuid, wipe`.

So the 18 was an artifact of their 1.1 pipeline, not a description of our state. Our generated
records were complete on `player_death` and always had been. Worth telling them — and it was,
in the adoption report: the correction they issued against themselves does not describe a
downstream defect.

**What we *are* missing**, both traceable to our stale pin rather than to anything they changed:

- `gameui_hidden` — confirmed absent from our schema (we have 272 events, they now have 273).
- The `int16 c4` field on `bomb_defused`, `bomb_exploded` and `bomb_planted` — all three carry 2
  fields in ours, none with `c4`.

### Distribution: fine for consuming, blocking for publishing

They offered to prioritise NuGet.org if it blocks adoption, and asked us to say so if it does.
**It does — but only on one side, and it took a second look to see it.**

*Consuming* is a solved problem for us. Our `nuget.config` already carries a `private-github` source
and a committed-`.nupkg` `local-packages/` fallback with a `packageSourceMapping` pattern — exactly
the shape `CS2OpenDev.Sdk` is consumed through today. Building against `CS2OpenDev.Protos` needs no
new infrastructure.

*Publishing* is the problem. `Cs2DemoKit.Parser` is itself a published package — 0.x to a GitHub
Packages feed, with nuget.org reserved for the 1.0.0+ line (`.github/workflows/nuget.yml`).
A `PackageReference` on `CS2OpenDev.Protos` becomes a **dependency in our published nuspec**, and a
package on nuget.org cannot depend on one that isn't there — restore fails for every consumer.
Even on the 0.x GitHub Packages channel it would force our consumers to authenticate against the
CS2OpenDev org feed to restore a transitively-required package.

`PrivateAssets="all"` does not rescue this, and we checked rather than assumed: **seven public
members of `Cs2DemoKit.Parser` take concrete generated proto types as parameters** —
`StringTableProcessor.ProcessCreate/ProcessSnapshot/ProcessUpdate`,
`GameEventDecoder.Decode/LoadSchema`, `EntityTracker.PeekEntityUpdates`, and
`RuntimeSchema.Parse`. A private dependency would ship a package whose public signatures name types
the consumer cannot see. The dependency has to flow.

So the ask back is concrete: **`CS2OpenDev.Protos` on nuget.org is a prerequisite for
`Cs2DemoKit.Parser` adopting it**, and it gates our own 1.0.0 nuget.org flip. That was the answer
to the question they asked, and it went back with the adoption report.

**Decision (2026-08-10): adopt, and flip both families to nuget.org together when ready.**
`Cs2DemoKit.Parser` now depends on `CS2OpenDev.Protos` 2.0.1; the 0.x line continues on GitHub
Packages, and the 1.0.0 nuget.org flip waits on the upstream publish. Recorded as a release gate in
`docs/distribution/nuget-packaging-plan.md` §1 item 3, which is the document read before that flip.

The version guidance is noted: take `CS2OpenDev.Sdk.GameEvents` **2.0.11 or newer** (2.0.4 shipped
two different files under one version; everything below 2.0.11 lacks its build stamp), and prefer
the `CS2BuildId` / `CS2SchemaRevision` assembly metadata over the SemVer build metadata, which
GitHub Packages drops.

---

## What we owe

| # | Item | Status |
|---|---|---|
| **1** | **Closure-derivation PR to `CS2OpenDev-SchemaTracker`** | Resolved 2026-08-11 — maintainer reviewed [PR#5](https://github.com/CS2OpenDev/CS2OpenDev-SchemaTracker/pull/5) (measurements reproduced independently), landed their own implementation as `bdad0c74` with house conventions folded in (DERIVED CLOSURE stamp, clone-not-mutate fix, e2e tests, docs), closed the PR in its favor; #3 closed, shipped in bundle v1.2.0. Algorithm + safety case stand as we proposed |
| **2** | **Protos adoption report** | Sent 2026-08-11 — [CS2OpenDev-SDK#4](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/4), after the adoption branch merged to main |
| **3** | **Move our schema/proto source to SchemaTracker** | Open, and substantially larger than a pin bump — see the trap below |
| **4** | **Tell them the corrections** | Sent 2026-08-11 — folded into [CS2OpenDev-SDK#4](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/4): `player_death` was never 18 on our side; SDK 2.0 is a no-op for `SchemaNames` consumers; nuget.org blocks publishing not consuming (Protos on nuget.org is a 1.0.0 prerequisite); `valveextensions.proto` does exist in the GameTracking tree |

---

## The trap waiting in item 3 (SchemaTracker move)

Scoped 2026-08-10, not yet acted on. Recording it now because it is silent, and because it changes
what item 3 should probably be.

Our `cs2-opendocs/docs/gameevents_schema.json` is a JSON Schema carrying **272 already-resolved**
events. SchemaTracker's `gameevents.json` is a flat list of **289 records under 273 distinct
names** — unresolved, with the `source` field (`core.gameevents` / `game.gameevents` /
`mod.gameevents`) still present and **15 names carrying more than one record, across 31 records**.
Those figures match upstream's exactly.

The failure mode is specific and severe:

```
player_death [core.gameevents]:  2 fields
player_death [mod.gameevents]:  22 fields     <- the one CS2 actually fires
```

A reader that takes the first match, or the last, has a good chance of binding `player_death` to a
**2-field** record. It compiles, it parses, and every kill loses its weapon, headshot, assister,
penetration and distance. The duplicated names are not obscure either — they include `player_hurt`,
`round_end` and `player_team`, which the analysis layer depends on.

Correct resolution is source priority **mod > game > core**, which is exactly what upstream's
`GameEventRegistry` in `CS2OpenDev.Sdk.GameEvents` already implements.

Two things that size the risk honestly:

- **`tools/DemoViewer.NET.Codegen` has no resolution logic at all today** — verified, not assumed:
  no mention of `source`, priority, or first/last selection anywhere in `GameEventsGenerator.cs`. It
  has never needed any, because its input arrives pre-resolved. So this is *new* logic to write, not
  broken logic to fix.
- **The content delta is almost nothing.** 273 distinct names against our 272 differ by
  `gameui_hidden` alone (plus the `c4` field on the three bomb events). Essentially all the risk in
  this move sits in the resolution rule, not in the data.

**So item 3 has a fork.** Rewriting `tools/DemoViewer.NET.Codegen`'s reader means reimplementing
that resolution ourselves and owning the trap. Adopting `CS2OpenDev.Sdk.GameEvents` 2.0.11 (the
delivered decoder/registry asks, shipped and unconsumed at that point) would delete our game-event
codegen path instead — the decoder, the 272-entry factory table and the resolution rule all come
from upstream, and `GameEventEnvelope<T>` covers the per-fire transport context.

### What blocked full adoption of the upstream decoder at the time: field naming

Scoped 2026-08-10 against `CS2OpenDev.Sdk.GameEvents` 2.0.11. The decoder, registry and envelope
are all fine. The blocker is the one thing the gap report listed as *out of scope* and correct on
upstream's side:

> **Field-name renames.** DVN renames `userid` to `VictimSlot`/`KillerSlot`/`PlayerSlot`/
> `AttackerSlot` to expose semantic role. The SDK preserving the raw `Userid` matches the wire and
> is the right default.

Those semantic names are not a cosmetic preference here — they are a **public authoring contract**:

- Shipped rule files reference them directly: `event.PlayerSlot == player.slot`,
  `event.AttackerSlot == event.VictimSlot` (`rules/highlights_objective.rules.yaml`,
  `rules/player_stats.rules.yaml`). User-authored rulesets in the wild do too.
- `rules/catalog.json` — the Rulesets v2 authoring catalog — carries 110 `Slot` occurrences.
- `EventRegistry` / `EventFieldAccessor` resolve `event.<Field>` by **reflecting on record
  properties**, so upstream's `Userid` simply would not satisfy `event.PlayerSlot`.
- **280 C# references across 72 files** outside the generated records (analysis engine, rules
  expression compiler, UI view-models, AnalysisBench, tests).

So adopting upstream records raw would silently break every shipped and user-authored ruleset, plus
280 code sites. That is not a 1–2 day change, and the silent half is the dangerous half.

**The fix already had an upstream ticket** — the source-generator hook parameterised on a
consumer override JSON, matching our `tools/DemoViewer.NET.Codegen/gameevents_overrides.json` shape
(`className` rename, `fields.{name}.rename`, `inject`). Upstream status at the time: *accepted, low
priority, not started.* It was filed as a nice-to-have; this scoping promoted it to **the** thing
gating decoder adoption for us, with reasoning concrete enough to hand over as-is.

**Recommended sequencing:** re-raise the override-hook ask with this evidence, take the
SchemaTracker move via our own reader in the meantime (small, preserves the DSL contract), and
adopt the decoder once upstream can emit the semantic names. One partial mitigation if the hook
stalls: the alias could live in `EventFieldAccessor` alone, which would keep the rules DSL intact
— but it would not save the 280 C# sites, which access the typed properties directly.

Not decided.

---

## Six identifiers the 3.0 splitter missed

New, 2026-08-10, after adopting the 3.0.x family. This is the one place we do have a strong
argument for an upstream rename, and it is not about our vocabulary — it is upstream's own
stated convention not being met.

`bbb96d89` says CS2_GEN_006 now reports zero run-together names. In `CS2OpenSchema.Events` at
3.0.3, these still read as unsplit runs:

| Shipped 3.0.3 | Native | Record(s) | Expected |
|---|---|---|---|
| `Isbot` | `isbot` | `PlayerTeamEvent`, `PlayerTeamCoreEvent` | `IsBot` |
| `Noreplay` | `noreplay` | `PlayerDeathEvent` | `NoReplay` |
| `Damagebits` | `damagebits` | `EntityKilledEvent` | `DamageBits` |
| `Totalrewards` | `totalrewards` | `TournamentRewardEvent` | `TotalRewards` |
| `WeaponFauxitemid` | `weapon_fauxitemid` | `PlayerDeathEvent`, `OtherDeathEvent` | `WeaponFauxItemId` |
| `Hcontent` | `hcontent` | `UgcFileDownload{Start,Finished}Event` | `HContent` |

`WeaponFauxitemid` is the sharpest, because it sits on `PlayerDeathEvent` beside `WeaponItemId`
— which *did* split. So `item` and `id` are both in the vocabulary and only `faux` is missing;
the same record shows the rule working and not working two properties apart.

They look like three distinct causes rather than six missing words:

- **short function words** — `isbot` = is|bot, `noreplay` = no|replay. Two-letter leading words,
  plausibly below a minimum-length guard rather than absent.
- **missing nouns** — `faux`, and whichever of `damage|bits` / `total|rewards` is not present.
- **a single-letter Hungarian prefix** — `hcontent` is h|content, the Steam UGC handle prefix.
  That is the `ID`-fold rule's shape, not a vocabulary entry.

We adopted 3.0.3 as-is and followed the fix; none of these blocked us. Flagged because they had
just done the work and would presumably rather hear it than ship it, and because `names.lock.json`
means these spellings get pinned once anything depends on them.

Method, in case it is useful as a check: scan the emitted property names for
`^[A-Z][a-z]{6,}$`, then read the ~26 hits and discard the genuine single words (`Achievement`,
`Assister`, `Distance`, …). The residue is the list above.

---

## Protos adoption report (sent 2026-08-11 as [CS2OpenDev-SDK#4](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/4), with the corrections above folded in)

Adopted `CS2OpenDev.Protos` 2.0.1 in `Cs2DemoKit.Parser` on 2026-08-10, branch
`feat/adopt-cs2opendev-protos`.

**What we deleted.** The whole local pipeline: a `RoslynCodeTaskFactory` inline task that staged a
namespace-injected copy of every `.proto` into `obj/`, the staging target keyed by namespace so a
rename invalidated stale copies, 16 `<Protobuf>` items, the `Grpc.Tools` 2.57.0 pin, and the
parser's build-time dependency on the `cs2-opendocs` submodule. The csproj went from 149 lines to
53. Cold parser build 9.5s → 1.6s (both measured cold, `obj/` removed first).

**What went smoothly.** The namespace swap (`Cs2DemoKit.Parser.Protos` → `CS2OpenSchema.Protos`)
touched 11 `GlobalUsings.cs` files and nothing else — the global-using pattern meant zero call
sites changed. Your constraint that proto short names and `.proto` filenames stay untouched held:
the App's `ProtoIndex` / `PayloadNodeBuilder` name matching and `HeaderKind` detection needed no
change at all.

**What we checked before trusting it, and would recommend to the next adopter.** The compiler only
proves names resolve. It cannot see the failure mode that matters when the *source* changes from
GameTracking-CS2 to SchemaTracker: a differing field number, wire type or label compiles clean and
silently misparses. We diffed descriptors field-by-field between the two sources over the 13 files
we compile:

- **2,753 fields common to both; ZERO differ in number, type or label.** That is the result that
  made the swap safe, and it is the one we would want stated in the package README.
- 47 fields dropped, 50 added. The drops are GC / close-caption cruft and `descriptor.proto`
  internals; none is read by us. We verified that mechanically rather than by eye.

**Results.** Parser 209/209, Analysis 1087 (0 failed), analysis accuracy suite 5/5 passed with no
demo regressing against its recorded baseline (two identical, three higher — the recorded figures
predate other work, so the gains are not attributable to this change).

**What we expected to find and didn't.** Nothing broke. Given a source change spanning four months
and a different extraction method (binary-derived rather than a mirror of Valve's published
protos), we budgeted for wire-level drift and found none.

**One correction to your 08-07 note.** You wrote that `valveextensions.proto` "does not exist in the
GameTracking tree, so it could not have been in your set." It does — it is one of the 42 files in
our pin, carrying the Valve `FieldOptions` / `EnumValueOptions` extensions and
`EProtoDebugVisiblity`. What is genuinely new is the *import edge*: our `networkbasetypes.proto`
imports only `descriptor.proto` and `network_connection.proto`, and nothing in our tree imports
`valveextensions` at all, whereas SchemaTracker's imports it at line 9. Your conclusion stands —
the 13-file set doesn't compile against the tracker's protos — but for that reason rather than a
missing file.

**Known gap we're carrying.** The parser's types now come from SchemaTracker build 24537688, while
the App's `ProtoIndex` still scans `cs2-opendocs/data/Protobufs` at 2026-04-02 for its source-link
strip. Source links for anything added or renamed since silently miss. Degradation, not breakage,
and it is the justification for item 3 above.

---

## Still open upstream

| Ask | Where it stands |
|---|---|
| derive the GC closure stub | Closed — landed upstream as `bdad0c74` (their implementation of our PR#5 design), #3 closed, v1.2.0 |
| facts-layer widenings ([SchemaTracker#7](https://github.com/CS2OpenDev/CS2OpenDev-SchemaTracker/issues/7)) | Closed — items 1+2 landed as `schema_evolution` 0.6.0 (`pair_candidates`), 3+6 as 0.7.0 (attribute diffs + per-transition dates), 5 as `1b7290c6` (evolution-staleness observability). The closure/facts prerequisite for rename curation was met and the SDK consumed it immediately ([SDK PR#11](https://github.com/CS2OpenDev/CS2OpenDev-SDK/pull/11): candidate-drafting tool over the tracker's 0.8.0 evidence) |
| `option csharp_namespace` overlay in Docs | Moot for the package (they inject at stage time); still wanted for source consumers. They suggest `protos.descriptorset` (268 KB) as the stronger single artifact |
| source-generator override hook | Accepted, low priority, not started |
| Lens metadata vs our V1.1 spec | Audit done 2026-08-11 (the audit doc is in git history) → SDK responded on [SDK#6](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/6) (data layer accepted conditional on a staleness gate; transforms split to the codegen side; wrapper codegen declined pending an entity abstraction) → data layer delivered as [SDK PR#7](https://github.com/CS2OpenDev/CS2OpenDev-SDK/pull/7): migrations + stable naming + `CS2_GEN_010/011/012` staleness gates + `schema-lens/state.json` (`lens-canon-1` hash) + wire-widths, genesis seeded from our V1.1 (58 classes / 144 fields / 3 aliases). Merged 2026-08-11/12, and the SDK is driving: [PR#9](https://github.com/CS2OpenDev/CS2OpenDev-SDK/pull/9) staged `schema-lens/` in the unattended refresh + caught `state.json` up to build 24662694; [PR#11](https://github.com/CS2OpenDev/CS2OpenDev-SDK/pull/11) began rename curation (drafting tool over SchemaTracker 0.8.0 candidate evidence). We consume `state.json` and keep transforms/lanes locally. Companion [Docs#26](https://github.com/CS2OpenDev/CS2OpenDev-Docs/issues/26) closed 2026-08-14 (`eb318a8`, live): `aliasChain` → `confirmedRename` standardized everywhere; presence intervals explicitly documented as a **hull**, not continuous presence (citing the 775-class blip), with per-field gap emission correctly deferred upstream to SchemaTracker; the wrong platform claim fixed |
| host the Lens codegen tool upstream | Accepted in principle 2026-08-13 ([their reply](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/6#issuecomment-5277500413) to [the filed proposal](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/6#issuecomment-5276658964); the full proposal text is in git history). §9.1's `DictionaryEntityReader` (test story with zero demo bytes) was the decisive piece. Sequencing was contract-first: SDK hand-writes the abstractions assembly + reference reader + conformance suite (own `version.json` clock — Q1 settled by the Protos 3.0.7 patch-removal incident) → DVN adapters over `EntityState`/`EntityTracker` against that suite → emitter (our pipeline as seed) → package. All 7 open questions dispositioned; both freeze blockers resolved in [our reply](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/6#issuecomment-5277599590) (`Resolve<T> where T : EntityWrapper` accepted; `CreateWrapper` delegate dropped for a pure-data manifest + generated `EntityWrapperRegistry.Create` switch). Contract shipped 2026-08-13: `CS2OpenDev.Sdk.Entities.Abstractions` **0.1.1** ([SDK PR#19](https://github.com/CS2OpenDev/CS2OpenDev-SDK/pull/19) + release-wiring fix [PR#20](https://github.com/CS2OpenDev/CS2OpenDev-SDK/pull/20)) — BCL-only, trimmable/AOT-clean, own version clock; `DictionaryEntityReader` + `BindingConformance` ship in the package; 34-test conformance suite; final names `TryReadObject` / `TryReadByEnginePath`; goes 1.0 only when our adapter (the second implementation) passes the suite over `EntityState`/`EntityTracker`. Pinned here at 0.1.1 the same day. Adapter delivered 2026-08-13 (merged to main): `LensBoundReader`/`TrackerEntityWorld`/`LensBindingBuilder`/`LensOrdinalMap` in `src/Parser/Cs2DemoKit.Parser/Entities/SdkAbstractions/` — zero changes to `EntityState`/`EntityTracker`, 43 ported conformance tests green, full Parser suite 268/268 re-run post-merge; [findings report posted on SDK#6](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/6#issuecomment-5286434787) (kept here as `docs/upstream/sdk6-adapter-findings.md`; three 0.x items flagged: Vector3/QAngle cross-read semantics, reference reader's checked handle conversion vs sentinel round-trip, Lens-hash home on the future registry). That met the 1.0 criterion on our side. Upstream's [findings disposition](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/6#issuecomment-5288149741) shipped as **Abstractions 0.2.1** (2026-08-14): F3 fixed exactly as we argued (reference reader folds every integral width unchecked — the sentinel crosses; the contract now states the requirement), F2 documented implementation-defined with the emitter owning discrimination, F1 recorded for the registry spec, F4/F5 accepted, alias-direction doc clarification queued into the emitter work. 1.0 was deliberately not cut until we re-verified on the fixed reference — done the same day: 43/43 + Parser 268/268 unchanged (no assertion of ours encoded the old checked behavior), [green light posted](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/6#issuecomment-5288388878). Seed + Q5 inventory delivered late on 2026-08-13 ([posted](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/6#issuecomment-5288459035); the seed document `docs/upstream/sdk6-emitter-seed.md` is in git history): pipeline map (what transplants vs what the contract obsoletes), emit decisions worth carrying, full side-table inventory (seen-aware `m_lifeState` + staged money fields; factory exclusions; transform vocabulary as emit decisions; 58-entry `EngineToNetName` in full; wide-int explicitly empty-pending SchemaTracker#10), six input divergences vs their `state.json` (headline: origin canonicals already diverged on 3 classes — cross-state ordinal comparison is meaningless, join by canonical path), three-stage verification recipe. Errata corrected two of our own prose slips (58-not-60; stale `& 0x3FFF` doc comment repaired in `LensState.cs`). All DVN deliverables complete; #6 closed 2026-08-14 after upstream verified the ordinal-divergence headline against their state and split the emitter into [SDK#25](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/25). Their one outstanding ask — the narrow F3 statement ("did the conformance port re-run clean against the handle fold, not just the tree build") — [answered exactly as posed](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/6#issuecomment-5288717770): 268/268/0-skipped, no absent-on-`int -1` assertion ever existed, `EntityHandle_InvalidSentinelCrossesUndecoded` asserts present-`0xFFFFFFFF` and predates their fix — the answer 1.0.0 was waiting on. Family renumber housekeeping also landed upstream (PR#24 `versionHeightOffset`): Sdk/Protos/GameEvents 5.1.0 + Abstractions 0.3.0 are content renumbers — adopted together with Abstractions 1.0.0 in one audited bump when it landed |
| wrapper emitter (`CS2OpenDev.Sdk.Entities`) | [SDK#25](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/25) (opened 2026-08-14, split from #6) — faithful distillation of our seed (we diffed it; nothing misstated; their fail-don't-curate width rule is stronger than ours and right). Verification division of labor [agreed on-thread](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/25#issuecomment-5288718754): upstream emits + runs stage 1 (BindingConformance in CI); DVN runs stages 2–3 when the first emitted package lands (composition over a real tracker incl. high-bit-serial handle; real-demo A/B joined by canonical path). F1 lands here as registry `LensHash`/`SchemaBuild` constants; each side asserts its own hash (preimages not comparable). Shipped, and #25 closed, 2026-08-14: `CS2OpenDev.Sdk.Entities` **0.1.1** (PR#27 wrappers/bindings/registry; PR#28 fixed the 0.1.0 registry doc that told runtimes to cross-compare Lens hashes — our F1 note catching its second real bug, same mechanism as F3: self-consistent assumptions survive every test the reference can write). Emit census per handoff: 58 wrappers (13 with properties), 56 registry cases (our two exclusions honored), `m_lifeState` `int?`, `Buttons` `ulong` with the wide-int table absent (width derives through `effectiveBuiltin` — the SchemaTracker#10 arc completing exactly as designed), handles raw with resolved companions only for curated targets. Adopted here with the combined renumber bump (Sdk/GameEvents 5.1.0, Protos 5.1.1, Abstractions 0.3.0, Entities 0.1.1 — all tag-diff-audited as content-clean; gates green). Stage-2/3 verification delivered 2026-08-14 ([report on #25](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/25#issuecomment-5291168053)): 11 new tests, all census claims measured-equal, origin alias bridge proven on both fabricated and structural evidence, **2,539 real-demo ordinal comparisons / 0 mismatches**, zero adapter changes (the contract's portability datum). Findings for the next emit: F1 companion flatness (weapon-typed companions structurally null under a registry-faithful runtime), F2 suspected `C_`-spelling companion-matcher miss on `m_hPlayerPawn`, F3 origin-is-a-GOTV-phantom curation note, F5 hash-prohibition confirmed from the consuming side. That closed the last DVN deliverable; outstanding upstream-side niceties only — the Abstractions 1.0.0 cut (a trivial audited pin bump here whenever it lands) and any upstream response to F1/F2. Both landed 2026-08-14 and are adopted + re-verified per [SDK#34](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/34): `Abstractions` **1.0.0** (tag-diff vs our verified 0.2.1 is `version.json` only) + `Entities` **0.2.0** folding the #29 companion fixes (F1: `ActiveWeapon`/`LastWeapon` typed `EntityWrapper?`, registry returns the concrete wrapper — measured 10/10 resolving on real data where 0.1.1 was structurally null; F2: `CSPlayerController.PlayerPawn` exists via the `C_`-prefix fold — measured 10/11 with Health round-trip, the one null a genuinely empty slot) and our F3 (seen-aware `Origin` `Vector3?` on the three relocated-origin classes — measured null on 10/10 live pawns while cell leaves position all 10; the deliberate compile break cost one line here). Stage-3 A/B re-run: **2,539 comparisons / 0 mismatches**, totals identical to 0.1.1; zero production adapter changes again; gates at baseline (Parser 279/0/0, Analysis 997/0/114). One new curation observation: `CIncendiaryGrenade` is absent from the curated 58 (a `LastWeapon` target on real data resolves null correctly). Report appended to the stage-2/3 report (git history) and posted on #34 |
| wrapper inheritance / ordinal-space design ([SDK#30](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/30)) | Proposal only (`docs/DESIGN-wrapper-inheritance.md` in the SDK repo, merged via SDK PR#39, explicitly not accepted-by-merge): prefix ordinal layout — `layout(C) = layout(nearestCuratedAncestor(C)) ++ ordinal-sort(own)` — keeps base ordinal constants valid in every descendant with the contract untouched. Upstream routed two questions our way; [answered 2026-08-14 with measurements](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/30#issuecomment-5298010867). Q2 (decisive, closed with bytes): composed the prefix manifest from the 0.2.0 package's own bindings (base 8 ++ gun 3, `m_iClip1` at ordinal 2 = `Ord.Clip1`) and bound it to 25 live concrete weapon classes on the reference demo — every gun-chain serializer carries 11/11 paths (`CAK47` reads `m_iClip1` through the base ordinal, bit-for-bit with `Fields`), 275 A/B comparisons / 0 mismatches; the shotguns' 8/11 shows the wire flattens exactly the schema's true ancestry, so prefix manifests must follow the real parent chain (their walk does). The §6 failure fear (inherited fields absent on real data) does not occur. Q1: no ≥1-own-field assumption (binder can't see own-vs-inherited; empty case already exercised); nothing keyed globally by path (per-binding tables; the one cross-binding key is the per-world map cache on (EngineClass, shape-ref)); inherited aliases fine per-binding, but their planned alias-collision emit gate is load-bearing from our seat — keep error severity. Corrected §4's expected-failure list to three named tests (census non-empty pin, the 0.2.0 companion type-identity pins, stage-3 count==13) — their sortedness prediction doesn't apply (ours pins our own builder). Battery rerun = acceptance. Probe source attached on-thread, deliberately not committed. Shipped the same day as Entities 0.3.0 (design → emit inside 24h; our curation footnote taken — CIncendiaryGrenade curated, 674 paths not 666; surprise: its schema parent is CMolotovGrenade, promoting the molotov to a base). Re-verified here, the 1.0-deciding round: battery 13/13 — A/B over the shipped 674-path set 4,434 comparisons / 0 mismatches (36/59 classes live), shotgun negative case confirmed built, inherited Clip1 round-trips 10/10 on live weapons, inherited-alias bridge proven fabricated, LastWeapon 10/10, zero adapter changes for the third consecutive round at 4.6× manifest growth. Parser 281/0/0 (baseline +2 for the new prefix-law + inheritance-seam tests), Analysis 997/0/114. Report appended to the stage-2/3 report (git history), posted on #34 |
| cutover curation asks ([SDK#41](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/41), filed 2026-08-14) | From the cutover-readiness inventory (git history; a full production sweep vs the 0.3.0 emitted surface): 62 field paths read in production, 39 curated, all 10 Analysis wrapper properties 1:1. Asks, ranked: (1) the six `CBodyComponent` cell/vec position leaves (hot path — hatch boxing unaffordable there; we keep the world-coord arithmetic per the #34 seam split), (2) `CCSTeam` class, (3) `CInferno` class, (4) match-total action-tracking K/D/A+damage, (5) five minor hatch-viable leaves. Explicit non-asks: enumeration/metadata/snapshots stay parser-side per `IEntityWorld`'s design note. All additive; none gated the 1.0 on #34. Cutover shape: ~190–250 edited lines across ~31 files, deletes 2,628 LOC of local generated wrappers. Side find fixed here: dead `m_pItemServices.m_ArmorValue` armor fallback (path never on the wire). Answered the same night as Entities 1.1.0 (all 5 asks, additive, 61 classes/735 paths) and [adopted + measured back](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/41#issuecomment-5299869311): battery 13/13, A/B 5,085/0 with CSTeam+Inferno swept; 165 alias-bridge reads prove the wire uses the *flat* `m_pActionTrackingServices.m_i*` spellings (their m_matchStats-canonical aliases are load-bearing — answers their "does your scoreboard work today" back-question: yes, via the wire spelling); position leaves seen-aware int?/float? (their CNetworkedQuantizedFloat emitter bug found+fixed because of the ask); tail sweep all 5 benchmark demos ~25,600 comparisons/0 mismatches, 9/16 tail weapons exercised, and a new wire fact reported: USPSilencer/M4A1Silencer/MP5SD/Revolver never materialise while their base-slot classes are live in every demo (CZ75a counterexample noted) — those four bindings look unreachable on GOTV data. The third stage of the cutover landed on the back of it: local lens migration 0001 curated the six cell/vec leaves onto typed lanes and PositionUtil reads them seen-aware with zero boxing (legacy coercion kept as fallback for unlensed trackers), zero consumer changes |
| lens derivation: embed `state.json` in the nupkg + cell wire-aliases ([SDK#44](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/44), filed 2026-08-15) | Open. Context: DVN retired its local Schema Lens migration JSONs 2026-08-15 — the lens registry is now derived from the pinned `Sdk.Entities` package (bindings + `schema-lens/state.json`), parity-proven 162/162 then extended to the full curation (735 rules / 61 classes), golden-verified value-identical, memory flat despite 4.5× lane coverage. Ask 1: ship `state.json` inside the nupkg (the assemblies carry no per-path `schemaType`; today we pass `--state` pointing at a sibling checkout). Ask 2: emit the engine's flat wire spellings for the origin cell/vec leaves as binding + state aliases — measured: 18/162 legacy wire spellings (all `CBodyComponent.m_{cell,vec}{X,Y,Z}` on pawn/projectile/C4) have no binding alias, and the wire never presents the nested canonical, so binding-alias-only resolution cannot reach wire-keyed storage there. Interim wire-flattening rule lives in `SchemaLensSdkDeriver` (becomes a no-op when the aliases ship). Side effect measured during the switch: with the derived lens keying SDK canonicals, the SDK wrappers' `OriginCell*` reads resolve in DVN for the first time |
| effective width through struct-typed fields | [SchemaTracker#10](https://github.com/CS2OpenDev/CS2OpenDev-SchemaTracker/issues/10) (filed 2026-08-13, from SDK#6 Q5): `CPlayer_MovementServices.m_nButtons` → `CInButtonState` whose sole field is `uint64[3]` — width derivation must reach through one struct hop + one fixed-array wrapper. If it lands, the codegen-side curated wide-int table never exists (`m_steamID` already derivable via `widthBytes: 8`); genuine curation residue = seen-aware `m_lifeState` + factory exclusions. Resolved 2026-08-13 ([ST PR#12](https://github.com/CS2OpenDev/CS2OpenDev-SchemaTracker/pull/12), `entity_schema` **0.10.0**): class-level `effectiveBuiltin { builtin, elementWidth, elementCount }`, set iff flattening the class (through by-value struct chains + fixed-array wrappers, parents included) yields exactly one fixed-width builtin leaf — `CInButtonState` resolves `uint64 / 8 / 3`; 381 facts (windows) / 199 (linux) at 24701871, zero cross-platform mismatches. Closed 2026-08-14: committed corpus re-emitted for 24701871 through the 0.10.0 host (`0c7aa54d`, both platforms, content-equality gated; interim — the next full rewalk supersedes it and backfills history). The fact the SDK#25 emitter needs (`m_nButtons → CInButtonState → uint64/8/3`) is now in the artifacts the SDK consumes; the derivability gate can retire the curated wide-int entry, and the table never exists |
| Protos semver release gate | [SDK#13](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/13) (filed 2026-08-13): 3.0.6 → 3.0.7 removed **188 types as a patch** (no gate in the unattended path — upstream's own Q1 evidence). Ask: public-surface diff; additions auto-release, removals withhold for a human major. Root cause identified the same day ([comment](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/13#issuecomment-5277900065)): the derived GC closure (SchemaTracker `bdad0c74`, our PR#5 design) landing in the 24701871 extraction — 17 of 162 `cstrike15_gcmessages` top-level types kept, three import files dropped; content correct, number wrong (republish-as-4.0.0 became the concrete option). Audit run + 3.0.7 adopted 2026-08-13: 188 removals enumerated from the release-tag diff, zero references in DVN (code + string literals), all removals outside the demo/net wire families; gates green (build clean, Parser 225, Analysis 1111, LiveSync 62, App suite, goldens byte-identical modulo timestamps). Closed the same day: upstream shipped [PR#14](https://github.com/CS2OpenDev/CS2OpenDev-SDK/pull/14) (release fails when the proto surface shrinks without a version bump — the exact gate asked for) and [PR#12](https://github.com/CS2OpenDev/CS2OpenDev-SDK/pull/12) (renumbered the closure content as **Protos 4.1**, full inventory in `docs/MIGRATION-4.1-protos.md`); our pin moved 3.0.7 → 4.1.1 (byte-identical `protos/`, empty tag diff; 3.0.7 may be deleted from GitHub Packages). [PR#15](https://github.com/CS2OpenDev/CS2OpenDev-SDK/pull/15) also resolved the handle bit-split discrepancy our proposal cited — the unsourced 15-bit claim is removed from `docs/HANDLES.md`, acknowledging our 14-bit implementation. Sdk/GameEvents restamps 4.1.5/4.1.4 (same-build regens of 24701871) adopted 2026-08-13 for hygiene (build clean, Parser/Analysis at baseline). SDK#5 has since gone GitHub-Packages-only — see its row |
| `*_pawn` companion keys absent from every schema source | Closed — [SchemaTracker#6](https://github.com/CS2OpenDev/CS2OpenDev-SchemaTracker/issues/6) root-caused by the SDK as its own read rule and fixed in [SDK PR#8](https://github.com/CS2OpenDev/CS2OpenDev-SDK/pull/8) (released 4.1; our review supplied the live-demo wire verification). Adopted here 2026-08-12 — see Delivered |
| handle type-family spec ([SchemaTracker#4](https://github.com/CS2OpenDev/CS2OpenDev-SchemaTracker/issues/4) disposition) | Delivered as [SDK PR#10](https://github.com/CS2OpenDev/CS2OpenDev-SDK/pull/10) (`docs/HANDLES.md`, the six-name family + the longest-first prefix trap). Audited our matching against it 2026-08-12: decode paths sound (all six names, correct widths/sentinels, pinned to the demofile-net oracle); one real defect found and fixed — the entity inspector's `StartsWith("CStrongHandle")` swept resource handles into entity resolution |
| walker rev: `atomic_category` + `CollectionOfT` fixed-buffer count ([SchemaTracker#8](https://github.com/CS2OpenDev/CS2OpenDev-SchemaTracker/issues/8)) | Closed — the maintainer's full `--all --commit --single-walk` rewalk landed as the 0.9.0 backfill across the corpus (tracker `12f775d2`, both platforms, incl. new build 24701871), #8 closed, and the SDK exercised the regenerated corpus the same day (its §4 rename-exposure re-measurement on SDK#6: the load-bearing zero survives) |
| document `schema_evolution` 0.6.0 surfaces ([Docs#27](https://github.com/CS2OpenDev/CS2OpenDev-Docs/issues/27)) | Closed 2026-08-14 (`eb318a8`, live) — one docs pass covered 0.6.0/0.7.0/0.8.0: Evidence-surfaces reference section (frozen `pairedEvidence` + the three unselected-candidate lists with exact signal vocabularies, no-union guarantee, `offsetAdjacent`-never-emitted rule), transitions Date column + per-transition Steam manifest timestamps, class-attribute scalar-change prose. The docs side is now fully closed |
| NuGet.org publish (the `NUGET_API_KEY` credential) | [SDK#5](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/5) — decided upstream 2026-08-13: GitHub Packages only, no NuGet.org publishing. Credential dropped (not deferred), dispatch dropped, issue retitled to unlisting the stale GPL-labeled `CS2OpenDev.Sdk` 1.0.1. Direct consequence for us, [answered on the thread](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/5#issuecomment-5286051610): a nuget.org package cannot depend on a GitHub-only one, and seven public `Cs2DemoKit.Parser` members take `CS2OpenSchema.Protos` types — so our 1.0.0 milestone re-scopes to GitHub Packages (may be reopened). Designed fallback if it reopens: wrap/internalize the proto-typed public members so `Protos` becomes `PrivateAssets`-rescuable (their option 3); self-publishing Protos (option 4) declined on principle — one source of truth is the whole point of this effort. Closed 2026-08-14: 1.0.1 unlisted (verified — search index 1→0 hits; explicit `Version="1.0.1"` pins still restore, which is unlisting's intended shape); credential, prefix, and re-dispatch all formally dropped; zero listed NuGet.org versions is the deliberate end state |
| TypeMapper templated-atomic repair (SDK 5.0) | [SDK#18](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/18) (theirs, filed 2026-08-13): since schema 2.0 every classification lookup keys on a bare template name while atomics carry fully-templated names — no templated atomic matches, 1,931 of 5,013 field-level atomics project as mangled stubs, and `CHandle<T>` is referenced by zero generated properties. Upstream asked DVN to weigh in on sequencing before landing; [our position posted](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/18#issuecomment-5286051442): compile-surface non-event for us (measured — zero stub-type refs; we consume `SchemaNames.*` only), green light, sequence *before* the #6 wrapper emitter (it should be born against properly-typed collections/handles), the seam still carries handles as raw `uint` regardless, migration tooling should index by declaring class, and `CS2_GEN_015` should assert zero post-fix rather than report. Closed the same day ([SDK PR#22](https://github.com/CS2OpenDev/CS2OpenDev-SDK/pull/22), family **5.0.1** released): stubs 1,923 → 8, `CHandle<T>` on 403 properties, `docs/MIGRATION-5.0.md` indexed by declaring class as we asked, and the assert-zero ask generalized — the exporter now exits non-zero on any error-severity diagnostic (Abstractions stayed 0.1.1 through the major: the Q1 isolation from #6 working). Adopted here the same day: protos/ byte-identical to 4.1.1, GameEvents source untouched; build clean, Parser 268/268, Analysis 997/0/114, golden A/B value-identical on all 5 bench demos; [adoption confirmed on-thread](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/18). SDK#16 also closed: post-repair measurement shows 115/119 atoms agree with `atomicCategory`; the 4 divergences are the deliberate curation our corpus data already blessed |
| entity-handle bit split (engine constants) | [SchemaTracker#11](https://github.com/CS2OpenDev/CS2OpenDev-SchemaTracker/issues/11) closed 2026-08-13 as a verified negative: the packing constants are not binary-named on any walked surface (string pools clean, `CEntityHandle` opaque ATOMIC, no proto/convar carrier), so `engine_constants.json` cannot carry them under its only-binary-named-constants rule; the curated slot for a citable number is Docs' `well_known_constants.json`. Reconciliation worth keeping: hl2sdk `const.h` defines a separate networked encoding (`NUM_NETWORKED_EHANDLE_BITS` = 14-bit index + 10-bit serial) — our `EntityTracker` `& 0x3FFF` matches the networked handle; the SDK's retracted 15 matched the in-memory entry width implied by `MAX_TOTAL_ENTITIES = 0x8000`. Two encodings, both implementations right about different ones; handles keep crossing the SDK#6 seam raw either way |

---

## How the consume-vs-vendor decision was settled

The protos package shipping made the question the build review deferred live: **consume
`CS2OpenDev.Protos`, or vendor the protos into the parser repo as the proto build review (§2;
retired to git history) recommended?** Resolved 2026-08-10 in favour of consuming; kept here as
the reasoning.

The review's objection was cadence coupling — a library whose whole job is the demo wire format
sourcing its core types from another repo's release schedule. Two things have changed since:

- The proto-scoped version clock landed, so a schema regen that leaves the `.proto` files alone
  does not bump the protos package. The coupling is materially weaker than the objection assumed.
- The source moved from GameTracking-CS2 to **SchemaTracker**, and the two are not the same tree.
  Vendoring from our current source can no longer reproduce their set.

On that second point, one correction to their 08-07 note, in our favour: they wrote that
`valveextensions.proto` "does not exist in the GameTracking tree, so it could not have been in your
set." **It does** — it is one of the 42 files in our pin, and it carries the Valve `FieldOptions` /
`EnumValueOptions` extensions plus `EProtoDebugVisiblity`. What is new is the *import edge*: our
`networkbasetypes.proto` imports only `descriptor.proto` and `network_connection.proto`, and nothing
in our tree imports `valveextensions` at all, whereas theirs imports it at line 9. That is
SchemaTracker dumping from the binary rather than mirroring GameTracking — a real difference, just
not the one they described.

The substantive conclusion survives the correction, and is independently supported: we measured 11
of 42 common files differing between our pin and the GameTracking revision the SDK used *before* it
moved to SchemaTracker, plus one file we don't have at all. Vendoring is still possible, but it now
means vendoring *from SchemaTracker* — which is most of the way to just consuming the package.

That last point decided it: once vendoring means tracking SchemaTracker anyway, the package is the
same dependency with less machinery. The cadence objection is real but weaker than it looked,
since the proto-scoped version clock keeps a schema regen from bumping the protos package. The
residual cost is the nuget.org gate above, accepted deliberately rather than discovered later.
