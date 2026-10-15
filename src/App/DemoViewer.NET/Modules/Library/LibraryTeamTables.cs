#region

using System.Globalization;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.Modules.Library;

/// <summary>The final scoreboard state the library card and the demo cache store.</summary>
/// <param name="Ct">CT-side (team 3) final score, or null.</param>
/// <param name="T">T-side (team 2) final score, or null.</param>
/// <param name="CtClan">CT-side clan name, or null.</param>
/// <param name="TClan">T-side clan name, or null.</param>
/// <param name="CoachSlots">Slots of the players who coached a side.</param>
public sealed record LibraryTeamState(int? Ct, int? T, string? CtClan, string? TClan, HashSet<int> CoachSlots)
{
    /// <summary>No score, no clans, no coach.</summary>
    public static LibraryTeamState Empty() => new(null, null, null, null, []);

    /// <summary>
    ///     The state both library reads return. Scores are both or nothing, and nothing when they sum to zero
    ///     (warmup only); a score the match-end panel closed is completed (<see cref="LibraryFinalScore" />).
    ///     A demo with no score marks no coach: the table samples coaches at round close, which a warmup-only
    ///     demo never reaches, and the replay must read the same.
    /// </summary>
    public static LibraryTeamState Of(int? ct, int? t, string? ctClan, string? tClan, HashSet<int> coachSlots, bool matchEnded)
    {
        ArgumentNullException.ThrowIfNull(coachSlots);
        if (ct is not { } c || t is not { } tt || c + tt == 0)
        {
            return Empty();
        }

        (int fullCt, int fullT) = LibraryFinalScore.Complete(c, tt, matchEnded);
        return new LibraryTeamState(fullCt, fullT, ctClan, tClan, coachSlots);
    }
}

/// <summary>
///     Completes a final score one round short of a result. Tournament GOTV can drop the entity snapshot of
///     the match's last tick, where the deciding point lands, so <c>CCSTeam.m_iScore</c> ends on 12:11 for a
///     13:11; a later full packet carries the point, so a read that primed from one has it already.
/// </summary>
public static class LibraryFinalScore
{
    /// <summary>
    ///     True for a finished MR12 result: 13 with the loser at 11 or below, or an MR3 overtime's 16, 19, ...
    ///     with the loser 2 to 4 behind.
    /// </summary>
    public static bool IsResult(int winner, int loser) =>
        (winner == 13 && loser is >= 0 and <= 11)
        || (winner >= 16 && (winner - 13) % 3 == 0 && loser >= winner - 4 && loser <= winner - 2);

    /// <summary>
    ///     The score with the leader's deciding point added when the match ended, the score is not a result
    ///     and one more round for the leader makes it one. The match winner wins the last round, so the
    ///     missing point is the leader's. Anything else comes back as it was.
    /// </summary>
    public static (int Ct, int T) Complete(int ct, int t, bool matchEnded)
    {
        if (!matchEnded || ct == t || IsResult(Math.Max(ct, t), Math.Min(ct, t))
            || !IsResult(Math.Max(ct, t) + 1, Math.Min(ct, t)))
        {
            return (ct, t);
        }

        return ct > t ? (ct + 1, t) : (ct, t + 1);
    }
}

/// <summary>
///     The library's two rulesets, <c>library_teams</c> and <c>library_coaches</c>, and the read of their
///     tables off a rules run. Both are core stamped rulesets whose tables every merged run records.
/// </summary>
public static class LibraryTeamTables
{
    public const string TeamsRulesetId = "library_teams";
    public const string CoachesRulesetId = "library_coaches";
    public const string TeamsTable = "library_teams";
    public const string CoachesTable = "library_coaches";

    private const int SideT = 2;
    private const int SideCt = 3;

    /// <summary>The two ruleset ids.</summary>
    public static IReadOnlyList<string> RulesetIds { get; } = [TeamsRulesetId, CoachesRulesetId];

    /// <summary>The two table names.</summary>
    public static IReadOnlySet<string> Tables { get; } = new HashSet<string>(StringComparer.Ordinal) { TeamsTable, CoachesTable };

    /// <summary>True for either library ruleset.</summary>
    public static bool IsLibraryRuleset(string rulesetId) =>
        string.Equals(rulesetId, TeamsRulesetId, StringComparison.Ordinal) || string.Equals(rulesetId, CoachesRulesetId, StringComparison.Ordinal);

