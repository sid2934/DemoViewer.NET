using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Abstractions;

namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     The five buy classes plus the one a row carries when the inputs were not there. Values are the
///     <c>round_facts</c> ruleset's <c>buy_type</c> vocabulary; labels lower-case the name.
/// </summary>
public enum BuyType
{
    /// <summary>The inputs were missing.</summary>
    Unknown = 0,

    /// <summary>A pistol round.</summary>
    Pistol,

    /// <summary>A full save.</summary>
    Eco,

    /// <summary>A partial buy.</summary>
    Semi,

    /// <summary>A buy with too little money for a full one.</summary>
    Force,

    /// <summary>A full buy.</summary>
    Full
}

/// <summary>Which half a round belongs to. Defined from the match round number, never observed.</summary>
public enum RoundHalf
{
    /// <summary>The round number was missing.</summary>
    Unknown = 0,

    /// <summary>Regulation, first half.</summary>
    First,

    /// <summary>Regulation, second half.</summary>
    Second,

    /// <summary>Any overtime round.</summary>
    Overtime
}

/// <summary>Where the bomb went down, as a letter. <see cref="Unknown" /> keeps the raw entity index beside it.</summary>
public enum BombSite
{
    /// <summary>No plant, or a site the map did not name.</summary>
    Unknown = 0,

    /// <summary>Site A.</summary>
    A,

    /// <summary>Site B.</summary>
    B
}

/// <summary>Which signal produced <see cref="RoundFacts.EndTick" />.</summary>
public enum RoundEndSource
{
    /// <summary>No end at all: a truncated last round, or a freeze end that never resolved.</summary>
    None = 0,

    /// <summary>The <c>m_iRoundWinStatus</c> transition: the instant the round was decided.</summary>
    WinStatus,

    /// <summary>Only <c>round_officially_ended</c> was available, seven seconds after the decision.</summary>
    OfficiallyEndedEvent
}

/// <summary>
///     The <c>m_eRoundWinReason</c> values under the names every parser uses. The hostage and VIP values never
///     occur in a defusal match but are kept so a raw value always has a name.
/// </summary>
public enum RoundEndReason
{
    /// <summary>No reason recorded.</summary>
    Unknown = 0,

    /// <summary>The bomb exploded.</summary>
    TargetBombed = 1,

    /// <summary>VIP escaped.</summary>
    VipEscaped = 2,

    /// <summary>VIP killed.</summary>
    VipKilled = 3,

    /// <summary>Terrorists escaped.</summary>
    TerroristsEscaped = 4,

    /// <summary>CTs stopped the escape.</summary>
    CTStoppedEscape = 5,

    /// <summary>Terrorists stopped.</summary>
    TerroristsStopped = 6,

    /// <summary>The bomb was defused.</summary>
    BombDefused = 7,

    /// <summary>CTs eliminated the Terrorists.</summary>
    CTWin = 8,

    /// <summary>Terrorists eliminated the CTs.</summary>
    TerroristsWin = 9,

    /// <summary>A draw.</summary>
    Draw = 10,

    /// <summary>Hostages rescued.</summary>
    HostagesRescued = 11,

    /// <summary>Time ran out with no plant.</summary>
    TargetSaved = 12,

    /// <summary>Hostages not rescued.</summary>
    HostagesNotRescued = 13,

    /// <summary>Terrorists did not escape.</summary>
    TerroristsNotEscaped = 14,

    /// <summary>VIP did not escape.</summary>
    VipNotEscaped = 15,

    /// <summary>The game started.</summary>
    GameStart = 16,

    /// <summary>Terrorists surrendered.</summary>
    TerroristsSurrender = 17,

    /// <summary>CTs surrendered.</summary>
    CTSurrender = 18,

    /// <summary>Terrorists planted.</summary>
    TerroristsPlanted = 19,

    /// <summary>CTs reached a hostage.</summary>
    CTsReachedHostage = 20
}

/// <summary>The phase a tick falls in. <see cref="RoundPhases" /> computes the intervals.</summary>
public enum RoundPhase
{
    /// <summary>A round that is not live.</summary>
    None = 0,

    /// <summary>Before the first freeze end.</summary>
    Warmup,

    /// <summary>Buy time.</summary>
    Freeze,

