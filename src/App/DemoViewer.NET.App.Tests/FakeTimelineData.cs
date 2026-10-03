#region

using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core.Timeline;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Hand-rolled <see cref="ITimelineData" />: dictionaries in, primitives out. Its own file, linked into
///     the extension test project too (TagTrackTests, TagLabelModeTests), so it is not pulled in alongside
///     <c>TimelineTrackTests</c>'s own tests.
/// </summary>
internal sealed class FakeTimelineData : ITimelineData
{
    public FakeTimelineData(int totalFrames) => TotalFrames = totalFrames;

    public Dictionary<string, int[]> EventFrames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TimelineEventRecord[]> Events { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, int> Ticks { get; } = new();

    public int TotalFrames { get; }
    public int TickRate => 64;

    public int FrameIndexAtTick(int tick) => Ticks.TryGetValue(tick, out int frame) ? frame : tick / 2;

    public IReadOnlyList<int> FramesForEvent(string eventName) =>
        EventFrames.TryGetValue(eventName, out int[]? frames) ? frames : Array.Empty<int>();

    public IReadOnlyList<TimelineEventRecord> EventsOfType(string eventName) =>
        Events.TryGetValue(eventName, out TimelineEventRecord[]? records)
            ? records
            : Array.Empty<TimelineEventRecord>();

    public bool HasEvent(string eventName) =>
        EventFrames.ContainsKey(eventName) || Events.ContainsKey(eventName);
}
