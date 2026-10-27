#region

using System.Globalization;
using System.Numerics;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;

/// <summary>One table row as either reader hands it over: the dimensions and the column values, by name.</summary>
/// <param name="Dimensions">The row's dimensions.</param>
/// <param name="Values">The row's column values.</param>
public readonly record struct GrenadeTableRow(IReadOnlyDictionary<string, object?> Dimensions, IReadOnlyDictionary<string, object?> Values);

/// <summary>
///     The grenade walk's contributed rulesets and the fold of their tables: <c>grenade_projectiles</c> (one row per
///     grenade whose thrower resolved, <c>for: each_projectile</c>), <c>grenade_releases</c> (one row per grenade
///     <c>weapon_fire</c>, <c>per: player_event</c>) and <c>grenade_events</c> (the detonations and every landing,
///     <c>for: match</c>). The grenade pass reads them off the visit's rules run; <see cref="Read(Func{string, IReadOnlyList{FactRow}}, int)" />
///     and <see cref="Read(AnalysisRun, int)" /> turn either reader's rows into <see cref="GrenadeTableRows" />.
/// </summary>
public static class GrenadeTables
{
    /// <summary>The extension's own names for the three rulesets.</summary>
    public const string ProjectilesRuleset = "grenade_projectiles";

    public const string ReleasesRuleset = "grenade_releases";

    public const string EventsRuleset = "grenade_events";

    /// <summary>The tables the rulesets declare.</summary>
    public const string ProjectilesTable = "grenade_projectiles";

    public const string ReleasesTable = "grenade_releases";

    public const string HeDetonationsTable = "grenade_he_detonations";

    public const string FlashDetonationsTable = "grenade_flash_detonations";

    public const string SmokeDetonationsTable = "grenade_smoke_detonations";

    public const string DecoyStartsTable = "grenade_decoy_starts";

    public const string InfernoStartsTable = "grenade_inferno_starts";

    public const string LandingsTable = "grenade_landings";

    private const string ResourcePrefix = "DemoViewer.NET.Extensions.StratBook.Rulesets.";

    /// <summary>The rulesets, in registration order.</summary>
    public static IReadOnlyList<string> Rulesets { get; } = [ProjectilesRuleset, ReleasesRuleset, EventsRuleset];

    /// <summary>Every table the rulesets declare.</summary>
    public static IReadOnlyList<string> Tables { get; } =
    [
        ProjectilesTable, ReleasesTable, HeDetonationsTable, FlashDetonationsTable, SmokeDetonationsTable,
        DecoyStartsTable, InfernoStartsTable, LandingsTable
    ];

    /// <summary>The qualified ids, as the YAML spells them.</summary>
    public static IReadOnlyList<string> RulesetIds { get; } = [.. Rulesets.Select(RulesetId)];

    /// <summary>A ruleset's qualified id.</summary>
    /// <param name="ruleset">One of <see cref="Rulesets" />.</param>
    public static string RulesetId(string ruleset) => RulesetContribution.QualifiedId(StratBookPack.PackId, ruleset);

    /// <summary>A ruleset's YAML.</summary>
    /// <param name="ruleset">One of <see cref="Rulesets" />.</param>
    public static Stream OpenYaml(string ruleset) =>
        typeof(GrenadeTables).Assembly.GetManifestResourceStream(ResourcePrefix + ruleset + ".rules.yaml")
        ?? throw new InvalidOperationException($"the embedded ruleset '{ruleset}' is missing");

    /// <summary>A ruleset's YAML as text.</summary>
    /// <param name="ruleset">One of <see cref="Rulesets" />.</param>
    public static string ReadYaml(string ruleset)
    {
        using StreamReader reader = new(OpenYaml(ruleset));
        return reader.ReadToEnd();
    }

    /// <summary>The shipped rulesets, parsed. Throws when one does not load.</summary>
    public static IReadOnlyList<RulesetDoc> LoadRulesets()
    {
        RuleConfigLoadResult read = YamlConfigLoader.LoadDocuments([.. Rulesets.Select(r => (r + ".rules.yaml", ReadYaml(r)))]);
        return read.Success && read.Rulesets.Count == Rulesets.Count
            ? read.Rulesets
            : throw new InvalidOperationException("the grenade rulesets do not load: " + string.Join("; ", read.Errors));
    }

