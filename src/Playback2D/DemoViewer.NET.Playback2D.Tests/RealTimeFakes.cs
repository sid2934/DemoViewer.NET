#region

using DemoViewer.NET.Playback2D.Core.Annotations;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Cadence and long-stroke builders for the real-time ink suites.
///     <para>
///         Separate from <c>AnnotationFakes</c> on purpose. That type's <c>Stroke</c> is a THREE-sample
///         stub, which is the right shape for "is this on the correct floor" and exactly the wrong shape
///         for a replay: a stroke with three points has no prefix to reveal, no tail to fade and no
///         outline cost to measure. Everything here is a real one, hundreds of samples, laid out
///         left to right so that "how far has the head got" is a pixel column.
///     </para>
/// </summary>
internal static class RealTimeFakes
{
    /// <summary>Samples in the standard replay fixture.</summary>
    public const int SampleCount = 200;

    /// <summary>World X of the first sample.</summary>
    public const float LeftWorld = -450f;

    /// <summary>World X of the last sample.</summary>
    public const float RightWorld = 450f;

    /// <summary>
    ///     A run table from explicit <c>(sample, tickOffset)</c> boundaries, the last of which is also the
    ///     duration. This is what <c>DrawTool</c> commits; building it by hand here is deliberate:
    ///     these suites must be able to author a cadence that no clock could be persuaded to produce.
    /// </summary>
    /// <param name="runs">Boundaries, ordered by sample index, offsets non-decreasing.</param>
    public static StrokeTiming Cadence(params (int Sample, int Tick)[] runs)
    {
        ArgumentNullException.ThrowIfNull(runs);

        TimingRun[] table = new TimingRun[runs.Length];
        for (int i = 0; i < runs.Length; i++)
        {
            table[i] = new TimingRun(runs[i].Sample, runs[i].Tick);
        }

        return new StrokeTiming(table, runs[^1].Tick);
    }

    /// <summary>One continuous motion at a constant speed, as the two-entry table §2 describes.</summary>
    /// <param name="sampleCount">Samples in the stroke.</param>
    /// <param name="durationTicks">Ticks from the first sample to the last.</param>
    public static StrokeTiming Steady(int sampleCount, int durationTicks) =>
        Cadence((0, 0), (sampleCount - 1, durationTicks));

    /// <summary>
    ///     A stroke drawn straight through, then <b>stopped</b> at the halfway sample, then finished. The
    ///     pause is what a viewer actually reads as "it is replaying me" (§2), so it is the sharp case.
    /// </summary>
    /// <param name="sampleCount">Samples in the stroke.</param>
    /// <param name="halfTicks">Ticks each moving half takes.</param>
    /// <param name="pauseTicks">Ticks the hand rested at the halfway sample.</param>
    public static StrokeTiming WithPause(int sampleCount, int halfTicks, int pauseTicks)
    {
        int mid = sampleCount / 2;
        return Cadence(
            (0, 0),
            (mid - 1, halfTicks),
            (mid, halfTicks + pauseTicks),
            (sampleCount - 1, halfTicks + pauseTicks + halfTicks));
    }

    /// <summary>
    ///     A left-to-right stroke spanning <see cref="LeftWorld" />..<see cref="RightWorld" />, offset in Y.
    /// </summary>
    /// <param name="count">Sample count.</param>
    /// <param name="y">World Y of every sample.</param>
    public static InkPoint[] Line(int count = SampleCount, float y = 0)
    {
        InkPoint[] points = new InkPoint[count];
        float step = (RightWorld - LeftWorld) / Math.Max(1, count - 1);
        for (int i = 0; i < count; i++)
        {
            points[i] = new InkPoint(LeftWorld + i * step, y, 0.5f);
        }

        return points;
    }

    /// <summary>The world X of a sample index in <see cref="Line" />.</summary>
    /// <param name="index">Sample index.</param>
    /// <param name="count">Sample count the line was built with.</param>
    public static float WorldXOf(int index, int count = SampleCount) =>
        LeftWorld + index * ((RightWorld - LeftWorld) / Math.Max(1, count - 1));

    /// <summary>
    ///     A real-time element: a <see cref="Line" /> with a cadence, over a trapezoid that opens at
    ///     <paramref name="from" />, holds and then fades out. <see cref="TimeEnvelope.FadeInTicks" /> is
    ///     0 throughout: a lead-in is a different animation and would blur every reveal assertion here.
    /// </summary>
    /// <param name="timing">The authoring cadence.</param>
    /// <param name="from">The tick the stroke starts drawing itself.</param>
    /// <param name="hold">Fully-opaque ticks each section gets before it starts to dissolve.</param>
    /// <param name="fadeOut">Lead-out length in ticks.</param>
    /// <param name="count">Sample count; must match the cadence's last boundary.</param>
    /// <param name="width">Stroke width in world units.</param>
    /// <param name="y">World Y of the stroke.</param>
    public static AnnotationElement RealTime(StrokeTiming timing, int from = 100, int hold = 4000,
        int fadeOut = 32, int count = SampleCount, float width = 20f, float y = 0) =>
        new(
            Guid.NewGuid(),
            AnnotationKind.Freehand,
            AnnotationStyle.Default with
            {
                WidthWorld = width
            },
            new SpaceRef.World(0),
            new TimeEnvelope(from, from + hold, 0, fadeOut),
            Line(count, y),
            null,
            timing);

    /// <summary>
    ///     The same geometry with <b>no</b> cadence: what every element in the persisted format is today,
    ///     and the byte-identity reference the real-time path is measured against.
    /// </summary>
    /// <param name="from">First fully-opaque tick.</param>
    /// <param name="hold">Fully-opaque ticks.</param>
    /// <param name="count">Sample count.</param>
    /// <param name="width">Stroke width in world units.</param>
    /// <param name="y">World Y of the stroke.</param>
    public static AnnotationElement Untimed(int from = 100, int hold = 4000,
        int count = SampleCount, float width = 20f, float y = 0) =>
        new(
            Guid.NewGuid(),
            AnnotationKind.Freehand,
            AnnotationStyle.Default with
            {
                WidthWorld = width
            },
            new SpaceRef.World(0),
            new TimeEnvelope(from, from + hold, 0, 0),
            Line(count, y),
            null);
}
