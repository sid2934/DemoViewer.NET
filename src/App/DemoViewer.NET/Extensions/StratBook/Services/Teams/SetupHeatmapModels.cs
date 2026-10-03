#region

using DemoViewer.NET.Playback2D.Core.Overlay;
using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     One team's Setup Heatmaps By Buy (plan.md §3, Phase 5): one heatmap per map and CT buy the team
///     defended on, the map with the most such rounds first, buys cheapest first. Built by <see cref="SetupHeatmapService.Build" />.
/// </summary>
public sealed class SetupHeatmapSet
{
    public Guid TeamId { get; init; }

    public IReadOnlyList<SetupHeatmap> Heatmaps { get; init; } = [];

    /// <summary>Demos of the team that had a current positions file to read.</summary>
    public int DemosRead { get; init; }

    /// <summary>Demos of the team with no current positions file (never indexed, or under a stale fingerprint).</summary>
    public int DemosWithoutPositions { get; init; }
}

/// <summary>
///     The team's defensive positions over every CT round of one map at one buy class: the overlay
///     points (CT side only, the setup window of every round), the places those points name split
///     into fixed and rotating, and the rounds themselves so the heatmap can open them.
/// </summary>
public sealed class SetupHeatmap
{
    public string Map { get; init; } = "";

    /// <summary>The team's own buy that round, which is the CT side's: the team is on CT in every round here.</summary>
    public BuyType Buy { get; init; }

    /// <summary>Every CT round of this map and buy, in demo order then round order.</summary>
    public IReadOnlyList<SetupHeatmapRound> Rounds { get; init; } = [];

    /// <summary>The overlay input: every alive CT tuple of every sampled step in each round's setup window.</summary>
    public IReadOnlyList<OverlayPoint> Points { get; init; } = [];

    /// <summary>Sampled steps the points came from, for <see cref="OverlayDocument.Replace" />.</summary>
    public int StateCount { get; init; }

    /// <summary>Rounds that gave at least one sampled step: the denominator of every place share.</summary>
    public int SampledRounds { get; init; }

    /// <summary>The places held in the setup window, most-held first.</summary>
    public IReadOnlyList<SetupPosition> Positions { get; init; } = [];
}

/// <summary>
///     A place the team held in the setup window of some sampled rounds. <see cref="IsFixed" /> when it
///     was held in at least <see cref="SetupHeatmapService.FixedShare" /> of them: a spot they always
///     stand on at this buy, as against one a player rotates into some rounds and not others.
/// </summary>
/// <param name="Place">The place name the positions file resolved.</param>
/// <param name="RoundsHeld">Sampled rounds with at least one player there during the window.</param>
/// <param name="SampledRounds">The heatmap's <see cref="SetupHeatmap.SampledRounds" />.</param>
/// <param name="IsFixed">Held in at least the fixed share of the sampled rounds.</param>
public sealed record SetupPosition(string Place, int RoundsHeld, int SampledRounds, bool IsFixed);

/// <summary>One CT round a heatmap stacks: what the Review Queue clip opens.</summary>
/// <param name="DemoPath">The demo's path.</param>
/// <param name="Sha256">The demo's content hash, or null.</param>
/// <param name="RoundNumber">The Round Facts round number.</param>
/// <param name="FreezeEndTick">Frame clock.</param>
/// <param name="ClipEndTick">Frame clock: the end of the setup window plus a tail, never past the round's end.</param>
/// <param name="TickRate">The demo's tick rate.</param>
/// <param name="Sampled">The round gave the heatmap at least one sampled step.</param>
public sealed record SetupHeatmapRound(
    string DemoPath,
    string? Sha256,
    int RoundNumber,
    int FreezeEndTick,
    int ClipEndTick,
    int TickRate,
    bool Sampled);
