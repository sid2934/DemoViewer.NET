namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     One of a team's demos as Period Diff counts it (plan.md §3, Period Diff, Phase 5): the map, the
///     resolved winner where the final score is unambiguous, the side-round split, and the roster Team
///     Identity put the team's side on. Built by <see cref="PeriodDiffService.Build" />.
/// </summary>
/// <param name="DemoPath">The demo.</param>
/// <param name="Sha256">The demo's content hash, or null.</param>
/// <param name="Map">The map, or "" when the index has none.</param>
/// <param name="OrderTicks">Team Identity's own order stamp: <c>DemoCacheRecord.ModifiedTicks</c> until a real match date exists.</param>
/// <param name="Side">2 = T, 3 = CT: the team's end-of-demo side.</param>
/// <param name="RosterId">The roster Team Identity matched this side to, or null when the side never joined one.</param>
/// <param name="RosterLabel">The roster's own label when the user set one, else <see cref="RosterId" />; "" without a roster.</param>
/// <param name="StandIn">Overlap 3 or 4 with a member outside the roster's anchor (design §3.3).</param>
/// <param name="Won">True/false when the final score resolves a winner; null on an equal or missing score.</param>
/// <param name="CtRounds">Rounds won on the CT side, match-wide (F12: the grain <c>CtSideWins</c> is cached at).</param>
/// <param name="TRounds">Rounds won on the T side, match-wide.</param>
public sealed record PeriodDiffDemoRow(
    string DemoPath, string? Sha256, string Map, long OrderTicks, int Side,
    string? RosterId, string RosterLabel, bool StandIn, bool? Won, int CtRounds, int TRounds);

/// <summary>One roster's share of a period, most-played first.</summary>
/// <param name="RosterId">The roster.</param>
/// <param name="Label">Its label, or the id when the user never set one.</param>
/// <param name="Demos">Demos of the period this roster played.</param>
public sealed record PeriodDiffRosterRow(string RosterId, string Label, int Demos);

/// <summary>
///     One side of a Period Diff: the last (or previous) <see cref="PeriodDiffSet.WindowSize" /> demos of
///     the team, newest first within the period. Everything here is derived from
///     <see cref="Demos" />; nothing is looked up again.
/// </summary>
public sealed class PeriodDiffPeriod
{
    /// <summary>"last" or "previous": the word the section uses for this side of the diff.</summary>
    public required string Label { get; init; }

    /// <summary>The demos counted, newest first; empty when the team has none this far back.</summary>
    public IReadOnlyList<PeriodDiffDemoRow> Demos { get; init; } = [];

    public int Count => Demos.Count;

    public int Wins => Demos.Count(d => d.Won == true);

    public int Losses => Demos.Count(d => d.Won == false);

    /// <summary>Demos with no resolvable winner (an equal final score, or none at all).</summary>
    public int Undetermined => Demos.Count(d => d.Won is null);

    /// <summary>Null when no demo in the period resolved a winner.</summary>
    public double? WinRate => Wins + Losses > 0 ? (double)Wins / (Wins + Losses) : null;

    public int CtRoundsWon => Demos.Sum(d => d.CtRounds);

    public int TRoundsWon => Demos.Sum(d => d.TRounds);

    public bool HasRoundData => CtRoundsWon + TRoundsWon > 0;

    /// <summary>CT's share of the counted rounds, or null with no round data.</summary>
    public double? CtRoundShare => HasRoundData ? (double)CtRoundsWon / (CtRoundsWon + TRoundsWon) : null;

    /// <summary>Rosters that played this period, most demos first, ties by id.</summary>
    public IReadOnlyList<PeriodDiffRosterRow> Rosters { get; init; } = [];

    /// <summary>The roster that played most of the period, or null when no side in it ever joined one.</summary>
    public PeriodDiffRosterRow? DominantRoster => Rosters.Count > 0 ? Rosters[0] : null;

    /// <summary>Demos of the period played with a stand-in (design §3.3, tier-1 only).</summary>
    public int StandInCount => Demos.Count(d => d.StandIn);

    /// <summary>An empty period: no demo reaches this far back yet.</summary>
    /// <param name="label">Its label.</param>
    public static PeriodDiffPeriod Empty(string label) => new() { Label = label };
}

/// <summary>
///     A team's Period Diff (plan.md §3, Phase 5): its last <see cref="WindowSize" /> demos against the
///     <see cref="WindowSize" /> before those, "roster to roster" (team-identity.md §3, Period Diff).
///     Built by <see cref="PeriodDiffService.Build" />, synchronously like the Map Pool Record: both
///     periods come from <see cref="TeamIdentityService.SidesOf" />, already held in memory, so nothing
///     here reads a demo the cache has not already loaded for another section.
/// </summary>
public sealed class PeriodDiffSet
{
    public Guid TeamId { get; init; }

    /// <summary>Demos per period, as asked; a period holds fewer only when the team has not played that many yet.</summary>
    public int WindowSize { get; init; }

    /// <summary>Every demo of the team Team Identity knows, the denominator the two periods are cut from.</summary>
    public int TotalDemos { get; init; }

    public PeriodDiffPeriod Recent { get; init; } = PeriodDiffPeriod.Empty("last");

    public PeriodDiffPeriod Previous { get; init; } = PeriodDiffPeriod.Empty("previous");

    /// <summary>Both periods hold at least one demo, so every number below is comparing something to something.</summary>
    public bool HasBothPeriods => Recent.Count > 0 && Previous.Count > 0;

    /// <summary>
    ///     The two periods' dominant rosters are both known and differ: a roster boundary (an explicit
    ///     "start a new roster here", or a founded-elsewhere roster inheriting the team) sits between them.
    /// </summary>
    public bool RosterChanged { get; init; }

    /// <summary>Recent minus previous; null unless both periods resolved at least one winner.</summary>
    public double? WinRateDelta => Recent.WinRate is { } r && Previous.WinRate is { } p ? r - p : null;

    /// <summary>Recent minus previous; null unless both periods carry round data.</summary>
    public double? CtRoundShareDelta => Recent.CtRoundShare is { } r && Previous.CtRoundShare is { } p ? r - p : null;
}
