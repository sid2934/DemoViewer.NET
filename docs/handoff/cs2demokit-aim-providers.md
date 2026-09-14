# CS2DemoKit handoff: aim-rating providers and scanners

**For:** the CS2DemoKit team
**From:** DemoViewer.NET, v0.8.1 aim-rating workstream
**Against:** CS2DemoKit v0.10.0
**Status:** implemented, and released in CS2DemoKit 0.11.0. `VisibilityTransitionScanner`,
`AimVantageScanner` and the eye/punch angle providers all landed engine-side, and DemoViewer.NET
pins that release.

DemoViewer.NET is adding a family of 18 aim-quality statistics (counter-strafing, spray control,
crosshair placement, the time-to-X ladder). Most of them need per-tick entity state the engine does
not currently surface, and one needs a capability the engine does not have at all: a per-player
rising edge for "this enemy just became visible to me".

This document specifies every new provider and scanner, and how to validate each one.

Every wire type below was **probed from a real demo**, not read off a schema dump. The probe:

```sh
dotnet run -c Release --project tools/EntityDecodeProbe -- --schema <demo.dem> <Class1,Class2>
```

run against `assets/tour/sample-de_nuke.dem` (pro GOTV, trimmer rung `v3c`).

## 1. What the probe found

| Field | Serializer class | Wire type | Encoder | Declared type | Compatible today |
|---|---|---|---|---|---|
| `m_angEyeAngles` | `CCSPlayerPawn` | `QAngle` | `qangle_precise` | float | **No** |
| `m_flDuckAmount` | `CCSPlayer_MovementServices` | `float32` | - | float | Yes |
| `m_flMaxspeed` | `CCSPlayer_MovementServices` | `float32` | bc=12, high=2048 | float | Yes |
| `m_iShotsFired` | `CCSPlayerPawn` | `int32` | - | int | Yes |
| `m_bIsScoped` | `CCSPlayerPawn` | `bool` | - | bool | Yes |
| `m_flFlashDuration` | `CCSPlayerPawn` | `float32` | - | float | Yes |
| `m_flRecoilIndex` | `CCSWeaponBaseGun` | `float32` | - | float | Yes (hop 2) |
| `m_iRecoilIndex` | `CCSWeaponBaseGun` | `int32` | - | int | Yes (hop 2) |
| `m_fAccuracyPenalty` | `CCSWeaponBaseGun` | `float32` | - | float | Yes (hop 2) |
| `m_predictableBaseAngle` | `CCSPlayer_AimPunchServices` | `QAngle` | `qangle` | float | **No** |
| `m_fLastShotTime` | `CCSWeaponBaseGun` | `GameTime_t` | - | float | **No** |
| `m_nDeployTick` | `CCSWeaponBaseGun` | `GameTick_t` | - | int | **No** |

Four things worth calling out.

**`m_angEyeAngles` is `qangle_precise`.** This has circulated as a community-overlay claim; it is now
confirmed from the wire. It sets the error bar on every angular metric we are building, and it is finer
than the default 1/64-degree quantisation, so crosshair-placement numbers are not precision-limited.

**`CCSWeaponBase` is not a serializer class.** The probe reports `NOT FOUND`. The class that actually
carries the weapon fields is **`CCSWeaponBaseGun`** (139 fields). Any spec naming `CCSWeaponBase` will
not validate.

**`m_flMaxspeed` is quantised to 12 bits over 0..2048**, so roughly 0.5 u/s steps. That is comfortably
finer than the 0.34 threshold needs (about 73 u/s for an AK), but it is not a full float.

**`m_vecVelocity` does not appear on `CCSPlayerPawn` at all**, consistent with the note in
`PerPlayerEntityValueProviderRegistry.cs:25-27` that a movement provider was prototyped and removed
because velocity reads uniformly zero on GOTV pawns. We are not asking for it. See section 4.

## 2. Generic `ProviderSpec` additions

These need no new code beyond the spec entries, because their wire types are already in the
`Coerce` and `IsWireTypeCompatible` allowlists.

