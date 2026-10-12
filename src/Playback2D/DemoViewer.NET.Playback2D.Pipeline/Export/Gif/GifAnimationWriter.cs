namespace DemoViewer.NET.Playback2D.Pipeline.Export.Gif;

/// <summary>
///     Writes a looping GIF89a over one global palette.
///     <para>
///         Frames after the first are written as the bounding box of what changed, with unchanged pixels
///         left transparent over the previous frame (disposal "do not dispose"). Changed pixels are
///         mapped to the palette with Floyd-Steinberg error diffusion. Output depends only on the
///         frames and palette given.
///     </para>
/// </summary>
internal sealed class GifAnimationWriter
{
    /// <summary>The most colours a palette may hold; the last table slot is the transparent index.</summary>
    public const int MaxPaletteColors = 255;

    private const byte TransparentIndex = 255;
    private const int MinCodeSize = 8;

    private readonly int[] _errorRowA;
    private readonly int[] _errorRowB;
    private readonly int _height;
    private readonly GifLzwEncoder _lzw = new();
    private readonly Stream _output;
    private readonly byte[] _palette;
    private readonly int _paletteCount;
    private readonly int _width;

    // Exact nearest palette entry per 24-bit colour, plus one; zero means not computed yet.
    private readonly ushort[] _nearest = new ushort[1 << 24];
    private byte[] _indices = [];

