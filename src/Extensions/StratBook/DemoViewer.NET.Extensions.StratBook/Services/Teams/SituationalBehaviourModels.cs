namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     One team's Situational Behaviour (plan.md §3, Phase 5): one block per map and side the team
///     played, the map with the most rounds first, T before CT. Built by
///     <see cref="SituationalBehaviourService.Build" />. Unlike the other Phase 5 sections this one reads
///     Round Facts alone: pistol rounds, buy types and the alive counts after each kill are all there
///     already, so no positions file and no Grenade Index row is needed.
/// </summary>
public sealed class SituationalBehaviourSet
{
    public Guid TeamId { get; init; }

    public IReadOnlyList<SituationalBehaviourBlock> Blocks { get; init; } = [];

    /// <summary>Demos of the team with Round Facts rows: the ones the blocks are built from.</summary>
    public int DemosRead { get; init; }
}

/// <summary>
///     The team's situational play on one map and side: how its pistol rounds went and what they set up,
///     how it played the rounds where the opponent bought eco or semi, the rounds where it held an
///     alive-count edge and whether it closed them out, and what it bought after a loss. Every number the
///     Dossier prints is a <see cref="TendencyBucket" />, so every number opens the rounds behind it.
/// </summary>
public sealed class SituationalBehaviourBlock
{
    public string Map { get; init; } = "";

    /// <summary>2 = T, 3 = CT.</summary>
    public int Side { get; init; }

    /// <summary>Every live round of this map on this side: the denominator of the sections below.</summary>
    public TendencyBucket SideRounds { get; init; } = TendencyBucket.Empty("rounds");

    /// <summary>Rounds Round Facts classed as a pistol round on this side: the denominator of the two lists below.</summary>
    public TendencyBucket PistolRounds { get; init; } = TendencyBucket.Empty("pistols");

    /// <summary>How the pistol rounds ended for the team.</summary>
    public IReadOnlyList<TendencyBucket> PistolOutcomes { get; init; } = [];

    /// <summary>
    ///     A pistol's result paired with the following round's, most common first: "won pistol, won the
    ///     bonus" and so on. A pistol whose following round is not live, or not the team's, is left out.
    /// </summary>
    public IReadOnlyList<TendencyBucket> PistolFollowUp { get; init; } = [];

    /// <summary>Rounds where the opponent bought eco or semi buy: the denominator of the three lists below.</summary>
    public TendencyBucket AntiEcoRounds { get; init; } = TendencyBucket.Empty("vs low buy");

    /// <summary>The opponent's buy on those rounds, eco split from semi.</summary>
    public IReadOnlyList<TendencyBucket> AntiEcoBuyTypes { get; init; } = [];

    /// <summary>How those rounds ended for the team.</summary>
    public IReadOnlyList<TendencyBucket> AntiEcoOutcomes { get; init; } = [];

    /// <summary>First contact against a low-buy opponent, by clock bin; "no contact" last.</summary>
    public IReadOnlyList<TendencyBucket> AntiEcoContactClock { get; init; } = [];

    /// <summary>Rounds where the team held an alive-count edge at some point: the denominator of the two lists below.</summary>
    public TendencyBucket ManAdvantageRounds { get; init; } = TendencyBucket.Empty("man advantage");

    /// <summary>The size of the largest edge the team held, most rounds first.</summary>
    public IReadOnlyList<TendencyBucket> ManAdvantage { get; init; } = [];

    /// <summary>How the man-advantage rounds ended for the team: converted, or the lead given back.</summary>
    public IReadOnlyList<TendencyBucket> ManAdvantageOutcomes { get; init; } = [];

    /// <summary>Rounds the team lost: the denominator of <see cref="SaveDiscipline" />.</summary>
    public TendencyBucket RoundsLost { get; init; } = TendencyBucket.Empty("losses");

    /// <summary>
    ///     What the team bought the round after one of those losses, most common first. A loss with no
    ///     following round, or whose following round Team Identity cannot place the team in, is left out.
    /// </summary>
    public IReadOnlyList<TendencyBucket> SaveDiscipline { get; init; } = [];
}
