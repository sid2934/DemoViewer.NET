# Asset Pipeline — Top-Level Design (synthesis)

> Design synthesis, now shipped. Reconciles the three parallel design docs this folder once held
> and records the cross-cutting decisions + locked seams. **Implemented via the §0.5 amendment:** a
> build-time baker (`tools/DemoViewer.NET.AssetBaker`) plus a VRF-free app consumer
> (`Modules/Playback2D/MapAssetBundle.cs`, `FloorSplitter`) shipped on main. The §1–§6
> in-process-VRF framework spine is superseded by §0.5 and was **not** built; the SkiaSharp-3 (S-A2)
> blocking gate is retired — the app never loads VRF.
>
> **Read order:** this doc is the synthesis-of-record. The original inputs it reconciles — the shared
> seed (vocabulary + the two correctness flags) and the three parallel component designs (**A**
> tooling/VRF/SkiaSharp, **B** the framework spine, **C** lifecycle/versioning/cache) — were removed
> once the §0.5 amendment superseded the in-process-VRF framework. Any "seed §N" or
> "A/B/C" pointer in the body below refers to those retired inputs; this doc restates every
> load-bearing decision itself.
>
> The factual base is `docs/research/cs2-map-assets.md` (authoritative on CS2/VRF facts; carries every
> primary-source URL). This synthesis cites the component docs by section rather than re-deriving facts.
> `[UNCERTAIN]` / `[INFERENCE]` tags are carried through, not laundered.

---

## 0. Executive summary

DemoViewer.NET will read **CS2 (Source 2) map assets from the user's own install** through
**ValveResourceFormat (VRF) + ValvePak as in-process `net10.0` NuGet libraries**, behind a **wrapper
that insulates the whole app from VRF types**. A small framework (`AssetRef` → `GetDerivedAsync<T>` →
versioned on-disk cache) serves consumers (the 2D Playback module first; future visibility analysis
second) **ready-to-use derived artifacts** — a decoded radar bitmap + transform, a collision triangle
soup / BVH for line-of-sight, a flattened nav graph. The framework is a **peer library that never
depends on demo-parsing** and **branches on no map name or id** (the three binding principles hold).
Correctness is enforced by a **three-factor cache key** (`AssetRef × SourceVersion × ProcessorVersion`,
rebuild-when-in-doubt) and we **ship nothing of Valve origin** (SSA §2.G → bake-on-first-run /
bake-on-change only).

**The one decision that gates everything:** VRF 19.x forces **SkiaSharp 3.119.4**; our Avalonia 11.3.12
stack is compiled against **SkiaSharp 2.88.x**. Under Central Package Management, pulling VRF in-process
moves the **entire app's** SkiaSharp to 3.x — so this is not a radar-feature risk, it is a *does-the-whole-UI-still-render*
risk. **Spike S-A2 is a blocking go/no-go gate that must pass before the SkiaSharp pin is added to
`Directory.Packages.props`** (§D1). The recommendation remains **VRF in-process, conditioned on S-A2**,
with **out-of-process bake** as the de-risked fallback the design already accommodates as a packaging
swap (not a redesign).

---

## 0.5 Amendment — ship pre-baked assets; app stays VRF-free (owner decision, 2026-07-05)

> **This amendment reverses two locked decisions above (§D5 "ship nothing of Valve origin" and §D1's
> "VRF in-process") after an owner call on 2026-07-05. The original text is kept for history; where it
> conflicts, THIS section governs.** The three binding principles + the framework seams (`AssetRef`,
> `IAssetProcessor`, three-factor version key) are unchanged — only the *default asset source* and the
> *shipping posture* change.

**What changed.**
1. **Ship pre-baked assets (reverses §D5).** The app **ships pre-computed, Valve-*derived* assets inside
   the binary** — radar PNGs, floor Z-band tables, world→radar transforms, map bounds, and (later)
   collision BVH / nav footprints — selected at load by a **version key**. This is the **awpy-style
   posture**: redistribution of Valve-derived *data* (not raw `.vpk`/`vtex_c`). Owner accepted the SSA
   §2.G risk; awpy is multi-year precedent with no takedown. **Hygiene (binding):** ship only *derived*
   data never raw Valve files; include attribution/notice; keep the runtime-bake fallback so we can pivot
   to bake-only if challenged.
