namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     One team's Opening Tendencies (plan.md §3, Phase 5): one block per map and side the team played,
///     the map with the most rounds first, T before CT. Built by <see cref="OpeningTendenciesService.Build" />.
/// </summary>
public sealed class OpeningTendenciesSet
{
    public Guid TeamId { get; init; }

    public IReadOnlyList<OpeningTendencies> Blocks { get; init; } = [];

    /// <summary>Demos of the team with Round Facts rows: the ones the blocks are built from.</summary>
    public int DemosRead { get; init; }

    /// <summary>Demos read with no grenade rows in the Grenade Index; their rounds are left out of the utility numbers.</summary>
    public int DemosWithoutGrenades { get; init; }

    /// <summary>Demos read with no current positions file; their T rounds are left out of the lurk numbers.</summary>
    public int DemosWithoutPositions { get; init; }
}

/// <summary>
///     The team's openings on one map and side. Every number the Dossier prints is a
///     <see cref="TendencyBucket" />, so every number opens the rounds behind it. The site split, the
///     entry player by site and the lurk numbers are the T side's; a CT block leaves them empty.
/// </summary>
public sealed class OpeningTendencies
{
    public string Map { get; init; } = "";

    /// <summary>2 = T, 3 = CT.</summary>
    public int Side { get; init; }

    /// <summary>Every live round of this map on this side, clipped from freeze end.</summary>
    public TendencyBucket Rounds { get; init; } = TendencyBucket.Empty("rounds");

    /// <summary>The team's first thrown grenade per round, by release time in <see cref="OpeningTendenciesService.ClockBinSeconds" /> bins; "none" last.</summary>
    public IReadOnlyList<TendencyBucket> FirstUtilityClock { get; init; } = [];

    /// <summary>The same first grenades by kind and landing place, most common first.</summary>
    public IReadOnlyList<TendencyBucket> FirstUtilityPlaces { get; init; } = [];

    /// <summary>Rounds whose utility could be read: the denominator of the two utility lists.</summary>
    public int UtilityRounds { get; init; }

    /// <summary>First contact by time after freeze end, in clock bins; "no contact" last.</summary>
    public IReadOnlyList<TendencyBucket> FirstContactClock { get; init; } = [];

    /// <summary>Where the round went: the plant site, "no plant" last. T side only.</summary>
    public IReadOnlyList<TendencyBucket> SiteSplit { get; init; } = [];

    /// <summary>The team's player in the round's opening duel, per site the round went to. T side only.</summary>
    public IReadOnlyList<TendencyBucket> EntryBySite { get; init; } = [];

    /// <summary>When a player first split off alone, in clock bins; "no lurk" last. T side only.</summary>
    public IReadOnlyList<TendencyBucket> LurkClock { get; init; } = [];

    /// <summary>Who lurked, most rounds first. T side only.</summary>
    public IReadOnlyList<TendencyBucket> Lurkers { get; init; } = [];

    /// <summary>T rounds whose positions could be read: the denominator of the two lurk lists.</summary>
    public int LurkRounds { get; init; }
}

/// <summary>A number in the Dossier and the rounds behind it; <see cref="Count" /> is the number.</summary>
/// <param name="Label">What the number counts: "10-15 s", "smoke at TopMid", "A", "p5 at B".</param>
/// <param name="Rounds">The rounds, each clipped around the moment it was counted for.</param>
public sealed record TendencyBucket(string Label, IReadOnlyList<TendencyRound> Rounds)
{
    public int Count => Rounds.Count;

    /// <summary>A bucket with no rounds.</summary>
    /// <param name="label">Its label.</param>
    public static TendencyBucket Empty(string label) => new(label, []);
}

/// <summary>One round behind a number: what the Review Queue clip opens.</summary>
/// <param name="DemoPath">The demo's path.</param>
/// <param name="Sha256">The demo's content hash, or null.</param>
/// <param name="RoundNumber">The Round Facts round number.</param>
/// <param name="FromTick">Frame clock: the clip's start, never before freeze end.</param>
/// <param name="ToTick">Frame clock: the clip's end, never past the round's end.</param>
/// <param name="TickRate">The demo's tick rate.</param>
public sealed record TendencyRound(
    string DemoPath,
    string? Sha256,
    int RoundNumber,
    int FromTick,
    int ToTick,
    int TickRate);
