// Adapted from perfect-freehand v1.2.2 (MIT, © 2021 Stephen Ruiz Ltd) — specifically
// packages/perfect-freehand/src/getStrokePoints.ts and getStrokeOutlinePoints.ts.
// See THIRD-PARTY-NOTICES.md § "perfect-freehand (MIT)" for the full licence text and the list of
// adapted files. Upstream: https://github.com/steveruizok/perfect-freehand

#region

using DemoViewer.NET.Playback2D.Core.Annotations;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Ink;

/// <summary>
///     The perfect-freehand stroke outliner, ported to C#.
///     <para>
///         <b>Two stages, kept separate.</b> <see cref="GetStrokePoints" /> streamlines the raw samples
///         and derives per-point vectors; <see cref="GetStrokeOutline" /> walks those and emits the closed
///         polygon. Each is tested against its own reference vectors, so a sign error in the second stage
///         cannot hide behind a plausible-looking first stage.
///     </para>
///     <para>
///         <b>Allocation discipline.</b> Every entry point writes into caller-supplied lists, and the
///         internal left/right/cap buffers are <c>[ThreadStatic]</c> and reused — the §6 budget is zero
///         bytes per frame, and a stroke is redrawn on every frame it is wet.
///     </para>
/// </summary>
public static class FreehandOutline
{
    // Upstream's RATE_OF_PRESSURE_CHANGE and FIXED_PI. FIXED_PI is deliberately a hair over π: a
    // rotation of exactly π lands the arc's last point on top of its first, which Skia then treats as a
    // degenerate edge.
    private const double RateOfPressureChange = 0.275;

    private static readonly double FixedPi = Math.PI + 1e-4;

    [ThreadStatic] private static List<Vec>? _left;
    [ThreadStatic] private static List<Vec>? _right;
    [ThreadStatic] private static List<Vec>? _startCap;
    [ThreadStatic] private static List<Vec>? _endCap;

    /// <summary>
    ///     Stage 1: streamline the raw samples and derive each point's back-vector, step distance and
    ///     running length. Clears <paramref name="output" /> first.
    /// </summary>
    /// <param name="input">Raw samples, oldest first.</param>
    /// <param name="o">Stroke options; only <c>Streamline</c> and <c>Size</c> are read.</param>
    /// <param name="output">Destination, cleared then filled.</param>
    public static void GetStrokePoints(ReadOnlySpan<InkPoint> input, in FreehandOptions o,
        List<StrokePoint> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        output.Clear();
        if (input.Length == 0)
        {
            return;
        }

        double t = 0.15 + (1 - o.Streamline) * 0.85;
        ExpandedInput points = new(input);

        points.Get(0, out double firstX, out double firstY, out double firstP, out bool firstHas);
        output.Add(new StrokePoint(firstX, firstY, firstHas && firstP >= 0 ? firstP : 0.25, 1, 1, 0, 0));

        bool reachedMinimumLength = false;
        double runningLength = 0;
        double prevX = firstX;
        double prevY = firstY;
        int max = points.Count - 1;

        for (int i = 1; i < points.Count; i++)
        {
            points.Get(i, out double rawX, out double rawY, out double rawP, out bool hasPressure);

            // Streamline: lerp the sample toward the previous ACCEPTED point.
            double x = prevX + (rawX - prevX) * t;
            double y = prevY + (rawY - prevY) * t;
            if (x.Equals(prevX) && y.Equals(prevY))
            {
                continue;
            }

            double distance = Hypot(x - prevX, y - prevY);
            runningLength += distance;

            // Until the stroke is at least `size` long, samples are folded into the running length but
            // not emitted — that is what keeps a tap from becoming a wobbly worm.
            if (i < max && !reachedMinimumLength)
            {
                if (runningLength < o.Size)
                {
                    continue;
                }

                reachedMinimumLength = true;
            }

            double vx = prevX - x;
            double vy = prevY - y;
            double length = Hypot(vx, vy);

            output.Add(new StrokePoint(x, y, hasPressure && rawP >= 0 ? rawP : 0.5,
                vx / length, vy / length, distance, runningLength));

            prevX = x;
            prevY = y;
        }

        // The first point has no previous one to point at, so it borrows the second's vector.
        StrokePoint head = output[0];
        if (output.Count > 1)
        {
            output[0] = head with
            {
                VectorX = output[1].VectorX,
                VectorY = output[1].VectorY
            };
        }
        else
        {
            output[0] = head with
            {
                VectorX = 0,
                VectorY = 0
            };
        }
    }

