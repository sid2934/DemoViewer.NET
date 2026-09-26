#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.ViewModels.Stats;

/// <summary>
///     An aim-relevant signal, named for the wire event (or net message) that carries it.
///     <para>
///         Membership is deliberately narrow: these are the signals the metrics in
///         <see cref="AimMetric" /> read, not every event a demo can carry.
///     </para>
/// </summary>
public enum AimSignal
{
    /// <summary><c>bullet_damage</c>: one fire per damaging bullet, carrying the damage it did.</summary>
    BulletDamage,

    /// <summary><c>bullet_impact</c>: one fire per bullet landing, hit or miss, with the impact point.</summary>
    BulletImpact,

    /// <summary><c>weapon_fire</c>: one fire per shot leaving a weapon. The denominator of every rate.</summary>
    WeaponFire,

    /// <summary><c>player_hurt</c>: damage applied to a player, with the hitgroup it landed on.</summary>
    PlayerHurt,

    /// <summary><c>player_blind</c>: a flash landing on a player, with how long it held.</summary>
    PlayerBlind,

    /// <summary><c>weapon_zoom</c>: a scope going in or out.</summary>
    WeaponZoom,

    /// <summary><c>player_footstep</c>: one fire per step taken, attributed to the player who took it.</summary>
    PlayerFootstep,

    /// <summary>
    ///     <c>player_sound</c>: the broader "a player emitted a sound" event, footsteps included behind a
    ///     step flag. Sources observed so far ship one of this and <see cref="PlayerFootstep" />, never both.
    /// </summary>
    PlayerSound,

    /// <summary>
    ///     <c>svc_UserCmds</c>: the sub-tick input stream. Counted as net-message payloads rather than
    ///     game events, and the only signal here whose usefulness is a density rather than a presence
    ///     (see <see cref="SubtickObservation" />).
    /// </summary>
    UserCmds
}

/// <summary>
///     An aim metric a consumer might want to compute. Each one is answered by
///     <see cref="AimCapabilityProbe" /> with a <see cref="MetricVerdict" /> derived from what the demo
///     actually contains.
/// </summary>
public enum AimMetric
{
    /// <summary>Shots taken, per player and per round.</summary>
    ShotsFired,

    /// <summary>Hits divided by shots.</summary>
    HitAccuracy,

    /// <summary>Share of hits that landed on the head.</summary>
    HeadshotRate,

    /// <summary>Damage divided by shots taken.</summary>
    DamagePerShot,

    /// <summary>Where each bullet in a burst landed, misses included.</summary>
    SprayTrace,

    /// <summary>Aim quality while the shooter was flashed.</summary>
    FlashedAiming,

    /// <summary>Aim quality while scoped, and the scope discipline around it.</summary>
    ScopedAiming,

    /// <summary>Aim that a sound cue preceded: pre-aiming a step, holding an angle on audio.</summary>
    AudioCuedAiming,

    /// <summary>Aim resolved below tick granularity, from the sub-tick input stream.</summary>
    SubtickAimTiming,

    /// <summary>Any of the above sliced per round, which needs the demo to be segmentable at all.</summary>
    PerRoundAim
}

/// <summary>
///     How well a demo supports one <see cref="AimMetric" />.
///     <para>
///         <see cref="Degraded" /> is the state that makes this probe worth having. A signal that is
///         present but thin, or a substitute standing in for the signal a metric really wants, produces a
///         number rather than an error, and a consumer that only asks "present or absent" ships that
///         number as if it were the real one.
///     </para>
/// </summary>
public enum MetricSupport
{
    /// <summary>The demo carries nothing the metric can be computed from. Computing it yields zeros.</summary>
    Unsupported,

    /// <summary>Computable, but from a substitute or from too little data. The number will be biased.</summary>
    Degraded,

    /// <summary>The signals the metric wants are present at a usable density.</summary>
    Supported
}

/// <summary>
///     What one <see cref="AimSignal" /> amounts to in a demo: how many times it fired, and how dense
///     that is against two independent denominators (rounds and ticks), because a raw count says nothing
///     without the length of the demo it came from.
/// </summary>
/// <param name="Signal">Which signal this describes.</param>
/// <param name="Wire">The wire name counted, e.g. <c>bullet_damage</c>.</param>
/// <param name="Count">Fires observed across the whole demo.</param>
/// <param name="PerRound">
///     <paramref name="Count" /> divided by the segmentable round count, or 0 when the demo could not be
///     segmented at all.
/// </param>
/// <param name="PerThousandTicks">
///     <paramref name="Count" /> per 1000 server ticks. Independent of round segmentation, so it stays
///     meaningful on a demo whose boundary events are missing.
/// </param>
/// <param name="Fields">
///     Decoded field names on the first fire, empty when the signal never fired. Two sources can ship the
///     same event with different fields, and a metric that reads a field the source omits is as broken as
///     one whose event is missing.
/// </param>
public sealed record SignalObservation(
    AimSignal Signal,
    string Wire,
    int Count,
    double PerRound,
    double PerThousandTicks,
    IReadOnlyList<string> Fields)
{
    /// <summary>True when the demo carries at least one fire.</summary>
    public bool Present => Count > 0;
}

