# Cs2DemoKit — NuGet Packaging Plan (Parser / Entity Tracking / Analysis extraction)

Cs2DemoKit 0.7.0 + 0.8.0 shipped to the sid2934 GitHub feed 2026-08-05; the 1.0.0 milestone (nuget.org flip + waivers + scrubs) is owner-parked. Remaining owner items in §7.

Decided over two review passes: 2026-08-03 (subsystem maps, competing packaging proposals, and an empirical `dotnet pack` on the then-current main) and 2026-08-04 after the v0.6.0 merge (drift audit, pack-matrix re-run, and a second look at the package-count question from three angles). Appendix A holds the friction inventory those passes produced.

---

## 0. Decisions recorded

**2026-08-03 (owner):**
1. **License: MIT** for the repo and all packages, with THIRD-PARTY-NOTICES.md covering the demofile-net-adapted files.
2. **Full rebrand before publish** — package id == assembly name == root namespace under a new brand; the repo and the desktop app remain DemoViewer.NET. Namespace-only edits to protected parser files still require per-file approval at execution time per the protected-file policy.
3. **CS2OpenDev.Sdk: publish to nuget.org** from the CS2OpenDev-SDK repo (same maintainer).
4. **Proto namespace fix approved for P0** (generated Valve proto types leave the global namespace) as a coordinated breaking rename.