    /// <summary>
    ///     The state out of <paramref name="run" />'s two tables, through <see cref="LibraryTeamState.Of" />; a
    ///     blank clan is null. A run without the tables reads as <see cref="LibraryTeamState.Empty" />.
    /// </summary>
    /// <param name="run">A run that recorded the two tables, or null.</param>
    /// <param name="players">The demo's roster, which the coach rows join to.</param>
    public static LibraryTeamState Read(AnalysisRun? run, IEnumerable<PlayerInfo> players)
    {
        ArgumentNullException.ThrowIfNull(players);
        if (run?.Build.Outputs is not { } outputs)
        {
            return LibraryTeamState.Empty();
        }

        List<OutputDef> mine = [.. outputs.Where(o => Tables.Contains(o.Id))];
        if (mine.Count == 0)
        {
            return LibraryTeamState.Empty();
        }

        IReadOnlyList<MetricTable> tables = (run with { Build = run.Build with { Outputs = mine } }).ProjectConfiguredOutputs();
        MetricTable? teams = tables.FirstOrDefault(t => string.Equals(t.Name, TeamsTable, StringComparison.Ordinal));
        MetricTable? coaches = tables.FirstOrDefault(t => string.Equals(t.Name, CoachesTable, StringComparison.Ordinal));

        int? ct = null, t = null;
        string? ctClan = null, tClan = null;
        bool matchEnded = false;
        foreach (MetricRow row in teams?.Rows ?? [])
        {
            matchEnded |= ToInt(row.Values.GetValueOrDefault("match_ended")) > 0;
            int? score = ToInt(row.Values.GetValueOrDefault("score"));
            string? clan = row.Values.GetValueOrDefault("clan_name") as string is { Length: > 0 } c ? c : null;
            switch (ToInt(row.Dimensions.GetValueOrDefault("side")))
            {
                case SideT:
                    t = score;
                    tClan = clan;
                    break;
                case SideCt:
                    ct = score;
                    ctClan = clan;
                    break;
            }
        }

        return LibraryTeamState.Of(ct, t, ctClan, tClan, CoachSlots(coaches, players), matchEnded);
    }

    /// <summary>
    ///     Evaluates the two library rulesets of <paramref name="rulesets" /> alone over a held parse and reads
    ///     them. For tests and tools; the library itself reads the visit's merged run.
    /// </summary>
    public static LibraryTeamState Evaluate(ParsedDemo parsed, IReadOnlyList<RulesetDoc> rulesets)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(rulesets);
        List<RulesetDoc> mine = [.. rulesets.Where(r => r.Enabled && IsLibraryRuleset(r.Id))];
        if (mine.Count == 0 || parsed.Frames.Count == 0)
        {
            return LibraryTeamState.Empty();
        }

        BuildResult build = DemoAnalysis.Build(parsed, mine);
        return Read(DemoAnalysis.Evaluate(parsed, build, new AnalysisOptions { CaptureSnapshots = false }), parsed.Players.Values);
    }

    // A coach row joins its player by SteamID64 and name; a row with no SteamID64 joins by slot.
    private static HashSet<int> CoachSlots(MetricTable? coaches, IEnumerable<PlayerInfo> players)
    {
        HashSet<int> slots = [];
        if (coaches is null)
        {
            return slots;
        }

        List<PlayerInfo> roster = [.. players];
        foreach (MetricRow row in coaches.Rows)
        {
            if (ToInt(row.Values.GetValueOrDefault("coaching_team")) is null or 0)
            {
                continue;
            }

            string? steamId = row.Dimensions.GetValueOrDefault("steam_id")?.ToString();
            string? name = row.Dimensions.GetValueOrDefault("player_name")?.ToString();
            int? slot = ToInt(row.Dimensions.GetValueOrDefault("player_slot"));
            foreach (PlayerInfo p in roster)
            {
                bool same = steamId is { Length: > 0 }
                    ? string.Equals(p.SteamId64.ToString(CultureInfo.InvariantCulture), steamId, StringComparison.Ordinal)
                      && string.Equals(p.Name, name, StringComparison.Ordinal)
                    : p.Slot == slot;
                if (same)
                {
                    slots.Add(p.Slot);
                }
            }
        }

        return slots;
    }

    private static int? ToInt(object? v) => v switch
    {
        null => null,
        int i => i,
        long l => (int)l,
        short s => s,
        byte b => b,
        uint u => (int)u,
        double d => (int)d,
        float f => (int)f,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
        _ => null
    };
}
