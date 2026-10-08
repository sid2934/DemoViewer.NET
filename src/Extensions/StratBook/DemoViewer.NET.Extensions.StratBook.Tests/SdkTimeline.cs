#region

using DemoViewer.NET.Extensions.Sdk.Playback;
using Core = DemoViewer.NET.Playback2D.Core.Timeline;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>A core timeline fake as an SDK track reads it, the way the host hands it over.</summary>
internal sealed class SdkTimeline(Core.ITimelineData inner) : ITimelineData
{
    public int TotalFrames => inner.TotalFrames;

    public int TickRate => inner.TickRate;

    public int FrameIndexAtTick(int tick) => inner.FrameIndexAtTick(tick);

    public IReadOnlyList<TimelineEvent> EventsOfType(string eventName) =>
        [.. inner.EventsOfType(eventName).Select(e => new TimelineEvent(e.Tick, e.FrameIndex, e.Fields))];

    public bool HasEvent(string eventName) => inner.HasEvent(eventName);
}