| Name | Class | Path | Type | Notes |
|---|---|---|---|---|
| `entity.pawn.duck_amount` | `CCSPlayerPawn` | `m_pMovementServices.m_flDuckAmount` | float | 0..1 continuous ramp, not a bool |
| `entity.pawn.max_speed` | `CCSPlayerPawn` | `m_pMovementServices.m_flMaxspeed` | float | Per-weapon; the counter-strafe threshold is `0.34 * this` |
| `entity.pawn.shots_fired` | `CCSPlayerPawn` | `m_iShotsFired` | int | Resets on trigger release or weapon change. Spray position, not a round counter |
| `entity.pawn.is_scoped` | `CCSPlayerPawn` | `m_bIsScoped` | bool | |
| `entity.pawn.flash_duration` | `CCSPlayerPawn` | `m_flFlashDuration` | float | Seconds; 0 means not flashed. Used to exclude blinded engagements |
| `entity.weapon.recoil_index` | 2-hop | `m_pWeaponServices.m_hActiveWeapon` -> `m_flRecoilIndex` | float | |
| `entity.weapon.accuracy_penalty` | 2-hop | `m_pWeaponServices.m_hActiveWeapon` -> `m_fAccuracyPenalty` | float | Accumulated inaccuracy state. Does **not** include the movement term |

**The two-hop caveat, restated because it bites quietly.** `ValidateHandleField` judges hop 1 only. If
a hop-2 field name is wrong or drifts, the slot is skipped and the provider reads null: a column of
nulls that looks like "this player had no weapon", not an error. Both hop-2 fields above are confirmed
present on `CCSWeaponBaseGun`, but that guarantee is per demo, so please add the probe assertions in
section 6.

## 3. Bespoke component providers

`m_angEyeAngles` and `m_predictableBaseAngle` are `QAngle`. The rules type vocabulary has no vector
type and no member access, so these must be split into scalar components, exactly as
`PawnPositionProvider` does for position.

Requested:

| Name | Source | Component |
|---|---|---|
| `entity.pawn.eye_pitch` | `CCSPlayerPawn.m_angEyeAngles` | X, degrees |
| `entity.pawn.eye_yaw` | `CCSPlayerPawn.m_angEyeAngles` | Y, degrees |
| `entity.pawn.punch_pitch` | `CCSPlayer_AimPunchServices.m_predictableBaseAngle` | X, degrees |
| `entity.pawn.punch_yaw` | `CCSPlayer_AimPunchServices.m_predictableBaseAngle` | Y, degrees |

`PawnPositionProvider` is the template: implement `IPerPlayerEntityValueProvider`,
`IWorkerCloneable<IPerPlayerEntityValueProvider>` and `IPawnStateReader`, do the real read in
`ReadForPawnState`, and throw `NotSupportedException` from `ReadForPawn`.

**One thing the position template cannot solve for us.** `PawnPositionProvider` sidesteps the
declared-type check by naming a scalar leaf (`CBodyComponent.m_vecX`, a `CNetworkedQuantizedFloat`) as
its `FieldName` while reading the composite. `m_angEyeAngles` has **no scalar leaf** to name, so a
`float` provider over it fails `IsWireTypeCompatible` and throws at prime time.

We would like `IsWireTypeCompatible` to gain a `QAngle` arm under `float`:

```csharp
if (declaredType == typeof(float))
{
    return wireType is "float32" or "CNetworkedQuantizedFloat" or "float64" or "QAngle";
}
```

with a comment recording that a `float` provider over a `QAngle` field is declaring "one component of
this angle", which is the only shape the rules language can express. If you would rather not widen the
allowlist, the alternative is a `ComponentOf` field on `ProviderSpec`; we have no preference beyond
wanting the failure to stay loud.

**Aim punch has a shape trap.** The flat `m_aimPunchAngle` was removed at build 22894272 and replaced
by `CCSPlayer_AimPunchServices`. The current fields are a **spring state sampled at a base tick**, not
a resolved angle: getting the punch at an arbitrary tick means integrating decay forward from
`(m_predictableBaseTick, m_predictableBaseAngle, m_predictableBaseAngleVel)`. A naive read of
`m_predictableBaseAngle` returns a plausible number that is wrong away from the base tick.
`m_aimPunchCache`, which used to carry per-shot history, has been gone since build 21529689. Please
gate the provider with `SchemaVersionAttribute(since: "22894272")`.

There is an oracle for this: on a demo carrying `bullet_damage`, `AimPunchX/Y/Z` is the server's own
resolved punch at the shot. The integrated value must agree with it.

## 4. Derived speed, and why we are not asking for velocity

Counter-strafing needs 2D speed at the shot instant. `m_vecVelocity` is not usable, so speed must come
from `pos_x` / `pos_y` deltas across consecutive ticks. `PawnPositionProvider` already supplies those in
v0.10.0, so **no new provider is needed**; the derivation belongs in the scanner (section 5).

Two accuracy notes for whoever writes it:

