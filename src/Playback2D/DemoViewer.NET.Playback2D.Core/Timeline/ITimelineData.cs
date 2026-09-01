namespace DemoViewer.NET.Playback2D.Core.Timeline;

/// <summary>
///     Normalized field keys an <see cref="ITimelineData" /> adapter writes and tracks read. The adapter owns
///     the demo-domain translation (payload property names, slot→display-name resolution, the per-tick team
///     lookup); a track only ever sees these eight keys, so it stays free of parser and host vocabulary.
/// </summary>
public static class TimelineEventKeys
{
    /// <summary>Resolved display name of the attacker (kills).</summary>
    public const string Attacker = "attacker";

    /// <summary>Resolved display name of the victim (kills).</summary>
    public const string Victim = "victim";

    /// <summary>Resolved display name of the assister, absent when there was none.</summary>
    public const string Assister = "assister";

    /// <summary>Weapon short name (kills).</summary>
    public const string Weapon = "weapon";

    /// <summary>"1" when the kill was a headshot; absent or "0" otherwise.</summary>
    public const string Headshot = "headshot";

    /// <summary>Bombsite label (bomb events).</summary>
    public const string Site = "site";

    /// <summary>Winning team number as a string (round_end): "2" = T, "3" = CT.</summary>
    public const string Winner = "winner";

    /// <summary>
    ///     The CREDITING side of an event that names an attacker, in <see cref="Winner" />'s encoding:
    ///     "2" = T, "3" = CT. ABSENT when the demo cannot say which side the attacker was on at that
    ///     tick. A consumer must fall back to its neutral rendering rather than guess, because
    ///     guessing paints a kill onto the wrong team's ledger.
    ///     <para>
    ///         Team is per-tick state, not identity: it is deliberately absent from the roster
    ///         (see <c>PlayerRosterEntry</c>) because it swaps at half, so an adapter has to resolve
    ///         it AT the event's tick, not at the playhead's.
    ///     </para>
    /// </summary>
    public const string Team = "team";

    /// <summary>
    ///     The VICTIM's side at the event's tick, in <see cref="Winner" />'s encoding. Absent under the
    ///     same rule as <see cref="Team" />.
    ///     <para>
    ///         A separate key rather than a second lookup at the consumer: the two sides are resolved by
    ///         one walk in the adapter, which is the only place that knows GOTV emits <c>player_team</c>
    ///         solely for the halftime swap. A consumer re-deriving it would be a second copy of that
    ///         finding, and the copies would drift.
    ///     </para>
    /// </summary>
    public const string VictimTeam = "victimteam";
}

/// <summary>One decoded demo event, already resolved onto the frame axis and flattened to strings.</summary>
public readonly record struct TimelineEventRecord(
    int Tick,
    int FrameIndex,
    IReadOnlyDictionary<string, string> Fields);

/// <summary>
///     The demo-shaped facts a track needs, in primitives only: no parser, host or UI types, so the
///     contract moves to Core unchanged. Implementations cache; a track may call any member freely.
/// </summary>
public interface ITimelineData
{
    /// <summary>Total frames in the demo; the timeline's x-axis domain. 0 when nothing is loaded.</summary>
    int TotalFrames { get; }

    /// <summary>Server tick rate (ticks/second).</summary>
    int TickRate { get; }

    /// <summary>First frame index at/after <paramref name="tick" />, or -1.</summary>
    int FrameIndexAtTick(int tick);

    /// <summary>Sorted, de-duplicated frame indices carrying <paramref name="eventName" />; empty when absent.</summary>
    IReadOnlyList<int> FramesForEvent(string eventName);

    /// <summary>Every occurrence of <paramref name="eventName" />, sorted by tick; empty when absent.</summary>
    IReadOnlyList<TimelineEventRecord> EventsOfType(string eventName);

    /// <summary>Whether this demo carries <paramref name="eventName" /> at all.</summary>
    bool HasEvent(string eventName);
}