2. **App stays VRF-free (reverses §D1 / retires S-A2).** VRF/SkiaSharp-3 live **only** in a **separate,
   independently-versioned baker** project — never referenced by the app. The app loads baked bundles
   (PNG + JSON) and **stays on Avalonia's SkiaSharp 2.88.x**, so the whole-app-Skia-3 gate (**S-A2**)
   **never has to be answered.** **S-A1 is already PROVEN** (2026-07-05: VRF decoded a real radar
   `vtex_c`→PNG on macOS; the stack is cached — VRF 19.2.6339 / ValvePak 4.0.0.142 / SkiaSharp 3.119.2 +
   macOS natives).

**The resulting architecture (three parties, file-boundary interop):**
```
 BAKER (separate project; VRF + ValvePak + SkiaSharp 3.x; versioned independently)
   input: a CS2 install (or the cs2-assets cache)
   output per (map × version): radar PNG(s) + floor-Z table + world→radar transform + bounds
                               (+ collision BVH / nav footprint later)  ── a "baked bundle" (PNG + JSON)
        │  build-time: bakes the shipped bundles                        ┌─ manifest keyed on
        ▼                                                               │  (game build/patch, map name, map CRC32)
 SHIPPED BUNDLE (in-app content/embedded) + MANIFEST  ◄─────────────────┘
        │  runtime select: MapName + BuildNumber/PatchVersion + installed-map CRC → matching bundle
        ▼
 APP / 2D Playback (VRF-FREE, SkiaSharp 2.88.x): loads PNG + JSON, no decode, no VRF
        │  version MISS (map/game newer than shipped)
        ▼
 FALLBACK: invoke the baker OUT-OF-PROCESS against the user's own install → cache the bundle;
           if no install → degrade to nav-footprint / grid (never throws, §D6 holds)
```

**How it maps to the unchanged seams:** the **baker = an `IAssetProcessor` run out-of-process** (A §4.3's
packaging swap, now the default not the fallback); the **shipped manifest = the three-factor cache,
precomputed and version-keyed** (§D3/§D7 — `SourceVersion` = map CRC32, `ProcessorVersion` folds the baker
+ VRF version); the **original "runtime-extract from the user's install" posture (§0) is demoted from
default to the version-miss fallback.** Version keys are already parsed: `ParsedDemo.{BuildNumber,
PatchVersion, DemoVersionGuid}` + `MapName`, plus per-file vpk CRC32. Selection **branches on version
data, not on map name** (principle 3 holds — name is identity-for-fetch only).

**Net for the SkiaSharp discussion that dominated this doc:** it is **moot for the app.** The app never
references VRF, so it never moves off SkiaSharp 2.88.x. VRF's 3.x lives in the baker, across a file
boundary. (The module-level consumption plan — the former `2d-playback-assets-plan.md` — was retired
once its work shipped.)

---

## 1. The reconciled architecture in one picture

```
 consumer: 2D Playback module / future visibility analysis
    │  IAssetService.GetDerivedAsync<TDerived>(AssetRef)          ← version-FREE logical identity (B §4)
    ▼
 AssetService  (spine — B owns the shape)
    │  1. probe source version  ─────────────►  IAssetSource.ProbeVersionAsync(ref)  [Flag 1 NEED]
    │  2. cache lookup  (AssetRef × SourceVersion × ProcessorVersion) ─►  on-disk cache  [C owns]
    │  3. on miss: acquire bytes ────────────►  IAssetSource.GetBytesAsync(ref)      [Flag 1 NEED]
    │  4.          process bytes ────────────►  IAssetProcessor<TDerived>            [A's VRF wrapper]
    │  5.          store + serve (off UI thread, lifetime owned by cache)            [C owns]
    ▼
 TDerived  (neutral DTO: RadarBitmap+RadarTransform | CollisionMesh/BVH | NavGraph | …)
                                              │  raw bytes for AssetRef K + referenced paths in K's scope
 ┌── externally-owned acquisition + file-locating layer (MOVES IN LATER) ──────────────────────────┐
 │  reconcile-against-the-real-acquisition-layer-at-move-in   (Flag 1 — code we cannot inspect)     │
 └───────────────────────────────────────────────────────────────────────────────────────────────┘
```

