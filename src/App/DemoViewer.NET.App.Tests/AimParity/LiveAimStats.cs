#region

using System.Security.Cryptography;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests.AimParity;

/// <summary>
///     Runs <c>rules/aim_rating.rules.yaml</c> over one demo and hands back the per-player numbers,
///     read out of the materialized graph by qualified stat id.
///     <para>
///         <b>Derived, never stored.</b> There is no committed <c>ours</c> for the aim board, for
///         the reason <c>tests/fixtures/README.md</c> gives: a parity test that loads both sides
///         off disk compares two files that agree by construction, never opens a demo, and reports
///         green while asserting nothing. The cost of deriving is that a demo which is not on this
///         machine now SKIPS, which is the honest answer.
///     </para>
///     <para>
///         <b>Read by rule id, not by column label.</b> The <c>show:</c> block names a subset:
///         <c>cs_clean</c> is a numerator and never reaches the scoreboard, and it is exactly the
///         column a counter-strafing comparison needs. Reading <c>NodesByRuleId</c> also survives a
///         label being renamed for the UI, which a display string would not.
///     </para>
///     <para>
///         <b>Visibility is a capability, not a default.</b> Tier 3 needs baked map geometry, and
///         without it every Tier 3 column reads 0.0 rather than blank, which looks like a terrible
///         score instead of an absent measurement. <see cref="AimRunResult.VisibilityAvailable" />
///         is how a caller tells those apart. Every map the pack ships now carries a bake, so on the
///         benchmark set the flag reads true; it stays a flag rather than an assumption because a
///         demo on a map outside that pack, or a checkout with no <c>assets/</c>, still has none.
///     </para>
/// </summary>
public static class LiveAimStats
{
    /// <summary>The ruleset file this harness evaluates, relative to the repository root.</summary>
    public const string RulesetRelativePath = "rules/aim_rating.rules.yaml";

