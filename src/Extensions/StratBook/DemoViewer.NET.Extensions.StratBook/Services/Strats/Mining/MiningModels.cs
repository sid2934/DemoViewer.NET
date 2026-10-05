#region

using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;

/// <summary>What a mined pattern is: a site take, or how a side stands and throws before anything happens.</summary>
public enum PatternKind
{
    /// <summary>A T side taking a site: the utility and positions leading into the take.</summary>
    Execute,

    /// <summary>The opening seconds of a round: T defaults and CT holds.</summary>
    Setup
}

/// <summary>A player at an anchor, from the round positions file.</summary>
/// <param name="Slot">Controller slot.</param>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="Z">World Z.</param>
/// <param name="Place">The place name, or null.</param>
public readonly record struct MinedPawn(int Slot, float X, float Y, float Z, string? Place);

/// <summary>One grenade a side threw inside the signature's window.</summary>
/// <param name="Kind">What was thrown.</param>
/// <param name="Seconds">Seconds from the signature's anchor; negative before it.</param>
/// <param name="Landing">Where it went off.</param>
/// <param name="LandingPlace">The zone it landed in, or null.</param>
/// <param name="Origin">Where it was thrown from.</param>
/// <param name="ThrowerSteamId">The thrower's SteamID64, or 0 when the walk did not read one.</param>
/// <param name="ThrowerName">The thrower's name at the time, or null.</param>
/// <param name="LineupId">The Grenade Index lineup, or null when the throw is in no lineup.</param>
public sealed record MinedThrow(
    GrenadeKind Kind,
    double Seconds,
    WorldPoint Landing,
    string? LandingPlace,
    WorldPoint Origin,
    ulong ThrowerSteamId,
    string? ThrowerName,
    Guid? LineupId);

/// <summary>
///     One side of one round reduced to what the miner compares: the players at a few anchors, the utility in
///     the window and, for an execute, when the take started. Built from cached files only.
/// </summary>
public sealed class RoundSignature
{
    public required string DemoPath { get; init; }

    public string? Sha256 { get; init; }

    public required int Round { get; init; }

    public required string Map { get; init; }

    /// <summary>2 = T, 3 = CT.</summary>
    public required int Side { get; init; }

    public required PatternKind Kind { get; init; }

    /// <summary><c>A</c> or <c>B</c> for an execute; null for a setup.</summary>
    public string? Site { get; init; }

    public BuyType Buy { get; init; }

    /// <summary>Whether the side won the round; null when the winner is unknown.</summary>
    public bool? Won { get; init; }

    /// <summary>The team Team Identity puts on this side this round, or null.</summary>
    public Guid? TeamId { get; init; }

    public int TickRate { get; init; } = 64;

    public int FreezeEndTick { get; init; }

    /// <summary>The frame-clock tick the window is measured from: freeze end for a setup, the take for an execute.</summary>
    public int AnchorTick { get; init; }

    /// <summary>Seconds from freeze end to <see cref="AnchorTick" />; the execute's timing.</summary>
    public double AnchorSeconds => TickRate > 0 ? (AnchorTick - FreezeEndTick) / (double)TickRate : 0;

    /// <summary>
    ///     The side's alive players at each anchor offset (<see cref="StratMiner.AnchorsFor" />), in order. An
    ///     empty list is an anchor with no sample.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<MinedPawn>> Anchors { get; init; }

    /// <summary>
    ///     The side's grenades in the window, or null when the demo has no grenade rows. Null is unknown, not
    ///     none: a round without rows never scores as threw-nothing against one with rows.
    /// </summary>
    public IReadOnlyList<MinedThrow>? Throws { get; init; }

    /// <summary><c>path#round#side#kind</c>: one signature per side, round and kind.</summary>
    public string Key => $"{DemoPath}#{Round}#{Side}#{Kind}";
}

/// <summary>A round that belongs to a pattern.</summary>
public sealed record MinedMember(
    string DemoPath,
    string? Sha256,
    int Round,
    Guid? TeamId,
    bool? Won,
    BuyType Buy,
    int FreezeEndTick,
    int AnchorTick,
    int TickRate,
    double Distance);

/// <summary>A throw most of a pattern's rounds share, described from the medoid round.</summary>
/// <param name="Throw">The medoid's throw.</param>
/// <param name="Rounds">How many of the pattern's rounds with grenade rows threw a matching grenade.</param>
public sealed record MinedCommonThrow(MinedThrow Throw, int Rounds);

/// <summary>Near-identical rounds: a pattern the inbox offers as a strat.</summary>
public sealed class MinedPattern
{
    /// <summary>Stable across re-mines while the pattern's shape holds; see <see cref="StratMiner.PatternKey" />.</summary>
    public required string Key { get; init; }

    public required PatternKind Kind { get; init; }

    public required string Map { get; init; }

    public required int Side { get; init; }

    public string? Site { get; init; }

    /// <summary>The members, the medoid first.</summary>
    public required IReadOnlyList<MinedMember> Members { get; init; }

    /// <summary>The medoid's signature: the round the strat is built from.</summary>
    public required RoundSignature Medoid { get; init; }

    /// <summary>The largest distance between two members; lower is tighter.</summary>
    public double Spread { get; init; }

    /// <summary>True when every member has grenade rows, so the utility was compared, not assumed.</summary>
    public bool UtilityCompared { get; init; }

    public IReadOnlyList<MinedCommonThrow> CommonThrows { get; init; } = [];

    public IReadOnlyList<Guid> Teams => [.. Members.Where(m => m.TeamId is not null).Select(m => m.TeamId!.Value).Distinct()];

    public int Support => Members.Count;

    public int Wins => Members.Count(m => m.Won == true);

    public int Demos => Members.Select(m => m.Sha256 ?? m.DemoPath).Distinct(StringComparer.Ordinal).Count();
}
