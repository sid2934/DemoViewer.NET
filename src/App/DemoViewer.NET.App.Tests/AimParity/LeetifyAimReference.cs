#region

using System.Globalization;
using System.Text.Json;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests.AimParity;

/// <summary>
///     Leetify's own per-player numbers for one benchmark demo, read from the
///     <c>demos/benchmarks/&lt;demo-id&gt;.leetify.json</c> cache that sits beside the demo.
///     <para>
///         This is the only external reference the aim board has, and it does not mean what a
///         schema dump would suggest: the payload declares <c>recoilShots</c> and
///         <c>recoilShotsHit</c>, the two fields a spray-control comparison would need, and both
///         are <c>null</c> in every row of all five demos. Schema-present is not data-populated,
///         and a loader that mapped a missing field to zero would hand a spray oracle a perfect
///         score to agree with. Every field below is therefore nullable and <c>null</c> means
///         "Leetify did not report this", never zero. <c>shotsHitFoeHead</c> is the same shape:
///         present, and zero in every row we hold, which is why it is not used as a head-hit
///         numerator anywhere.
///     </para>
///     <para>
///         <b>Names, not ids, are the join key.</b> The rest of the parity fixtures key players by
///         display name (see <c>tests/fixtures/README.md</c>), so this does too, and
///         <see cref="LeetifyAimRow.Steam64Id" /> is carried alongside only so a mismatch can be
///         diagnosed rather than guessed at.
///     </para>
/// </summary>
public sealed class LeetifyAimReference
{
    private LeetifyAimReference(string demoId, string? mapName, IReadOnlyDictionary<string, LeetifyAimRow> byName)
    {
        DemoId = demoId;
        MapName = mapName;
        Players = byName;
    }

    /// <summary>The fixture id, which is the demo filename without its <c>.dem</c> extension.</summary>
    public string DemoId { get; }

    /// <summary>Map Leetify recorded for this match, or <c>null</c> when the payload omits it.</summary>
    public string? MapName { get; }

    /// <summary>Every player row, keyed by the display name Leetify reported.</summary>
    public IReadOnlyDictionary<string, LeetifyAimRow> Players { get; }

    /// <summary>
    ///     The fields this reference declares but never populates, on any row of any demo in the
    ///     benchmark set. Named here rather than left as a comment because a future payload could
    ///     start carrying them, and the assertion that keeps this list honest is what would notice.
    /// </summary>
    public static IReadOnlyList<string> KnownUnpopulatedFields { get; } =
        ["recoilShots", "recoilShotsHit", "shotsHitFoeHead"];

