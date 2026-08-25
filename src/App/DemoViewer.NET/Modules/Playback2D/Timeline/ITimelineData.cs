namespace DemoViewer.NET.Modules.Playback2D.Timeline;

/// <summary>
///     Normalized field keys an <see cref="ITimelineData" /> adapter writes and tracks read. The adapter owns
///     the demo-domain translation (payload property names, slot→display-name resolution); a track only ever
///     sees these seven keys, so it stays free of parser and host vocabulary.
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
}

/// <summary>One decoded demo event, already resolved onto the frame axis and flattened to strings.</summary>
public readonly record struct TimelineEventRecord(
    int Tick,
    int FrameIndex,
    IReadOnlyDictionary<string, string> Fields);

/// <summary>
///     The demo-shaped facts a track needs, in primitives only — no parser, host or UI types, so the
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