    /// <summary>From the freeze end to the opening kill.</summary>
    Opening,

    /// <summary>After the opening kill, before a plant.</summary>
    MidRound,

    /// <summary>From the plant to the end, seen from the T side or no side.</summary>
    PostPlant,

    /// <summary>From the plant to the end, seen from the CT side.</summary>
    Retake,

    /// <summary>From the end on: the win panel.</summary>
    PostRound
}

/// <summary>
///     The parameter values that produced a <see cref="SideFacts.BuyType" />. HLTV's bands are the defaults,
///     scaled per player on the side; every value is a <c>params:</c> entry of the <c>round_facts</c> ruleset.
/// </summary>
public sealed record BuyThresholds
{
    /// <summary>The shipped defaults: HLTV's "full eco" and "full buy" bands at five players.</summary>
    public static BuyThresholds Default { get; } = new();

    /// <summary>The most money per player an eco may have.</summary>
    public int EcoMaxPerPlayer { get; init; } = 1000;

    /// <summary>The CT full-buy floor per player.</summary>
    public int FullMinPerPlayerCt { get; init; } = 4000;

    /// <summary>The T full-buy floor per player.</summary>
    public int FullMinPerPlayerT { get; init; } = 4000;

    /// <summary>The most money per player left after a force buy.</summary>
    public int ForceMoneyMaxPerPlayer { get; init; } = 400;

    /// <summary>The reliability guard: an account outside [0, this] marks the side's money read implausible.</summary>
    public int MoneySaneMax { get; init; } = 16000;

    /// <summary>24 for MR12; an MR15 league sets 30 in its override.</summary>
    public int RegulationRounds { get; init; } = 24;

    /// <summary>The full-buy floor for a side, per player.</summary>
    /// <param name="side">2 = T, 3 = CT.</param>
    public int FullMinPerPlayer(int side) => side == 3 ? FullMinPerPlayerCt : FullMinPerPlayerT;
}

/// <summary>One side of one round. Slots are the join to the roster; nothing here names a team.</summary>
public sealed class SideFacts
{
    /// <summary>2 = T, 3 = CT.</summary>
    public int Side { get; set; }

    /// <summary>Controllers on this side at freeze end.</summary>
    public int[] Slots { get; set; } = [];

    /// <summary>The rounds this side's team had won before this round.</summary>
    public int ScoreBefore { get; set; }

    /// <summary>The players on the side at freeze end: the classifier's per-player scale.</summary>
    public int PlayersAtFreezeEnd { get; set; }

    /// <summary>The sum of <c>m_unFreezetimeEndEquipmentValue</c> at freeze end.</summary>
    public int EquipmentFreezeEnd { get; set; }

    /// <summary>The sum of accounts at the freeze-end sample; null when not reliable.</summary>
    public int? MoneyAtFreezeEnd { get; set; }

    /// <summary>True when every account read was plausible.</summary>
    public bool MoneyReliable { get; set; }

    /// <summary>The side's buy class.</summary>
    public BuyType BuyType { get; set; }

    /// <summary>True when this side's team won the round before.</summary>
    public bool WonPreviousRound { get; set; }

    /// <summary>The parameter values <see cref="BuyType" /> was classified under.</summary>
    public BuyThresholds Thresholds { get; set; } = BuyThresholds.Default;
}

/// <summary>One kill in the round's live window, with the alive counts after it.</summary>
public sealed class KillStep
{
    /// <summary>Frame clock.</summary>
    public int Tick { get; set; }

    /// <summary>The victim's slot.</summary>
    public int VictimSlot { get; set; } = -1;

    /// <summary>The attacker's slot; -1 when absent (fall, bomb, world).</summary>
    public int AttackerSlot { get; set; } = -1;

    /// <summary>The victim's side: 2 = T, 3 = CT, 0 when unknown.</summary>
    public int VictimSide { get; set; }

    /// <summary>CTs alive after the kill.</summary>
    public int CtAlive { get; set; }

    /// <summary>Ts alive after the kill.</summary>
    public int TAlive { get; set; }
}

/// <summary>
///     One round as the <c>round_facts</c> ruleset saw it. All ticks are frame clock. Null means "the demo did
///     not say"; <see cref="RoundFactsSourceKind.Unavailable" /> in <see cref="Sources" /> means "the engine did
///     not provide the column".
/// </summary>
public sealed class RoundFacts
{
    /// <summary>The clip round number: the join key with <see cref="LibraryRound.Number" />.</summary>
    public int Number { get; set; }