- CS2 splits a tick into two simulations on every button press or release, so the demo records only the
  end-of-tick velocity. A counter-strafe is precisely a keypress-and-fire inside one or two ticks, which
  means tick-sampled speed carries quantisation error that CS:GO did not have.
- `m_flVelocityModifier` is the damage and taser slowdown multiplier, not a walk toggle. Do not use it
  to infer intent.

## 5. New scanners

### 5.1 `AimVantageScanner`

Per sampled tick, build one `Vantage` per live pawn from the digest columns (`pos_*`, `eye_pitch`,
`eye_yaw`, `duck_amount`), plus derived 2D speed from the previous sampled tick.

This is the "future lever" named in `VisibilityAnalyzer.cs:24-27`: once vantage is available inside the
existing digest walk, the standalone visibility replay no longer needs its own second decode pass.

**Cost, stated honestly.** Eye angles, duck and position all change on nearly every frame for nearly
every pawn, so these columns largely defeat the v0.10.0 delta encoding, exactly as
`PawnPositionProvider`'s own doc comment records for position. Providers are reference-gated by name,
so a ruleset reading none of them pays nothing; we are asking you to accept the cost only when an aim
ruleset is loaded.

### 5.2 `VisibilityTransitionScanner`

The capability this document was written to request; none of it was in v0.10.0.
`VisibilityAnalyzer.Analyze` computes per-tick pairwise visibility and then **throws the booleans
away**, accumulating only seconds. There is no rising-edge detection and nothing in `Report` carries
a tick.

Requested: detect `false -> true` transitions per directed (viewer, target) enemy pair and synthesize an
event carrying:

```
viewer_slot        int
target_slot        int
tick               int
angle_to_chest_deg float   // angle between the viewer's eye ray and the target's chest anchor
```

`EvaluatePair`, `TryVantage` and `PlayerVantage.BuildAnchors` are already public and reusable, so the
geometry is solved; what is missing is edge detection and an event to hang it on.

**Sampling stride must be 1**, not the default 4. Stride 4 is 62 ms resolution, and the metrics that
consume this have a total dynamic range of roughly 150 ms across the whole skill ladder.

**Chest anchor at 48u**, which is `PlayerVantage.BuildAnchors`' first anchor and the most stable against
crouch transitions.