    /// <summary>
    ///     Stage 2: walk the stroke points and emit the closed outline polygon
    ///     (<c>left ++ endCap ++ reverse(right) ++ startCap</c>). Clears <paramref name="outline" /> first.
    /// </summary>
    /// <param name="points">Output of <see cref="GetStrokePoints" />.</param>
    /// <param name="o">Stroke options.</param>
    /// <param name="outline">Destination, cleared then filled.</param>
    public static void GetStrokeOutline(List<StrokePoint> points, in FreehandOptions o, List<SKPoint> outline)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(outline);

        outline.Clear();
        if (points.Count == 0 || o.Size <= 0)
        {
            return;
        }

        List<Vec> left = Scratch(ref _left);
        List<Vec> right = Scratch(ref _right);
        List<Vec> startCap = Scratch(ref _startCap);
        List<Vec> endCap = Scratch(ref _endCap);

        double size = o.Size;
        double thinning = o.Thinning;
        bool simulate = o.SimulatePressure;
        double totalLength = points[^1].RunningLength;
        double taperStart = o.TaperStart;
        double taperEnd = o.TaperEnd;
        double minDistance = size * o.Smoothing * (size * o.Smoothing);

        // Seed the pressure filter from the first ten samples, so a stroke does not open at whatever
        // pressure the very first sample happened to report.
        double prevPressure = points[0].Pressure;
        int seed = Math.Min(10, points.Count);
        for (int i = 0; i < seed; i++)
        {
            double pressure = points[i].Pressure;
            if (simulate)
            {
                double sp = Math.Min(1, points[i].Distance / size);
                double rp = Math.Min(1, 1 - sp);
                pressure = Math.Min(1, prevPressure + (rp - prevPressure) * (sp * RateOfPressureChange));
            }

            prevPressure = (prevPressure + pressure) / 2;
        }

        double radius = StrokeRadius(size, thinning, points[^1].Pressure);
        double firstRadius = double.NaN;
        double prevVecX = points[0].VectorX;
        double prevVecY = points[0].VectorY;
        Vec pl = new(points[0].X, points[0].Y);
        Vec pr = pl;
        Vec tl = pl;
        Vec tr = pr;
        bool isPrevPointSharpCorner = false;

        for (int i = 0; i < points.Count; i++)
        {
            StrokePoint sp0 = points[i];
            double pressure = sp0.Pressure;
            Vec point = new(sp0.X, sp0.Y);
            double vecX = sp0.VectorX;
            double vecY = sp0.VectorY;

            // The last three units of a stroke are dropped: their vectors are noise from the pointer
            // coming to rest, and they show up as a hook on the end cap.
            if (i < points.Count - 1 && totalLength - sp0.RunningLength < 3)
            {
                continue;
            }

            if (thinning != 0)
            {
                if (simulate)
                {
                    double s = Math.Min(1, sp0.Distance / size);
                    double r = Math.Min(1, 1 - s);
                    pressure = Math.Min(1, prevPressure + (r - prevPressure) * (s * RateOfPressureChange));
                }

                radius = StrokeRadius(size, thinning, pressure);
            }
            else
            {
                radius = size / 2;
            }

            if (double.IsNaN(firstRadius))
            {
                firstRadius = radius;
            }

            double ts = sp0.RunningLength < taperStart ? TaperStartEase(sp0.RunningLength / taperStart) : 1;
            double trail = totalLength - sp0.RunningLength;
            double te = trail < taperEnd ? TaperEndEase(trail / taperEnd) : 1;
            radius = Math.Max(0.01, radius * Math.Min(ts, te));

            double nextVecX = i < points.Count - 1 ? points[i + 1].VectorX : vecX;
            double nextVecY = i < points.Count - 1 ? points[i + 1].VectorY : vecY;
            double nextDpr = i < points.Count - 1 ? vecX * nextVecX + vecY * nextVecY : 1;
            double prevDpr = vecX * prevVecX + vecY * prevVecY;

            bool isPointSharpCorner = prevDpr < 0 && !isPrevPointSharpCorner;
            bool isNextPointSharpCorner = nextDpr < 0;

            if (isPointSharpCorner || isNextPointSharpCorner)
            {
                // A direction reversal: sweep a semicircle of offset points around the corner rather
                // than letting the two offsets cross and knot the outline.
                Vec offset = new(prevVecY * radius, -prevVecX * radius);
                for (double step = 1.0 / 13, t = 0; t <= 1; t += step)
                {
                    tl = RotAround(new Vec(point.X - offset.X, point.Y - offset.Y), point, FixedPi * t);
                    left.Add(tl);
                    tr = RotAround(new Vec(point.X + offset.X, point.Y + offset.Y), point, FixedPi * -t);
                    right.Add(tr);
                }

                pl = tl;
                pr = tr;
                if (isNextPointSharpCorner)
                {
                    isPrevPointSharpCorner = true;
                }

                continue;
            }

            isPrevPointSharpCorner = false;

            if (i == points.Count - 1)
            {
                Vec offset = new(vecY * radius, -vecX * radius);
                left.Add(new Vec(point.X - offset.X, point.Y - offset.Y));
                right.Add(new Vec(point.X + offset.X, point.Y + offset.Y));
                continue;
            }

            // Offset perpendicular to the direction the stroke is actually heading — the average of the
            // incoming and outgoing vectors, weighted by how sharply they disagree.
            double lx = nextVecX + (vecX - nextVecX) * nextDpr;
            double ly = nextVecY + (vecY - nextVecY) * nextDpr;
            Vec off = new(ly * radius, -lx * radius);

            tl = new Vec(point.X - off.X, point.Y - off.Y);
            if (i <= 1 || Dist2(pl, tl) > minDistance)
            {
                left.Add(tl);
                pl = tl;
            }

            tr = new Vec(point.X + off.X, point.Y + off.Y);
            if (i <= 1 || Dist2(pr, tr) > minDistance)
            {
                right.Add(tr);
                pr = tr;
            }

            prevPressure = pressure;
            prevVecX = vecX;
            prevVecY = vecY;
        }