    /// <summary>The tables as a pass reads them off its visit's run.</summary>
    /// <param name="table">The pass context's table reader.</param>
    /// <param name="serverStartTick">The demo's server start tick, which puts a release's server tick on the frame clock.</param>
    public static GrenadeTableRows Read(Func<string, IReadOnlyList<FactRow>> table, int serverStartTick)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Read(name => table(name).Select(r => new GrenadeTableRow(Cells(r.Dimensions), Cells(r.Values))), serverStartTick);
    }

    /// <summary>The tables off a run made outside a visit, a held parse's or a test's.</summary>
    /// <param name="run">The run; its build must hold the three rulesets.</param>
    /// <param name="serverStartTick">The demo's server start tick.</param>
    public static GrenadeTableRows Read(AnalysisRun run, int serverStartTick)
    {
        ArgumentNullException.ThrowIfNull(run);
        Dictionary<string, MetricTable> tables = run.ProjectConfiguredOutputs().ToDictionary(t => t.Name, StringComparer.Ordinal);
        return Read(name => tables.TryGetValue(name, out MetricTable? table)
            ? table.Rows.Select(r => new GrenadeTableRow(r.Dimensions, r.Values))
            : throw new InvalidOperationException($"the run recorded no {name} table: "
                                                  + string.Join("; ", run.Build.ExcludedRulesets.SelectMany(e => e.Diagnostics))), serverStartTick);
    }

    /// <summary>Folds the rows of every table, by name.</summary>
    /// <param name="rows">The rows of a table, by its name.</param>
    /// <param name="serverStartTick">The demo's server start tick.</param>
    public static GrenadeTableRows Read(Func<string, IEnumerable<GrenadeTableRow>> rows, int serverStartTick)
    {
        ArgumentNullException.ThrowIfNull(rows);
        List<ProjectileFacts> projectiles = [.. rows(ProjectilesTable).Select(Projectile).OfType<ProjectileFacts>()];
        projectiles.AddRange(rows(LandingsTable).Select(Landing).OfType<ProjectileFacts>());
        projectiles.Sort((a, b) => a.SpawnTick != b.SpawnTick ? a.SpawnTick.CompareTo(b.SpawnTick) : a.EntityIndex.CompareTo(b.EntityIndex));

        List<ReleaseFacts> releases = [.. rows(ReleasesTable).Select(r => Release(r, serverStartTick)).OfType<ReleaseFacts>()];
        releases.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        List<GrenadeDetonation> detonations = [];
        foreach ((string table, string name) in new[]
                 {
                     (HeDetonationsTable, GrenadeEventIndex.HeDetonate), (FlashDetonationsTable, GrenadeEventIndex.FlashDetonate),
                     (SmokeDetonationsTable, GrenadeEventIndex.SmokeDetonate), (DecoyStartsTable, GrenadeEventIndex.DecoyStarted),
                     (InfernoStartsTable, GrenadeEventIndex.InfernoStart)
                 })
        {
            foreach (GrenadeTableRow row in rows(table))
            {
                if (Int(row.Dimensions, "tick") is { } tick && Int(row.Values, "entity") is { } entity && Vector(row.Values, "x", "y", "z") is { } at)
                {
                    detonations.Add(new GrenadeDetonation(name, tick, entity, at));
                }
            }
        }

        return new GrenadeTableRows(projectiles, releases, GrenadeEventIndex.FromDetonations(detonations));
    }

    // A grenade_projectiles row. The thrower reads are null on a row whose created frame read nothing.
    private static ProjectileFacts? Projectile(GrenadeTableRow row)
    {
        IReadOnlyDictionary<string, object?> d = row.Dimensions, v = row.Values;
        if (Int(d, "entity_index") is not { } index || Int(d, "serial") is not { } serial || Str(v, "class_name") is not { } className
            || Int(d, "first_seen_tick") is not { } spawn || Int(d, "removed_tick") is not { } end)
        {
            return null;
        }

        int slot = Int(d, "player_slot") ?? -1;
        ThrowerAtSpawn atSpawn = new(Vector(v, "spawn_x", "spawn_y", "spawn_z"), Bool(v, "spawn_on_ground"), Math.Max(0, Int(d, "team") ?? 0),
            Int(v, "last_jump_tick") ?? -1, Float(v, "coverage") ?? 0);
        // A look-back from before the thrower had a pawn reads 0 on every column; the pawn index tells.
        ThrowerRead? offset = null;
        Vector3? offsetBefore = null;
        if (Int(v, "offset_pawn") is > 0)
        {
            bool held = GrenadeRules.IsGrenadeWeaponClass(Str(v, "offset_weapon"));
            offset = new ThrowerRead(spawn - GrenadeRules.ReleaseToSpawnTicks, Vector(v, "offset_x", "offset_y", "offset_z"),
                Float(v, "offset_pitch"), Float(v, "offset_yaw"), Bool(v, "offset_on_ground"), Bool(v, "offset_ducking"), atSpawn.Team,
                held ? Float(v, "offset_strength") : null, held);
            offsetBefore = Float(v, "offset_before_x") is { } bx && Float(v, "offset_before_y") is { } by ? new Vector3(bx, by, 0) : null;
        }

        return new ProjectileFacts(index, serial, className, slot, spawn, Int(d, "created_tick") ?? spawn, end, Bool(d, "removed") ?? true,
            Vector(v, "initial_x", "initial_y", "initial_z"), Vector(v, "velocity_x", "velocity_y", "velocity_z"),
            Vector(v, "end_x", "end_y", "end_z"), Int(v, "last_moved_tick") ?? spawn, Int(v, "bounces") ?? 0, Bool(v, "is_incendiary"),
            Positive(Int(v, "explode_effect_tick")), Positive(Int(v, "smoke_effect_tick")),
            Vector(v, "smoke_detonation_x", "smoke_detonation_y", "smoke_detonation_z"), atSpawn, offset, offsetBefore);
    }

    // A grenade_landings row of a projectile whose thrower never resolved. The walk adds its spawn fields from the flight.
    private static ProjectileFacts? Landing(GrenadeTableRow row)
    {
        IReadOnlyDictionary<string, object?> d = row.Dimensions, v = row.Values;
        if ((Int(v, "slot") ?? -1) >= 0 || Int(v, "entity") is not { } index || Int(v, "serial") is not { } serial
            || Str(v, "class_name") is not { } className || Int(v, "first_seen_tick") is not { } spawn || Int(d, "tick") is not { } end)
        {
            return null;
        }

        return new ProjectileFacts(index, serial, className, -1, spawn, spawn, end, true, null, null, Vector(v, "x", "y", "z"), end,
            Int(v, "bounces") ?? 0, null, null, null, null, null, null, null);
    }

    // The release sits on the event's own tick, not the frame's: the spawn windows were measured against it.
    private static ReleaseFacts? Release(GrenadeTableRow row, int serverStartTick)
    {
        IReadOnlyDictionary<string, object?> d = row.Dimensions, v = row.Values;
        if (Int(d, "tick") is not { } frameTick || Int(d, "player_slot") is not { } slot || Str(v, "weapon") is not { } weapon
            || GrenadeRules.ProjectileClassOfWeapon(weapon) is not { } projectileClass)
        {
            return null;
        }

        int tick = Int(v, "release_tick") is { } server ? server - serverStartTick : frameTick;
        int team = Int(d, "team") ?? 0;
        if (Int(v, "pawn") is not > 0)
        {
            // No pawn yet: the row's reads are defaults, as a walker that found no pawn read nothing.
            return new ReleaseFacts(slot, tick, projectileClass, weapon, new ThrowerRead(tick, null, null, null, null, null, team, null, false), null);
        }

        bool held = GrenadeRules.IsGrenadeWeaponClass(Str(v, "weapon_class"));
        ThrowerRead pawn = new(tick, Vector(v, "pos_x", "pos_y", "pos_z"), Float(v, "eye_pitch"), Float(v, "eye_yaw"), Bool(v, "on_ground"),
            Bool(v, "ducking"), team, held ? Float(v, "throw_strength") : null, held);
        // The look-back reads 0 from before the slot had a pawn, as offset_pawn does on a projectile row.
        Vector3? before = Int(v, "before_pawn") is > 0 && Float(v, "before_x") is { } bx && Float(v, "before_y") is { } by
            ? new Vector3(bx, by, 0)
            : null;
        return new ReleaseFacts(slot, tick, projectileClass, weapon, pawn, before);
    }

    private static int? Positive(int? value) => value is > 0 ? value : null;

    private static Vector3? Vector(IReadOnlyDictionary<string, object?> row, string x, string y, string z) =>
        Float(row, x) is { } a && Float(row, y) is { } b && Float(row, z) is { } c ? new Vector3(a, b, c) : null;

    private static Dictionary<string, object?> Cells(IReadOnlyDictionary<string, FactValue> cells) =>
        cells.ToDictionary(c => c.Key, c => c.Value.Kind switch
        {
            FactValueKind.Whole => c.Value.AsInteger(),
            FactValueKind.Number => c.Value.AsNumber(),
            FactValueKind.Boolean => c.Value.AsBoolean(),
            FactValueKind.Text => (object?)c.Value.AsText(),
            _ => null
        }, StringComparer.Ordinal);

    private static int? Int(IReadOnlyDictionary<string, object?> row, string column) =>
        row.GetValueOrDefault(column) switch
        {
            null => null,
            bool b => b ? 1 : 0,
            double n => double.IsNaN(n) ? null : (int)n,
            float n => float.IsNaN(n) ? null : (int)n,
            string s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : null,
            IConvertible c => c.ToInt32(CultureInfo.InvariantCulture),
            _ => null
        };

    private static float? Float(IReadOnlyDictionary<string, object?> row, string column) =>
        row.GetValueOrDefault(column) switch
        {
            null => null,
            float f => float.IsNaN(f) ? null : f,
            double d => double.IsNaN(d) ? null : (float)d,
            bool b => b ? 1 : 0,
            IConvertible c => c.ToSingle(CultureInfo.InvariantCulture),
            _ => null
        };

    private static bool? Bool(IReadOnlyDictionary<string, object?> row, string column) =>
        row.GetValueOrDefault(column) switch
        {
            null => null,
            bool b => b,
            string s => bool.TryParse(s, out bool parsed) ? parsed : null,
            IConvertible c => c.ToDouble(CultureInfo.InvariantCulture) != 0,
            _ => null
        };

    private static string? Str(IReadOnlyDictionary<string, object?> row, string column) =>
        row.GetValueOrDefault(column) switch
        {
            null => null,
            string s => s,
            var other => Convert.ToString(other, CultureInfo.InvariantCulture)
        };
}
