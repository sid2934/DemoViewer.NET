#region

using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

/// <summary>
///     The round index's samples as a contributed ruleset: a one-second cadence from each freeze end and a
///     <c>player_event</c> table of every player's position, place, liveness, pawn entity index and side at
///     each step. The round index pass reads the table off the visit's rules run, and
///     <see cref="Samples(IEnumerable{FactRow})" /> turns its rows into the samples <see cref="RoundIndexBuilder" />
///     folds.
/// </summary>
public static class RoundIndexTable
{
    /// <summary>The extension's own name for the ruleset.</summary>
    public const string RulesetName = "roundindex_positions";

    /// <summary>The table the ruleset declares.</summary>
    public const string TableName = "roundindex_positions";

    private const string ResourceName = "DemoViewer.NET.Extensions.StratBook.Rulesets.roundindex_positions.rules.yaml";

    /// <summary>The ruleset's qualified id, as its YAML spells it.</summary>
    public static string RulesetId { get; } = RulesetContribution.QualifiedId(StratBookPack.PackId, RulesetName);

    /// <summary>The ruleset's YAML.</summary>
    public static Stream OpenYaml() =>
        typeof(RoundIndexTable).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException($"the embedded ruleset '{ResourceName}' is missing");

    /// <summary>The ruleset's YAML as text.</summary>
    public static string ReadYaml()
    {
        using StreamReader reader = new(OpenYaml());
        return reader.ReadToEnd();
    }

    /// <summary>What the rows mean: the shipped YAML and the engine build, hashed. The index fingerprint carries it.</summary>
    public static string Identity { get; } = ComputeIdentity();

    /// <summary>The shipped ruleset, parsed. Throws when it does not load.</summary>
    public static RulesetDoc LoadRuleset()
    {
        RuleConfigLoadResult read = YamlConfigLoader.LoadDocuments([(RulesetName + ".rules.yaml", ReadYaml())]);
        return read.Success && read.Rulesets.Count == 1
            ? read.Rulesets[0]
            : throw new InvalidOperationException("the round index ruleset does not load: " + string.Join("; ", read.Errors));
    }

    /// <summary>
    ///     The samples of a held parse: the shipped ruleset run over it alone. For a caller with no visit; a pass
    ///     reads the table off the visit's run.
    /// </summary>
    /// <param name="parsed">The parse.</param>
    public static List<PositionSample> Samples(ParsedDemo parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        AnalysisRun run = DemoAnalysis.Run(parsed, [LoadRuleset()], new AnalysisOptions { CaptureSnapshots = false });
        MetricTable table = run.ProjectConfiguredOutputs().SingleOrDefault(t => t.Name == TableName)
                            ?? throw new InvalidOperationException("the round index ruleset recorded no table: "
                                                                   + string.Join("; ", run.Build.ExcludedRulesets.SelectMany(e => e.Diagnostics)));
        return Samples(table.Rows.Select(r => (r.Dimensions, r.Values)));
    }

    /// <summary>The table's rows, as <see cref="IPassContext.Table" /> reads them, as fold samples.</summary>
    /// <param name="rows">The rows.</param>
    public static List<PositionSample> Samples(IEnumerable<FactRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return Samples(rows.Select(r => ((IReadOnlyDictionary<string, object?>)Cells(r.Dimensions), (IReadOnlyDictionary<string, object?>)Cells(r.Values))));
    }

    /// <summary>
    ///     The table's rows as fold samples, in tick order and pawn entity-index order within a tick. A row whose
    ///     position did not read is no sample, the collector's rule.
    /// </summary>
    /// <param name="rows">The rows: dimensions <c>tick</c> and <c>player_slot</c>, the ruleset's columns as values.</param>
    public static List<PositionSample> Samples(IEnumerable<(IReadOnlyDictionary<string, object?> Dimensions,
        IReadOnlyDictionary<string, object?> Values)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        List<(int Tick, int Index, int Order, PositionSample Sample)> keyed = [];
        foreach ((IReadOnlyDictionary<string, object?> dimensions, IReadOnlyDictionary<string, object?> values) in rows)
        {
            if (Int(dimensions, "tick") is not { } tick || Int(dimensions, "player_slot") is not { } slot
                || Float(values, "pos_x") is not { } x || Float(values, "pos_y") is not { } y || Float(values, "pos_z") is not { } z)
            {
                continue;
            }

            PositionSample sample = new(0, tick, slot, new Vector3(x, y, z), values.GetValueOrDefault("place") as string,
                Int(values, "side") ?? 0, Int(values, "is_alive") is > 0 || values.GetValueOrDefault("is_alive") is true);
            keyed.Add((tick, Int(values, "entity_index") ?? int.MaxValue, keyed.Count, sample));
        }

        // The fold sums floats in arrival order, which is entity-index order within a frame.
        keyed.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick)
            : a.Index != b.Index ? a.Index.CompareTo(b.Index) : a.Order.CompareTo(b.Order));
        return [.. keyed.Select(k => k.Sample)];
    }

    private static Dictionary<string, object?> Cells(IReadOnlyDictionary<string, FactValue> cells) =>
        cells.ToDictionary(c => c.Key, c => c.Value.Kind switch
        {
            FactValueKind.Whole => c.Value.AsInteger(),
            FactValueKind.Number => c.Value.AsNumber(),
            FactValueKind.Boolean => c.Value.AsBoolean(),
            FactValueKind.Text => (object?)c.Value.AsText(),
            _ => null
        }, StringComparer.Ordinal);

    private static string ComputeIdentity()
    {
        // The informational version: NBGV keeps AssemblyVersion at major.minor.0.0 for every patch and prerelease.
        Assembly assembly = typeof(DemoAnalysis).Assembly;
        string engine = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?? assembly.GetName().Version?.ToString() ?? "";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(ReadYaml() + "\n" + engine));
        return Convert.ToHexStringLower(hash, 0, 16);
    }

    private static int? Int(IReadOnlyDictionary<string, object?> row, string column) =>
        row.GetValueOrDefault(column) switch
        {
            null => null,
            bool b => b ? 1 : 0,
            string s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : null,
            IConvertible c => c.ToInt32(CultureInfo.InvariantCulture),
            _ => null
        };

    private static float? Float(IReadOnlyDictionary<string, object?> row, string column) =>
        row.GetValueOrDefault(column) switch
        {
            null => null,
            float f => f,
            double d => (float)d,
            IConvertible c => c.ToSingle(CultureInfo.InvariantCulture),
            _ => null
        };
}