        Vec firstPoint = new(points[0].X, points[0].Y);
        Vec lastPoint = points.Count > 1
            ? new Vec(points[^1].X, points[^1].Y)
            : new Vec(points[0].X + 1, points[0].Y + 1);

        if (points.Count == 1)
        {
            if (taperStart == 0 && taperEnd == 0)
            {
                // A dot: a full circle of radius `firstRadius` around the single sample.
                double dx = firstPoint.X - lastPoint.X;
                double dy = firstPoint.Y - lastPoint.Y;
                double ux = dy;
                double uy = -dx;
                double ulen = Hypot(ux, uy);
                ux /= ulen;
                uy /= ulen;

                double dotRadius = double.IsNaN(firstRadius) || firstRadius == 0 ? radius : firstRadius;
                Vec start = new(firstPoint.X - ux * dotRadius, firstPoint.Y - uy * dotRadius);
                for (double step = 1.0 / 13, t = step; t <= 1; t += step)
                {
                    outline.Add(ToSkia(RotAround(start, firstPoint, FixedPi * 2 * t)));
                }

                return;
            }
        }
        else
        {
            if (taperStart == 0)
            {
                if (o.CapStart)
                {
                    for (double step = 1.0 / 13, t = step; t <= 1; t += step)
                    {
                        startCap.Add(RotAround(right[0], firstPoint, FixedPi * t));
                    }
                }
                else
                {
                    Vec corners = new(left[0].X - right[0].X, left[0].Y - right[0].Y);
                    Vec a = new(corners.X * 0.5, corners.Y * 0.5);
                    Vec b = new(corners.X * 0.51, corners.Y * 0.51);
                    startCap.Add(new Vec(firstPoint.X - a.X, firstPoint.Y - a.Y));
                    startCap.Add(new Vec(firstPoint.X - b.X, firstPoint.Y - b.Y));
                    startCap.Add(new Vec(firstPoint.X + b.X, firstPoint.Y + b.Y));
                    startCap.Add(new Vec(firstPoint.X + a.X, firstPoint.Y + a.Y));
                }
            }

            Vec direction = new(-points[^1].VectorY, points[^1].VectorX);

            if (taperEnd != 0)
            {
                endCap.Add(lastPoint);
            }
            else if (o.CapEnd)
            {
                Vec start = new(lastPoint.X + direction.X * radius, lastPoint.Y + direction.Y * radius);
                for (double step = 1.0 / 29, t = step; t < 1; t += step)
                {
                    endCap.Add(RotAround(start, lastPoint, FixedPi * 3 * t));
                }
            }
            else
            {
                endCap.Add(new Vec(lastPoint.X + direction.X * radius, lastPoint.Y + direction.Y * radius));
                endCap.Add(new Vec(lastPoint.X + direction.X * radius * 0.99,
                    lastPoint.Y + direction.Y * radius * 0.99));
                endCap.Add(new Vec(lastPoint.X - direction.X * radius * 0.99,
                    lastPoint.Y - direction.Y * radius * 0.99));
                endCap.Add(new Vec(lastPoint.X - direction.X * radius, lastPoint.Y - direction.Y * radius));
            }
        }