/// <summary>
///     The sub-tick input stream, measured as a yield rather than a presence.
///     <para>
///         Two Valve matchmaking demos eight months apart, same parser and same code path, carried
///         1,324,574 and 1,540,561 <c>svc_UserCmds</c> payloads and yielded 0.026 versus 0.88 sub-tick
///         events per payload: a 39x spread with both demos reporting the message as present. A consumer
///         that checks presence alone reads the first as fully instrumented.
///     </para>
/// </summary>
/// <param name="MessageCount">Total <c>svc_UserCmds</c> payloads across every frame.</param>
/// <param name="CarrierFrames">Frames carrying at least one payload.</param>
/// <param name="SampledMessages">
///     Payloads the yield was computed from, the denominator of <paramref name="YieldPerMessage" />.
///     Equal to <paramref name="MessageCount" />: the walk is exact rather than sampled (see
///     <paramref name="Stats" />), the name kept so a caller reading the ratio still finds its
///     denominator here.
/// </param>
/// <param name="SampledEvents">Sub-tick events decoded out of those payloads.</param>
/// <param name="Stats">
///     Per-command outcome from the <c>UserCmdReconstructor</c> that walked the demo: how many
///     commands rebuilt from a full baseline versus a delta against one, and how many could not be
///     rebuilt at all (no baseline yet, out of order, or a decode failure). A current (build-10896+)
///     demo ships almost entirely <c>Delta</c>; a pre-10896 one ships almost entirely <c>Full</c>
///     because it never carries <c>delta_data</c> to begin with.
/// </param>
/// <param name="YieldPerMessage">
///     <paramref name="SampledEvents" /> divided by <paramref name="SampledMessages" />, or 0 when there
///     were no payloads to decode.
/// </param>
public sealed record SubtickObservation(
    int MessageCount,
    int CarrierFrames,
    int SampledMessages,
    int SampledEvents,
    UserCmdReconstructionStats Stats,
    double YieldPerMessage)
{
    /// <summary>True when the demo carries any sub-tick input at all.</summary>
    public bool Present => MessageCount > 0;

    /// <summary>
    ///     Share of reconstructed commands that came from a delta rather than a full baseline, in
    ///     [0, 1]. 0 when nothing reconstructed at all (an empty demo, or every command unreadable).
    ///     Near 1 is the current-demo shape this item exists for; near 0 with <see cref="Present" />
    ///     true is the pre-10896 shape, where every command already arrives self-contained.
    /// </summary>
    public double DeltaShare => Stats.Full + Stats.Delta > 0
        ? (double)Stats.Delta / (Stats.Full + Stats.Delta)
        : 0.0;
}

/// <summary>
///     What <c>player_hurt</c>'s hitgroup field carries in practice.
///     <para>
///         Presence of the field proves nothing: a source that always writes the generic group makes
///         every hit read as the same body part, and a headshot rate computed off it is a constant rather
///         than a measurement. Only the observed variety says whether the field is populated.
///     </para>
/// </summary>
/// <param name="Fires"><c>player_hurt</c> fires whose payload could be read.</param>
/// <param name="DistinctValues">
///     Distinct hitgroup values across those fires. 1 means the field never varies, which is as useless
///     as its absence.
/// </param>
/// <param name="NonZeroFires">Fires naming a specific body part (hitgroup 0 is the generic group).</param>
public sealed record HitGroupObservation(int Fires, int DistinctValues, int NonZeroFires);

/// <summary>
///     Which round boundary events the demo actually carries, and the segmentation they permit.
///     <para>
///         <c>round_start</c> and <c>round_end</c> were absent from every source tested (trimmed pro GOTV
///         and two Valve matchmaking demos alike), so segmenting on them produces one round covering the
///         whole match. The workable markers are <c>round_prestart</c>, <c>round_freeze_end</c> and
///         <c>round_officially_ended</c>, and not every source ships all three.
///     </para>
/// </summary>
/// <param name="StartEvent">Best available opening marker, or <c>null</c> when the demo carries none.</param>
/// <param name="StartCount">Fires of <paramref name="StartEvent" />.</param>
/// <param name="EndEvent">Best available closing marker, or <c>null</c> when the demo carries none.</param>
/// <param name="EndCount">Fires of <paramref name="EndEvent" />.</param>
/// <param name="RoundCount">Rounds the demo can be cut into, 0 when it carries no usable marker.</param>
/// <param name="Strategy">Human-readable description of the segmentation the markers permit.</param>
/// <param name="Counts">Every candidate boundary event and its fire count, zeros included.</param>
public sealed record RoundBoundaryObservation(
    string? StartEvent,
    int StartCount,
    string? EndEvent,
    int EndCount,
    int RoundCount,
    string Strategy,
    IReadOnlyDictionary<string, int> Counts)
{
    /// <summary>True when the demo can be cut into rounds at all.</summary>
    public bool Segmentable => RoundCount > 0;
}

