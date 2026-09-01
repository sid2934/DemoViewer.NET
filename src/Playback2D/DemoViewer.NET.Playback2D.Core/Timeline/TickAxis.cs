namespace DemoViewer.NET.Playback2D.Core.Timeline;

/// <summary>
///     A pure tick ↔ pixel mapping, used <b>inside</b> envelope drag math.
///     <para>
///         <b>Domain warning (integrator correction 6).</b> A1's timeline lays out on the
///         <i>frame-index</i> axis (design §5.6) and exposes
///         <c>Playback2DTimelineViewModel.XForFrame</c>/<c>FrameIndexAt</c>. This type must therefore
///         <b>never</b> be used to position anything in <c>TimelineControl</c>. A tick-keyed x on a
///         frame-index axis is a silent mis-placement, not an error anyone would see. The App builds one
///         of these per drag from A1's pixel mapping plus <c>ITimelineData.FrameIndexAtTick</c>, and
///         converts back at the seam. Annotation envelopes are authored in ticks, which is why the
///         conversion exists at all.
///     </para>
/// </summary>
/// <param name="FirstTick">Tick at x = 0.</param>
/// <param name="LastTick">Tick at x = <paramref name="PixelWidth" />.</param>
/// <param name="PixelWidth">Width of the axis in device-independent pixels.</param>
public readonly record struct TickAxis(int FirstTick, int LastTick, double PixelWidth)
{
    /// <summary>Ticks covered by one pixel. 0 on a degenerate axis.</summary>
    public double TicksPerPixel =>
        PixelWidth > 0 && LastTick > FirstTick ? (LastTick - FirstTick) / PixelWidth : 0;

    /// <summary>The x of a tick, clamped to the axis.</summary>
    /// <param name="tick">A tick.</param>
    public double XOf(int tick)
    {
        if (PixelWidth <= 0 || LastTick <= FirstTick)
        {
            return 0;
        }

        double t = (double)(tick - FirstTick) / (LastTick - FirstTick);
        return Math.Clamp(t, 0, 1) * PixelWidth;
    }

    /// <summary>The tick at an x, clamped to <c>[FirstTick, LastTick]</c>.</summary>
    /// <param name="x">A pixel offset from the axis origin.</param>
    public int TickAt(double x)
    {
        if (PixelWidth <= 0 || LastTick <= FirstTick)
        {
            return FirstTick;
        }

        double t = Math.Clamp(x / PixelWidth, 0, 1);
        return FirstTick + (int)Math.Round(t * (LastTick - FirstTick));
    }
}