Three parties, three pairwise seams (seed §6), one open discriminator (`AssetKind`). The parser world
and the asset world **meet only at the world-coordinate system** — the host hands the module
`WorldPosition` from the parser; the module asks the asset service for the map those coordinates are
drawn onto / raycast against. Neither imports the other (§D2).

---

## 2. Cross-cutting decisions (locked)

Each decision records the resolution and, where the component designs disagreed, the reconciliation.

### D1 — SkiaSharp 3.x is a whole-app decision; S-A2 is a blocking pre-pin gate

**The finding (A §2.2, flag F-A1):** VRF core pins **SkiaSharp 3.119.4**. `Avalonia.Skia 11.3.12`
declares **SkiaSharp ≥ 2.88.9** and was *compiled* against the 2.88.x assembly. SkiaSharp 2→3 is a
breaking major. SkiaSharp 3.x lands in **Avalonia 12.0.x**, not 11.3.x.

**The correction that matters (and that the in-process recommendation must NOT obscure):** under
**Central Package Management** (`Directory.Packages.props`, one `<PackageVersion>` per package for the
whole solution), the moment any project references VRF, **SkiaSharp resolves to 3.119.4 for the entire
app — Avalonia.Skia included.** There is no "VRF on 3.x while Avalonia stays on 2.x" in-process state;
CPM forecloses it. Therefore:

- **In-process, the `byte[]`-PNG-vs-live-`SKBitmap` crossing knob (A §4.1) is irrelevant to the
  SkiaSharp exposure.** An `SKBitmap` is a 3.x object on both sides regardless. That knob's *only* value
  is enabling the **out-of-process fallback** (where a separate OS process owns 3.x and only bytes
  cross). State it that way — it does **not** "version-decouple in-process." `[corrects an earlier
  framing]`
- **Going in-process IS the decision to move the whole app to SkiaSharp 3.x.** The risk is therefore
  "does Avalonia's entire render path still work," not "does the radar feature work."
- **Spike S-A2 is a blocking go/no-go gate, run BEFORE the SkiaSharp pin is added to
  `Directory.Packages.props`** (adding the pin is what moves Avalonia onto 3.x). It is *not* a routine
  spike buried in a list. S-A2 = add VRF to the real app on a throwaway branch, run the existing
  headless Avalonia Skia frame-capture UI tests (`src/App/DemoViewer.NET.App.Tests`; memory
  `feedback_ui_testing_headless_skia`). Pass → proceed. Fail → fallback ladder.
- **Even a passing S-A2 is a fragile standing state, not a resolution.** "Compiled against 2.88.x,
  running against 3.119.4" can be broken by any future SkiaSharp/Avalonia patch. The **durable,
  version-aligned** outcomes are: **(a) move to Avalonia 12.0.x** (whole app on 3.x, Avalonia *compiled*
  for 3.x — cleanest), or **(b) out-of-process bake** (app stays on 2.88.x, untouched). Document
  in-process-on-mismatched-majors as **provisional even if it works today**.

**Recommendation (unchanged in direction, sharpened in conditions):** **VRF in-process, preferred,
conditioned on S-A2 as a hard gate.** If there is any pressure to ship before S-A2 passes or before
Avalonia 12 is adoptable, **out-of-process bake is the de-risked default.** This is cheap to keep open
because C's derived artifacts are already **on-disk byte blobs** (`.dvradar` / `.dvbvh` / `.dvnav`,
C §2) — in-process-vs-out-of-process is a **packaging swap behind the insulation boundary (A §4.3), not
a redesign.**

