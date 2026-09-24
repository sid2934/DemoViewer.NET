#region

using System.Text.Json.Serialization;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;

#endregion

namespace DemoViewer.NET.Services.RoundFacts;

/// <summary>
///     The five buy classes plus the one a row carries when the inputs were not there. Values are the
///     ruleset's <c>buy_type</c> vocabulary; consumers lower-case the name for labels.
/// </summary>
public enum BuyType
{
    Unknown = 0,
    Pistol,
    Eco,
    Semi,
    Force,
    Full
}

/// <summary>Which half a round belongs to. Defined from the match round number, never observed.</summary>
public enum RoundHalf
{
    Unknown = 0,
    First,
    Second,
    Overtime
}

/// <summary>Where the bomb went down, as a letter. <see cref="Unknown" /> keeps the raw entity index beside it.</summary>
public enum BombSite
{
    Unknown = 0,
    A,
    B
}

/// <summary>Which signal produced <see cref="RoundFacts.EndTick" />.</summary>
public enum RoundEndSource
{
    /// <summary>No end at all: a truncated last round, or a freeze-end that never resolved.</summary>
    None = 0,

    /// <summary>The <c>m_iRoundWinStatus</c> transition: the instant the round was decided.</summary>
    WinStatus,

    /// <summary>Only <c>round_officially_ended</c> was available, seven seconds after the decision.</summary>
    OfficiallyEndedEvent
}

/// <summary>
///     The <c>m_eRoundWinReason</c> values under the names every parser uses. The hostage and VIP
///     values never occur in a defusal match but are kept so a raw value always has a name.
/// </summary>
public enum RoundEndReason
{
    Unknown = 0,
    TargetBombed = 1,
    VipEscaped = 2,
    VipKilled = 3,
    TerroristsEscaped = 4,
    CTStoppedEscape = 5,
    TerroristsStopped = 6,
    BombDefused = 7,
    CTWin = 8,
    TerroristsWin = 9,
    Draw = 10,
    HostagesRescued = 11,
    TargetSaved = 12,
    HostagesNotRescued = 13,
    TerroristsNotEscaped = 14,
    VipNotEscaped = 15,
    GameStart = 16,
    TerroristsSurrender = 17,
    CTSurrender = 18,
    TerroristsPlanted = 19,
    CTsReachedHostage = 20
}

/// <summary>The phase a tick falls in. See <see cref="RoundPhases" /> for the intervals.</summary>
public enum RoundPhase
{
    None = 0,
    Warmup,
    Freeze,
    Opening,
    MidRound,
    PostPlant,
    Retake,
    PostRound
}

/// <summary>
///     The parameter values that produced a <see cref="SideFacts.BuyType" />. HLTV's bands are the
///     defaults, scaled per player on the side; every value is a <c>params:</c> entry of the
///     <c>round_facts</c> ruleset so a user changes the classification by editing YAML, not the app.
/// </summary>
public sealed record BuyThresholds
{
    /// <summary>The shipped defaults: HLTV's "full eco" and "full buy" bands at five players.</summary>
    public static BuyThresholds Default { get; } = new();

    public int EcoMaxPerPlayer { get; init; } = 1000;

    public int FullMinPerPlayerCt { get; init; } = 4000;

    public int FullMinPerPlayerT { get; init; } = 4000;

    public int ForceMoneyMaxPerPlayer { get; init; } = 400;

    /// <summary>The reliability guard: an account outside [0, this] marks the side's money read implausible.</summary>
    public int MoneySaneMax { get; init; } = 16000;

    /// <summary>24 for MR12; an MR15 league sets 30 in its override.</summary>
    public int RegulationRounds { get; init; } = 24;

    /// <summary>The full-buy floor for a side, per player.</summary>
    /// <param name="side">2 = T, 3 = CT.</param>
    public int FullMinPerPlayer(int side) => side == 3 ? FullMinPerPlayerCt : FullMinPerPlayerT;
}

/// <summary>One side of one round. Slots are the Team Identity join; nothing here names a team.</summary>
public sealed class SideFacts
{
    /// <summary>2 = T, 3 = CT.</summary>
    public int Side { get; set; }

    /// <summary>Controllers on this side at freeze end.</summary>
    public int[] Slots { get; set; } = [];

