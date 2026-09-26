---
name: cs2-icon-extraction
description: The baked CS2 icon pipeline — where the artwork comes from, why it is PNG not vector, and the two guards that shape how it reaches consumers
metadata:
  type: project
---

CS2 ships its weapon and HUD icons as **compiled Panorama SVG** (`.vsvg_c`) in
`game/csgo/pak01_dir.vpk`, not as textures. VRF decodes them with no image pipeline:
`((Panorama)resource.DataBlock).Data` is the **original SVG bytes, verbatim**.

Built 2026-09-08 and shipping: `tools/DemoViewer.NET.AssetBaker --icons` (Steam auto-discovery via
`libraryfolders.vdf`, or `--cs2=<path>`) bakes **332 keys → `assets/icons/`,
665 files, 1.9 MB** at two scales (32 and 96), with `icons.json` carrying intrinsic sizes and
the Premier tier table.
`src/GameIcons/` embeds them and is the only way the rest of the solution reaches them
(`IconCatalogue.Weapon/Modifier/Ui/PremierEmblem/CompetitiveRank/WingmanRank`).

**Skill Group badges are `rank/competitive/0..18` and `rank/wingman/0..18`** plus `none` / `expired`
(and competitive-only `needwins`), from `icons/skillgroups/skillgroup*` and `wingman*`. They cost
**about 1 MB of that total** — full-colour, so they do not compress like the masks. Rank NAMES come from
`resource/csgo_english.txt` (`skillgroup_0..18`), a plain vpk file needing no VRF wrapper; **Wingman
reuses the Competitive names** — the game ships only per-*state* Wingman strings. Danger Zone's 18
badges sit in the same directory, deliberately not taken.

