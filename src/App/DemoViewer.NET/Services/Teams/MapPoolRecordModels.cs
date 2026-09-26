namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     One map's aggregate over a team's demos (plan.md §3, Map Pool Record, Phase 5). Every number
///     carries its own sample size: <see cref="Played" /> for the record, <see cref="CtRoundsWon" /> +
///     <see cref="TRoundsWon" /> for the side split.
/// </summary>
public sealed class MapPoolMapRow
{
    public required string Map { get; init; }

    public int Played { get; init; }

    public int Wins { get; init; }

    public int Losses { get; init; }

    /// <summary>Demos on this map with no resolvable winner (an equal final score, or no score at all).</summary>
    public int Undetermined { get; init; }

    /// <summary>
    ///     Rounds won on the CT side, summed across every demo counted in this row. Match-wide, not
    ///     scoped to the team the record is for (F12: <c>CtSideWins</c>/<c>TSideWins</c> are already
    ///     cached at that grain, and a team-scoped split would need per-round side tracking this tier
    ///     does not carry).
    /// </summary>
    public int CtRoundsWon { get; init; }

    public int TRoundsWon { get; init; }

    public bool HasRoundData => CtRoundsWon + TRoundsWon > 0;

    /// <summary>Null when no demo on this map resolved a winner.</summary>
    public double? WinRate => Wins + Losses > 0 ? (double)Wins / (Wins + Losses) : null;

    /// <summary>CT's share of the counted rounds on this map, or null with no round data.</summary>
    public double? CtRoundShare => HasRoundData ? (double)CtRoundsWon / (CtRoundsWon + TRoundsWon) : null;
}

/// <summary>
///     The decider record across every recognizable best-of series (plan.md F12/D5): a same-day,
///     same-opponent group of three or five demos, ordered by file time, whose last demo is the decider.
///     Anything else — a single map, an uneven group, an unaffiliated opponent — is not inferable and is
///     left out, so <see cref="Played" /> is the record's own sample size.
/// </summary>
public sealed class DeciderRecord
{
    public int Played { get; init; }

    public int Wins { get; init; }

    public int Losses { get; init; }

    public double? WinRate => Wins + Losses > 0 ? (double)Wins / (Wins + Losses) : null;
}

/// <summary>
///     The Map Pool Record for one team (plan.md §3, Phase 5): the demo-derivable substitute for the
///     veto model (F12). Everything here comes from demos the team is known to have played in; the veto
///     history a user enters by hand lives beside it in <see cref="VetoHistoryStore" /> and is not folded
///     into these numbers.
/// </summary>
public sealed class MapPoolRecord
{
    public Guid TeamId { get; init; }

    /// <summary>Every map played, most-played first.</summary>
    public IReadOnlyList<MapPoolMapRow> Maps { get; init; } = [];

    public DeciderRecord Deciders { get; init; } = new();

    /// <summary>Demos counted anywhere in this record: the record's own overall sample size.</summary>
    public int TotalDemos { get; init; }
}