    public int ScoreBefore { get; set; }

    /// <summary>n: the classifier's per-player scale.</summary>
    public int PlayersAtFreezeEnd { get; set; }

    /// <summary>E: the sum of <c>m_unFreezetimeEndEquipmentValue</c> at freeze end.</summary>
    public int EquipmentFreezeEnd { get; set; }

    /// <summary>M: the sum of accounts at the freeze-end sample; null when not reliable.</summary>
    public int? MoneyAtFreezeEnd { get; set; }

    public bool MoneyReliable { get; set; }

    public BuyType BuyType { get; set; }

    public bool WonPreviousRound { get; set; }

    public BuyThresholds Thresholds { get; set; } = BuyThresholds.Default;
}

/// <summary>One kill in the round's live window, with the alive counts after it.</summary>
public sealed class KillStep
{
    /// <summary>Frame clock.</summary>
    public int Tick { get; set; }

    public int VictimSlot { get; set; } = -1;

    /// <summary>-1 when absent (fall, bomb, world).</summary>
    public int AttackerSlot { get; set; } = -1;

    public int VictimSide { get; set; }

    public int CtAlive { get; set; }

    public int TAlive { get; set; }
}

/// <summary>
///     One round as the <c>round_facts</c> ruleset saw it. All ticks are FRAME CLOCK. Null means "the
///     demo did not say"; <see cref="RoundFactsSourceKind.Unavailable" /> in <see cref="Sources" /> means
///     "the engine did not provide the column".
/// </summary>
public sealed class RoundFacts
{
    /// <summary>== <c>ClipRound.Number</c>: the join key with <c>CachedRound</c>.</summary>
    public int Number { get; set; }

    /// <summary><c>total_rounds_played</c> at freeze end plus one. A fact, never a key.</summary>
    public int? MatchRoundNumber { get; set; }

    public RoundHalf Half { get; set; }

    /// <summary>The engine's <c>m_gamePhase</c>, stored raw beside the defined <see cref="Half" />.</summary>
    public int? GamePhase { get; set; }

    /// <summary>False for a freeze-end with no end and no next freeze-end (truncated or warmup).</summary>
    public bool IsLive { get; set; }

    /// <summary>== <c>CachedRound.StartTickFrameClock</c>.</summary>
    public int FreezeEndTick { get; set; }

    /// <summary>First enemy <c>player_hurt</c>, or the opening kill if earlier.</summary>
    public int? FirstContactTick { get; set; }

    /// <summary>First enemy <c>player_death</c>.</summary>
    public int? OpeningKillTick { get; set; }

    public int? PlantTick { get; set; }

    public BombSite PlantSite { get; set; }

    /// <summary>The raw <c>Site</c> entity index, for Zone Baking to cross-check.</summary>
    public int? PlantSiteEntity { get; set; }

    public int? PlanterSlot { get; set; }

    public int? DefuseTick { get; set; }

    public int? ExplodeTick { get; set; }

    /// <summary>The win-status transition, else <c>round_officially_ended</c>; see <see cref="EndSource" />.</summary>
    public int? EndTick { get; set; }

    public RoundEndSource EndSource { get; set; }

    public RoundEndReason EndReason { get; set; }

    /// <summary>2, 3 or 0.</summary>
    public int WinnerSide { get; set; }

    public int? RoundTimeSeconds { get; set; }

    public SideFacts Ct { get; set; } = new()
    {
        Side = 3
    };

    public SideFacts T { get; set; } = new()
    {
        Side = 2
    };

    /// <summary>Tick order, live window only.</summary>
    public List<KillStep> Kills { get; set; } = [];

    /// <summary>User-added columns: the extension point. Values are scalars.</summary>
    public Dictionary<string, object?> Extra { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Per known column: <see cref="RoundFactsSourceKind.Engine" /> or <see cref="RoundFactsSourceKind.Unavailable" />.</summary>
    public Dictionary<string, string> Sources { get; set; } = new(StringComparer.Ordinal);

    public List<string> ProjectionWarnings { get; set; } = [];

    /// <summary>The side that won, as the label vocabulary spells it: <c>T</c>, <c>CT</c> or <c>none</c>.</summary>
    [JsonIgnore]
    public string WinnerLabel => WinnerSide switch
    {
        2 => "T",
        3 => "CT",
        _ => "none"
    };
}

/// <summary>The two values a <see cref="RoundFacts.Sources" /> entry can hold.</summary>
public static class RoundFactsSourceKind
{
    public const string Engine = "Engine";