**Fallback ladder (A §8), re-weighted:**
1. **Avalonia 12.0.x** — version-aligned on 3.x; trades an Avalonia upgrade. *Durable.*
2. **Out-of-process / separate-OS-process bake** — app stays on 2.88.x; only baked bytes cross.
   *Durable; the robust isolation form.*
3. ~~Isolated `AssemblyLoadContext`~~ — **down-weighted to "avoid."** Two ALCs each loading their own
   `libSkiaSharp` native is a known native-coexistence failure mode. A §8 lumped ALC with separate-process;
   they are **not** equal — the separate **OS process** is the robust form, ALC is the trap. `[INFERENCE]`
4. **Pin an older VRF still on SkiaSharp 2.88.x** — keeps Avalonia untouched but trades CS2 format
   currency; `[UNCERTAIN]` whether a recent-enough such VRF exists (A §8).

### D2 — Project structure + dependency direction

- **Two new projects, mirroring the existing `Analysis` / `Analysis.Abstractions` split:**
  - `src/Assets/DemoViewer.NET.Assets.Abstractions` — pure contract: `AssetRef`, `AssetKind`,
    `SourceVersion`, `IAssetService`, `IAssetProcessor<T>`, `IAssetSource` (the NEED), the neutral
    derived-DTO types. References only BCL (+ SkiaSharp **only if** a DTO names `SKBitmap` — see D10).
  - `src/Assets/DemoViewer.NET.Assets` — the spine + the VRF reader/wrapper + the cache. References
    Abstractions, `ValveResourceFormat`, `ValvePak`, `SkiaSharp`.
  - *(Naming reconciliation: B proposed `src/Assets/…`, A wrote `src/Asset/DemoViewer.NET.Assets`. Adopt
    B's `src/Assets/` + the two-project split — B owns architecture and the Abstractions precedent
    supports it.)*
- **HARD CONSTRAINT (principle 3 + seed §3):** neither asset project may reference
  `DemoViewer.NET.Parser`, `…Parser.EntityTracking`, or `…Entities`. The asset framework is a **peer the
  module/analysis depend on**; the dependency never runs the other way. Rationale (A §4.6): keeps the
  asset layer parser-blind (forecloses name-keyed branches on parsed demo state), 3D-native (no ticks/
  frames/entities), and reusable by tools with no demo loaded.
- **The App is the only project that references both worlds** and wires them at load time.

### D3 — The canonical abstraction set

Locked vocabulary all three docs bind to:

| Type | Shape | Notes |
|---|---|---|
| `AssetRef` | `record struct (string Game, AssetKind Kind, string LogicalName)` | **Version-free**, stable, serializable. The consumer-facing key. `LogicalName` (`"de_dust2"`) is identity for fetch+cache, **never a behavior branch** (principle 3). |
| `AssetKind` | `record struct (string Value)`, open URN `game.family.kind` (e.g. `"cs2.map.collision-mesh"`) | **Open string, not a closed enum** — a new kind/game registers a processor with zero edits to existing consumers. First-party constants in a static `AssetKinds` for convenience. |
| `SourceVersion` | `record struct (string Token)` — opaque to the framework | Discovered post-probe; see D4 for the opaque-but-content-derived contract. |
| `IAssetSource` | `GetBytesAsync(ref)` + `ProbeVersionAsync(ref) → SourceVersion` | The **NEED** (Flag 1). Two opaque ops, framed as a consumer need, tagged reconcile-at-move-in. **Widened by D8.** |
| `IAssetProcessor<TDerived>` | `Kind`, `ProcessorVersion`, `ProcessAsync(bytes) → TDerived` | A's VRF wrapper **implements** these, one per kind. The only code that `#using`s VRF. |
| `IAssetService` | `GetDerivedAsync<TDerived>(AssetRef)`, `IsAvailableAsync(AssetRef)` | The consumer surface. Returns ready-to-use neutral DTOs, never raw bytes, never VRF types. |
| **Cache key** | `AssetRef × SourceVersion × ProcessorVersion` (three factors) | B provides the first two; C adds `ProcessorVersion` and owns the composite (D7). |

