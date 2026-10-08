#region

using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>The SDK's frame-clock header as the annotation store's <see cref="ClockIdentity" />, field for field.</summary>
internal static class ClockHeaders
{
    /// <summary>The header as a value the annotation and tag stores compare.</summary>
    /// <param name="clock">The header.</param>
    public static ClockIdentity ToIdentity(this RoundFactsClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new ClockIdentity(clock.Kind, clock.TickRate, clock.FrameCount, clock.FirstTick, clock.LastTick);
    }
}
