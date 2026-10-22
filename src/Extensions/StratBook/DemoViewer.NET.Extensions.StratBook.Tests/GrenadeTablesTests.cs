#region

using System.Numerics;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The grenade walk's three rulesets validate against the engine, declare the tables the fold reads, and fold
///     synthetic rows the way the walk joins them: spawn order, the landing of an unresolved thrower as a bare
///     grenade, the release pawn and its look-back, and the detonations by event name.
/// </summary>
public class GrenadeTablesTests
{
    [Test]
    public async Task TheShippedRulesets_LoadAndValidate_AndDeclareTheTables()
    {
        IReadOnlyList<RulesetDoc> docs = GrenadeTables.LoadRulesets();
        RulesetValidationResult validation = DemoAnalysis.ValidateRulesets(docs);
        string[] declared = [.. docs.SelectMany(d => d.Show?.Tables.Select(t => t.Name) ?? [])];

        using (Assert.Multiple())
        {
            await Assert.That(validation.Diagnostics).IsEmpty().Because(string.Join("; ", validation.Diagnostics));
            await Assert.That(docs.Select(d => d.Id)).IsEquivalentTo(GrenadeTables.RulesetIds);
            await Assert.That(declared).IsEquivalentTo(GrenadeTables.Tables);
        }
    }

