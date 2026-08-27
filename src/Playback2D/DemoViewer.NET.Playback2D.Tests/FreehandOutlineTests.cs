#region

using System.Text.Json;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Ink;
using SkiaSharp;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The perfect-freehand port, pinned against reference vectors generated from upstream v1.2.2 and
///     checked into <c>tests/fixtures/playback2d/freehand/</c>.
///     <para>
///         <b>Point counts are asserted exactly.</b> ~300 lines of vector maths with arc insertion at
///         sharp corners and taper easing will happily produce a plausible-looking wrong outline; a
///         differing point count means a structurally wrong port, never rounding, so it is the assertion
///         that catches a mis-ported branch.
///     </para>
/// </summary>
public class FreehandOutlineTests
{
    private const double RelativeTolerance = 1e-6;
    private const double AbsoluteTolerance = 1e-4;

    [Test]
    [Arguments("straight")]
    [Arguments("pressure-curve")]
    [Arguments("sharp-corner")]
    public async Task Matches_ReferenceVector(string name)
    {
        FreehandReference reference = FreehandReference.Load(name);

        List<StrokePoint> points = [];
        FreehandOutline.GetStrokePoints(reference.Input, in reference.Options, points);

        await Assert.That(points.Count).IsEqualTo(reference.StrokePoints.Count)
            .Because($"{name}: stroke-point COUNT differs — the streamline/minimum-length stage is " +
                     "structurally wrong, not just imprecise");

        for (int i = 0; i < points.Count; i++)
        {
            double[] expected = reference.StrokePoints[i];
            StrokePoint actual = points[i];
            await Close(actual.X, expected[0], $"{name} strokePoints[{i}].x");
            await Close(actual.Y, expected[1], $"{name} strokePoints[{i}].y");
            await Close(actual.Pressure, expected[2], $"{name} strokePoints[{i}].pressure");
            await Close(actual.VectorX, expected[3], $"{name} strokePoints[{i}].vector.x");
            await Close(actual.VectorY, expected[4], $"{name} strokePoints[{i}].vector.y");
            await Close(actual.Distance, expected[5], $"{name} strokePoints[{i}].distance");
            await Close(actual.RunningLength, expected[6], $"{name} strokePoints[{i}].runningLength");
        }

        List<SKPoint> outline = [];
        FreehandOutline.GetStrokeOutline(points, in reference.Options, outline);

        await Assert.That(outline.Count).IsEqualTo(reference.Outline.Count)
            .Because($"{name}: outline COUNT differs — a mis-ported cap, taper or corner branch");

        for (int i = 0; i < outline.Count; i++)
        {
            await Close(outline[i].X, reference.Outline[i][0], $"{name} outline[{i}].x");
            await Close(outline[i].Y, reference.Outline[i][1], $"{name} outline[{i}].y");
        }
    }

    /// <summary>
    ///     What <c>DrawTool</c> commits for a tap: two coincident samples. Upstream's streamline pass
    ///     collapses them to one stroke point, which is what selects its dot branch — so this, not a
    ///     lone sample, is the path that produces an exact circle.
    /// </summary>
    [Test]
    public async Task CoincidentPair_ProducesAClosedDot()
    {
        List<StrokePoint> scratch = [];
        List<SKPoint> outline = [];
        FreehandOptions options = FreehandOptions.Default;
        SKPoint centre = new(10, 10);

        FreehandOutline.GetOutline([new InkPoint(10, 10, 0.5f), new InkPoint(10, 10, 0.5f)],
            in options, scratch, outline);

        await Assert.That(scratch.Count).IsEqualTo(1);
        await Assert.That(outline.Count).IsGreaterThan(8);

        double radius = Distance(outline[0], centre);
        for (int i = 1; i < outline.Count; i++)
        {
            await Assert.That(Math.Abs(Distance(outline[i], centre) - radius)).IsLessThan(1e-3);
        }
    }

    /// <summary>
    ///     A lone sample gains a companion one unit away on both axes (upstream's own expansion), so it
    ///     draws as a very short capped stroke rather than a circle. It still has to be a closed blob
    ///     around the sample, roughly the stroke's own width across.
    /// </summary>
    [Test]
    public async Task SinglePoint_ProducesAClosedBlobAroundTheSample()
    {
        List<StrokePoint> scratch = [];
        List<SKPoint> outline = [];
        FreehandOptions options = FreehandOptions.Default;

        FreehandOutline.GetOutline([new InkPoint(10, 10, 0.5f)], in options, scratch, outline);

        await Assert.That(outline.Count).IsGreaterThan(8);

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < outline.Count; i++)
        {
            minX = Math.Min(minX, outline[i].X);
            minY = Math.Min(minY, outline[i].Y);
            maxX = Math.Max(maxX, outline[i].X);
            maxY = Math.Max(maxY, outline[i].Y);
        }