/// <summary>One metric's verdict, with the observation that produced it stated in words.</summary>
/// <param name="Metric">The metric judged.</param>
/// <param name="Support">The verdict.</param>
/// <param name="Reason">
///     Why, naming the signals and counts involved. Written to be shown to a user as it stands: a metric
///     that silently reads zero is the failure this probe exists to prevent, so the explanation travels
///     with the verdict instead of being reconstructed by each consumer.
/// </param>
public sealed record MetricVerdict(AimMetric Metric, MetricSupport Support, string Reason)
{
    /// <summary>True only for <see cref="MetricSupport.Supported" />: a degraded metric is not supported.</summary>
    public bool IsSupported => Support == MetricSupport.Supported;
}

/// <summary>
///     What one demo actually contains, for the aim metrics that read it.
///     <para>
///         <b>Observational, not declarative.</b> Capability is a property of the demo source rather than
///         of CS2, and a source's own advertisement of its capabilities is not reliable: the committed
///         <c>assets/tour/sample-de_nuke.dem</c> declares <c>HasPlayerBlind</c>,
///         <c>HasRoundOfficiallyEnded</c>, <c>HasWeaponReload</c> and <c>HasWeaponZoom</c> while carrying
///         zero fires of all four, and carries 2,249 <c>player_sound</c> fires it never declares.
///         Everything here is counted off the demo; <see cref="DeclarationDrift" /> records where the
///         counting and the advertisement disagree.
///     </para>
/// </summary>
/// <param name="SourceKind">The classifier's verdict on where the demo came from, for context only.</param>
/// <param name="DistinctEventTypes">
///     Distinct game-event names in the demo. A blunt richness measure that tracked the other findings
///     closely: 31 types on trimmed pro GOTV against 45 and 46 on the two Valve matchmaking demos.
/// </param>
/// <param name="TotalGameEvents">Game-event fires of every kind, the denominator for the above.</param>
/// <param name="Signals">Every <see cref="AimSignal" />, present or not. Never a partial map.</param>
/// <param name="Subtick">The sub-tick input stream, measured as a yield.</param>
/// <param name="HitGroups">Whether <c>player_hurt</c>'s hitgroup field is actually populated.</param>
/// <param name="Rounds">Which boundary events exist and what segmentation they permit.</param>
/// <param name="Metrics">One verdict per <see cref="AimMetric" />, in declaration order.</param>
/// <param name="DeclarationDrift">
///     One line per disagreement between the source's advertised <see cref="DemoFeatureSet" /> and the
///     observed fire counts. Empty when the advertisement holds.
/// </param>
public sealed record AimCapabilityReport(
    DemoSourceKind SourceKind,
    int DistinctEventTypes,
    int TotalGameEvents,
    IReadOnlyDictionary<AimSignal, SignalObservation> Signals,
    SubtickObservation Subtick,
    HitGroupObservation HitGroups,
    RoundBoundaryObservation Rounds,
    IReadOnlyList<MetricVerdict> Metrics,
    IReadOnlyList<string> DeclarationDrift)
{
    /// <summary>The observation for one signal. Always present: the probe fills every enum member.</summary>
    /// <param name="signal">The signal to look up.</param>
    public SignalObservation Observe(AimSignal signal) => Signals[signal];

    /// <summary>The verdict for one metric. Always present: the probe fills every enum member.</summary>
    /// <param name="metric">The metric to look up.</param>
    /// <exception cref="InvalidOperationException">If the report was built without this metric.</exception>
    public MetricVerdict Verdict(AimMetric metric)
    {
        foreach (MetricVerdict verdict in Metrics)
        {
            if (verdict.Metric == metric)
            {
                return verdict;
            }
        }

        throw new InvalidOperationException($"no verdict for {metric} in this report");
    }

    /// <summary>
    ///     Whether a metric can be computed honestly. False for both unsupported and degraded, because a
    ///     caller asking this is deciding whether to show a number, and a degraded number is the one that
    ///     misleads.
    /// </summary>
    /// <param name="metric">The metric to test.</param>
    public bool Supports(AimMetric metric) => Verdict(metric).IsSupported;
}
