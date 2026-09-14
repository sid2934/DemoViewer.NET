# Game icons

CS2's own weapon, HUD, rank and map artwork, baked out of the game and shipped with the app.

## Adding an icon

Three steps, and only the first needs thought.

**1. Find its source path.** The baker lists what the game ships and marks what we already take:

```sh
scripts/bake-icons.sh --list defus
```

```
[x] panorama/images/icons/ui/defuser  ->  ui/defuser
[ ] panorama/images/icons/ui/defuser_white
```

`--list` with no filter lists everything (1120 entries); `--free` and `--taken` narrow it. The script
exists only to pick the host RID — the baker's own default is macOS, and forgetting `-r` fails in a
way that does not mention RIDs.

**2. Add it to the curation.** `tools/DemoViewer.NET.AssetBaker/IconSet.cs` is the only file that
decides *what* gets baked; everything else is mechanism. Put the path (minus `.vsvg_c`) in the
dictionary for its namespace:

```csharp
["panorama/images/icons/ui/defuser_white"] = "defuser_solid",
```

Namespaces exist because they have different key sources, and mixing them makes lookup ambiguous:

| Namespace | Keyed by | How entries are added |
|---|---|---|
| `equipment/` | the raw `player_death.weapon` string | whole directory, automatic |
| `modifier/` | `KillFeedRow`'s flags | `IconSet.Modifiers` |
| `ui/` | a short name we choose | `IconSet.Ui` |
| `rank/` | Skill Group number, or a state | `IconSet.Ranks` |
| `map/` | the map name | whole directory, automatic |
| `premier/` | CS Rating tier | derived — see below |

**3. Re-bake and commit.**

```sh
scripts/bake-icons.sh
```

This needs a CS2 install (found through Steam's `libraryfolders.vdf`, or passed as
`--cs2=<path>`). It writes `assets/icons/`, which **is committed** — so nobody else needs CS2 to
build, and CI never needs the game.

If you add a name the current CS2 build does not have, the bake fails and names it. That is
deliberate: a silently-missing curated icon is worse than a stopped build.

## Using an icon

```xml
<controls:GameIcon Key="ui/bomb_c4" Fallback="C4" IconHeight="14"
                   Foreground="{DynamicResource TextValue}" />
```

`Fallback` is the point of the whole design: **an icon can go missing and the screen still works.**
That is what makes it safe to adopt icons one site at a time, and to keep using them across CS2
updates that rename things.

### The four absence states

`IconCatalogue.Demand(key, out availability)` distinguishes them, and only one suppresses the
fallback:

| State | Means | Draws | Logged |
|---|---|---|---|
| `Available` | artwork exists | the icon | no |
| `Blank` | **CS2 ships empty art on purpose** — the environment deaths (`world`, `worldent`, `trigger_hurt`) | nothing | no |
| `Missing` | typo, never curated, or renamed by an update | `Fallback` | once per key |
| `None` | no key bound at all | `Fallback` | no |

`Blank` is the asymmetry that matters. An environment death has no weapon; printing "world" there
would name a thing the game is telling us is not there. The baked manifest carries the blank list so
the catalogue can tell that apart from a genuine gap.

### Probe vs demand

- `IconCatalogue.Get(key)` — **probe.** Use from classifiers ("is this entity class a weapon?").
  Records nothing.
- `IconCatalogue.Demand(key, out _)` — **draw.** Records a miss once.

Probing with `Demand` pollutes the miss registry: `WeaponIconKey.EntityClass` would log every player
pawn it ever saw.

### Converters compose, they do not resolve

`WeaponIconKey.Key` returns `equipment/<weapon>` unconditionally and lets the catalogue classify it.
An earlier version resolved in the converter and returned null for both `world` and `not_a_weapon` —
collapsing `Blank` and `Missing` before `GameIcon` could tell them apart. Any new key converter
should compose only.

### Tinting

Most icons are flat white alpha masks and take the call site's `Foreground`. Some are pictures —
every rank badge and Premier emblem, all map icons, and four genuinely coloured UI icons
(`ui/defuser`, `ui/defuse`, `ui/mvp`, `ui/assists`). `GameIcon` checks `IconRef.Tintable` and draws
those untinted; tinting a Global Elite badge would flatten it to a coloured blob.

The baker decides tintability by **hue, not whiteness**. A whiteness test would also exclude merely
*shaded* greys (the airborne wing, the armour plate), and an icon that is never tinted renders
white-on-white in the light theme — a worse failure than losing some internal shading.

## Where icons belong, and where they do not

This pattern covers **game vocabulary**: weapons, kill modifiers, round objects, sides, ranks, maps.

It is **not** licence to iconify navigation. `design-system.md` records that an icons-for-NavStrip
pass was built and rejected ("icons just not good"), and the NavStrip and command palette ship
text-forward by deliberate preference. That decision stands. If anything this pattern reinforces it:
text is the substrate the icon is an *enhancement* of, which is exactly why every adopted site keeps
a fallback.