    /// <summary><c>total_rounds_played</c> at freeze end plus one. A fact, never a key.</summary>
    public int? MatchRoundNumber { get; set; }

    /// <summary>Which half, defined from <see cref="MatchRoundNumber" />.</summary>
    public RoundHalf Half { get; set; }

    /// <summary>The engine's <c>m_gamePhase</c>, stored raw beside the defined <see cref="Half" />.</summary>
    public int? GamePhase { get; set; }

    /// <summary>False for a freeze end with no end and no next freeze end (truncated or warmup).</summary>
    public bool IsLive { get; set; }

    /// <summary>The round's start: <see cref="LibraryRound.StartTick" />.</summary>
    public int FreezeEndTick { get; set; }

    /// <summary>First enemy <c>player_hurt</c>, or the opening kill if earlier.</summary>
    public int? FirstContactTick { get; set; }

    /// <summary>First enemy <c>player_death</c>.</summary>
    public int? OpeningKillTick { get; set; }

    /// <summary>The bomb plant.</summary>
    public int? PlantTick { get; set; }

    /// <summary>Where the bomb went down.</summary>
    public BombSite PlantSite { get; set; }

    /// <summary>The raw site entity index.</summary>
    public int? PlantSiteEntity { get; set; }

    /// <summary>The planter's slot.</summary>
    public int? PlanterSlot { get; set; }

    /// <summary>The defuse.</summary>
    public int? DefuseTick { get; set; }

    /// <summary>The explosion.</summary>
    public int? ExplodeTick { get; set; }

    /// <summary>The win-status transition, else <c>round_officially_ended</c>; see <see cref="EndSource" />.</summary>
    public int? EndTick { get; set; }

    /// <summary>Which signal produced <see cref="EndTick" />.</summary>
    public RoundEndSource EndSource { get; set; }

    /// <summary>Why the round ended.</summary>
    public RoundEndReason EndReason { get; set; }

    /// <summary>2, 3 or 0.</summary>
    public int WinnerSide { get; set; }

    /// <summary>The round's length in seconds, when the demo said.</summary>
    public int? RoundTimeSeconds { get; set; }

    /// <summary>The CT side.</summary>
    public SideFacts Ct { get; set; } = new() { Side = 3 };

    /// <summary>The T side.</summary>
    public SideFacts T { get; set; } = new() { Side = 2 };

    /// <summary>Tick order, live window only.</summary>
    public List<KillStep> Kills { get; set; } = [];

    /// <summary>Columns a user's override added. Values are scalars.</summary>
    public Dictionary<string, object?> Extra { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Per known column: <see cref="RoundFactsSourceKind.Engine" /> or <see cref="RoundFactsSourceKind.Unavailable" />.</summary>
    public Dictionary<string, string> Sources { get; set; } = new(StringComparer.Ordinal);

    /// <summary>What the projection had to say about this round.</summary>
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
    /// <summary>The engine provided the column.</summary>
    public const string Engine = "Engine";

    /// <summary>The ruleset does not emit the column, distinct from a null the demo produced.</summary>
    public const string Unavailable = "Unavailable";
}

/// <summary>Every round of one demo, with the frame-clock header the rows were read under.</summary>
public sealed class RoundFactsRows
{
    /// <summary>The library fact the rows are stamped under (<see cref="LibraryDemo.Fact" />).</summary>
    public const string FacetId = "roundfacts";

    /// <summary>The shape the host writes rows at now. Rows at another schema are stale.</summary>
    public const int CurrentSchema = 1;

    /// <summary>The shape version the rows were written at.</summary>
    public int Schema { get; set; }

    /// <summary>The frame-clock header of the read the rows came from.</summary>
    public RoundFactsClock? Clock { get; set; }

    /// <summary>The demo's SHA-256, or null when it was not hashed yet.</summary>
    public string? DemoSha256 { get; set; }

    /// <summary>The rounds, in number order.</summary>
    public List<RoundFacts> Rounds { get; set; } = [];