This **refines the seed §4** in one load-bearing way: the seed bundled `SourceVersion` *into* `AssetKey`,
but a consumer cannot know the source version at call time (it's a property of the user's disk, found by
probing the moved-in source). B split it into **version-free `AssetRef`** (consumer-supplied) +
**opaque `SourceVersion`** (framework-discovered). The cache keys on the union. A and C are unaffected
(A keys off `Kind`; C's cache key is the three-factor product).

### D4 — `SourceVersion`: opaque to the framework, content-derived by contract; layered probe

The tension: **B** made `SourceVersion` an **opaque token** the framework compares but never interprets;
**C (flag B-1)** requires it be **content-identity** (per-file CRC32) or per-asset invalidation degrades
to coarse, install-level rebuilds.

**Reconciliation — both hold:** the token stays **opaque to the framework's comparison logic**, *and*
the moved-in acquisition layer must **guarantee it is content-derived** — "same token ⇒ same bytes;
changes iff that asset's bytes change." That guarantee is a **quality contract on the provider**, tagged
reconcile-at-move-in. C's ranking is the **recommendation to that provider**: per-file CRC32 (from the
VPK directory, no decompress) **>** whole-file MD5 **>** Steam buildid **>** (size, mtime). buildid/mtime
are **not acceptable as sole authority** (too coarse / not content-derived).

**Preserve C's *layered* probe (C §1.1, §4.2) — do not flatten it to "just hash the bytes":**
- the **cheap probe** (CRC32 from the directory, no payload read) drives the **acquire/rebuild
  decision** — it is what lets us decide *without* paying acquisition+decompress;
- a **post-acquire content hash** is a **backstop folded into the cache *entry*, compared only when bytes
  are already in hand** (explicit refresh, or when no cheap probe exists). It is **never on the
  cache-hit path** (that would force acquire-always and defeat the cache).

### D5 — Bake-vs-ship: Flag 2 collapses it; provenance is a single hook

- **Under SSA §2.G we ship nothing derived from Valve assets.** Radar bitmap, collision BVH, flattened
  nav are **all** Valve-derived → **none shippable.** The matrix **collapses to bake-on-first-run vs
  bake-on-change**, extracted **at runtime from the user's own install** (VRF's own posture). There is
  **no legitimate "ship prebaked" cell** for any CS2 map artifact.
- **Per-artifact trigger (C §3.2):** radar = **eager-bake at demo-load** (cheap, always needed);
  collision BVH = **lazy-on-first-use** (expensive — the 0.7–9.6 s build `[UNCERTAIN]`) + optional
  background pre-bake if visibility stats are enabled; nav = **lazy-on-first-use**.
- **Provenance hook (C flag B-3):** add a single forward-looking **origin flag** (Valve-derived vs
  independently-authored), defaulting to **Valve-derived**, as **kind-registered metadata** so the
  bake-vs-ship policy is data-driven (principle 3), not a hardcoded list. **Keep it a hook, not a policy
  engine** — there are **zero** independently-authored artifacts (e.g. SimpleRadar-style original art) in
  scope today; the flag exists only so that if one is ever added, "ship prebaked" re-opens for *that*
  artifact alone without a name-keyed branch.

### D6 — Derived-asset lifetime & ownership (a seam neither component doc stated; locked here)

`GetDerivedAsync<T>` returns a ready object; C's in-memory cache holds bounded/bursty residency — but
**neither doc said who owns the returned object's lifetime.** This bites hardest on `SKBitmap`
(`IDisposable`, native memory) and the 2–8 MB BVH: if the cache hands out a shared `SKBitmap` and one
consumer disposes it, every other holder gets a use-after-free.