        await Assert.That(minX).IsLessThan(10f);
        await Assert.That(maxX).IsGreaterThan(10f);
        await Assert.That(minY).IsLessThan(10f);
        await Assert.That(maxY).IsGreaterThan(10f);
        await Assert.That(maxX - minX).IsLessThan((float)options.Size * 1.5f);
        await Assert.That(maxY - minY).IsLessThan((float)options.Size * 1.5f);
    }

    [Test]
    public async Task Outline_IsClosed_And_EnclosesTheInput_ForSmoothInput()
    {
        InkPoint[] input =
        [
            new(0, 0, 0.5f), new(20, 4, 0.5f), new(40, 10, 0.5f), new(60, 18, 0.5f), new(80, 24, 0.5f)
        ];

        List<StrokePoint> scratch = [];
        List<SKPoint> outline = [];
        FreehandOptions options = FreehandOptions.Default;
        FreehandOutline.GetOutline(input, in options, scratch, outline);

        await Assert.That(outline.Count).IsGreaterThan(3);

        // Closed: consecutive vertices never jump further than the stroke is wide, including the
        // wrap-around edge. A self-intersecting or torn outline shows up here as a long edge.
        double longest = 0;
        for (int i = 0; i < outline.Count; i++)
        {
            longest = Math.Max(longest, Distance(outline[i], outline[(i + 1) % outline.Count]));
        }

        await Assert.That(longest).IsLessThan(options.Size * 2);
    }

    [Test]
    public async Task Streamline_Zero_PreservesInputPositions()
    {
        InkPoint[] input =
        [
            new(0, 0, 0.5f), new(30, 0, 0.5f), new(60, 0, 0.5f), new(90, 0, 0.5f)
        ];

        FreehandOptions options = FreehandOptions.Default with
        {
            Streamline = 0
        };

        List<StrokePoint> points = [];
        FreehandOutline.GetStrokePoints(input, in options, points);

        await Assert.That(points.Count).IsEqualTo(4);
        for (int i = 0; i < points.Count; i++)
        {
            await Close(points[i].X, input[i].X, $"strokePoints[{i}].x");
            await Close(points[i].Y, input[i].Y, $"strokePoints[{i}].y");
        }
    }

    /// <summary>
    ///     §6's budget is zero bytes per frame, and a wet stroke is re-outlined on every frame it is
    ///     live. Warm lists plus the outliner's thread-static buffers must therefore allocate nothing.
    ///     <para>
    ///         <b><c>NotInParallel</c>, like <c>BudgetTests</c>.</b>
    ///         <c>GC.GetAllocatedBytesForCurrentThread</c> measures the THREAD, not this method, so a
    ///         sibling test whose async continuation lands on the same pool thread between the two reads
    ///         is counted as this outliner's allocation. Observed once in a busy six-project run and
    ///         green on every isolated one: it fails for a reason internal to the test runner's thread
    ///         reuse, not this outliner's code, and a required lane that flakes like that gets muted. The
    ///         repo already made this same call twice before (<c>ExportHudAndLadderTests</c>' two-window
    ///         rewrite, <c>TimelineLayoutTests</c>' environment-dependent literal).
    ///     </para>
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task NoAllocation_OnWarmLists()
    {
        InkPoint[] input = new InkPoint[64];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = new InkPoint(i * 3f, MathF.Sin(i * 0.2f) * 20f, 0.5f);
        }

        FreehandOptions options = FreehandOptions.Default;
        List<StrokePoint> scratch = new(256);
        List<SKPoint> outline = new(1024);

        for (int i = 0; i < 64; i++)
        {
            FreehandOutline.GetOutline(input, in options, scratch, outline);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            FreehandOutline.GetOutline(input, in options, scratch, outline);
        }

        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(delta).IsEqualTo(0);
    }

    private static double Distance(SKPoint a, SKPoint b) =>
        Math.Sqrt((a.X - b.X) * (double)(a.X - b.X) + (a.Y - b.Y) * (double)(a.Y - b.Y));

    private static async Task Close(double actual, double expected, string what)
    {
        double tolerance = Math.Max(AbsoluteTolerance, Math.Abs(expected) * RelativeTolerance);
        await Assert.That(Math.Abs(actual - expected)).IsLessThanOrEqualTo(tolerance)
            .Because($"{what}: expected {expected}, got {actual}");
    }
}

/// <summary>One checked-in perfect-freehand reference vector.</summary>
internal sealed class FreehandReference
{
    private FreehandReference(FreehandOptions options, InkPoint[] input,
        IReadOnlyList<double[]> strokePoints, IReadOnlyList<double[]> outline)
    {
        Options = options;
        Input = input;
        StrokePoints = strokePoints;
        Outline = outline;
    }

    /// <summary>The options upstream was called with.</summary>
    public readonly FreehandOptions Options;

    /// <summary>The raw input samples.</summary>
    public readonly InkPoint[] Input;

    /// <summary>Upstream's <c>getStrokePoints</c> output, flattened.</summary>
    public IReadOnlyList<double[]> StrokePoints { get; }

    /// <summary>Upstream's <c>getStroke</c> output.</summary>
    public IReadOnlyList<double[]> Outline { get; }

    /// <summary>Loads a named vector from the committed corpus.</summary>
    /// <param name="name">e.g. <c>sharp-corner</c>.</param>
    public static FreehandReference Load(string name)
    {
        string path = Path.Combine(FixtureCorpus.Root, "freehand", name + ".json");
        if (!File.Exists(path))
        {
            throw new SkipTestException($"reference vector missing: {path}");
        }

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = json.RootElement;
        JsonElement o = root.GetProperty("options");

        FreehandOptions options = new(
            o.GetProperty("size").GetDouble(),
            o.GetProperty("thinning").GetDouble(),
            o.GetProperty("smoothing").GetDouble(),
            o.GetProperty("streamline").GetDouble(),
            o.GetProperty("simulatePressure").GetBoolean(),
            true, 0, true, 0);

        List<InkPoint> input = [];
        foreach (JsonElement sample in root.GetProperty("input").EnumerateArray())
        {
            input.Add(new InkPoint(
                (float)sample[0].GetDouble(), (float)sample[1].GetDouble(), (float)sample[2].GetDouble()));
        }

        return new FreehandReference(options, [.. input],
            ReadRows(root.GetProperty("strokePoints")), ReadRows(root.GetProperty("outline")));
    }

    private static List<double[]> ReadRows(JsonElement array)
    {
        List<double[]> rows = [];
        foreach (JsonElement row in array.EnumerateArray())
        {
            double[] values = new double[row.GetArrayLength()];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = row[i].GetDouble();
            }

            rows.Add(values);
        }

        return rows;
    }
}
