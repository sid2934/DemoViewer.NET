#region

using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;

#endregion

namespace DemoViewer.NET.Services.Facts;

/// <summary>
///     The SDK's frame-clock header as the annotation store's <see cref="ClockIdentity" />, field for field, so
///     every per-demo store reads the same header.
/// </summary>
public static class RoundFactsClocks
{
    /// <summary>The persisted form of a clock the frame-clock helper built.</summary>
    /// <param name="clock">The clock.</param>
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

    /// <summary>The header as a value the annotation store can compare.</summary>
    /// <param name="clock">The header.</param>
    public static ClockIdentity ToIdentity(this RoundFactsClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new ClockIdentity(clock.Kind, clock.TickRate, clock.FrameCount, clock.FirstTick, clock.LastTick);
    }

    /// <summary>The rows' header as a value the annotation store can compare; unknown when they carry none.</summary>
    /// <param name="rows">The rows.</param>
    public static ClockIdentity ClockIdentity(this RoundFactsRows rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Clock?.ToIdentity() ?? Playback2D.Pipeline.Annotations.ClockIdentity.Unknown;
    }
}