Stat column headers are also deliberately left as words — a column header is read, not scanned.

## Guard rails

- **`IconKeyReferenceTests`** scans shipping source for literal keys and fails the build on one that
  is not in the bake. Test projects are out of scope (not shipping code, and they synthesise keys
  freely). A key that must be absent on purpose — the `icon-fallback` gallery needs one to photograph
  the missing state — carries `missing_on_purpose` in its name and is skipped by that marker, so the
  real keys sitting beside it in the same file stay guarded.
- **`EnsureIconsAreBaked`** (in `DemoViewer.NET.GameIcons.csproj`) fails the build if `icons.json`
  is absent, rather than letting the app throw later.
- **Missing keys surface** as one log line per distinct key (`AppLog.IconKeyMissing`) and as an
  always-on `icons` row in the Diagnostics tab's environment table, so a copied bug report carries
  the bake hash and the miss count even when internal logging is off.
- **`dotnet run --project src/App/DemoViewer.NET.UiCapture -- icon-fallback`** renders all five
  states; `-- game-icons` and `-- rank-badges` render the sets.

## Packaging

Icons are **embedded** in `DemoViewer.NET.GameIcons.dll`, not copied beside the executable. The
Browser head has no filesystem to probe, and per-map bundles (radar, collision) are the only assets
that ship loose. `publish.sh` copies `assets/` then deletes `assets/icons` again for that reason —
otherwise the same ~1.9 MB would ride in every release and every Velopack delta for nothing.

## Premier tiers are generated, not curated

CS2 ships **no** rating badge image. The emblem is one greyscale plate that Panorama washes per
tier, so the bake produces the seven colours itself, plus an unranked plate.

Tier colours and thresholds are lifted from the game's own
`panorama/styles/rating_emblem.vcss` and its script — not transcribed from a wiki — so a Valve retune
arrives as a re-bake diff. If you touch that code: the tint must be `SKBlendMode.Modulate`, never
`Multiply`. `Multiply` is a separable Porter-Duff mode that composites as well as blends, so against
a transparent destination it paints the tint at full opacity and turns each parallelogram badge into
a filled rectangle. `PremierEmblemTests.SlantedCorners_AreTransparent` guards it.

## Why the artwork is bundled rather than extracted on the user's machine

Bundling is a deliberate choice, not an oversight. `assets/icons/` is committed and ships with the
app; the notices file records what it contains and whose it is (THIRD-PARTY-NOTICES.md §b2).

The alternative — ship no Valve artwork and extract it from the user's own CS2 install on first run —
was investigated and measured rather than guessed. Recording the numbers here so the question does not
have to be re-costed if it is ever reopened:

| Approach | Cost | Covers |
|---|---:|---|
| In-process extraction, icons only | ~1.1 MB of deps, ~2.8 s first run | icons only |
| Baker shipped standalone, self-contained | 98 MB | everything |
| ...trimmed | 44 MB | everything, but trimming breaks the manifest write and the map paths are untested under it |
| Baker shipped inside the app folder, sharing its runtime | **21.6 MB** | everything |

Findings worth keeping:

- **Icons need neither ValveResourceFormat nor SkiaSharp 3.** A `.vsvg_c` is a Source 2 header, a
  block table, and the SVG text sitting in the `DATA` block after a 6-byte prefix — about 30 lines to
  read. VRF is only needed for *texture* decoding, which icons never touch. Rasterising with
  Svg.Skia 1.x runs on the app's own SkiaSharp 2.88.9 pin and produced output byte-identical to the
  shipped bake for 4 of 7 sampled icons, and within a mean of 1/255 per pixel for the other three
  (the gradient ones).
- **Two SkiaSharp majors can share one publish folder**, because 187 of the baker's 221 files are the
  same .NET runtime the app already ships and only four differ. The four go in a private
  subdirectory, and the tool redirects to them with `NativeLibrary.SetDllImportResolver` registered
  against the SkiaSharp assembly as it loads. `AssemblyLoadContext.ResolvingUnmanagedDll` does **not**
  work for this — it is a last resort that only fires when default probing fails, and probing succeeds
  by finding the app's 2.88 native, which then dies inside `SKColorSpace`'s static constructor. The
  four names must also be removed from the tool's `deps.json` or the host resolves them from the flat
  directory first. This was proven with a full 332-icon bake; the map paths were never tested.
- **None of it helps the Browser head**, which has no filesystem and no CS2 to extract from.
- **Exposure is not proportional to size.** The 86.5 MB of `collision.tris` is derived triangle
  geometry; the 3.4 MB of icons and map radars is literal artwork.

## Known gap

`rank/` and `premier/` have **no data to bind to yet.** Neither the app nor CS2DemoKit decodes a
skill group, CS Rating or MVP field. The artwork is ready for when that extraction happens; it is not
usable today, and that is a parser change rather than a view one.
