#region

using DemoViewer.NET.Extensions.StratBook.Playback2D.Keyframes;
using DemoViewer.NET.Playback2D.Core.Timeline;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.StratBook.Canvas;

/// <summary>
///     The strat canvas's timeline axis: the strat frame clock is the frame index,
///     one frame per tick from the round start to the last step, and there are no demo events. The tracks
///     draw from the projection, not from here.
/// </summary>
/// <param name="lastTick">The last step's tick.</param>
public sealed class StratTimelineData(int lastTick) : ITimelineData
{
    /// <inheritdoc />
    public int TotalFrames { get; } = Math.Max(0, lastTick) + 1;

    /// <inheritdoc />
    public int TickRate => StepSchedule.TicksPerSecond;

    /// <inheritdoc />
    public int FrameIndexAtTick(int tick) => tick >= 0 && tick < TotalFrames ? tick : -1;

    /// <inheritdoc />
    public IReadOnlyList<int> FramesForEvent(string eventName) => [];

    /// <inheritdoc />
    public IReadOnlyList<TimelineEventRecord> EventsOfType(string eventName) => [];

    /// <inheritdoc />
    public bool HasEvent(string eventName) => false;
}