    /// <summary>Warnings that belong to no one round.</summary>
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
///     The frame-clock header every persisted per-demo store writes: the read's tick rate, its frame count and the
///     server ticks of its first and last frames. A store whose header does not match the demo's was written
///     against another read.
/// </summary>
public sealed class RoundFactsClock
{
    /// <summary>The kind of clock the host's stores carry.</summary>
    public const string FrameClockKind = "dv-frame-clock";

    /// <summary>The kind; <see cref="FrameClockKind" /> for every host store.</summary>
    public string Kind { get; set; } = FrameClockKind;

    /// <summary>Ticks per second.</summary>
    public int TickRate { get; set; }

    /// <summary>The frames the read saw.</summary>
    public int FrameCount { get; set; }

    /// <summary>The first frame's server tick.</summary>
    public int FirstTick { get; set; }

    /// <summary>The last frame's server tick.</summary>
    public int LastTick { get; set; }

    /// <summary>The header for a held parse. A demo with no tick rate reads as 64.</summary>
    /// <param name="parsed">The parse.</param>
    public static RoundFactsClock For(ParsedDemo parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        IReadOnlyList<DemoFrame> frames = parsed.Frames;
        return new RoundFactsClock
        {
            TickRate = parsed.TickRate > 0 ? parsed.TickRate : 64,
            FrameCount = frames.Count,
            FirstTick = frames.Count > 0 ? frames[0].ServerTick : 0,
            LastTick = frames.Count > 0 ? frames[^1].ServerTick : 0
        };
    }

    /// <summary>The header for the demo a module context holds; 64 ticks over no frames when it holds none.</summary>
    /// <param name="context">The module context.</param>
    public static RoundFactsClock For(IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new RoundFactsClock
        {
            TickRate = context.TickRate > 0 ? context.TickRate : 64,
            FrameCount = context.TotalFrames,
            FirstTick = context.FirstTick,
            LastTick = context.LastTick
        };
    }
}

/// <summary>One fact on a round, in the label vocabulary <see cref="RoundFactsRules.Labels" /> owns.</summary>
/// <param name="Group">The vocabulary group: <c>side</c>, <c>buy</c>, <c>score</c>, <c>end</c>, <c>plant</c>, <c>contact</c>, <c>phase</c> or <c>extra</c>.</param>
/// <param name="Key">The label, absolute per side (<c>buy.ct</c>, never <c>buy.us</c>).</param>
/// <param name="Value">The value as text.</param>
public sealed record FactLabel(string Group, string Key, string Value);

/// <summary>The clock bands a tick-anchored filter names, in seconds since the freeze end.</summary>
public enum ClockBand
{
    /// <summary>[0, 30) seconds after the freeze end.</summary>
    Early,

    /// <summary>[30, 75) seconds.</summary>
    Middle,

    /// <summary>75 seconds and later.</summary>
    Late
}

/// <summary>The relative man count at a tick, absolute per side like every other fact.</summary>
public enum ManCountState
{
    /// <summary>Both sides have as many alive.</summary>
    Even,

    /// <summary>More CTs alive.</summary>
    CtUp,

    /// <summary>More Ts alive.</summary>
    TUp
}

/// <summary>
///     The score situation before the round, absolute per side. <see cref="MatchPoint" /> is a side one round
///     from the regulation win; overtime has no fixed match point in the row, so it is a regulation-only answer.
/// </summary>
public enum ScoreSituation
{
    /// <summary>Level.</summary>
    Tied,

    /// <summary>The CT side's team leads.</summary>
    CtLeading,

    /// <summary>The T side's team leads.</summary>
    TLeading,

    /// <summary>A team is one round from the regulation win.</summary>
    MatchPoint
}

/// <summary>
///     A cross-demo round filter. Every field is optional and they AND together. The tick-anchored ones
///     (<see cref="Phase" />, <see cref="ClockBand" />, <see cref="ManCount" />, <see cref="CtAlive" />,
///     <see cref="TAlive" />) are read at a tick through <see cref="RoundFactsRules.MatchesAt" />.
/// </summary>
public sealed class RoundFactsFilter
{
    /// <summary>Restrict to these demo paths; null means every demo with rows.</summary>
    public IReadOnlySet<string>? Demos { get; init; }

    /// <summary>The CT side's buy.</summary>
    public BuyType? BuyCt { get; init; }