    /// <summary>The engine did not provide the surface (a partial CS2DemoKit #54), distinct from a null the demo produced.</summary>
    public const string Unavailable = "Unavailable";
}

/// <summary>
///     The Analysis-tier payload: every round of one demo plus the clock header every persisted per-demo
///     store carries. Invalidated by <c>DemoCacheRecord.RoundFactsFingerprint</c>, never by the tier stamp.
/// </summary>
public sealed class RoundFactsRows
{
    /// <summary>The payload shape version; folded into the fingerprint so a shape change re-runs the evaluator.</summary>
    public int Schema { get; set; }

    /// <summary>The <c>dv-frame-clock</c> header, the annotation sidecar's shape.</summary>
    public RoundFactsClock? Clock { get; set; }

    /// <summary>Null until Content Identity has hashed the demo; the join key is the StableKey until then.</summary>
    public string? DemoSha256 { get; set; }

    public List<RoundFacts> Rounds { get; set; } = [];

    /// <summary>Warnings that belong to no one round: rows the deriver had no round for, and the reverse.</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>The header as a value the annotation store can compare.</summary>
    public ClockIdentity ClockIdentity() => Clock?.ToIdentity() ?? Playback2D.Pipeline.Annotations.ClockIdentity.Unknown;
}

/// <summary>
///     The persisted form of a <see cref="ClockIdentity" />: the annotation sidecar's <c>clock</c> block
///     (<c>docs/playback2d-v2/annotations-format.md</c>), field for field, so every per-demo store reads
///     the same header.
/// </summary>
public sealed class RoundFactsClock
{
    public string Kind { get; set; } = ClockIdentity.DvFrameClock;

    public int TickRate { get; set; }

    public int FrameCount { get; set; }

    public int FirstTick { get; set; }

    public int LastTick { get; set; }

    /// <summary>The persisted form of a clock the frame-clock helper built.</summary>
    public static RoundFactsClock From(ClockIdentity clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new RoundFactsClock
        {
            Kind = clock.Kind,
            TickRate = clock.TickRate,
            FrameCount = clock.FrameCount,
            FirstTick = clock.FirstTick,
            LastTick = clock.LastTick
        };
    }

    public ClockIdentity ToIdentity() => new(Kind, TickRate, FrameCount, FirstTick, LastTick);
}

/// <summary>One fact on a round, in the parser-namespace vocabulary <c>IRoundFactsSource.FactsFor</c> owns.</summary>
/// <param name="Group">The vocabulary group: <c>side</c>, <c>buy</c>, <c>score</c>, <c>end</c>, <c>plant</c>, <c>contact</c>, <c>phase</c> or <c>extra</c>.</param>
/// <param name="Key">The label, absolute per side (<c>buy.ct</c>, never <c>buy.us</c>).</param>
/// <param name="Value">The value as text.</param>
public sealed record FactLabel(string Group, string Key, string Value);

/// <summary>
///     A cross-demo round query. Every field is optional and they AND together; the tick-anchored
///     facts (phase, man count, clock band) are per-round reads a consumer makes through
///     <see cref="RoundPhases" /> once it has a tick, so they are not fields here.
/// </summary>
public sealed class RoundFactsFilter
{
    /// <summary>Restrict to these demo paths; null means every demo with rows.</summary>
    public IReadOnlySet<string>? Demos { get; init; }

    public BuyType? BuyCt { get; init; }

    public BuyType? BuyT { get; init; }

    public int? WinnerSide { get; init; }

    public RoundEndReason? EndReason { get; init; }

    public BombSite? PlantSite { get; init; }

    public RoundHalf? Half { get; init; }

    /// <summary>Equality on user-added columns, compared as text.</summary>
    public IReadOnlyDictionary<string, string>? Extra { get; init; }

    /// <summary>Drop rounds with <see cref="RoundFacts.IsLive" /> false. On by default.</summary>
    public bool LiveOnly { get; init; } = true;
}