This is the same missing capability that `rules/highlights_aim_pro.rules.yaml:20-23` names as the
blocker for quickscope ("per-player entity providers are pull-only, so latching the scope-in tick needs
new per-player transition scanning in `EntityChangeScanner`"). Solving it here would unblock that too.

### 5.3 Catalog surface

A new view `enemy_spotted`, actor-slot bound to the **viewer**, with roles `viewer` and `target`, plus
enrichments under `enrich.spotted.*`. After adding it:

```sh
dotnet run --project tools/CS2DemoKit.RulesCatalog
```

`CatalogDriftTests` byte-compares the regenerated `catalog.json` and the v2 schema, so a stale commit
fails loudly.

## 6. How to validate each piece

| Concern | Gate | Where |
|---|---|---|
| Field is really networked in a real demo | Add a `CriticalField` entry | `test/CS2DemoKit.Analysis.Tests/SchemaKeysAssertionTests.cs:42` |
| Field path spelling before committing | `EntityDecodeProbe --descriptors` then `--schema` | `DemoViewer.NET/tools/EntityDecodeProbe` |
| Provider registered on both sides | `ProviderDigestParityTests` compares hand-written against spec-built digests byte for byte | Register in **both** `PerPlayerEntityValueProviderRegistry.CreateDefault()` and `BuiltinProviderSpecs`, at the same position |
| Declared type matches the wire | `TryValidateProviderSchema` throws at prime time; extend the pinned cases | `ProviderSchemaValidationTests` |
| Hop-2 field is present | Not covered by any existing gate. Add an explicit probe assertion | see the two-hop caveat in section 2 |
| Aim-punch integration is correct | Compare against `bullet_damage.AimPunchX/Y/Z` on a matchmaking demo | new oracle test |
| Derived speed is correct | Compare against `bullet_damage.InaccuracyMove` on a matchmaking demo, which is the engine's own movement penalty at the shot | new oracle test |
| New view or enrichment | Regenerate the catalog, then `CatalogDriftTests` | `tools/CS2DemoKit.RulesCatalog` |
| New event semantics | Inline YAML plus an independent hand-written C# fold over `demo.AllGameEvents` | copy `RulesV2/V2KindGoldenSupport.cs` and `BucketKindGoldenTests.cs` |

Run a single test with `dotnet run`, never `dotnet test`, which silently ignores `--filter` and reports
success while running nothing:

```sh
dotnet run --project test/CS2DemoKit.Analysis.Tests -c Debug --no-build -- \
  --treenode-filter "/*/*/ProviderDigestParityTests/*" --disable-logo --no-progress
```

## 7. Two oracles worth knowing about

Both come from demo sources you may not be testing against today, and both turn a guess into a check.

**`bullet_damage` is the server's own aim telemetry**, carrying `InaccuracyTotal`, `InaccuracyMove`,
`InaccuracyAir`, `RecoilIndex`, `ShootAng*`, `AimPunch*`, `AttackTickCount` and `AttackTickFrac` per
damaging bullet. Where it is present, it is strictly more authoritative than anything reconstructed
from entity state, and it is the oracle for both the speed derivation and the aim-punch integration.

**It is not present everywhere.** Probing three demos with `DemoSourceDetails`:

| Signal | Pro GOTV (trimmed) | Valve MM Jan 2025 | Valve MM Sep 2025 |
|---|---|---|---|
| `bullet_damage` | 0 | 683 | present |
| `svc_UserCmds` | 0 | 1,324,574 | 1,540,561 |
| `player_footstep` | 0 | 542 | present |
| `player_sound` | 2,249 | 0 | 0 |
| Game-event types | 31 | 45 | 46 |

The trimmer's `v3c` rung strips animation frames and `svc_UserCmds` but never game events, and the pro
sample is contemporaneous with the September MM demo, so neither trimming nor version drift explains
the game-event differences. **Capability is a property of the demo source.** The engine's per-profile
coverage declarations currently say `shot_landed` is available on all five profiles, which our data
contradicts, so a consumer that trusts the declaration will silently produce zeros.

We are building an observational capability probe on our side. If a per-demo (rather than per-profile)
coverage signal is something you would want in the engine, we would rather contribute it there.

## 8. Defect: the aim-punch column does not decode to degrees

Found while implementing spray control, and it is the reason that metric now reads the event rather
than the entity. Measured on a Valve matchmaking demo (de_vertigo, 353 landed bullets) by comparing
the entity column against `bullet_damage.AimPunchX`, which is the server's own resolved punch at the
same instant:

| Quantity | Value |
|---|---|
| mean absolute entity column | **68.369 deg** |
| mean absolute server value | **1.311 deg** |
| mean absolute difference | 67.059 deg (worst 94.088) |
| agreeing within 0.25 deg | 90 of 353 (25.5%) |

The 25.5% that agree are the shots where both read zero, so the column tracks nothing but the
zero case. Sample pairs, raw before wrapping:

```
oracle -0.180  ->  column 266.726     oracle  0.000  ->  column 0.000
oracle -0.177  ->  column 266.712     oracle -1.210  ->  column 268.623
oracle -0.151  ->  column 266.575     oracle -2.101  ->  column 269.254
```

Two things point at the decode rather than the semantics. The values cluster tightly around 266 to
269 regardless of the true magnitude, and the companion `m_aimPunchAngleVel` reads a **constant
272.6** on every shot in the demo, which is not a physical angular velocity. This is the flat
pre-22894272 field family (`m_aimPunchAngle`, `m_aimPunchAngleVel`, `m_aimPunchTickBase`,
`m_aimPunchTickFraction`), which is what every real demo we hold carries; the services family
appears only on the bundled trimmed sample.

Reproduce with `src/App/DemoViewer.NET.App.Tests/AimPunchOracleProbe.cs`, which walks every
`bullet_damage` in a demo and prints the comparison. It is `[Category("RealDemo")]` and needs
`DEMO_PATH`.

**We were not blocked on it.** `bullet_damage.ShootAng` already carries the resolved shot direction
with the punch folded in, so spray control is computed from that instead. The column itself no longer
lies: the engine's `AimPunchSchema` detector picks whichever field family the demo actually carries,
and a guard returns null when what it found is not physically a punch, so `player.punch_pitch` /
`player.punch_yaw` read empty on a demo like this one rather than reading 266 degrees.

**One related finding worth having regardless:** `ShootAng = eyeAngle + AimPunch`, verified per shot
on the same demo. That identity makes the real per-shot recoil recoverable as
`ShootAng - m_angEyeAngles` without touching the punch column at all, which is also how the weapon's
spray pattern could be derived empirically rather than by reimplementing Valve's PRNG.
