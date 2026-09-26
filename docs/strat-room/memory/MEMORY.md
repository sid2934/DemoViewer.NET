# DemoViewer.NET Project Memory

- [AssetBaker on Windows](assetbaker-run-windows.md) — run with `-r win-x64`; bakes INTO committed assets/; a bundle.json does NOT imply a collision.tris
- [Playback2D v2](playback2d-v2-design.md) — DELIVERED as PR #10 (2026-08-25): Skia clean-core compositor, annotations, export, levels, timeline, dv2d CLI; open criteria in design.md §0
- [Attribution suppression](attribution-suppression.md) — no co-author line AND no Claude-Session trailer; needs all four settings keys, not just includeCoAuthoredBy
- [claude-tooling suite](claude-tooling-suite.md) — repo-scrub/change-scrub in ~/.claude; filter-repo + gitleaks installed; ALWAYS git fetch before scoping a scrub
- [Prose calibration](prose-calibration.md) — measured comment rates, house comment shape, and the em-dash decision; replaces the deleted repo-local scrub-prose skill
- [Sight columns and "no data"](sight-columns-no-data.md) — the 17 columns that need collision geometry; the board says so now instead of rendering 0
- [CS2 icon extraction](cs2-icon-extraction.md) — SHIPPED: 149 icons baked to assets/icons via AssetBaker --icons, consumed through src/GameIcons; PNG beats vector, and Core's guard + killfeed goldens shape how icons reach consumers
- [CS2DemoKit tick clocks](cs2demokit-tick-clocks.md) — three tick values, two clocks; GameTick is ALREADY the frame clock, do not "correct" it
- [Bench run variance](bench-run-variance.md) — AnalysisBench drifts 27-31% between sessions; interleave builds and quote plain mode
- [Tour sample demo is invalid](tour-sample-demo-invalid.md) — assets/tour/sample-de_nuke.dem is INCOMPLETE; never derive results from it, never use it to un-skip a test
- [Agent token cost model](agent-token-cost-model.md) — measured: cost is turn count × context, not bytes; batch per-file edits into one call (~20× cheaper)
- [Stat evidence anchors](stat-evidence-anchors.md) — deferred: contact-anchored stats need an engine change; spray/counter-strafe do not
- [CS2DemoKit local checkout](cs2demokit-local-checkout.md) - engine source at C:\dev\CS2DemoKit is STALE (v0.11.0) against the 0.12.0 pin; reflect over the built DLLs, and no InternalsVisibleTo for DemoViewer
- [CheckedRuleset is the authoring model](cs2demokit-checked-ruleset-model.md) - public, carries ValueType/ConcreteEvents/DeclaredReads/Position; connects 17/17 compute stats where AuthoringGraph connects 0
- [AnalysisBench forward path](analysisbench-forward-path.md) - the bench defaults to STREAMING since 0.12.0; perf-sweep needs --retained, and there is no benchmarks.md in this repo
- [NEVER link the demo corpus](never-link-the-demo-corpus.md) - a worktree removal followed a junction and destroyed 2.8 GB; use DEMO_PATH, and a PreToolUse hook now guards it
- [App-suite cross-test races](unknown-card-test-race.md) - at least 3 tests fail only under interference; WHICH ones depends on the runner's partitioning, and CI sees none
- [SkiaSharp 3 sampling](skiasharp3-sampling-translation.md) - SKFilterQuality.High is NOT one resampler; the naive Mitchell translation blurs 1:1 blits and every video export
- [Skia 3 Linux raster cost](skia3-linux-raster-cost.md) - ~1.8x slower CPU raster on Linux only; export is CPU by construction, so it is user-facing
- [WSL Linux CI repro](wsl-linux-ci-repro.md) - reproduce Linux-only CI failures locally; needs ArtifactsPath isolation and MSYS_NO_PATHCONV
- [Strat Room plan](strat-room-plan.md) — plan of record docs/strat-room/plan.md (untracked); Phases 0-5 built and full-tier tested on feature/strat-book (head c37c9d0, 2026-09-26); pro-demo round facts parked on CS2DemoKit #75; paused for live review
- [Strat Book workstream rules](strat-book-workstream-rules.md) — feature/strat-book branch layout, one build item at a time, verify-merge-push, no planning md commits, no .dem deletion, no PRs until live verification, 15-min updates
- [User-command delta_data](usercmd-delta-data.md) — MM GOTV demos DO carry all players inputs; since ~2026-07 they are 99.8% delta_data, which CS2DemoKit 0.12.0 cannot decode; never cite the tour sample for input presence