    /// <summary>
    ///     Loads the reference for one demo id, or returns <c>null</c> when no
    ///     <c>&lt;demo-id&gt;.leetify.json</c> is committed for it. Absence is a normal state (a
    ///     fixture directory can exist for a demo Leetify never analysed), so callers decide whether
    ///     it is a skip or a failure.
    /// </summary>
    /// <param name="demoId">The fixture directory name, which is also the demo's bare filename.</param>
    /// <returns>The parsed reference, or <c>null</c>.</returns>
    public static LeetifyAimReference? TryLoad(string demoId)
    {
        string? path = PathFor(demoId);
        if (path is null)
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("playerStats", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        Dictionary<string, LeetifyAimRow> byName = new(StringComparer.Ordinal);
        foreach (JsonElement row in rows.EnumerateArray())
        {
            string? name = Text(row, "name");
            if (string.IsNullOrEmpty(name))
            {
                continue; // A nameless row cannot be joined to ours, and inventing a key would hide that.
            }

            byName[name] = new LeetifyAimRow(
                name,
                Text(row, "steam64Id"),
                Number(row, "shotsFired"),
                Number(row, "shotsHitFoe"),
                Number(row, "shotsHitFoeHead"),
                Number(row, "accuracy"),
                Number(row, "accuracyHead"),
                Number(row, "shotsFiredEnemySpotted"),
                Number(row, "shotsHitEnemySpotted"),
                Number(row, "accuracyEnemySpotted"),
                Number(row, "sprayAccuracy"),
                Number(row, "preaim"),
                Number(row, "reactionTime"),
                Number(row, "hsp"),
                Number(row, "counterStrafingShotsAll"),
                Number(row, "counterStrafingShotsGood"),
                Number(row, "counterStrafingShotsBad"),
                Number(row, "counterStrafingShotsGoodRatio"),
                Number(row, "recoilShots"),
                Number(row, "recoilShotsHit"));
        }

        return new LeetifyAimReference(demoId, Text(root, "mapName"), byName);
    }

    /// <summary>
    ///     Every demo id with a committed Leetify payload, sorted. Empty when the benchmark
    ///     directory is missing, which is what a checkout without the corpus looks like.
    /// </summary>
    /// <returns>The demo ids.</returns>
    public static IReadOnlyList<string> AllDemoIds()
    {
        string? dir = BenchmarkDirectory();
        if (dir is null)
        {
            return [];
        }

        const string Suffix = ".leetify.json";
        return Directory.EnumerateFiles(dir, "*" + Suffix)
            .Select(Path.GetFileName)
            .Where(file => !string.IsNullOrEmpty(file))
            .Select(file => file![..^Suffix.Length])
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The path of one demo's Leetify payload, or <c>null</c> when it is not committed.</summary>
    /// <param name="demoId">The fixture directory name.</param>
    /// <returns>The path, or <c>null</c>.</returns>
    public static string? PathFor(string demoId)
    {
        string? dir = BenchmarkDirectory();
        if (dir is null)
        {
            return null;
        }

        string path = Path.Combine(dir, demoId + ".leetify.json");
        return File.Exists(path) ? path : null;
    }

    private static string? BenchmarkDirectory()
    {
        if (DemoTestHelper.FindRepoRoot() is not { } root)
        {
            return null;
        }

        string dir = Path.Combine(root, "demos", "benchmarks");
        return Directory.Exists(dir) ? dir : null;
    }

    // A JSON null and an absent property are the same statement here ("not reported"), and both
    // must stay distinguishable from 0. A numeric field arriving as a string is coerced rather than
    // dropped: this is a cached API response, not a schema this repository controls.
    private static double? Number(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => null
        };
    }

    private static string? Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
///     One player's aim-relevant Leetify row. Every value is nullable, and <c>null</c> means the
///     reference did not report it.
/// </summary>
/// <param name="Name">Display name, the join key against our own output.</param>
/// <param name="Steam64Id">Steam id as text, carried for diagnosing a name mismatch.</param>
/// <param name="ShotsFired">Bullets fired.</param>
/// <param name="ShotsHitFoe">Bullets that hit an enemy.</param>
/// <param name="ShotsHitFoeHead">
///     Head hits. Zero in every row of every benchmark demo, so it is carried for the
///     unpopulated-field assertion rather than used as a numerator.
/// </param>
/// <param name="Accuracy">
///     <c>shotsHitFoe / shotsFired</c> as a 0..1 ratio. That identity reproduces on every row we
///     hold, so this column is a genuine cross-check rather than a black box.
/// </param>
/// <param name="AccuracyHead">
///     Head-hit share as a 0..1 ratio. Its denominator is NOT any published column: fitted per row
///     against every candidate, it lands slightly BELOW <c>shotsHitFoe</c> (46 hits against a
///     denominator of 43, 45 against 41, 89 against 87), which is the signature of a hit count
///     collapsed to distinct bullets where ours counts damage instances. Comparable in direction,
///     not in value.
/// </param>
/// <param name="ShotsFiredEnemySpotted">Bullets fired after an enemy became visible.</param>
/// <param name="ShotsHitEnemySpotted">Bullets that hit, after an enemy became visible.</param>
/// <param name="AccuracyEnemySpotted">The ratio of the previous two, as 0..1.</param>
/// <param name="SprayAccuracy">Accuracy inside a spray, as 0..1.</param>
/// <param name="Preaim">Mean degrees off target at contact. Lower is better.</param>
/// <param name="ReactionTime">
///     Time to damage, in SECONDS. We publish no counterpart: the time-to-X ladder is deferred in
///     <c>rules/aim_rating.rules.yaml</c> because the spot and the shot sit on different tick
///     clocks. Carried so the gap shows up in the report rather than being silently absent.
/// </param>
/// <param name="Hsp">Headshot share of kills, as 0..1.</param>
/// <param name="CounterStrafingShotsAll">The counter-strafing DENOMINATOR: shots that counted as an attempt.</param>
/// <param name="CounterStrafingShotsGood">Attempts that were clean.</param>
/// <param name="CounterStrafingShotsBad">Attempts that were still moving.</param>
/// <param name="CounterStrafingShotsGoodRatio">Good over all, as 0..1.</param>
/// <param name="RecoilShots">Null in every row of every benchmark demo. See the class summary.</param>
/// <param name="RecoilShotsHit">Null in every row of every benchmark demo. See the class summary.</param>
public sealed record LeetifyAimRow(
    string Name,
    string? Steam64Id,
    double? ShotsFired,
    double? ShotsHitFoe,
    double? ShotsHitFoeHead,
    double? Accuracy,
    double? AccuracyHead,
    double? ShotsFiredEnemySpotted,
    double? ShotsHitEnemySpotted,
    double? AccuracyEnemySpotted,
    double? SprayAccuracy,
    double? Preaim,
    double? ReactionTime,
    double? Hsp,
    double? CounterStrafingShotsAll,
    double? CounterStrafingShotsGood,
    double? CounterStrafingShotsBad,
    double? CounterStrafingShotsGoodRatio,
    double? RecoilShots,
    double? RecoilShotsHit)
{
    /// <summary>Reads one field by its Leetify wire name, so a stat table can name it as data.</summary>
    /// <param name="field">The Leetify property name.</param>
    /// <returns>The value, or <c>null</c> when the reference does not report it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The name is not one this row carries.</exception>
    public double? Read(string field) => field switch
    {
        "shotsFired" => ShotsFired,
        "shotsHitFoe" => ShotsHitFoe,
        "shotsHitFoeHead" => ShotsHitFoeHead,
        "accuracy" => Accuracy,
        "accuracyHead" => AccuracyHead,
        "shotsFiredEnemySpotted" => ShotsFiredEnemySpotted,
        "shotsHitEnemySpotted" => ShotsHitEnemySpotted,
        "accuracyEnemySpotted" => AccuracyEnemySpotted,
        "sprayAccuracy" => SprayAccuracy,
        "preaim" => Preaim,
        "reactionTime" => ReactionTime,
        "hsp" => Hsp,
        "counterStrafingShotsAll" => CounterStrafingShotsAll,
        "counterStrafingShotsGood" => CounterStrafingShotsGood,
        "counterStrafingShotsBad" => CounterStrafingShotsBad,
        "counterStrafingShotsGoodRatio" => CounterStrafingShotsGoodRatio,
        "recoilShots" => RecoilShots,
        "recoilShotsHit" => RecoilShotsHit,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "not a field this row carries")
    };
}