**The Premier CS Rating emblem is the one icon that breaks the tint rule.** CS2 ships no rating
badge image — the emblem is `icons/ui/premier_rating_bg`, a *shaded* plate (bright #E6E6E6 chevrons,
#393737 body, six gradients), and Panorama washes it per tier with **multiply**, not SrcIn. So the
bake emits seven pre-tinted variants (`premier/tier0..tier6`) plus `premier/unranked`, and consumers
must draw them **untinted** — re-tinting flattens them.

**Use `SKBlendMode.Modulate`, never `Multiply`, to apply a tint that must keep its alpha.** Skia's
`Multiply` is a separable Porter-Duff mode that composites as well as blends: against a transparent
destination it resolves to `ar = as + ad - as*ad = 1`, so it paints the tint at FULL OPACITY over
every clear pixel. It silently turned each parallelogram badge into a filled rectangle — visible
only as the slanted corners filling in, and only on the tinted variants, never on the untinted
plate. `Modulate` is the plain componentwise product on premultiplied values and leaves `ar = ad`.
Guarded by `PremierEmblemTests.SlantedCorners_AreTransparent` (in the Playback2D suite, which has an
image decoder — the icon assembly deliberately has none); that test was confirmed to fail on the old
mode before being kept. Tier colours and thresholds are lifted from
the game's own `panorama/styles/rating_emblem.vcss` (`@define color-csrating-tier-N`) and its script
(`clamp(floor(rating / 5000), 0, 6)`): #b0c3d9 #8cc6ff #6a7dff #c166ff #f03cff #eb4b4b #ffd700 at
0/5k/10k/15k/20k/25k/30k. Panorama sources extract like any other resource — `PanoramaStyle` /
`PanoramaScript` `.Data` is the original text, which is how those numbers were obtained rather than
guessed.

**Why PNG and not vector**, since it looks like vector should win: CS2's weapon icons are dense
Illustrator traces carrying **6.5–10 KB of path data each** — `awp` alone is 4.9 KB. A measured
SVG→path converter came out *bigger* than PNG at three resolutions, failed a fidelity gate on 40 of
110 icons, and needed a converter to maintain. Two scales (32, 96) because a 96 master downscales to
48 px at 1.65/255 mean alpha error and to 32 px at 5.3, but to 16 px at 12.2.

**Two guards shape everything downstream, and both were nearly tripped:**
- `ArchitectureTests.Core_ReferencesOnlySkiaSharpAndBcl` forbids Playback2D.Core referencing the
  icon assembly. So Core declares `IIconSource` and Pipeline implements `SkiaIconSource`.
- `hud.killfeed` is in the golden fixture set, so `KillFeedLayer`'s icon path is **opt-in and
  defaults to the old text tokens**. Turning it on for export re-baselines goldens — deliberately
  left as a decision, not done.

**Check `IconRef.Tintable` before recolouring anything.** The catalogue is no longer uniformly
maskable: rank badges and Premier emblems are pictures, and 4 UI icons (`defuser`, `defuse`, `mvp`,
`assists`) carry real hue. The baker decides this by **hue, not whiteness** — a whiteness test also
excludes merely *shaded* greys (the airborne wing, the armour plate), and an icon that is never
tinted renders white-on-white in the light theme, which is a worse failure than losing its shading.
`GameIcon` draws non-tintable icons plain.

**How to apply:** tintable icons are white-on-transparent alpha masks, so tint at draw time — the `GameIcon`
Avalonia control uses `PushOpacityMask` (Avalonia has no image tint), Skia uses
`SKColorFilter.CreateBlendMode(c, SrcIn)`. the catalogue is a **mix** of wide silhouettes and square glyphs (0.33×–4.78×) — assume
neither; size by height and let width follow `IconRef.WidthAt`. Weapon lookup takes `player_death.weapon` verbatim —
no mapping table. Entity classes go through `WeaponIconKey.ForEntityClass` (strip `C`/`CWeapon`,
lowercase, plus ~6 aliases). Verify visually with
`dotnet run --project src/App/DemoViewer.NET.UiCapture -- game-icons --size 780x300`.
See [[assetbaker-run-windows]] — the baker still needs `-r win-x64` here.

**Packaging.** `publish.sh` copies `assets/` next to the exe then **deletes `assets/icons` again** — the
icons are embedded in the assembly (the Browser head has no filesystem), so a loose copy would double
them into every release and every Velopack delta. It instead verifies `DemoViewer.NET.GameIcons.dll`
landed; CI's `wasm-build` job asserts the same for the wasm payload, in a check kept separate from the
"boot-critical" list because a missing icon assembly does not stop the head booting, it throws later.
`GameIcons.csproj` has an `EnsureIconsAreBaked` target so a missing bake fails the build, not the app.
Measured 2026-09-17 at 332 keys: **2.06 MB in the assembly, 1.92 MB brotli** on the wasm download.
**`GameIcons.Tests` runs in CI's `library-tests` lane** (`scripts/test.sh -p gameicons`), wired there
during the #14 merge review; it was in the solution but named by no lane, so its 78 cases compiled
and never ran.

**Two defects worth remembering, both found by measuring rather than reasoning.**
1. `SkiaIconSource.Lookup` originally composed its cache key with `$"{key}@{scale}"`. Lookup runs twice
   per icon per row per frame (measure, then draw), so that alone was **4,290 B/frame** in a layer whose
   documented budget is 0. Fixed with a `ValueTuple` key; `IconCatalogue.ScaleFor` also had to stop
   `foreach`-ing `IReadOnlyList<int>`, which boxes an enumerator. Guarded by
   `KillFeedIconAllocationTests`, which measures the icon path as a **delta over the text path** (the
   text feed is genuinely 0 B/frame) — an absolute assertion there would be measuring TextBlobCache.
2. The CI allocation bench renders `synthetic-tenplayers`, which mounts **no HUD layer**, so nothing in
   the existing gates could ever have seen either bug.

**The rank and Premier badges currently have no data to bind to.** Two independent sweeps confirmed the
app and CS2DemoKit decode no skill group, CS Rating or MVP field anywhere; `docs/ui/stats-components.md`
already records rank badges as a deliberate exclusion. The artwork is ready for when that data is
extracted; it is not usable today. Also note `docs/ui/design-system.md` records that an icons-for-nav
pass was tried and **rejected** ("icons just not good") — NavStrip and CommandPalette ship text-forward
by decision and must not be iconified.

**The fallback pattern (the way icons get adopted safely).** `IconCatalogue.Demand(key, out
IconAvailability)` returns FOUR states, and the distinction is the whole design: `Available`;
`Blank` (CS2 ships empty artwork — environment deaths — draw nothing, report nothing); `Missing`
(typo/uncurated/renamed — fall back and report once); `None` (no key bound — fall back, report
nothing). `GameIcon.Fallback` draws text for `Missing` **and** `None`, never for `Blank`. Manifest
schema 2 carries the `blank` list so the catalogue can tell them apart.

Consequences worth remembering:
- **Converters must COMPOSE keys, not resolve them.** `WeaponIconKey.Key` returns
  `equipment/<weapon>` unconditionally; resolving there collapsed `Blank` and `Missing` into one
  null before `GameIcon` could tell them apart. `WeaponIconKey.Missing` is deleted.
  `.EntityClass` still resolves — different question, and a `CCSPlayerPawn` must not log a miss.
- Use `Get` to PROBE (classifiers), `Demand` to DRAW. Probing with Demand pollutes the miss registry.
- Misses surface as one log line per distinct key (`AppLog.IconKeyMissing`, deduped in the
  catalogue) plus an always-on `icons` row in `RuntimeEnvInfo.SystemRows()` so copied bug reports
  carry it even when the ILogger pillar is switched off.
- `IconKeyReferenceTests` scans shipping source for literal keys and fails the build on a bad one.
  Test projects are out of scope; a key that must be
  absent on purpose carries `missing_on_purpose` in its name and is skipped by that marker. Excluding
  the whole UiCapture project instead (the first attempt) silently unguarded ~30 real gallery keys —
  the printed "N keys checked" count is what catches that, which is why the test asserts on it.
- Visual proof of all five states: `dotnet run --project src/App/DemoViewer.NET.UiCapture -- icon-fallback`.

**Adding an icon later is a three-step loop, documented in `docs/ui/game-icons.md`:**
`--list-icons=<substring>` on the baker prints every icon this CS2 build ships, marks the ones the
curation already takes with `[x]`, and prints the exact source path to paste (`--free` / `--taken`
narrow it). Put it in `IconSet.cs`, re-run `--icons`, commit `assets/icons/`. The baker stops the bake
by name if two entries claim one key, or if a curated name is not in the current CS2 build.
`docs/ui/design-system.md` now carries a `game-icon-scope` anchor stating this set covers GAME
vocabulary only and does not reopen the rejected icons-for-nav decision.

**Decision 2026-09-09: bundle the Valve artwork, do not extract at runtime.** Owner's call, made after
the alternative was measured; deal with any legal question if it arises. `assets/icons/` stays
committed, THIRD-PARTY-NOTICES.md §b2 records what it is. Do not re-open this or re-cost it unless
asked — the numbers and the two hard-won technical findings (icons need no VRF and no SkiaSharp 3;
two SkiaSharp majors can share a publish folder via `NativeLibrary.SetDllImportResolver`, NOT via
`ResolvingUnmanagedDll`) are written up in `docs/ui/game-icons.md` under "Why the artwork is bundled".