## CS2 Developer Reference (Primary Documentation)

**Always consult these docs when working on CS2-related code (demo parsing, entities, protobufs, schemas, etc.)**

**Moved 2026-09** — the old `sid2934/CS2-OpenDevDocs` raw paths all 404 now.

AGENTS.md source: `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/AGENTS.md`

Key doc URLs (machine-readable; the schema dump has no `MNetwork*` metadata, so it cannot
tell you whether a field reaches a demo — see cs2-docs.md for the three ways to decide):
- Full schema: `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/downstream-codegen-schemas/cs2_schema.json`
- Field history (build a field appeared/vanished): `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/downstream-codegen-schemas/field_history.json`
- Game events: `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/data/gameevents.json`
- Wire IDs: `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/data/network.json`
- ConVars: `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/data/convars.json`
- Protos: `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/downstream-codegen-schemas/proto/{demo,netmessages,cs_gameevents,cs_usercmd,usercmd}.proto`
- Per-class pages: `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/schemas/<module>/<TypeName>.md`

See [cs2-docs.md](./cs2-docs.md) for full entity/schema reference.

## Project Structure

- `DemoViewer.NET/` — Avalonia UI shared library (ViewModels, Views, App)
- `DemoViewer.NET.Desktop/` — Desktop entry point
- `DemoViewer.NET.Browser/` — Browser/WASM entry point
- `DemoViewer.NET.Parser/` — CS2 demo parser class library
- `DemoViewer.NET.Entities/` — Typed entity wrappers (SchemaVersionAttribute, EntityBase, Generated/)
- `DemoViewer.NET.Codegen/` — Console tool: reads schema/server.json → generates Entities/Generated/*.g.cs
- `Directory.Packages.props` — Central NuGet package version management

## Parser Library

Key files in `DemoViewer.NET.Parser/`:
- `DemoParser.cs`, `DemoFrame.cs`, `BitBuffer.cs`
- `Entities/RuntimeSchema.cs` — parses CSVCMsg_FlattenedSerializer
- `Entities/EntityState.cs` — Dictionary<string, object?> backing store
- `Entities/EntitySet.cs` — 16384-slot entity array
- `Entities/EntityTracker.cs` — stateful replay: DEM_SendTables → schema, DEM_ClassInfo → registry, svc_PacketEntities → delta decode
- `Entities/FieldDecoder.cs` — decoder factory per field type
- `Entities/FieldPathEncoding.cs` — Huffman-coded 39-op field path encoding (adapted from demofile-net MIT)
- `Entities/FieldPath.cs` — InlineArray[8] value-type field path cursor
- `Entities/HuffmanNode.cs` — generic Huffman tree using PriorityQueue

BitBuffer: now a regular `struct` (was `ref struct`) with `byte[]` backing; added ReadVarInt32, ReadUVarInt64, ReadUBitVarFieldPath, ReadFloat, ReadCoord, ReadCoordPrecise, Read3BitNormal, ReadAngle, ReadStringUtf8, Clone, ReadBitsAsBytes.

Proto generation:
- Submodule `protobufs/` = `SteamDatabase/Protobufs` (canonical source)
- C# types generated via Grpc.Tools from `protobufs/csgo/*.proto`
- **Use Grpc.Tools 2.57.0** — newer versions (2.67+) treat absl log lines as MSBuild errors
- `DemoFrame.Tick` is `int` (not `DemoTick` — that was a DemoFile type)
- `CCSUsrMsg_WarmupHasEnded` (338) and `CCSUsrMsg_GlowPropTurnOff` (360) do not exist in SteamDatabase protos; commented out in PayloadNodeBuilder.cs

Gotchas:
- Parser csproj needs `<ImplicitUsings>enable</ImplicitUsings>`
- Avalonia storage provider type is `IStorageProvider` (namespace `Avalonia.Platform.Storage`)
- Snappier 1.2.0 is a direct dep (was transitive via DemoFile)
- CSVCMsg_PacketEntities has no `HasPvsVisBits` field in our proto gen — removed from EntityTracker
- `PriorityQueue.Dequeue(out item, out priority)` does NOT exist; use `TryDequeue(out item, out priority)` instead
- InlineArray Span can't escape struct lifetime; use array copy for `FieldPath.AsSpan()`
- CDemoSendTables.data is size-prefixed CSVCMsg_FlattenedSerializer: read uvarint then proto bytes

## Entity Architecture (implemented)

- `EntityState` — Dictionary<string, object?> per entity; fields stored as dot-separated paths (e.g. "m_hWeaponServices.m_hActiveWeapon", "m_iAmmo[0]")
- `EntitySet` — 16384-slot array, slot = EntityState?
- `EntityTracker` — opt-in layer; call `tracker.AdvanceToIndex(frameIndex, frames)` or access `tracker.CurrentEntities`; `PeekEntityUpdates(pe)` for read-only decode
- `SchemaVersionAttribute` — `[SchemaVersion(since: "1.38.0.0", until?: "...")]` for generated typed classes
- `EntityBase` — abstract base with `_state` + indexer + AllFields

## Tick View (implemented)

- Toggle button (Frames | Ticks) in left panel header switches `IsTickView`
- `TickGroups` — `ObservableCollection<TickGroup>`, built from `_allFrames` grouped by tick
- `TickViewFrames` — frames in selected tick, `SelectedTickFrame` drives card/entity-seek
- `SubTickEvents` — `ObservableCollection<SubTickEventViewModel>`, extracted from `CSVCMsg_UserCommands` → `CSGOUserCmdPB.Base.SubtickMoves`
- `_prevTickSnapshot` — `Dictionary<int, Dictionary<string, object?>>?`, snapshot at end of prev tick
- `BuildCardsForFrame(DemoFrame)` — shared card-building logic used by both tick-frame and normal selections
- `SeekEntitiesWithDeltaAsync(TickGroup)` — calls `EntityTracker.AdvanceToIndexWithSnapshot(snapshotAt, endIdx, frames)` to seek + snapshot in one pass
- Entity fields show amber (#FFFFC107) when `IsDelta=true` via `Classes.deltaField` Avalonia style
- `EntityGroup.DeltaCount` — count of entities in group with any changed field; shown in header as `Δ{N}`
- New parser models: `DemoViewer.NET.Parser/Models/TickGroup.cs`, `SubTickEvent.cs`, `SubTickExtractor.cs`
- New VM: `DemoViewer.NET/ViewModels/SubTickEventViewModel.cs`
- `PayloadNode.PreviousValue` / `IsDelta` / updated `Display` for "OLD → NEW" display
- `EntitySet.AllIndexed()` and `Snapshot()` — for delta computation
- `EntityTracker.SnapshotCurrentFields()` and `AdvanceToIndexWithSnapshot(snapshotAt, frameIndex, frames)`

## UI Architecture (MessageCards)

- Replaced 3-column Frames|InnerMessages|Payload with 2-column Frames|MessageCards
- `MessageCardViewModel` — per-message card: `PropertyNodes`, `SelectedPayloadNode` (manually implemented to allow `ClearNodeSelection()` without re-triggering callback), `AccentBrush`, `IsExpanded`, `IsSelected`
- `MainViewModel.MessageCards` — rebuilt on every frame selection; one card per inner message (packet frames) or one for full frame payload
- `MainViewModel.PayloadNodes` — mirror of selected card's PropertyNodes; synced via `SyncPayloadNodesToCard(card)` before setting `SelectedPayloadNode`
- `_cardModeActive` flag — suppresses `OnSelectedMessageChanged` when `HandleCardSelected` sets `SelectedMessage` programmatically
- `HandleCardSelected` clears ALL other cards' node selections to prevent stale tree highlights

## HexView Highlights

- `HighlightRange(Start, Length, Brush, Depth=0)` — Depth is tiebreaker in InnermostBrush when two ranges have equal length (deeper wins)
- Always pass `node.Depth` as 4th arg when adding ancestor chain highlights
- Color palette: depth 0=red(4°), 1=blue(207°), 2=green(122°), 3=orange(36°), 4=purple, 5=teal, 6=amber, 7=indigo, 8=lime, 9=pink — alpha 0xC0
- Same palette in `MainViewModel.DepthBrushes` AND `DepthHighlightConverter.Brushes` (must stay in sync)
- [Workflow model routing](workflow-model-routing.md) — Sonnet for first-pass verifiers and light items, session model for design-heavy builds, fix pass and re-verify
