namespace DemoViewer.NET.Playback2D.Pipeline.Export.Gif;

/// <summary>
///     Xiaolin Wu's variance-minimising colour quantizer ("Efficient Statistical Computations for
///     Optimal Color Quantization", Graphics Gems II), over RGB at 5 bits per channel.
///     <para>
///         Pixels are added a frame at a time, so a global palette costs one histogram rather than a
///         second pass over every buffered frame. All moments are integers, so the palette depends only
///         on the pixels added, not on the order they were added in.
///     </para>
/// </summary>
internal sealed class WuColorQuantizer
{
    private const int IndexBits = 5;
    private const int Side = (1 << IndexBits) + 1;
    private const int TableLength = Side * Side * Side;

    private readonly long[] _m2 = new long[TableLength];
    private readonly long[] _mb = new long[TableLength];
    private readonly long[] _mg = new long[TableLength];
    private readonly long[] _mr = new long[TableLength];
    private readonly long[] _wt = new long[TableLength];

    /// <summary>Adds packed RGB pixels (3 bytes each) to the histogram.</summary>
    /// <param name="rgb">Pixels to count.</param>
    public void Add(ReadOnlySpan<byte> rgb)
    {
        for (int i = 0; i + 2 < rgb.Length; i += 3)
        {
            int r = rgb[i];
            int g = rgb[i + 1];
            int b = rgb[i + 2];
            int index = Index((r >> 3) + 1, (g >> 3) + 1, (b >> 3) + 1);
            _wt[index]++;
            _mr[index] += r;
            _mg[index] += g;
            _mb[index] += b;
            _m2[index] += (r * r) + (g * g) + (b * b);
        }
    }