    [Test]
    public async Task TheFold_OrdersBySpawn_JoinsALanding_AndReadsTheReleasePawn()
    {
        Dictionary<string, List<GrenadeTableRow>> tables = new(StringComparer.Ordinal);
        foreach (string table in GrenadeTables.Tables)
        {
            tables[table] = [];
        }

        tables[GrenadeTables.ProjectilesTable].Add(Row(
            new() { ["entity_index"] = 130L, ["serial"] = 9L, ["player_slot"] = 5L, ["first_seen_tick"] = 300L, ["created_tick"] = 300L, ["removed_tick"] = 900L, ["removed"] = true, ["team"] = 3L },
            new()
            {
                ["class_name"] = GrenadeProjectileClasses.Smoke, ["initial_x"] = 1.0, ["initial_y"] = 2.0, ["initial_z"] = 3.0,
                ["velocity_x"] = 10.0, ["velocity_y"] = 0.0, ["velocity_z"] = 5.0, ["is_incendiary"] = false,
                ["end_x"] = 50.0, ["end_y"] = 60.0, ["end_z"] = 3.0, ["bounces"] = 2L, ["last_moved_tick"] = 400L,
                ["explode_effect_tick"] = 0L, ["smoke_effect_tick"] = 9400L, ["smoke_detonation_x"] = 50.0, ["smoke_detonation_y"] = 60.0, ["smoke_detonation_z"] = 3.0,
                ["spawn_x"] = 0.0, ["spawn_y"] = 0.0, ["spawn_z"] = 0.0, ["spawn_on_ground"] = true,
                ["last_jump_tick"] = 290L, ["coverage"] = 0.5, ["offset_pawn"] = 14L, ["offset_x"] = -7.0, ["offset_y"] = 0.0, ["offset_z"] = 0.0,
                ["offset_pitch"] = -1.0, ["offset_yaw"] = 90.0, ["offset_on_ground"] = true, ["offset_ducking"] = false,
                ["offset_strength"] = 1.0, ["offset_weapon"] = "CSmokeGrenade", ["offset_before_x"] = -15.0, ["offset_before_y"] = 0.0
            }));
        tables[GrenadeTables.ProjectilesTable].Add(Row(
            new() { ["entity_index"] = 120L, ["serial"] = 7L, ["player_slot"] = 3L, ["first_seen_tick"] = 100L, ["created_tick"] = 102L, ["removed_tick"] = 204L, ["removed"] = true, ["team"] = 2L },
            new()
            {
                ["class_name"] = GrenadeProjectileClasses.HEGrenade, ["initial_x"] = double.NaN, ["end_x"] = 460.0, ["end_y"] = 0.0, ["end_z"] = 64.0,
                ["bounces"] = 1L, ["last_moved_tick"] = 190L, ["explode_effect_tick"] = 9000L, ["smoke_effect_tick"] = 0L,
                ["smoke_detonation_x"] = double.NaN, ["spawn_x"] = 5.0, ["spawn_y"] = 6.0, ["spawn_z"] = 7.0,
                ["spawn_on_ground"] = false, ["last_jump_tick"] = 94L, ["coverage"] = 1.0, ["offset_pawn"] = 0L, ["offset_x"] = 0.0, ["offset_y"] = 0.0,
                ["offset_z"] = 0.0, ["offset_weapon"] = "CAK47", ["offset_strength"] = 0.5
            }));
        tables[GrenadeTables.LandingsTable].Add(Row(
            new() { ["tick"] = 500L },
            new() { ["entity"] = 140L, ["serial"] = 2L, ["class_name"] = GrenadeProjectileClasses.Decoy, ["slot"] = -1L, ["x"] = 9.0, ["y"] = 8.0, ["z"] = 7.0, ["bounces"] = 3L, ["first_seen_tick"] = 250L }));
        tables[GrenadeTables.LandingsTable].Add(Row(
            new() { ["tick"] = 204L },
            new() { ["entity"] = 120L, ["serial"] = 7L, ["class_name"] = GrenadeProjectileClasses.HEGrenade, ["slot"] = 3L, ["x"] = 460.0, ["y"] = 0.0, ["z"] = 64.0, ["bounces"] = 1L, ["first_seen_tick"] = 100L }));
        tables[GrenadeTables.ReleasesTable].Add(Row(
            new() { ["tick"] = 294L, ["player_slot"] = 5L, ["team"] = 3L },
            new()
            {
                ["weapon"] = "weapon_smokegrenade", ["release_tick"] = 1293L, ["pawn"] = 14L, ["pos_x"] = 100.0, ["pos_y"] = 200.0, ["pos_z"] = 0.0, ["eye_pitch"] = -2.5, ["eye_yaw"] = 45.0,
                ["on_ground"] = true, ["ducking"] = true, ["throw_strength"] = 1.0, ["weapon_class"] = "CSmokeGrenade", ["before_x"] = 92.0, ["before_y"] = 200.0,
                ["before_pawn"] = 14L
            }));
        tables[GrenadeTables.ReleasesTable].Add(Row(
            new() { ["tick"] = 93L, ["player_slot"] = 3L, ["team"] = 2L },
            new() { ["weapon"] = "weapon_hegrenade", ["pawn"] = 0L, ["pos_x"] = 0.0, ["pos_y"] = 0.0, ["pos_z"] = 0.0, ["weapon_class"] = "CAK47", ["throw_strength"] = 1.0, ["before_x"] = 0.0, ["before_y"] = 0.0 }));
        tables[GrenadeTables.ReleasesTable].Add(Row(
            new() { ["tick"] = 60L, ["player_slot"] = 3L, ["team"] = 2L },
            new() { ["weapon"] = "weapon_ak47", ["pos_x"] = 1.0 }));
        tables[GrenadeTables.HeDetonationsTable].Add(Row(new() { ["tick"] = 204L }, new() { ["entity"] = 120L, ["x"] = 460.0, ["y"] = 0.0, ["z"] = 64.0 }));
        tables[GrenadeTables.InfernoStartsTable].Add(Row(new() { ["tick"] = 700L }, new() { ["entity"] = 300L, ["x"] = 1.0, ["y"] = 2.0, ["z"] = 3.0 }));

        GrenadeTableRows rows = GrenadeTables.Read(name => tables[name], 1000);

        ProjectileFacts he = rows.Projectiles[0], decoy = rows.Projectiles[1], smoke = rows.Projectiles[2];
        ReleaseFacts smokeRelease = rows.Releases.Single(r => r.Slot == 5);
        using (Assert.Multiple())
        {
            await Assert.That(rows.Projectiles.Select(p => p.Id)).IsEquivalentTo(["g120-7", "g140-2", "g130-9"]).Because("spawn order, the landing between");
            await Assert.That(he.ThrowerSlot).IsEqualTo(3);
            await Assert.That(he.InitialPosition).IsNull().Because("NaN is no position");
            await Assert.That(he.LastPosition).IsEqualTo(new Vector3(460, 0, 64));
            await Assert.That(he.ExplodeTickBegin).IsEqualTo(9000);
            await Assert.That(he.SmokeTickBegin).IsNull().Because("0 is unset");
            await Assert.That(he.LastTick).IsEqualTo(203);
            await Assert.That(he.AtSpawn!.Value.OnGround).IsFalse();
            await Assert.That(he.AtSpawn!.Value.Team).IsEqualTo(2);
            await Assert.That(he.AtSpawn!.Value.LastJumpTick).IsEqualTo(94);
            await Assert.That(he.OffsetRelease).IsNull().Because("a look-back from before the thrower had a pawn is no read");
            await Assert.That(he.OffsetBefore).IsNull();
            await Assert.That(smoke.OffsetRelease!.Value.HeldGrenade).IsTrue();

            await Assert.That(decoy.ThrowerSlot).IsEqualTo(-1);
            await Assert.That(decoy.SpawnTick).IsEqualTo(250);
            await Assert.That(decoy.EndTick).IsEqualTo(500);
            await Assert.That(decoy.LastPosition).IsEqualTo(new Vector3(9, 8, 7));
            await Assert.That(decoy.Bounces).IsEqualTo(3);
            await Assert.That(decoy.AtSpawn).IsNull();
            await Assert.That(rows.Projectiles.Count(p => p.Id == "g120-7")).IsEqualTo(1).Because("a resolved landing is not a second grenade");

            await Assert.That(smoke.OffsetRelease!.Value.Position).IsEqualTo(new Vector3(-7, 0, 0));
            await Assert.That(smoke.OffsetRelease!.Value.Tick).IsEqualTo(293);
            await Assert.That(smoke.OffsetRelease!.Value.ThrowStrength).IsEqualTo(1f);
            await Assert.That(smoke.OffsetBefore).IsEqualTo(new Vector3(-15, 0, 0));
            await Assert.That(smoke.SmokeTickBegin).IsEqualTo(9400);
            await Assert.That(smoke.SmokeDetonationPosition).IsEqualTo(new Vector3(50, 60, 3));

            await Assert.That(rows.Releases.Count).IsEqualTo(2).Because("a rifle shot is no release");
            await Assert.That(rows.Releases[0].Tick).IsEqualTo(93);
            await Assert.That(smokeRelease.ProjectileClass).IsEqualTo(GrenadeProjectileClasses.Smoke);
            await Assert.That(smokeRelease.Tick).IsEqualTo(293).Because("the event's own tick, on the frame clock");
            await Assert.That(smokeRelease.Pawn.Tick).IsEqualTo(293);
            await Assert.That(rows.Releases[0].Tick).IsEqualTo(93).Because("the frame's tick stands in without one");
            await Assert.That(rows.Releases[0].Pawn.Position).IsNull().Because("no pawn at the release: the zeros are defaults");
            await Assert.That(rows.Releases[0].Pawn.Team).IsEqualTo(2);
            await Assert.That(rows.Releases[0].Before).IsNull();
            await Assert.That(smokeRelease.Pawn.Position).IsEqualTo(new Vector3(100, 200, 0));
            await Assert.That(smokeRelease.Pawn.Ducked).IsTrue();
            await Assert.That(smokeRelease.Pawn.Team).IsEqualTo(3);
            await Assert.That(smokeRelease.Pawn.ThrowStrength).IsEqualTo(1f);
            await Assert.That(smokeRelease.Before).IsEqualTo(new Vector3(92, 200, 0));

            await Assert.That(rows.Events.ByEntity(GrenadeEventIndex.HeDetonate, 120, 100, 205)!.Tick).IsEqualTo(204);
            await Assert.That(rows.Events.NearestInferno(698, 699, null, new HashSet<GrenadeDetonation>())!.EntityIndex).IsEqualTo(300);
        }
    }