    /// <summary>Writes the header, the global colour table and the loop extension.</summary>
    /// <param name="output">Receives the file. Not disposed by the writer.</param>
    /// <param name="width">Canvas width, 1 to 65535.</param>
    /// <param name="height">Canvas height, 1 to 65535.</param>
    /// <param name="palette">Packed RGB, 1 to <see cref="MaxPaletteColors" /> entries.</param>
    public GifAnimationWriter(Stream output, int width, int height, byte[] palette)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, ushort.MaxValue);

        if (palette.Length == 0 || palette.Length % 3 != 0 || palette.Length / 3 > MaxPaletteColors)
        {
            throw new ArgumentException("The palette must hold 1 to 255 packed RGB entries.", nameof(palette));
        }

        _output = output;
        _width = width;
        _height = height;
        _palette = palette;
        _paletteCount = palette.Length / 3;
        _errorRowA = new int[(width + 2) * 3];
        _errorRowB = new int[(width + 2) * 3];

        output.Write("GIF89a"u8);

        // Logical screen descriptor: global table present, 8-bit colour resolution, 256 entries.
        WriteUInt16(width);
        WriteUInt16(height);
        output.WriteByte(0xF7);
        output.WriteByte(0);
        output.WriteByte(0);

        byte[] table = new byte[256 * 3];
        palette.CopyTo(table, 0);
        output.Write(table);

        // NETSCAPE2.0 application extension, loop count 0: loop forever.
        output.Write([0x21, 0xFF, 0x0B]);
        output.Write("NETSCAPE2.0"u8);
        output.Write([0x03, 0x01, 0x00, 0x00, 0x00]);
    }

    /// <summary>Appends one frame.</summary>
    /// <param name="rgb">The frame, packed RGB, <c>width * height * 3</c> bytes.</param>
    /// <param name="previous">The frame before it in the same layout, or empty for the first frame.</param>
    /// <param name="delayCentiseconds">How long the frame shows.</param>
    public void WriteFrame(ReadOnlySpan<byte> rgb, ReadOnlySpan<byte> previous, int delayCentiseconds)
    {
        int expected = _width * _height * 3;
        if (rgb.Length != expected || (!previous.IsEmpty && previous.Length != expected))
        {
            throw new ArgumentException("A frame does not match the canvas size.", nameof(rgb));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(delayCentiseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(delayCentiseconds, ushort.MaxValue);

        bool delta = !previous.IsEmpty;
        int left = 0, top = 0, right = _width - 1, bottom = _height - 1;

        if (delta && !ChangedBounds(rgb, previous, out left, out top, out right, out bottom))
        {
            // Nothing changed: one transparent pixel keeps the frame and its delay.
            left = top = right = bottom = 0;
        }

        int w = right - left + 1;
        int h = bottom - top + 1;
        if (_indices.Length < w * h)
        {
            _indices = new byte[w * h];
        }

        Span<byte> indices = _indices.AsSpan(0, w * h);
        bool anyTransparent = Map(rgb, delta ? previous : default, left, top, w, h, indices);

        // Graphic control extension: disposal 1 (do not dispose), transparency only where used.
        _output.Write([0x21, 0xF9, 0x04, (byte)((1 << 2) | (anyTransparent ? 1 : 0))]);
        WriteUInt16(delayCentiseconds);
        _output.WriteByte(anyTransparent ? TransparentIndex : (byte)0);
        _output.WriteByte(0);

        // Image descriptor: no local table, not interlaced.
        _output.WriteByte(0x2C);
        WriteUInt16(left);
        WriteUInt16(top);
        WriteUInt16(w);
        WriteUInt16(h);
        _output.WriteByte(0);

        _lzw.Encode(indices, MinCodeSize, _output);
    }

    /// <summary>Writes the trailer. The file is complete after this.</summary>
    public void Finish() => _output.WriteByte(0x3B);

    private bool ChangedBounds(ReadOnlySpan<byte> rgb, ReadOnlySpan<byte> previous,
        out int left, out int top, out int right, out int bottom)
    {
        left = _width;
        top = _height;
        right = -1;
        bottom = -1;
        int rowBytes = _width * 3;

        for (int y = 0; y < _height; y++)
        {
            ReadOnlySpan<byte> row = rgb.Slice(y * rowBytes, rowBytes);
            ReadOnlySpan<byte> before = previous.Slice(y * rowBytes, rowBytes);
            int first = row.CommonPrefixLength(before);
            if (first == rowBytes)
            {
                continue;
            }

            int last = rowBytes - 1;
            while (row[last] == before[last])
            {
                last--;
            }

            left = Math.Min(left, first / 3);
            right = Math.Max(right, last / 3);
            top = Math.Min(top, y);
            bottom = y;
        }

        return right >= 0;
    }

    private bool Map(ReadOnlySpan<byte> rgb, ReadOnlySpan<byte> previous, int left, int top, int w, int h,
        Span<byte> indices)
    {
        int[] current = _errorRowA;
        int[] next = _errorRowB;
        Array.Clear(current, 0, (w + 2) * 3);
        Array.Clear(next, 0, (w + 2) * 3);
        bool anyTransparent = false;

        for (int y = 0; y < h; y++)
        {
            int source = (((top + y) * _width) + left) * 3;

            for (int x = 0; x < w; x++, source += 3)
            {
                if (!previous.IsEmpty && previous[source] == rgb[source] &&
                    previous[source + 1] == rgb[source + 1] && previous[source + 2] == rgb[source + 2])
                {
                    indices[(y * w) + x] = TransparentIndex;
                    anyTransparent = true;
                    continue;
                }

                // Errors are carried in sixteenths; slot x + 1 is pixel x.
                int e = (x + 1) * 3;
                int r = Math.Clamp(rgb[source] + ((current[e] + 8) >> 4), 0, 255);
                int g = Math.Clamp(rgb[source + 1] + ((current[e + 1] + 8) >> 4), 0, 255);
                int b = Math.Clamp(rgb[source + 2] + ((current[e + 2] + 8) >> 4), 0, 255);

                int index = Nearest(r, g, b);
                indices[(y * w) + x] = (byte)index;

                int p = index * 3;
                Diffuse(current, next, e, r - _palette[p]);
                Diffuse(current, next, e + 1, g - _palette[p + 1]);
                Diffuse(current, next, e + 2, b - _palette[p + 2]);
            }

            (current, next) = (next, current);
            Array.Clear(next, 0, (w + 2) * 3);
        }

        return anyTransparent;
    }

    private static void Diffuse(int[] current, int[] next, int e, int error)
    {
        if (error == 0)
        {
            return;
        }

        current[e + 3] += error * 7;
        next[e - 3] += error * 3;
        next[e] += error * 5;
        next[e + 3] += error;
    }

    private int Nearest(int r, int g, int b)
    {
        int key = (r << 16) | (g << 8) | b;
        int cached = _nearest[key];
        if (cached != 0)
        {
            return cached - 1;
        }

        int best = 0;
        int bestDistance = int.MaxValue;
        for (int i = 0, p = 0; i < _paletteCount; i++, p += 3)
        {
            int dr = r - _palette[p];
            int dg = g - _palette[p + 1];
            int db = b - _palette[p + 2];
            int distance = (dr * dr) + (dg * dg) + (db * db);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        _nearest[key] = (ushort)(best + 1);
        return best;
    }

    private void WriteUInt16(int value)
    {
        _output.WriteByte((byte)value);
        _output.WriteByte((byte)(value >> 8));
    }
}