**2026-08-04 (second review pass):**
5. **Brand: `Cs2DemoKit`.** Verified unclaimed on nuget.org 2026-08-04 (search totalHits 0; `packageid:Cs2DemoKit` 0; flatcontainer 404; zero in-repo occurrences; no verified reserved prefix starts with Cs2/CS2 — nearest neighbor is the unverified SwiftlyS2.CS2). Casing `Cs2DemoKit` over `CS2DemoKit` (framework capitalization guidelines; matches the Cs2VideoGenerator.Core precedent — in-repo CS2OpenDev.Sdk is the outlier). Recorded caveat: the id contains a Valve game mark, which §7 item 1 flags for an explicit waiver (ample unenforced precedent: SwiftlyS2.CS2, CounterStrikeSharp.API, DemoFile.Game.Cs). Proto namespace becomes `Cs2DemoKit.Parser.Protos`.
6. **Package count: three** (down from the first pass's 8 + Suite), via two real project merges. The "9 feels like too many" instinct held up: looking at the split as a maintainer, as a consumer, and as a skeptic each suggested a different count (3, 5, 4), but every angle converged on the same core moves: the Parser-trio merge, folding Abstractions into Analysis, project merges (not shell packages) as the honest mechanism, and dropping the Suite metapackage. The two contested leaves (Visibility, Yaml) resolved toward merging — overrule triggers in §2. This does not contradict the first pass: that pass rejected multi-assembly *shell* packages as fragile; it never evaluated *project merges*, which the rebrand wave makes nearly free.
7. **Library version line starts 0.7.0** (`pkg-v0.7.0`), because the app shipped v0.6.0 and the audit empirically confirmed the collision (Rules/Visibility already stamp 0.6.0 on main today). Release-notes discipline: always "Cs2DemoKit 0.7.x", never bare "0.7".

**2026-08-05 (owner):**
8. **Distribution channel for the 0.x line: the maintainer's GitHub Packages feed** (`nuget.pkg.github.com/sid2934`), pushed by `nuget.yml` with the built-in `GITHUB_TOKEN` (`packages: write`) — no external secret. **nuget.org is the 1.0.0+ channel**; the flip is a commented block in the publish job plus a `NUGET_API_KEY` secret. Consequences: 0.x consumers need the feed + a `read:packages` PAT in their nuget.config (same pattern this repo already uses for Cs2VideoGenerator.Core); GitHub Packages rejects `.snupkg`, so symbols ship as run artifacts until the nuget.org flip; the CS2OpenDev.Sdk gate for 0.x is closed — the Sdk is published at 1.0.5 on the CS2OpenDev org feed (github.com/CS2OpenDev/CS2OpenDev-SDK/pkgs/nuget/CS2OpenDev.Sdk), so 0.x consumers map `CS2OpenDev*` to `nuget.pkg.github.com/CS2OpenDev/index.json` alongside the `Cs2DemoKit*` → sid2934 mapping (both need a `read:packages` PAT — GitHub Packages requires auth even for public packages). This repo keeps its committed `local-packages/` mapping (offline, credential-free builds); nuget.org publish of the Sdk moves to the 1.0.0 milestone alongside.

## 1. Goal and reference consumer

Make the Parser / Entity Tracking / Analysis capabilities adoptable by downstream projects as public NuGet packages, with the DemoViewer.NET desktop app becoming just one consumer (in-repo, via ProjectReferences).

**Reference consumer:** a headless server-side service where users submit CS2 demos; the service parses each demo once, evaluates a wide set of rules ("did player X achieve feat Y"), awards weekly-leaderboard points, and computes clip windows for automatic clip generation. No UI, many demos concurrently on big machines (64 GB+), mix of shipped and service-authored rules, results keyed by (player, feat, tick).

Two further tiers, now **dependency-defined rather than package-count-defined**: the **parser-only consumer** (must not inherit the rules engine, YamlDotNet, CS2OpenDev.Sdk, or M.E.Logging.Abstractions) and the **rule-tooling consumer** (validation/canonical-hash services that must not inherit Google.Protobuf or the engine).

## 2. Decision: package set (second pass)

| Package | Contents | Dependencies |
|---|---|---|
| `Cs2DemoKit.Parser` | **Project merge** of Parser + Parser.EntityTracking + Entities into one assembly: parse pipeline, `ParsedDemo` (incl. the v0.6.0 `Warnings`/`ParseWarning`/`ParseWarningCodes` diagnostics — consumer-facing stable API from first publish), 272 typed game events, `DemoFrame`/`NetMessage`, `DownstreamUtilities`; `EntityTracker`/`EntityState`/`StoreClassFilter`; 58 typed wrappers + Schema Lens generated registry. Gains `TickMapper`, tick-boundary-frames helper, `PositionUtil`, `EntitySeekService`, `EntityTrackerFactory.CreateCurated()` | Google.Protobuf, Snappier |
| `Cs2DemoKit.Analysis` | **Project merge** of Analysis.Abstractions + Analysis + Visibility + Analysis.Yaml into one assembly: contracts (`HighlightFired`, `RuleChainEvent`, `StateNode`, `EntityStateLayer`), `DemoAnalysis` facade + evaluator + projectors, embedded `catalog.json` **and** the 14 shipped `.rules.yaml` + schema, YAML loader, `VisibilityEngine` + LOS analyzer, clip planning (`ClipWindows`, `HighlightSurfacing`, `ClipPlanner` → `ClipPlan`), `HighlightConfigFingerprint` | Cs2DemoKit.Parser `[exact]`, Cs2DemoKit.Analysis.Rules `[exact]`, CS2OpenDev.Sdk, M.E.Logging.Abstractions, YamlDotNet |
| `Cs2DemoKit.Analysis.Rules` | Rename-only: v2 rules DSL semantic core (lexer/parser/canonical AST/normalizer/resolver/typed checker/`RuleHasher`). Zero dependencies. README caveat: Rules-alone covers syntax/canonicalization/hashing; whole-set composition validation needs `Cs2DemoKit.Analysis.ValidateRulesets` | — |

**No metapackage.** `Cs2DemoKit.Analysis` is the top of the DAG and transitively exact-pins the family — `dotnet add package Cs2DemoKit.Analysis` IS the known-good set. A metapackage can be added non-breakingly later; the bare `Cs2DemoKit` id stays available under the prefix-reservation attempt.

**Why the merges are right.** All three dissolved boundaries ran along unsigned `InternalsVisibleTo` edges (Parser→EntityTracking at `ParsedDemo.cs:18`; EntityTracking→Entities at `EntityTracking/AssemblyInfo.cs:12`; Abstractions→Analysis at `Abstractions/AssemblyInfo.cs:10`) — under exact-pin lockstep those package boundaries provided **zero dependency isolation to any consumer** while adding a runtime `MissingMethodException` skew class; merging eliminates that class outright instead of mitigating it with pins. EntityTracking/Entities add zero external packages beyond Parser's, so the parser-only tier loses nothing. The deciding asymmetry, applied consistently: **splitting later is non-breaking** (new package + `TypeForwardedTo` shims), **merging published ids later is breaking** and leaves permanent id debris — so every speculative boundary starts merged. Ecosystem comparable: demofile-net ships **three** packages (not one, as the first pass assumed — corrected on review), with parsing+entities+events fused in one install; this cut sits exactly there. Install story: parser tooling 3 ids → 1; full service 9 → 1; rule tooling unchanged; listing/README/version.json/smoke-restore surfaces shrink 9 → 3.

**Merged-assembly conventions:** former project boundaries survive as sub-namespaces (`Cs2DemoKit.Parser.EntityTracking`, `Cs2DemoKit.Parser.Entities`, `Cs2DemoKit.Analysis.Visibility`, `Cs2DemoKit.Analysis.Yaml`, `Cs2DemoKit.Analysis.Clips`) so a future `TypeForwardedTo` split stays mechanical. The merged Parser assembly carries the **union** of test IVT grants (Parser.Tests, Entities.Tests, Analysis.Tests, plus the deliberately-unrenamed DemoViewer.NET.App.Tests). Note for reviewers: former EntityTracking/Entities code inherits `AllowUnsafeBlocks=true` from Parser — no behavior change.

**Owner overrule triggers (decide before first publish; both leaves reopen at near-zero cost until then, permanently after):**
- **Visibility as a 4th package** if you intend to market standalone LOS within ~2 quarters. Default merged because the standalone story is hollow in v1: bundles are out-of-band 155 MB packs, collision bakes are missing for 4 Active Duty maps, LOS explicitly unmarketed.
- **Yaml separate** if quarantining YamlDotNet (the family's most collision-prone dep) outweighs the correctness win of rules + catalog living in ONE assembly (which structurally kills §4.5's silent-zero-highlights / cross-assembly-skew hazard). Default merged: YAML is the primary rule format — an engine-without-Yaml consumer is a near-empty set. Mitigation: floor-pin YamlDotNet and document.

**Cosmetic seams accepted** (recording them prevents a later "incomplete rename" false alarm): `Generated/*.g.cs` keep `[GeneratedCode("DemoViewer.NET.Codegen", ...)]`; catalog.json embeds the old tool name; App code mixes `namespace DemoViewer.NET.*` with `using Cs2DemoKit.*`; the repo and .slnx keep the DemoViewer.NET name (the .slnx filename is a repo-root sentinel for `FindRepoRoot` and 15+ test files).

## 3. Versioning and release model

- **Line and tags:** library line starts **0.7.0**, tags `pkg-v0.7.0`+, decoupled from the app's `v*` line (root version.json stays 0.6.x for the app).
- **Mechanics:** delete the five stale per-project `version.json` files; add `src/Parser/version.json` + `src/Analysis/version.json` (0.7.0, `inherit: true` — `src/Analysis/` physically covers the merged-in Visibility and Yaml sources; the Suite's third-file coverage problem is mooted by dropping the Suite). **Critical trap (second-pass find):** the root `publicReleaseRefSpec` matches only `main` and `v*` tags, so a CI build from a `pkg-v0.7.0` tag checkout would get `PublicRelease=false` and stamp **prerelease `0.7.0-g<sha>` family-wide**, making exact `[0.7.0]` pins unsatisfiable and re-firing the NU5104 class the cleanup exists to kill. The two subtree files MUST override `publicReleaseRefSpec` to add `^refs/tags/pkg-v\d+\.\d+`, and the publish workflow asserts `PublicRelease=true` + stable stamp before pack. Restate the deleted files' `pathFilters` deliberately in the new subtree files; decide whether `rules/` joins the `src/Analysis` pathFilters once shipped rules are embedded (a rules-only change should bump the library line).
- **Lockstep is now chosen, not forced.** After the merges no IVT crosses a package boundary; exact pins on the two remaining intra-family edges (Analysis→Parser, Analysis→Rules) are kept as pre-1.0 policy for simplicity, relaxable later without ceremony.
- **Exact-pin semantics, stated honestly (second-pass correction):** a consumer that *direct-references* one family member at a different version wins over a transitive exact pin (nearest-wins) and gets only **NU1608 — a warning**; restore succeeds and skew surfaces at runtime. Only transitive-vs-transitive conflicts hard-fail (NU1107). Therefore the quickstart and every README mandate `<WarningsAsErrors>$(WarningsAsErrors);NU1608;NU1605</WarningsAsErrors>` and the upgrade rule "bump all family refs together in one commit" (CPM `TransitivePinning` users must not pin members individually).
- **TFM:** net10.0 only at launch. Recorded as a decision, not an accident: net8.0-LTS server consumers are excluded and demofile-net multi-targets — add net8.0 on first credible external request.
- **SourceLink/RepositoryUrl:** deferred until the repo (or a mirror) is public; `.snupkg` symbols ship day one.

## 4. Verified constraints that shaped the design

1. **The layering is already package-shaped.** All 8 source projects verified free of App/Avalonia/LiveSync/Cs2VideoGenerator references (re-verified post-v0.6.0: that release touched exactly 3 files in the packageable tier, all Parser diagnostics). Grpc.Tools is `PrivateAssets=All` and does not flow.
2. **Global-namespace proto types.** ~625 generated Valve types in the global namespace, CS0433 collision potential. Fix (approved, §0 decision 4): MSBuild pre-Protobuf target copies submodule protos to an intermediate dir injecting `option csharp_namespace = "Cs2DemoKit.Parser.Protos";` + `GlobalUsings.cs`, no protected-file edits. Proto **short names** are unchanged, so the App's ProtoIndex/PayloadNodeBuilder name-matching and HeaderKind detection survive (verified).
   > **Superseded 2026-08-10 — the injection target is gone.** Upstream shipped `CS2OpenDev.Protos`, so the parser consumes prebuilt types instead of running protoc: staging target, inline task, `<Protobuf>` items, Grpc.Tools pin and the parser's submodule dependency all deleted. **The namespace is now `CS2OpenSchema.Protos`, not `Cs2DemoKit.Parser.Protos`** — every mention of the latter in this plan is historical. Short names are still unchanged, so the ProtoIndex/HeaderKind conclusion above still holds. See `docs/upstream/cs2opendev-sdk-consolidated-requests.md`.
3. **CS2OpenDev.Sdk gate.** Publishing to nuget.org is decided (§0 decision 3). Coupling is ~two dozen `SchemaNames.*` usages (23 at last count — re-count at execution) — inlining remains the documented fallback if the publish stalls. **nuget.config reality:** the `CS2OpenDev*` → local-packages `packageSourceMapping` pins the Sdk to the local feed *forever inside this repo* (most-specific wins), so "verify the nuget.org copy restores" must be tested outside the repo or by editing the mapping.
   > **Hardened 2026-08-10 — this is now the binding gate on the 1.0.0 flip, and the fallback no longer applies to all of it.** A second CS2OpenDev dependency landed: `Cs2DemoKit.Parser` → `CS2OpenDev.Protos`. Unlike the Sdk coupling, this one **cannot be inlined** — it is a *public* dependency (verified in the packed nuspec: `<dependency id="CS2OpenDev.Protos" version="2.0.1" />`), because seven public members take its types (`StringTableProcessor.ProcessCreate/ProcessSnapshot/ProcessUpdate`, `GameEventDecoder.Decode/LoadSchema`, `EntityTracker.PeekEntityUpdates`, `RuntimeSchema.Parse`). Inlining would mean re-adopting the protoc pipeline.
   >
   > **Consequence: `Cs2DemoKit.*` cannot publish to nuget.org until `CS2OpenDev.Protos` is there** — a consumer restoring from nuget.org alone would fail NU1101. Owner decision 2026-08-10: **flip both families together when ready.** Upstream has stated nuget.org is a missing credential rather than a design position, so this is a scheduling dependency, not a blocker. Add it to §7 as a release-gate item. Tracked upstream since 2026-08-11 as [CS2OpenDev-SDK#5](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/5) (prefix + `NUGET_API_KEY` + one re-dispatch; the pipeline side is done).
   >
   > Verified working today: the `scripts/nuget-smoke` consumer restores, builds and runs against the packed nupkgs, because its own nuget.config already maps `CS2OpenDev*` → `local-packages`. That proves the graph is sound for a consumer with both feeds — it does **not** prove a nuget.org-only consumer works, and by construction cannot until the upstream publish happens.
4. **Licensing.** No LICENSE/README/notices at the root or in any packageable project; 8 files adapted from demofile-net (BitBuffer.cs, RuntimeField.cs, EntityTracker.cs, HuffmanNode.cs, FieldDecoderFactory.cs, FieldPathEncoding.cs, FieldPath.cs, FieldEncodingInfo.cs) carry MIT obligations. First publish requires LICENSE (MIT, decided), THIRD-PARTY-NOTICES.md, and an explicit risk acceptance for Valve-proto-generated code. No NuGet warning backstops missing license metadata — process-enforced only.
5. **Shipped rules become library content.** The 14 `.rules.yaml` + `dv-rules.schema.json` embed into the merged `Cs2DemoKit.Analysis` (with `LoadShippedEmbedded()` / `ExtractShippedTo(dir)`), which also holds the embedded catalog — **rules and catalog in one assembly can never skew**, eliminating the first pass's cross-assembly-version hazard by construction. `RuleSetLocator`'s directory-probe fallback (`AppContext.BaseDirectory/rules` + parent walk, `RuleSetLocator.cs:77-100`) stays, documented in the Analysis README; its probe of a "DemoViewer.NET" user-config dir (`RuleSetLocator.cs:27`) needs a rename-or-App-only decision at execution.
6. **Visibility assets.** `CollisionAssetLocator` (BCL-only) + bundle.json DTOs move into `Cs2DemoKit.Analysis` (Visibility sub-namespace); the App's `MapAssetLoader` bitmap half stays behind (it holds Avalonia Bitmaps). **Hard rule:** `collision.tris`/baked geometry never enters any nupkg — out-of-band versioned asset pack with its own attribution NOTICE.
7. **Clip generation is planning-public, execution-private.** v1 packages the planning path (highlights → surfacing → clip windows → `ClipPlan` in `Cs2DemoKit.Analysis.Clips`; `TickMapper` → Parser). Execution (LiveSync + private Cs2VideoGenerator.Core + one-render-per-Windows-box) stays out unless CSVG's status changes.
8. **Tick-clock trap.** Frame clock vs absolute ServerTick; one public frame-clock round deriver becomes the single authority; the tick-clock table is mandatory in every README.
9. **Parse diagnostics partially shipped in v0.6.0.** `ParsedDemo.Warnings` now surfaces string-table decode failures, snapshot truncation, and unreadable player-info (4 stable codes, 256-warning cap, ThreadStatic accumulator drained in the `ParsedDemo` ctor — all unprotected files). Still owner-gated: net-message drop-site counts inside protected `DemoParser.cs`, and the `ParseOptions` overload (cancellation, DOP, progress, per-parse unknown-message callback). Design constraint to respect: the ThreadStatic channel cannot extend to pass-2 parallel worker threads without a redesign; README must document the pool-consumer caveats (cap + `warnings-truncated` marker; residue-on-throw may attribute a dead parse's warnings to its successor on the same thread).

## 5. API changes

### P0 — before first publish
| # | Change |
|---|---|
| 0a | **Merge wave** (new, second pass): merge Parser+EntityTracking+Entities and Abstractions+Analysis+Visibility+Yaml **under old names**, behavior-identical, own PR, full suite green incl. `scripts/test-app-suite.sh` (~12 mechanical csproj reference updates; 3 IVT grants deleted; test projects consolidated; codegen output path updated; `ParseDiagnosticsTests.cs` moves from App.Tests to the Parser test project so Parser-tier API coverage travels with the library) |
| 0b | **Rename wave**: 3 projects → `Cs2DemoKit.*` (ids, assemblies, namespaces, folders) + proto namespace + GlobalUsings + telemetry identifier rebrand, per the Appendix C checklist. Protected files get exactly one approved namespace-only edit each. Attempt `Cs2DemoKit.*` prefix reservation |
| 1 | LICENSE (MIT) + THIRD-PARTY-NOTICES.md + pack-metadata props layer (root `IsPackable=false`; per-project opt-in; exact intra-family pins; PackageTags carry cs2/counter-strike — discovery lives in tags, not the id) |
| 2 | CS2OpenDev.Sdk to nuget.org (fallback: inline the ~2-dozen constants) |
| 3 | NBGV: delete 5 stale `version.json`; add 2 subtree files at **0.7.0** with the `pkg-v*` `publicReleaseRefSpec` override (§3 trap) and restated `pathFilters` |
| 4 | Embed shipped rules + schema into merged Analysis; `LoadShippedEmbedded()` / `ExtractShippedTo()` |
| 5 | Promote `"warnings-truncated"` to a `ParseWarningCodes` constant — `ParseDiagnostics.cs:94` emits it as an inline string, contradicting the class's own never-inline-strings rule; consumers matching the catalogue would miss it. Trivial; must land before the API freezes |

### P1 — landed 2026-08-05 (all but the last bullet, which needs a maintainer call)

Execution notes on top of the bullets below: clip pipeline shipped as `Cs2DemoKit.Analysis.Clips` (`ClipRounds`/`ClipWindows`/`HighlightSurfacing`/`ClipPlanner`→`ClipPlan`) + `TickMapper`/`TickBoundaries` in Parser, with the App's reel path made frame-clock end-to-end behind a single emission boundary (`ReelJobService.Cs2Range`); diagnostics shipped as `BuildResult.RulesetDiagnostics`/`.ExcludedRulesets`, `DemoAnalysis.ValidateRulesets` (2 overloads; callers must pass the whole id namespace — validating a subset yields false unknown-ruleset errors), `YamlConfigLoader.LoadDocuments` + `LoadShippedWithOverlay`; the no-lens tripwire fires at first decode rather than registration (subsumes bind-too-late, keeps dict-only trackers silent); decode sink is per-tracker `EntityTracker.DecodeDiagnosticSink` (`Action<string>`, Console default); `AnalysisOptions.MaxDegreeOfParallelism` precedes `CancellationToken` in three public signatures (CA1068) — permanent after 0.8.0; `MapAssetBundle.Identity` is nullable by design (pre-version-keying manifests deserialize to null — a blank-guarded absent identity beats a fake-looking one); ~~`EntityFactoryRegistry.g.cs`'s auto-generated repo-tool header stays repo-coupled by design~~ (moot — file deleted in the SDK cutover). ParseOptions landed (owner approved 2026-08-05, implemented same day per the ParseOptions proposal — retired, git history). Actual protected-file footprint: 194 lines in the 8 approved sites (+36 over the preview, all XML-doc/wrapping — 20 forced by CS1573 under TreatWarningsAsErrors; zero structural deviation; other 3 protected files untouched). Perf A/B passed (Σ parse at/below baseline; A/A control shows per-demo noise floor > the 3% threshold — Σ parse is the discriminator on this bench). Static OnUnknownMessageType stays; the per-parse callback fires alongside it. New code: ParseWarningCodes.NetMessageDropped (+ ParseWarning.Count). The no-cancellation/DOP/progress, static-event cross-talk, and silent net-message-drop frictions are all closed. Known footnote: Parse(data, null) binds to the profile overload (not ambiguous — proposal §2.2 corrected).

Original bullets:
- **Clip pipeline extraction** into `Cs2DemoKit.Analysis.Clips`: `ClipWindows`, `HighlightSurfacing.Surface`, frame-clock round deriver as single authority, `ClipPlanner` → `ClipPlan`; `TickMapper` + tick-boundary-frames helper → Parser.
- **Ruleset diagnostics**: `BuildResult.Diagnostics` + `DemoAnalysis.ValidateRulesets()` + `YamlConfigLoader.LoadDocuments()` for DB-stored rules.
- **Moves**: `PositionUtil` + `EntitySeekService` → Parser (EntityTracking sub-namespace), verbatim, App forwards.
- **Entity ergonomics**: `EntityTrackerFactory.CreateCurated()`, injectable decode-error sink, `AdvanceToIndex` re-replay doc warnings, `SchemaLensLoader` → `EditorBrowsable(Never)`; reword repo-coupled guidance strings that ship in packaged assemblies (`SchemaLensLoader.cs:98,132`, `CatalogResource.cs:34`).
- **`AnalysisOptions.MaxDegreeOfParallelism`** plumbed through to `ParallelDigestProducer`.
- **Visibility hardening**: bundle `MapVersion`/`BakerVersion` surfaced in reports, `CancellationToken` on `Analyze`.
- **Owner-gated (protected `DemoParser.cs`)**: `ParseOptions` overload — cancellation, DOP, progress, per-parse unknown-message callback, net-message drop-site counts (the remaining unshipped half of the corruption-diagnostics story, §4.9). v1 ships on documented workarounds; target 0.8 if approved.

### P2 — later, demand-gated
- **Split-on-demand is the mechanism of record**: if a standalone Visibility, contracts/DTO, or Entities tier finds real consumers, split via `TypeForwardedTo` + a new package (non-breaking) — never pre-provision tiers.
- Final-values evaluation mode (`CaptureMode { Bare, FinalValues, Snapshots }`) — note `NodeSnapshot.numericValue` / `StateNode.GetNumericValue()` already exist; the gap is that final values require full snapshot capture.
- `VisibilityAnalyzer` interval/event emission — hollow until Active Duty collision coverage exists.
- Caller-supplied/merged `CatalogRoot` for custom provider vocabulary.
- SourceLink post-scrub; scoreboard projector down from the App; a Reels package only if Cs2VideoGenerator.Core's status changes.

## 6. Sequencing

1. **Phase 0 — owner items** (§7): copyright string + two waivers recorded; leaf-package overrule triggers decided (default: merged).
2. **Phase 1 — CS2OpenDev.Sdk to nuget.org**; verify restore *outside* the repo (the in-repo packageSourceMapping masks it).
3. **Phase 2 — merge wave** under old names (P0 item 0a), own PR, bisectable, suite green between waves. Safer for the 16 GB build-concurrency constraint too.
4. **Phase 3 — rename wave** (P0 item 0b, Appendix C checklist): 3 projects instead of 9; proto namespace; telemetry identifiers + their by-name listeners in the same commit; the docs' protected-file path references move in the same commit as the folder renames; post-rename CI grep gate for stray old-brand strings (the string-coupled breakages are all silent).
5. **Phase 4 — P0 collateral**: LICENSE/NOTICES/READMEs (tick-clock table mandatory), pack-metadata props, NBGV subtree files, rules embedding, `warnings-truncated` constant.
6. **Phase 5 — release machinery**: `.github/workflows/nuget.yml` (separate from app `release.yml`), `pkg-v*` tags + `workflow_dispatch`: fetch-depth 0 → proto fetch → green-run gate → **assert PublicRelease=true/stable NBGV stamp** → pack 3 → artifact leak scan → smoke-restore job that builds a sample console consumer against the freshly packed nupkgs **with its own nuget.config** (clear + `Cs2DemoKit*` → local smoke feed; the repo's mapping or nuget.org would otherwise mask breaks).
7. **Phase 6 — dry run + first publish**: tag `pkg-v0.7.0`; publish 3; verify clean `Cs2DemoKit.Analysis` restore in an empty project; prefix reservation attempt; commit `samples/LeaderboardWorker` quickstart.
8. **Phase 7 — P1 wave** (desktop app keeps shipping unchanged); submit the ParseOptions protected-file proposal.
9. **Phase 8 — P2 horizon**, demand-gated.

## 7. Remaining owner items

Resolved to date: license (MIT), rebrand (Cs2DemoKit), CS2OpenDev.Sdk (publish), proto namespace (approved), package count (3), version line (0.7.0), metapackage (dropped; bare id stays available).

Still open:
1. **Copyright/authors string** (`sid2934` vs real name) + two explicit waivers to record in this doc: Valve game mark in the package id; Valve-proto-generated code in a public package.
2. **Leaf overrule triggers** (§2): Visibility as a 4th package? Yaml separate? Default is merged; decide before first publish — free until then, breaking after.
3. **Protected-file approvals at execution time**: (a) one namespace-only edit per protected file in the rename wave; (b) `ParseOptions` + drop-site diagnostics in `DemoParser.cs` for 0.8.
4. **Collision bundles**: out-of-band channel (GitHub release asset pack vs CDN) + attribution NOTICE; Active Duty re-bake (needs the gitignored `cs2-assets/` cache + macOS-pinned baker).
5. **Repo publicity timing**: public repo → SourceLink: before v1, later minor, or a public mirror of the packaged projects.
6. **Cs2VideoGenerator.Core**: any path to public/licensable? Decides whether a Reels execution package is ever designed.

---

## Appendix A — friction inventory (from the first-pass subsystem maps)

What blocked or hurt a hypothetical headless consumer, per area. "Blocker" means no workaround
exists; "major" means an ugly one does; the rest are minor.

**Parser.** Major: ~625 generated proto types in the global namespace; no
CancellationToken/async/progress/DOP on Parse; full-retention memory model, no slim/filtered
parse (~2.5× file size retained); malformed known messages silently dropped — partially
resolved in v0.6.0 by `ParsedDemo.Warnings` (string-table/roster tier), net-message drop sites
still silent; packaging prerequisites (submodule protos, stale versions, no LICENSE); IVT
forcing exact version-lock with EntityTracking — dissolved by the Parser-trio merge. Minor:
process-global statics (unknown-message event, profiling accumulators); buffer-or-local-file
input only, 2 GB cap, mmap truncation hazard; tick nomenclature traps (`DemoFrame.ServerTick`
holds the game tick).

**Entity tracking / Entities.** Major: no incremental seek — `AdvanceToIndex` silently
re-replays from frame 0 (`EntityStateLayer` is the packaged answer); order-sensitive
typed-wrapper bootstrap with a silent-wrong failure mode; decode errors to `Console.WriteLine`;
unsigned IVT lockstep and NBGV skew across the pair — both dissolved by the merge + cleanup.
Minor: `EntityState.Fields` allocates a merged dict per call; process-wide profiling/tracing
statics vs concurrent batches; the Schema Lens loader/migration JSON is repo-coupled maintainer
API; 58 curated wrapper classes with some key classes string-path only; per-tracker descriptor
cache rebuilds.

**Analysis.** Blocker: CS2OpenDev.Sdk only on the local feed. Major: shipped rulesets not in
any library; `Build` silently discards composition diagnostics; embedded catalog = closed
vocabulary. Minor: aggregate stats require snapshot mode; no public DOP knob.

**App-trapped.** Blockers: highlight surfacing + clip-window planning live in App
modules/ViewModels; clip contracts/impl unpackageable (private CSVG). Major: reel capture =
one render per Windows box; batch orchestration primitives are App policy — consumers bring
their own; `RulesHighlightHarvester` App-trapped with packageable deps; `PositionUtil`
App-only; `TickMapper` stranded in LiveSync. Minor: two round-derivation authorities disagree
on clock; content-SHA dedup private in `DemoLibraryService`; `EntitySeekService` App-trapped;
scoreboard projection couples to engine column names; collision locator gates headless LOS.

**Visibility.** Blockers: canonical position resolver in App; collision bakes missing on
mirage/inferno/anubis/train; the Valve-derived geometry redistribution question. Major: no
packageable asset-resolution layer; name-only bundle selection, no version keying;
aggregate-seconds output only. Minor: baker macOS-pinned; `Analyze` uncancellable.

**Packaging infra.** Blockers: no license anywhere; zero pack metadata; the CS2OpenDev.Sdk
gate. Major: NBGV inconsistent; private repo blocks SourceLink; pack-time submodule protos +
the Valve redistribution question; shipped rules not in any library. Minor: net10.0-only;
TreatWarningsAsErrors escalates pack warnings; no NuGet publish machinery.

**Consumer simulation.** Blockers: nothing consumable today; `ClipWindows` locked in the App;
programmatic clip generation unreachable. Major: shipped rules are Desktop content; no library
rule-validation entry point; frozen rule vocabulary; no parallelism control; no parse
cancellation/progress; per-player stats require snapshot mode; surfacing policy internal to
the App; the tick-clock trap in the rounds API. Minor: non-file rule sources lose
directory-loader semantics.

## Appendix B — empirical pack matrices

**First pass (2026-08-03)** and **second-pass re-run (2026-08-04, post-v0.6.0)** — identical 4-OK/4-FAIL shape, same NU5104 mechanism, only version stamps moved:

| Project | first pass | second pass |
|---|---|---|
| Parser | OK, prerelease `0.0.1-g<sha>` | OK, prerelease `0.0.1-g<sha>` |
| Parser.EntityTracking | FAIL NU5104 | FAIL NU5104 (now stamps stable **0.6.0** → prerelease Parser dep) |
| Entities | FAIL NU5104 | FAIL NU5104 |
| Analysis.Abstractions | FAIL NU5104 | FAIL NU5104 |
| Analysis.Rules | OK 0.5.4 | OK **0.6.0** — now collides with the shipped app version |
| Analysis | OK, prerelease `0.0.1-g<sha>` | OK, prerelease `0.0.1-g<sha>` |
| Analysis.Yaml | FAIL NU5104 | FAIL NU5104 |
| Visibility | OK 0.5.4 | OK **0.6.0** — collides |

Mechanism (both runs): Parser and Analysis carry `version.json` files without `inherit: true`, so they miss the root `publicReleaseRefSpec` and stamp prerelease `0.0.1-g<sha>` even on main; projects with `inherit: true` stamp stable 0.0.1; projects with no file inherit root (now 0.6.0). Stable-over-prerelease inversion → NU5104 → escalated by `TreatWarningsAsErrors`. Schedule a post-merge re-run (3 projects; NU5104 expected gone once the subtree files land). Also observed: ProjectReference→dependency mapping defaults to min-floor ranges (exact pins must be authored); nuspecs carry placeholder authors/description; no toolchain warning for missing license.

## Appendix C — merge & rename execution checklist (second pass)

**Merge wave (old names, behavior-identical, own PR):**
- `git mv` EntityTracking + Entities sources into the Parser project (sub-namespace folders); Abstractions + Visibility + Yaml sources into the Analysis project.
- Delete the 3 cross-package IVT grants: `ParsedDemo.cs:18`, `EntityTracking/AssemblyInfo.cs:12`, `Abstractions/AssemblyInfo.cs:10` — none of these files is protected (verified).
- Merged Parser keeps the **union** of test IVT grants; consolidate test projects; move `ParseDiagnosticsTests.cs` from App.Tests into the Parser test project.
- Retarget embedded-resource wiring and `tools/DemoViewer.NET.Codegen` output paths; ~12 csproj reference updates across App/tools/tests; update `.slnx` entries (31 today).
- Full suite green incl. `scripts/test-app-suite.sh`.

**Rename wave (3 projects → Cs2DemoKit.*):**
- Folders + csproj + `.slnx` entries; the `.slnx` FILE keeps its name (repo-root sentinel).
- Namespaces + GlobalUsings; protected files get exactly one approved namespace-line edit each.
- Proto target: MOOT since the CS2OpenDev.Protos adoption (2026-08-10) — the parser runs no protoc; the Valve types live in upstream-owned `CS2OpenSchema.Protos` and are outside the rename.
- IVT strings: rename test grants; DemoViewer.NET.App.Tests grant stays old-brand (accepted mixed-brand grant).
- **Paired-change traps (all silent, runtime-only failures):** catalog `LogicalName` in the Analysis csproj + the hardcoded const at `CatalogResource.cs:17` change together · codegen emitter namespaces (entities `Program.cs:282`; the SchemaLens generators' emitted usings, e.g. `SchemaLensGenerator.cs:127` — the game-event generator is deleted, its records come from CS2OpenDev.Sdk.GameEvents) + regenerate committed `Generated/*.g.cs` in the same wave · telemetry Meter/EventSource/ActivitySource names (`EvaluatorMetrics.cs:12`, `EvaluatorEventSource.cs:9`, `AnalysisDiagnostics.cs:21`, `ProfilingSession.cs:29`) + their by-name listeners (`DiagnosticsTabViewModel.cs:43`, AnalysisBench `Program.cs:1607,1771`) in the same commit, else the Diagnostics tab goes silently dark · App source-link literals `SrcPath("DemoViewer.NET.Parser", ...)` (`ParserTabViewModel.cs:587,622,840,881`) — dead links, zero compile errors.
- `RuleSetLocator.cs:27` "DemoViewer.NET" user-config dir: rename/keep/App-only decision, documented.
- CI: `ci.yml:49` hardcodes the Parser.Tests path; `test-app-suite.sh` unaffected (App.Tests unrenamed).
- **The docs' protected-file paths + project-structure table move in the same commit as the folder renames.** 39 doc files reference old project names — sweep or add a rename note.
- Post-rename CI grep gate for stray `DemoViewer.NET.(Parser|Entities|Analysis|Visibility)` strings outside deliberately-unrenamed code.
- Env vars `DEMOVIEWER_PROFILE`/`DEMOVIEWER_TRACE_DECODE`/`DEMOVIEWER_COLLISION_DIR`: rebrand at first publish (free) or document as legacy identifiers — decide in the wave.