    [Test]
    public async Task AReleaseLookBack_FromBeforeTheSlotHadAPawn_IsNoRead()
    {
        Dictionary<string, List<GrenadeTableRow>> tables = GrenadeTables.Tables.ToDictionary(t => t, _ => new List<GrenadeTableRow>(), StringComparer.Ordinal);
        tables[GrenadeTables.ReleasesTable].Add(Row(
            new() { ["tick"] = 6L, ["player_slot"] = 2L, ["team"] = 3L },
            new()
            {
                ["weapon"] = "weapon_flashbang", ["release_tick"] = 1005L, ["pawn"] = 11L, ["pos_x"] = 300.0, ["pos_y"] = 40.0, ["pos_z"] = 0.0,
                ["weapon_class"] = "CFlashbang", ["throw_strength"] = 1.0, ["before_x"] = 0.0, ["before_y"] = 0.0, ["before_pawn"] = 0L
            }));

        ReleaseFacts release = GrenadeTables.Read(name => tables[name], 1000).Releases.Single();
        float? speed = GrenadeRules.HorizontalSpeed(release.Before, release.Tick - GrenadeRules.SpeedWindowTicks, release.Pawn.Position, release.Tick, 64);

        using (Assert.Multiple())
        {
            await Assert.That(release.Pawn.Position).IsEqualTo(new Vector3(300, 40, 0));
            await Assert.That(release.Before).IsNull().Because("the look-back's zeros are a default, not the origin");
            await Assert.That(speed).IsNull();
        }
    }

    private static GrenadeTableRow Row(Dictionary<string, object?> dimensions, Dictionary<string, object?> values) => new(dimensions, values);
}