    /// <summary>
    ///     Builds a palette of at most <paramref name="maxColors" /> entries, each the mean of the pixels
    ///     its box holds. Consumes the histogram: call once.
    /// </summary>
    /// <param name="maxColors">1 to 256.</param>
    /// <returns>Packed RGB, three bytes per entry; fewer entries when the pixels need fewer.</returns>
    public byte[] BuildPalette(int maxColors)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxColors, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxColors, 256);

        Accumulate();

        Box[] boxes = new Box[maxColors];
        double[] variance = new double[maxColors];
        boxes[0] = new Box { R1 = Side - 1, G1 = Side - 1, B1 = Side - 1 };

        int count = 1;
        int next = 0;
        while (count < maxColors)
        {
            if (Cut(ref boxes[next], ref boxes[count]))
            {
                variance[next] = boxes[next].Volume > 1 ? Variance(boxes[next]) : 0;
                variance[count] = boxes[count].Volume > 1 ? Variance(boxes[count]) : 0;
                count++;
            }
            else
            {
                variance[next] = 0;
            }

            next = 0;
            double best = variance[0];
            for (int k = 1; k < count; k++)
            {
                if (variance[k] > best)
                {
                    best = variance[k];
                    next = k;
                }
            }

            if (best <= 0)
            {
                break;
            }
        }

        List<byte> palette = new(count * 3);
        for (int k = 0; k < count; k++)
        {
            long weight = Volume(boxes[k], _wt);
            if (weight <= 0)
            {
                continue;
            }

            palette.Add(Mean(Volume(boxes[k], _mr), weight));
            palette.Add(Mean(Volume(boxes[k], _mg), weight));
            palette.Add(Mean(Volume(boxes[k], _mb), weight));
        }

        if (palette.Count == 0)
        {
            palette.AddRange([0, 0, 0]);
        }

        return [.. palette];
    }

    private static byte Mean(long sum, long weight) => (byte)Math.Clamp((sum + (weight / 2)) / weight, 0, 255);

    private static int Index(int r, int g, int b) => (((r * Side) + g) * Side) + b;

    private static long Volume(in Box c, long[] m) =>
        m[Index(c.R1, c.G1, c.B1)] - m[Index(c.R1, c.G1, c.B0)] - m[Index(c.R1, c.G0, c.B1)] +
        m[Index(c.R1, c.G0, c.B0)] - m[Index(c.R0, c.G1, c.B1)] + m[Index(c.R0, c.G1, c.B0)] +
        m[Index(c.R0, c.G0, c.B1)] - m[Index(c.R0, c.G0, c.B0)];

    private static long Bottom(in Box c, Axis axis, long[] m) => axis switch
    {
        Axis.Red => -m[Index(c.R0, c.G1, c.B1)] + m[Index(c.R0, c.G1, c.B0)] +
                    m[Index(c.R0, c.G0, c.B1)] - m[Index(c.R0, c.G0, c.B0)],
        Axis.Green => -m[Index(c.R1, c.G0, c.B1)] + m[Index(c.R1, c.G0, c.B0)] +
                      m[Index(c.R0, c.G0, c.B1)] - m[Index(c.R0, c.G0, c.B0)],
        _ => -m[Index(c.R1, c.G1, c.B0)] + m[Index(c.R1, c.G0, c.B0)] +
             m[Index(c.R0, c.G1, c.B0)] - m[Index(c.R0, c.G0, c.B0)]
    };

    private static long Top(in Box c, Axis axis, int position, long[] m) => axis switch
    {
        Axis.Red => m[Index(position, c.G1, c.B1)] - m[Index(position, c.G1, c.B0)] -
                    m[Index(position, c.G0, c.B1)] + m[Index(position, c.G0, c.B0)],
        Axis.Green => m[Index(c.R1, position, c.B1)] - m[Index(c.R1, position, c.B0)] -
                      m[Index(c.R0, position, c.B1)] + m[Index(c.R0, position, c.B0)],
        _ => m[Index(c.R1, c.G1, position)] - m[Index(c.R1, c.G0, position)] -
             m[Index(c.R0, c.G1, position)] + m[Index(c.R0, c.G0, position)]
    };

    // Turns the per-bin histogram into cumulative moments, so any box's totals are eight lookups.
    private void Accumulate()
    {
        long[] areaW = new long[Side];
        long[] areaR = new long[Side];
        long[] areaG = new long[Side];
        long[] areaB = new long[Side];
        long[] area2 = new long[Side];

        for (int r = 1; r < Side; r++)
        {
            Array.Clear(areaW);
            Array.Clear(areaR);
            Array.Clear(areaG);
            Array.Clear(areaB);
            Array.Clear(area2);

            for (int g = 1; g < Side; g++)
            {
                long lineW = 0, lineR = 0, lineG = 0, lineB = 0, line2 = 0;

                for (int b = 1; b < Side; b++)
                {
                    int index = Index(r, g, b);
                    lineW += _wt[index];
                    lineR += _mr[index];
                    lineG += _mg[index];
                    lineB += _mb[index];
                    line2 += _m2[index];

                    areaW[b] += lineW;
                    areaR[b] += lineR;
                    areaG[b] += lineG;
                    areaB[b] += lineB;
                    area2[b] += line2;

                    int below = Index(r - 1, g, b);
                    _wt[index] = _wt[below] + areaW[b];
                    _mr[index] = _mr[below] + areaR[b];
                    _mg[index] = _mg[below] + areaG[b];
                    _mb[index] = _mb[below] + areaB[b];
                    _m2[index] = _m2[below] + area2[b];
                }
            }
        }
    }

    private double Variance(in Box c)
    {
        double dr = Volume(c, _mr);
        double dg = Volume(c, _mg);
        double db = Volume(c, _mb);
        double xx = Volume(c, _m2);
        return xx - (((dr * dr) + (dg * dg) + (db * db)) / Volume(c, _wt));
    }

    private double Maximize(in Box c, Axis axis, int first, int last, out int cut,
        long wholeR, long wholeG, long wholeB, long wholeW)
    {
        long baseR = Bottom(c, axis, _mr);
        long baseG = Bottom(c, axis, _mg);
        long baseB = Bottom(c, axis, _mb);
        long baseW = Bottom(c, axis, _wt);

        double max = 0;
        cut = -1;

        for (int i = first; i < last; i++)
        {
            long halfR = baseR + Top(c, axis, i, _mr);
            long halfG = baseG + Top(c, axis, i, _mg);
            long halfB = baseB + Top(c, axis, i, _mb);
            long halfW = baseW + Top(c, axis, i, _wt);

            if (halfW == 0)
            {
                continue;
            }

            double temp = (((double)halfR * halfR) + ((double)halfG * halfG) + ((double)halfB * halfB)) / halfW;

            halfR = wholeR - halfR;
            halfG = wholeG - halfG;
            halfB = wholeB - halfB;
            halfW = wholeW - halfW;

            if (halfW == 0)
            {
                continue;
            }

            temp += (((double)halfR * halfR) + ((double)halfG * halfG) + ((double)halfB * halfB)) / halfW;

            if (temp > max)
            {
                max = temp;
                cut = i;
            }
        }

        return max;
    }

    private bool Cut(ref Box set1, ref Box set2)
    {
        long wholeR = Volume(set1, _mr);
        long wholeG = Volume(set1, _mg);
        long wholeB = Volume(set1, _mb);
        long wholeW = Volume(set1, _wt);

        double maxR = Maximize(set1, Axis.Red, set1.R0 + 1, set1.R1, out int cutR, wholeR, wholeG, wholeB, wholeW);
        double maxG = Maximize(set1, Axis.Green, set1.G0 + 1, set1.G1, out int cutG, wholeR, wholeG, wholeB, wholeW);
        double maxB = Maximize(set1, Axis.Blue, set1.B0 + 1, set1.B1, out int cutB, wholeR, wholeG, wholeB, wholeW);

        Axis axis;
        if (maxR >= maxG && maxR >= maxB)
        {
            if (cutR < 0)
            {
                return false;
            }

            axis = Axis.Red;
        }
        else
        {
            axis = maxG >= maxR && maxG >= maxB ? Axis.Green : Axis.Blue;
        }

        set2.R1 = set1.R1;
        set2.G1 = set1.G1;
        set2.B1 = set1.B1;

        switch (axis)
        {
            case Axis.Red:
                set2.R0 = set1.R1 = cutR;
                set2.G0 = set1.G0;
                set2.B0 = set1.B0;
                break;
            case Axis.Green:
                set2.G0 = set1.G1 = cutG;
                set2.R0 = set1.R0;
                set2.B0 = set1.B0;
                break;
            default:
                set2.B0 = set1.B1 = cutB;
                set2.R0 = set1.R0;
                set2.G0 = set1.G0;
                break;
        }

        return true;
    }

    private enum Axis
    {
        Red,
        Green,
        Blue
    }

    // R0/G0/B0 are exclusive lower bounds and R1/G1/B1 inclusive upper bounds, in histogram bins.
    private struct Box
    {
        public int R0;
        public int R1;
        public int G0;
        public int G1;
        public int B0;
        public int B1;

        public readonly int Volume => (R1 - R0) * (G1 - G0) * (B1 - B0);
    }
}