    /// <summary>The T side's buy.</summary>
    public BuyType? BuyT { get; init; }

    /// <summary>The winning side: 2 or 3.</summary>
    public int? WinnerSide { get; init; }

    /// <summary>Why the round ended.</summary>
    public RoundEndReason? EndReason { get; init; }

    /// <summary>Where the bomb went down.</summary>
    public BombSite? PlantSite { get; init; }

    /// <summary>Which half.</summary>
    public RoundHalf? Half { get; init; }

    /// <summary>The score situation before the round.</summary>
    public ScoreSituation? Score { get; init; }

    /// <summary>
    ///     The phase at the tick. <see cref="RoundPhase.Retake" /> is the post-plant interval seen from the CT
    ///     side, so it matches the same ticks as <see cref="RoundPhase.PostPlant" />.
    /// </summary>
    public RoundPhase? Phase { get; init; }

    /// <summary>Seconds since the freeze end at the tick, in bands.</summary>
    public ClockBand? ClockBand { get; init; }

    /// <summary>The relative alive count at the tick.</summary>
    public ManCountState? ManCount { get; init; }

    /// <summary>Exactly this many CTs alive at the tick.</summary>
    public int? CtAlive { get; init; }

    /// <summary>Exactly this many Ts alive at the tick.</summary>
    public int? TAlive { get; init; }

    /// <summary>Equality on user-added columns, compared as text.</summary>
    public IReadOnlyDictionary<string, string>? Extra { get; init; }

    /// <summary>Drop rounds with <see cref="RoundFacts.IsLive" /> false. On by default.</summary>
    public bool LiveOnly { get; init; } = true;

    /// <summary>
    ///     A per-round predicate over (demo path, round) from a caller that knows more than the rows do. It runs
    ///     after every other field, so it sees only rounds that already passed them. Null applies none.
    /// </summary>
    public Func<string, RoundFacts, bool>? Where { get; init; }

    /// <summary>True when any field reads a tick: the caller must pick one, or ask for any.</summary>
    public bool HasTickAnchored =>
        Phase is not null || ClockBand is not null || ManCount is not null || CtAlive is not null || TAlive is not null;
}

/// <summary>
///     Phase boundaries, derived from a round's stored ticks. Intervals are half-open on the frame clock:
///     <c>Freeze</c> before the freeze end; <c>Opening</c> to the opening kill; <c>MidRound</c> to a plant;
///     <c>PostPlant</c> or <c>Retake</c> from the plant to the end, named by the side asking; <c>PostRound</c>
///     from the end on. A round with no end runs its last phase to the last frame.
/// </summary>
public static class RoundPhases
{
    /// <summary>The phase of <paramref name="tick" /> within <paramref name="round" />.</summary>
    /// <param name="round">The round the tick falls in, or null before the first freeze end.</param>
    /// <param name="tick">Frame clock.</param>
    /// <param name="side">2 = T, 3 = CT, or null. Only the post-plant window reads it.</param>
    public static RoundPhase At(RoundFacts? round, int tick, int? side = null)
    {
        if (round is null)
        {
            return RoundPhase.Warmup;
        }

        if (!round.IsLive)
        {
            return RoundPhase.None;
        }

        if (tick < round.FreezeEndTick)
        {
            return RoundPhase.Freeze;
        }

        if (round.EndTick is int end && tick >= end)
        {
            return RoundPhase.PostRound;
        }

        if (round.PlantTick is int plant && tick >= plant)
        {
            return side == 3 ? RoundPhase.Retake : RoundPhase.PostPlant;
        }

        if (round.OpeningKillTick is int opening && tick >= opening)
        {
            return RoundPhase.MidRound;
        }

        return RoundPhase.Opening;
    }

    /// <summary>
    ///     Players alive on each side at <paramref name="tick" />: the freeze-end count until the first kill, then
    ///     the count after the last kill at or before the tick.
    /// </summary>
    /// <param name="round">The round.</param>
    /// <param name="tick">Frame clock.</param>
    public static (int Ct, int T) AliveAt(RoundFacts round, int tick)
    {
        ArgumentNullException.ThrowIfNull(round);

        int ct = round.Ct.PlayersAtFreezeEnd;
        int t = round.T.PlayersAtFreezeEnd;
        foreach (KillStep kill in round.Kills)
        {
            if (kill.Tick > tick)
            {
                break;
            }

            ct = kill.CtAlive;
            t = kill.TAlive;
        }

        return (ct, t);
    }
}

/// <summary>The round lookups, labels and filter rules every reader of Round Facts shares.</summary>
public static class RoundFactsRules
{
    /// <summary>Where <see cref="ClockBand.Early" /> ends, in seconds since the freeze end.</summary>
    public const int EarlyBandSeconds = 30;