**Locked:** **the cache owns lifetime. Consumers receive shared, read-only references and never dispose
them. Eviction is the only disposal path** (and eviction must not dispose an artifact still referenced by
an active consumer — refcount or weak-handle; `[INFERENCE]`, C to detail). Two adjacent invariants:
- **Bakes run off the UI thread.** A 9.6 s BVH build on the UI thread freezes Avalonia. (Implied by the
  async API; stated so it can't be missed.)
- **A decode/acquisition failure degrades to the fallback** (the grid background / "feature
  unavailable"), **never an exception that kills the host.** `IsAvailableAsync` (B §7.1) is the
  non-throwing probe consumers use; the 2D module's `bg ?? GridBackground` (B §7.2) is the pattern.
  C's *rebuild-when-in-doubt* covers **staleness**, not **decode failure** — these are distinct and both
  must be handled.

### D7 — `ProcessorVersion` folds in the VRF package version

`ProcessorVersion` is **per-`AssetKind`/per-processor** (a BVH-builder bug-fix bumps only the BVH
processor's version → invalidates only BVH caches, leaving radar/nav untouched). **It must additionally
incorporate the resolved VRF package version**: a VRF bump can silently change decode output (A §1.5), so
bumping VRF must bump every VRF-backed processor's version, forcing a clean re-bake of all VRF-derived
artifacts. This is the correctness hook that makes a VRF upgrade safe.

### D8 — Composite-source derived assets: root-key lookup + stored closure (later-phase)

Some derived assets come from **a root file + a transitive closure of referenced files discovered
mid-decode** by VRF's `IFileLoader` (A §4.5). Iteration-1 radar is only a mild case (texture + overview
txt, handled as **two DTOs the consumer combines**, A §4.1); the hard case is **`WorldMesh`** (a later
feature: `vwrld_c` → N `vmat_c` → `vtex_c`).

**Locked direction (don't over-invest — it bites only the later WorldMesh feature):**
- **`AssetRef` denotes the root**; the framework treats the reference closure as an implementation detail
  of producing that derived (B's "root-key + framework-owned closure").
- **Invalidation:** the closure's file identities **must** fold into the derived's `SourceVersion` —
  else a changed `vtex_c` under an unchanged root `vwrld_c` serves a stale mesh (A flag, extends D7).
- **Chicken-and-egg for *lookup*:** the closure is discovered mid-decode, so the full
  key can't be computed before deciding cache hit/miss. **Resolution:** key the cache **lookup** on the
  **root** `SourceVersion`; **store the closure's version-set in the cache entry**; on a hit, **re-probe
  the stored closure** (cheap probes) and rebuild if any changed. Iteration-1 radar does not hit this.
- **Flag 1's NEED widens (A §4.7 #3):** the acquisition layer must support **"bytes for an arbitrary
  referenced path within K's source scope,"** not only consumer-known `AssetRef`s — because `IFileLoader`
  asks for paths it discovered at decode time. Recorded against the reconcile-at-move-in boundary.

### D9 — Host-contract additions

Two additions to `src/App/DemoViewer.NET.Modules.Abstractions` (both are **contract bumps** →
`IWorkspaceModule.ContractVersion`):
- **`IModuleHost.Assets : IAssetService`** — the asset service is exposed on the **cold, app-lifetime
  creation surface** `IModuleHost`, **not** the per-frame `IPlaybackSnapshot` hot path (it is async, I/O-
  backed, returns shared cached instances). `Modules.Abstractions` gains a **pure interface→interface
  reference** to `Assets.Abstractions` (no parser/analysis weight → preserves its SDK-target property).
- **`IModuleContext.MapName : string?`** — `IModuleContext` today exposes no map identity (only
  `DemoPath`, the `.dem` path). The host derives `MapName` once at load (from the demo header / the
  networked `CCSGameRules` entity — data-driven, principle 3) so every module gets `AssetRef.LogicalName`
  without re-deriving it. This is the natural source of map identity on the read surface.
- **Analysis (non-UI) consumers** reach the **same** `IAssetService` via App-layer constructor injection
  (B §7.4) — `IModuleHost` merely *exposes* the single shared service to UI modules.

### D10 — DTO crossing form (conditioned by D1)

Neutral DTOs cross the boundary; **no VRF type ever does** (A §4.2). For the radar bitmap specifically:
the API **must not mandate a live `SKBitmap`** — it must allow **`byte[]` PNG** as the crossing form
(A flag F-AB-DTO). Per D1, this knob is **irrelevant to the in-process SkiaSharp exposure** but is
**mandatory for the out-of-process fallback** (only bytes cross a process boundary). In-process with a
passing S-A2, crossing a live `SKBitmap` is fine (Avalonia speaks SkiaSharp); design the DTO so both
forms are expressible and the choice is a packaging detail, not an API change.

---

## 3. Seam reconciliation table

Every cross-seam issue raised during design, and its resolution here.

| Seam | Flag | Resolution |
|---|---|---|
| **A↔app** | **F-A1** SkiaSharp 2→3 (CPM → whole app) | **D1** — S-A2 blocking pre-pin gate; in-process provisional even if it passes; out-of-process = de-risked fallback; ALC down-weighted. |
| A↔B | F-AB-DTO `SKBitmap` vs `byte[]` PNG | **D10** — API allows both; `byte[]` enables out-of-process; not an in-process decoupler. |
| A↔B / A↔C | F-AB/AC-composite (many files, mid-decode) | **D8** — root-key lookup, stored closure re-probed on hit; Flag-1 NEED widens to arbitrary referenced paths. |
| A↔B | `IFileLoader` backed by `IAssetSource` | A implements VRF's `IFileLoader` internally over the VPK reader (A §4.5); references resolve through the NEED, never `File.OpenRead`. **Confirmed compatible.** |
| B↔C | B-1 `SourceVersion` must be content-identity | **D4** — opaque to framework, content-derived by provider contract; CRC32 recommended; layered probe preserved. |
| B↔C | B-2 `AssetRef` stable/serializable for hashing | **D3** — `record struct` of primitives, canonical string form `"{Game}/{Kind}/{LogicalName}"`; deterministic, no `GetHashCode` reliance. |
| B↔C | B-3 provenance for bake-vs-ship | **D5** — single origin flag at kind registration, defaults Valve-derived; hook not engine. |
| A↔C | F-AC2 ProcessorVersion folds VRF version | **D7** — VRF bump → processor-version bump → re-bake. |
| A↔C | A-1 need in-process flat world-space triangles from `PhysAggregateData` | VRF surfaces `vphys` triangles **directly** (A §7, `cs2-phys-extractor` precedent) — collision/LOS path is clean. Hull faces triangulated in the reader (A G4, deterministic→hashable). |
| A↔C | A-2 embedded Rubikon BVH availability | **Build our own BVH** from triangles (C §2.2 — correctness-controlled, uniform over Hull+Mesh); embedded BVH noted as optional future input only. |
| A↔C | A-3 radar = bitmap (VRF) + overview-txt (us) | Two DTOs (`RadarBitmap` + `RadarTransform`), consumer combines (A §4.1, D8). |
| A↔C | A-4 nav `Areas`/corners(+Z)/connections | VRF `NavMeshFile` surfaces these structurally (A §7); `NavGraph` DTO preserves Z. |
| A↔C | **F-AC1** world-mesh export is glTF-only | **Later-phase.** Ortho-render builds on glTF or a glTF→triangles re-read hop; **collision/LOS unaffected** (triangles direct). Spike S-A3. |
| B↔acquisition | **Flag 1** the WHAT/WHEN boundary | `IAssetSource` = two opaque ops framed as NEED; widened by D8; tagged reconcile-at-move-in. The most important seam — a boundary to code we cannot inspect. |

---

## 4. WHAT / WHEN — phasing (the acquisition timing, never the HOW)

| Phase | Assets needed (WHAT) | When (WHEN) | Source location |
|---|---|---|---|
| **Iteration 1 — map background** | `cs2.map.radar-overview` (txt) + `cs2.map.radar-texture` (`vtex_c`) | On **demo-load**, eager (always needed) | `pak01_dir.vpk` (already mirrored for the txt) |
| **Later — occlusion / visibility** | `cs2.map.collision-mesh` (`vphys_c`) | **Lazy**, when the visibility engine first engages | per-map vpk |
| **Later — clean footprint / accel** | `cs2.map.nav-mesh` (`.nav`) | Lazy | per-map vpk |
| **Later — ortho render** | `cs2.map.world-mesh` (`vwrld_c`/…) | Lazy; composite-source (D8); glTF intermediary (F-AC1) | per-map vpk |
| **Later — callouts** | `cs2.map.entity-lump` (`vents_c`) | Lazy | per-map vpk |

The framework expresses WHEN purely by *which `AssetRef`s a consumer requests and at what lifecycle
moment*. It never schedules, pre-fetches from depots, or locates files — that is the moved-in layer's
job (Flag 1). **No launch-time scanning** (C §1.2): we only know which map matters at demo-load.

---

## 5. Verify-before-implementation gate list

Ordered by blocking-ness. **S-A2 is the gate; the rest are spikes.**

1. **S-A2 (blocking, pre-pin) — Avalonia 11.3.12 against SkiaSharp 3.119.4.** On a throwaway branch,
   add VRF to the real app and run the headless Avalonia Skia frame-capture UI tests
   (`src/App/DemoViewer.NET.App.Tests`). **This must pass before the SkiaSharp pin is committed to
   `Directory.Packages.props`** — committing the pin is what moves the whole app to 3.x. Fail → fallback
   ladder D1 (Avalonia 12, or out-of-process bake). (A §2.2)
2. **S-A1 — macOS `libSkiaSharp.dylib` headless resolution.** On darwin/arm64, reference VRF + the
   `SkiaSharp.NativeAssets.macOS 3.119.4` pin from a `net10.0` console stub; run
   `Texture.GenerateBitmap()` → `ToPngImage()` on one `vtex_c`; confirm a non-empty PNG, no
   `DllNotFoundException`. (A §2.1; seed §8.4 #1)
3. **S-A3 — world-mesh triangles without a glTF round-trip.** Only matters for the later ortho-render
   feature (F-AC1). (A §7 G1)
4. **Per-map vpk internal layout + `.nav` path** `[UNCERTAIN]` — affects whether `AssetRef` needs a
   sub-asset selector (B §6.3). Lives behind the acquisition boundary; confirm against a real install at
   move-in. (research §0, §8.4 #2)
5. **World→radar Y-sign / `zoom`/`rotate`** `[UNCERTAIN]` — lives in the `RadarTransform` consumer; the
   `.dvradar` artifact stores **raw** overview-txt values so a convention fix is a consumer change, not a
   re-bake (C §2.1). (research §4.3, §8.4 #3)
6. **`m_vecViewOffset` (eye-height) exposure** — a *consumer* (visibility analysis) concern, not the
   asset framework's; noted for the visibility phase. (research §8.4 #4)

---

## 6. What this design deliberately does NOT decide

- **HOW assets are acquired or located** (Steam depots, install discovery, vpk cracking) — Flag 1; the
  moved-in repo owns it. We specified the WHAT and WHEN and the NEED's shape only.
- **The raycaster / hitbox-anchoring / visibility statistics** — downstream consumer/analysis concerns
  (research §6); the framework's job ends at handing over a correct, cheap collision BVH (C §5).
- **The exact in-memory refcount/weak-handle mechanism for D6 lifetime** — C to detail at implementation;
  the invariant (cache owns lifetime, consumers never dispose) is locked.
- **Whether we ever ship independently-authored radar art** — out of scope; D5's provenance flag is the
  only forward hook.

---

## 7. Pointers

- The four design inputs this doc synthesizes — the shared seed and component docs **A** / **B** /
  **C** — were removed once the §0.5 amendment superseded the in-process-VRF framework; every
  load-bearing decision is restated above.
- CS2/VRF facts (all primary-source URLs): `../research/cs2-map-assets.md`