        for (int i = 0; i < left.Count; i++)
        {
            outline.Add(ToSkia(left[i]));
        }

        for (int i = 0; i < endCap.Count; i++)
        {
            outline.Add(ToSkia(endCap[i]));
        }

        for (int i = right.Count - 1; i >= 0; i--)
        {
            outline.Add(ToSkia(right[i]));
        }

        for (int i = 0; i < startCap.Count; i++)
        {
            outline.Add(ToSkia(startCap[i]));
        }
    }

    /// <summary>
    ///     Raw samples → closed outline polygon. Allocation-free once <paramref name="scratch" /> and
    ///     <paramref name="outline" /> are warm.
    /// </summary>
    /// <param name="input">Raw samples, oldest first.</param>
    /// <param name="o">Stroke options.</param>
    /// <param name="scratch">Caller-owned buffer for the intermediate stroke points.</param>
    /// <param name="outline">Destination, cleared then filled.</param>
    public static void GetOutline(ReadOnlySpan<InkPoint> input, in FreehandOptions o,
        List<StrokePoint> scratch, List<SKPoint> outline)
    {
        GetStrokePoints(input, in o, scratch);
        GetStrokeOutline(scratch, in o, outline);
    }

    private static List<Vec> Scratch(ref List<Vec>? slot)
    {
        slot ??= new List<Vec>(256);
        slot.Clear();
        return slot;
    }

    private static double StrokeRadius(double size, double thinning, double pressure) =>
        size * (0.5 - thinning * (0.5 - pressure));

    private static double TaperStartEase(double t) => t * (2 - t);

    private static double TaperEndEase(double t)
    {
        double r = t - 1;
        return r * r * r + 1;
    }

    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    private static double Dist2(Vec a, Vec b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static Vec RotAround(Vec a, Vec centre, double radians)
    {
        double s = Math.Sin(radians);
        double c = Math.Cos(radians);
        double px = a.X - centre.X;
        double py = a.Y - centre.Y;
        return new Vec(px * c - py * s + centre.X, px * s + py * c + centre.Y);
    }

    private static SKPoint ToSkia(Vec v) => new((float)v.X, (float)v.Y);

    private readonly record struct Vec(double X, double Y);

    /// <summary>
    ///     Upstream's two degenerate-input expansions, presented as an indexable view so the port needs
    ///     no temporary array. A two-sample stroke becomes five interpolated points (whose pressure is
    ///     deliberately UNSET, because upstream's lerp drops the third array element); a one-sample
    ///     stroke gains a companion one unit away on both axes, keeping its pressure.
    /// </summary>
    private readonly ref struct ExpandedInput
    {
        private readonly ReadOnlySpan<InkPoint> _source;
        private readonly Mode _mode;

        public ExpandedInput(ReadOnlySpan<InkPoint> source)
        {
            _source = source;
            _mode = source.Length switch
            {
                2 => Mode.TwoPoint,
                1 => Mode.OnePoint,
                _ => Mode.Passthrough
            };

            Count = _mode switch
            {
                Mode.TwoPoint => 5,
                Mode.OnePoint => 2,
                _ => source.Length
            };
        }

        public int Count { get; }

        public void Get(int i, out double x, out double y, out double pressure, out bool hasPressure)
        {
            switch (_mode)
            {
                case Mode.TwoPoint when i > 0:
                {
                    double t = i / 4.0;
                    x = _source[0].X + (_source[1].X - _source[0].X) * t;
                    y = _source[0].Y + (_source[1].Y - _source[0].Y) * t;
                    pressure = 0;
                    hasPressure = false;
                    return;
                }

                case Mode.OnePoint when i > 0:
                    x = _source[0].X + 1;
                    y = _source[0].Y + 1;
                    pressure = _source[0].Pressure;
                    hasPressure = true;
                    return;

                default:
                    x = _source[i].X;
                    y = _source[i].Y;
                    pressure = _source[i].Pressure;
                    hasPressure = true;
                    return;
            }
        }

        private enum Mode
        {
            Passthrough,
            OnePoint,
            TwoPoint
        }
    }
}