    /// <summary>
    ///     The benchmark demo ids the aim harness runs over: every fixture directory under
    ///     <c>tests/fixtures/</c> that carries a curated <c>expected.golden.json</c>. The fixture,
    ///     not the demo, is what defines the set, because the demos themselves are gitignored and
    ///     enumerating what happens to be on this machine would make the case list vary by checkout.
    /// </summary>
    /// <returns>The demo ids, sorted, or empty when the fixture tree is absent.</returns>
    public static IReadOnlyList<string> BenchmarkDemoIds()
    {
        if (DemoTestHelper.FindRepoRoot() is not { } root)
        {
            return [];
        }

        string fixtures = Path.Combine(root, "tests", "fixtures");
        if (!Directory.Exists(fixtures))
        {
            return [];
        }

        return Directory.EnumerateDirectories(fixtures)
            .Where(dir => File.Exists(Path.Combine(dir, "expected.golden.json")))
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    ///     Parses the demo for <paramref name="demoId" />, evaluates the aim ruleset over it and
    ///     returns the per-player numbers.
    /// </summary>
    /// <param name="demoId">The fixture id, which is the demo filename without <c>.dem</c>.</param>
    /// <returns>The run.</returns>
    /// <exception cref="SkipTestException">The demo is not on this machine.</exception>
    public static AimRunResult Derive(string demoId)
    {
        ArgumentNullException.ThrowIfNull(demoId);

        string demoFileName = demoId + ".dem";
        string demoPath = DemoTestHelper.RequireDemo(demoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(demoPath);
        return Derive(demoFileName, demoPath, demo);
    }

    /// <summary>
    ///     Evaluates the aim ruleset over an already-parsed demo. Split out so a caller holding a
    ///     demo for another reason (the counter-strafe oracle runs its own fold over the same frames)
    ///     does not pay for a second multi-gigabyte parse.
    /// </summary>
    /// <param name="demoFileName">The bare filename, stamped into the pinned document.</param>
    /// <param name="demoPath">Full path, hashed so a pin records which bytes it describes.</param>
    /// <param name="demo">The parsed demo.</param>
    /// <returns>The run.</returns>
    public static AimRunResult Derive(string demoFileName, string demoPath, ParsedDemo demo)
    {
        ArgumentNullException.ThrowIfNull(demo);

        VisibilityEngine? vision = TryLoadCollision(demo.MapName);
        AnalysisOptions options = new()
        {
            VisibilityEngine = vision
        };

        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(
            [(RulesetRelativePath, File.ReadAllText(RulesetPath()))]);
        if (!loaded.Success)
        {
            throw new RuleConfigException(loaded.Errors);
        }

        // Build takes the options too: the visibility engine is wired at BUILD time (it is what
        // lets the builder construct the enemy_spotted scanner at all), so passing it only to
        // Evaluate would leave every Tier 3 column silently empty.
        BuildResult build = DemoAnalysis.Build(demo, loaded.Rulesets, options);
        EvaluationResult result;
        try
        {
            result = DemoAnalysis.Evaluate(demo, build, options).Snapshots
                     ?? throw new InvalidOperationException("evaluation produced no snapshots");
        }
        catch (InvalidOperationException ex) when (AimSchemaDriftException.LooksLikeDrift(ex))
        {
            // The provider schema guard runs at prime time and throws, so ONE drifted field path
            // takes down the whole aim board rather than emptying its own column. Retyped here so a
            // caller can tell "this source cannot feed the aim ruleset" apart from "the aim ruleset
            // is wrong", which are different bugs owned by different repositories.
            throw AimSchemaDriftException.For(demoFileName, ex);
        }

        Dictionary<string, AimPlayerRow> players = new(StringComparer.Ordinal);
        foreach (PerPlayerNodeTemplate.MaterializedPlayer player in result.MaterializedPlayers)
        {
            if (string.IsNullOrEmpty(player.PlayerName))
            {
                continue; // Nameless rows cannot be joined to the pinned fixture.
            }

            Dictionary<string, double?> stats = new(StringComparer.Ordinal);
            foreach (AimStatDefinition stat in AimStatCatalogue.Stats)
            {
                stats[stat.Canonical] = ReadStat(player, stat.RuleId);
            }

            int team = demo.Players.TryGetValue(player.PlayerSlot, out PlayerInfo? info) ? info.Team : 0;

            // A slot can materialize more than once across a match (a reconnect gets a fresh
            // template). Keep the richest row rather than the last, on the same reasoning the
            // player-stats parity path uses: a context-only phantom row has almost every cell null
            // and would otherwise overwrite the real one.
            AimPlayerRow row = new(player.PlayerName, player.PlayerSlot, team, stats);
            if (!players.TryGetValue(row.Name, out AimPlayerRow? existing) || row.Populated > existing.Populated)
            {
                players[row.Name] = row;
            }
        }

        return new AimRunResult(
            demoFileName,
            Sha256OfFile(demoPath),
            demo.MapName,
            demo.TickRate,
            vision is not null,
            players);
    }

    /// <summary>
    ///     Lowercase hex SHA-256 of a demo file, matching the <c>demo_sha256</c> a fixture records.
    ///     A reference is valid only for the exact bytes it was pinned from: two files can carry the
    ///     same match and still differ, and comparing against a fixture pinned from a different
    ///     recording produces divergences that read exactly like engine regressions.
    /// </summary>
    /// <param name="path">The demo file.</param>
    /// <returns>The hash.</returns>
    public static string Sha256OfFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    ///     Locates a map's baked collision soup, compressed or plain, or <c>null</c> when neither
    ///     tree carries one. A map without a bake is a normal state and not an error.
    ///     <para>
    ///         Delegates to <see cref="CollisionSoup.Find" /> rather than composing the path, for
    ///         the reason the app's own resolver exists: the shipped pack carries
    ///         <c>collision.tris.gz</c>, so a probe that asks for the uncompressed name alone comes
    ///         back empty on every map, the run proceeds with no visibility engine, and every Tier 3
    ///         column reads a confident 0.0 that is indistinguishable from a terrible score.
    ///     </para>
    /// </summary>
    /// <param name="mapName">The map, for example <c>de_nuke</c>.</param>
    /// <returns>The path, or <c>null</c>.</returns>
    public static string? FindCollisionBake(string? mapName) => CollisionSoup.Find(mapName);

    private static VisibilityEngine? TryLoadCollision(string? mapName) =>
        FindCollisionBake(mapName) is { } path ? CollisionSoup.Load(path) : null;

    private static string RulesetPath()
    {
        string root = DemoTestHelper.FindRepoRoot()
                      ?? throw new SkipTestException(
                          "Repo root not located from the test assembly (looked for the solution file).");

        string path = Path.Combine(root, "rules", "aim_rating.rules.yaml");
        return File.Exists(path)
            ? path
            : throw new InvalidOperationException($"the aim ruleset is missing at {path}");
    }

    // A node that never activated reads null rather than 0: "nobody measured this" and "measured
    // zero" are the same number and opposite facts, and the whole point of carrying the population
    // columns is to keep them apart.
    private static double? ReadStat(PerPlayerNodeTemplate.MaterializedPlayer player, string ruleId)
    {
        if (player.NodesByRuleId is not { } byId || !byId.TryGetValue(ruleId, out StateNode? node))
        {
            return null;
        }

        return node.GetNumericValue();
    }
}

/// <summary>One player's aim numbers from a live run, keyed by canonical stat name.</summary>
/// <param name="Name">Display name, the join key against the pinned fixture.</param>
/// <param name="Slot">Player slot, for diagnostics.</param>
/// <param name="Team">Team number as the demo reports it.</param>
/// <param name="Stats">Canonical stat name to value; <c>null</c> means the node never activated.</param>
public sealed record AimPlayerRow(string Name, int Slot, int Team, IReadOnlyDictionary<string, double?> Stats)
{
    /// <summary>How many stats actually carry a value, used to pick the richest row for a slot.</summary>
    public int Populated => Stats.Count(pair => pair.Value is not null);

    /// <summary>Reads one canonical stat, or <c>null</c> when this row does not carry it.</summary>
    /// <param name="canonical">The canonical stat name.</param>
    /// <returns>The value, or <c>null</c>.</returns>
    public double? Read(string canonical) => Stats.TryGetValue(canonical, out double? value) ? value : null;
}

/// <summary>The outcome of one aim-ruleset run over one demo.</summary>
/// <param name="DemoFileName">The demo's bare filename.</param>
/// <param name="DemoSha256">Hash of the bytes this was derived from.</param>
/// <param name="MapName">The map, which decides whether a collision bake exists.</param>
/// <param name="TickRate">Ticks per second, needed to read a lookback window in seconds.</param>
/// <param name="VisibilityAvailable">
///     Whether baked geometry was found. When false every Tier 3 column is an empty population
///     reading 0.0, and comparing it against a reference would manufacture a divergence.
/// </param>
/// <param name="Players">Per-player rows, keyed by display name.</param>
public sealed record AimRunResult(
    string DemoFileName,
    string DemoSha256,
    string? MapName,
    double TickRate,
    bool VisibilityAvailable,
    IReadOnlyDictionary<string, AimPlayerRow> Players)
{
    /// <summary>Whether a stat's tier is actually supported by this run.</summary>
    /// <param name="tier">The tier to test.</param>
    /// <returns><c>true</c> when the tier can produce a real measurement on this run.</returns>
    public bool Supports(AimTier tier) => tier != AimTier.Visibility || VisibilityAvailable;
}

/// <summary>
///     Raised when a per-player entity provider the aim ruleset needs names a field the demo's
///     schema does not carry.
///     <para>
///         <b>Observed, not hypothetical.</b> Both Valve matchmaking demos at
///         <c>C:/dev/Cs2VideoGenerator/demos/</c> (September 2025 recordings) fail on
///         <c>entity.pawn.punch_pitch</c>, whose path is
///         <c>m_pAimPunchServices.m_predictableBaseAngle</c>. Those pawns carry no
///         <c>m_pAimPunchServices</c> sub-object at all; the aim punch is a QAngle directly on the
///         pawn as <c>m_aimPunchAngle</c>, alongside <c>m_aimPunchAngleVel</c>,
///         <c>m_aimPunchTickBase</c> and <c>m_aimPunchTickFraction</c>.
///     </para>
///     <para>
///         The guard fires during digest precompute, before any column is produced, so the failure
///         is total rather than confined to the two spray-residual columns that read the punch:
///         accuracy, which needs nothing but <c>weapon_fire</c> and <c>player_hurt</c>, goes down
///         with it. That is a capability question the aim board should answer per source (the shape
///         <c>AimCapabilityProbe</c> already uses), and it belongs to the engine package rather
///         than to this harness, which is why this exception reports it rather than working around
///         it.
///     </para>
/// </summary>
public sealed class AimSchemaDriftException : Exception
{
    /// <summary>Creates an empty exception.</summary>
    public AimSchemaDriftException() => DemoFileName = string.Empty;

    /// <summary>Creates the exception with a plain message.</summary>
    /// <param name="message">The message.</param>
    public AimSchemaDriftException(string message)
        : base(message) =>
        DemoFileName = string.Empty;

    /// <summary>Creates the exception with a message and inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The inner exception.</param>
    public AimSchemaDriftException(string message, Exception inner)
        : base(message, inner) =>
        DemoFileName = string.Empty;

    private AimSchemaDriftException(string message, string demoFileName, Exception inner)
        : base(message, inner) =>
        DemoFileName = demoFileName;

    /// <summary>The demo whose schema does not carry a field the ruleset needs.</summary>
    public string DemoFileName { get; }

    /// <summary>Wraps the provider guard's own message, attributed to the demo it fired on.</summary>
    /// <param name="demoFileName">The demo whose schema does not carry the field.</param>
    /// <param name="inner">The provider guard's exception.</param>
    /// <returns>The exception.</returns>
    public static AimSchemaDriftException For(string demoFileName, Exception inner) =>
        new($"'{demoFileName}' cannot feed the aim ruleset: {inner?.Message}", demoFileName, inner!);

    /// <summary>
    ///     Whether an exception is the provider schema guard rather than any other invalid-operation
    ///     failure. Matched on the guard's own wording because it throws a plain
    ///     <see cref="InvalidOperationException" /> with no distinguishing type.
    /// </summary>
    /// <param name="ex">The exception.</param>
    /// <returns><c>true</c> when it is the schema guard.</returns>
    public static bool LooksLikeDrift(Exception ex) =>
        ex?.Message.Contains("does not exist on", StringComparison.Ordinal) == true
        && ex.Message.Contains("provider '", StringComparison.Ordinal);
}