    /// <summary>Where <see cref="ClockBand.Late" /> starts, in seconds since the freeze end.</summary>
    public const int LateBandSeconds = 75;

    /// <summary>An enum name in the label vocabulary's spelling: <c>Pistol</c> to <c>pistol</c>, <c>MidRound</c> to <c>midRound</c>.</summary>
    /// <param name="value">The value.</param>
    public static string LowerCamel<T>(T value) where T : struct, Enum
    {
        string name = value.ToString();
        return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>
    ///     The round whose window holds a tick: from its freeze end to the next round's, the last round running to
    ///     the end of the demo. Null before the first freeze end.
    /// </summary>
    /// <param name="rounds">Rounds in number order.</param>
    /// <param name="frameClockTick">Frame clock.</param>
    public static RoundFacts? FindRound(IReadOnlyList<RoundFacts> rounds, int frameClockTick)
    {
        ArgumentNullException.ThrowIfNull(rounds);

        RoundFacts? hit = null;
        foreach (RoundFacts round in rounds)
        {
            if (round.FreezeEndTick > frameClockTick)
            {
                break;
            }

            hit = round;
        }

        return hit;
    }

    /// <summary>The label list for one round: the shared label vocabulary plus the round's per-side facts.</summary>
    /// <param name="round">The round.</param>
    /// <param name="atTick">The tick the <c>phase</c> group is asked at, or null to leave that group out.</param>
    public static IReadOnlyList<FactLabel> Labels(RoundFacts round, int? atTick = null)
    {
        ArgumentNullException.ThrowIfNull(round);

        List<FactLabel> labels =
        [
            new("side", "slots.ct", Join(round.Ct.Slots)),
            new("side", "slots.t", Join(round.T.Slots)),
            new("buy", "buy.ct", LowerCamel(round.Ct.BuyType)),
            new("buy", "buy.t", LowerCamel(round.T.BuyType)),
            new("buy", "equipment.ct", Text(round.Ct.EquipmentFreezeEnd)),
            new("buy", "equipment.t", Text(round.T.EquipmentFreezeEnd))
        ];

        Optional(labels, "buy", "money.ct", round.Ct.MoneyAtFreezeEnd);
        Optional(labels, "buy", "money.t", round.T.MoneyAtFreezeEnd);
        labels.Add(new FactLabel("buy", "moneyReliable.ct", Text(round.Ct.MoneyReliable)));
        labels.Add(new FactLabel("buy", "moneyReliable.t", Text(round.T.MoneyReliable)));

        labels.Add(new FactLabel("score", "round", Text(round.Number)));
        labels.Add(new FactLabel("score", "score.ct", Text(round.Ct.ScoreBefore)));
        labels.Add(new FactLabel("score", "score.t", Text(round.T.ScoreBefore)));
        Optional(labels, "score", "matchRound", round.MatchRoundNumber);
        labels.Add(new FactLabel("score", "half", LowerCamel(round.Half)));

        labels.Add(new FactLabel("end", "winner", round.WinnerLabel));
        labels.Add(new FactLabel("end", "endReason", round.EndReason.ToString()));
        Optional(labels, "end", "endTick", round.EndTick);
        Optional(labels, "end", "roundTime", round.RoundTimeSeconds);

        labels.Add(new FactLabel("plant", "plantSite", LowerCamel(round.PlantSite)));
        Optional(labels, "plant", "plantTick", round.PlantTick);
        Optional(labels, "plant", "plantSlot", round.PlanterSlot);
        Optional(labels, "plant", "defuseTick", round.DefuseTick);
        Optional(labels, "plant", "explodeTick", round.ExplodeTick);

        Optional(labels, "contact", "firstContactTick", round.FirstContactTick);
        Optional(labels, "contact", "openingKillTick", round.OpeningKillTick);

        if (atTick is int tick)
        {
            (int ct, int t) = RoundPhases.AliveAt(round, tick);
            labels.Add(new FactLabel("phase", "phase", LowerCamel(RoundPhases.At(round, tick))));
            labels.Add(new FactLabel("phase", "manCount.ct", Text(ct)));
            labels.Add(new FactLabel("phase", "manCount.t", Text(t)));
        }

        foreach ((string column, object? value) in round.Extra)
        {
            if (ToText(value) is { } text)
            {
                labels.Add(new FactLabel("extra", column, text));
            }
        }

        return labels;
    }

    /// <summary>
    ///     Whether one round passes a filter's round-level fields, <see cref="RoundFactsFilter.Where" /> included.
    ///     <see cref="RoundFactsFilter.Demos" /> is the caller's to apply, and the tick-anchored fields are
    ///     <see cref="MatchesAt" />'s.
    /// </summary>
    /// <param name="demoPath">The demo the round belongs to: what <see cref="RoundFactsFilter.Where" /> sees.</param>
    /// <param name="round">The round.</param>
    /// <param name="filter">The filter.</param>
    public static bool Matches(string demoPath, RoundFacts round, RoundFactsFilter filter)
    {
        ArgumentNullException.ThrowIfNull(demoPath);
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(filter);

        if (filter.LiveOnly && !round.IsLive)
        {
            return false;
        }

        if (filter.BuyCt is { } buyCt && round.Ct.BuyType != buyCt)
        {
            return false;
        }

        if (filter.BuyT is { } buyT && round.T.BuyType != buyT)
        {
            return false;
        }

        if (filter.WinnerSide is { } winner && round.WinnerSide != winner)
        {
            return false;
        }

        if (filter.EndReason is { } reason && round.EndReason != reason)
        {
            return false;
        }

        if (filter.PlantSite is { } site && round.PlantSite != site)
        {
            return false;
        }

        if (filter.Half is { } half && round.Half != half)
        {
            return false;
        }

        if (filter.Extra is not null)
        {
            foreach ((string column, string expected) in filter.Extra)
            {
                if (!round.Extra.TryGetValue(column, out object? value)
                    || !string.Equals(ToText(value), expected, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        if (filter.Score is { } score && !ScoreMatches(round, score))
        {
            return false;
        }

        return filter.Where is null || filter.Where(demoPath, round);
    }

    /// <summary>
    ///     Whether the tick-anchored fields pass at one tick: the phase, the clock band and the alive counts. True
    ///     when the filter has none.
    /// </summary>
    /// <param name="round">The round the tick falls in.</param>
    /// <param name="filter">The filter.</param>
    /// <param name="tick">Frame clock.</param>
    /// <param name="tickRate">Ticks per second, for the clock band; 0 fails a band rather than guessing.</param>
    public static bool MatchesAt(RoundFacts round, RoundFactsFilter filter, int tick, int tickRate)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(filter);

        if (filter.Phase is { } phase)
        {
            // Retake is the CT name for the post-plant interval; the filter asks with no side.
            RoundPhase at = RoundPhases.At(round, tick);
            RoundPhase wanted = phase == RoundPhase.Retake ? RoundPhase.PostPlant : phase;
            if (at != wanted)
            {
                return false;
            }
        }

        if (filter.ClockBand is { } band && (tickRate <= 0 || BandOf(round, tick, tickRate) != band))
        {
            return false;
        }

        if (filter.ManCount is not null || filter.CtAlive is not null || filter.TAlive is not null)
        {
            (int ct, int t) = RoundPhases.AliveAt(round, tick);
            if (filter.CtAlive is { } wantCt && ct != wantCt)
            {
                return false;
            }

            if (filter.TAlive is { } wantT && t != wantT)
            {
                return false;
            }

            if (filter.ManCount is { } state && state != (ct == t ? ManCountState.Even : ct > t ? ManCountState.CtUp : ManCountState.TUp))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Whether some tick of the round's live window passes <see cref="MatchesAt" />. Every tick-anchored
    ///     predicate is piecewise constant between the round's own ticks and the band edges, so testing each
    ///     breakpoint is exact.
    /// </summary>
    /// <param name="round">The round.</param>
    /// <param name="filter">The filter.</param>
    /// <param name="tickRate">Ticks per second.</param>
    public static bool MatchesAnywhere(RoundFacts round, RoundFactsFilter filter, int tickRate)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(filter);

        int end = round.EndTick ?? int.MaxValue;
        List<int> breakpoints = [round.FreezeEndTick];
        breakpoints.AddRange(round.Kills.Select(k => k.Tick));
        if (round.OpeningKillTick is int opening)
        {
            breakpoints.Add(opening);
        }

        if (round.PlantTick is int plant)
        {
            breakpoints.Add(plant);
        }

        if (tickRate > 0)
        {
            breakpoints.Add(round.FreezeEndTick + EarlyBandSeconds * tickRate);
            breakpoints.Add(round.FreezeEndTick + LateBandSeconds * tickRate);
        }

        return breakpoints.Any(tick => tick >= round.FreezeEndTick && tick < end && MatchesAt(round, filter, tick, tickRate));
    }

    private static ClockBand BandOf(RoundFacts round, int tick, int tickRate)
    {
        int seconds = (tick - round.FreezeEndTick) / tickRate;
        return seconds < EarlyBandSeconds ? ClockBand.Early : seconds < LateBandSeconds ? ClockBand.Middle : ClockBand.Late;
    }

    private static bool ScoreMatches(RoundFacts round, ScoreSituation score)
    {
        int ct = round.Ct.ScoreBefore;
        int t = round.T.ScoreBefore;
        return score switch
        {
            ScoreSituation.Tied => ct == t,
            ScoreSituation.CtLeading => ct > t,
            ScoreSituation.TLeading => t > ct,
            // The thresholds are per side but carry the same regulation length, so either side's copy answers.
            ScoreSituation.MatchPoint => Math.Max(ct, t) == round.Ct.Thresholds.RegulationRounds / 2,
            _ => true
        };
    }

    private static void Optional(List<FactLabel> labels, string group, string key, int? value)
    {
        if (value is int v)
        {
            labels.Add(new FactLabel(group, key, Text(v)));
        }
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(bool value) => value ? "true" : "false";

    private static string Join(int[] slots) =>
        string.Join(",", slots.Select(s => s.ToString(CultureInfo.InvariantCulture)));

    // An Extra cell read back from disk is a JsonElement; one fresh from the engine is a boxed scalar.
    private static string? ToText(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return text;
            case bool flag:
                return flag ? "true" : "false";
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Array => string.Join(",", element.EnumerateArray().Select(e => ToText(e) ?? "")),
                    _ => element.GetRawText()
                };
            case IEnumerable items:
                return string.Join(",", items.Cast<object?>().Select(v => ToText(v) ?? ""));
            default:
                return value.ToString();
        }
    }
}

/// <summary>
///     Round Facts: the per-round, per-side rows the core <c>round_facts</c> ruleset writes for every demo, read
///     through <see cref="IAnalysisFacts.RoundFacts" />. Values are absolute per side (<c>ct</c> / <c>t</c>).
/// </summary>
public interface IRoundFacts
{
    /// <summary>The shape rows are written at now: <see cref="RoundFactsRows.CurrentSchema" />.</summary>
    int Schema { get; }

    /// <summary>One demo's rows, or null when none were written. Reads the demo's record: call it off the UI thread.</summary>
    /// <param name="demoPath">Any path of the demo.</param>
    RoundFactsRows? TryGet(string demoPath);

    /// <summary>The round whose window holds <paramref name="frameClockTick" />, or null before the first freeze end or without rows.</summary>
    /// <param name="demoPath">Any path of the demo.</param>
    /// <param name="frameClockTick">Frame clock.</param>
    RoundFacts? RoundAt(string demoPath, int frameClockTick);

    /// <summary>
    ///     The labels of one round, absolute per side. The tick-anchored group (<c>phase</c>, <c>manCount.*</c>) is
    ///     present only when <paramref name="atTick" /> is given. Empty when the demo has no rows or no such round.
    /// </summary>
    /// <param name="demoPath">Any path of the demo.</param>
    /// <param name="round">The round number.</param>
    /// <param name="atTick">The tick the phase group is read at, or null.</param>
    IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null);

    /// <summary>Raised on the UI thread after a demo's rows were written; the argument is its path.</summary>
    event Action<string>? Updated;
}
