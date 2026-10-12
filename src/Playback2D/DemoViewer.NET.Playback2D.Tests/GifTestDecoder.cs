namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     A small, strict GIF89a reader for checking what the export writes: it composites every frame onto
///     the canvas the way a viewer would and keeps the timing and loop metadata.
///     <para>
///         Written from the format spec, not shared with the writer, so a misunderstanding on one side
///         shows up as a mismatch. It also decodes a fixture written by a third-party encoder.
///     </para>
/// </summary>
internal static class GifTestDecoder
{
    public static DecodedGif Decode(byte[] data)
    {
        int pos = 0;

        if (data.Length < 13 || data[0] != 'G' || data[1] != 'I' || data[2] != 'F' || data[3] != '8' ||
            data[4] != '9' || data[5] != 'a')
        {
            throw new InvalidDataException("Not a GIF89a file.");
        }

        pos = 6;
        int width = ReadUInt16(data, ref pos);
        int height = ReadUInt16(data, ref pos);
        byte screenFlags = data[pos++];
        pos += 2;

        byte[]? globalTable = null;
        if ((screenFlags & 0x80) != 0)
        {
            int size = 3 << ((screenFlags & 7) + 1);
            globalTable = data.AsSpan(pos, size).ToArray();
            pos += size;
        }

        byte[] canvas = new byte[width * height * 3];
        List<DecodedFrame> frames = [];
        int? loopCount = null;
        int delay = 0;
        int disposal = 0;
        int transparent = -1;

        while (true)
        {
            byte introducer = data[pos++];

            if (introducer == 0x3B)
            {
                break;
            }

            if (introducer == 0x21)
            {
                byte label = data[pos++];
                if (label == 0xF9)
                {
                    if (data[pos] != 4)
                    {
                        throw new InvalidDataException("A graphic control extension must be 4 bytes.");
                    }

                    byte flags = data[pos + 1];
                    disposal = (flags >> 2) & 7;
                    delay = data[pos + 2] | (data[pos + 3] << 8);
                    transparent = (flags & 1) != 0 ? data[pos + 4] : -1;
                    pos += 5;
                    ExpectTerminator(data, ref pos);
                }
                else if (label == 0xFF && data[pos] == 11 &&
                         data.AsSpan(pos + 1, 11).SequenceEqual("NETSCAPE2.0"u8))
                {
                    pos += 12;
                    byte[] payload = ReadSubBlocks(data, ref pos);
                    if (payload.Length >= 3 && payload[0] == 1)
                    {
                        loopCount = payload[1] | (payload[2] << 8);
                    }
                }
                else
                {
                    pos += data[pos] + 1;
                    _ = ReadSubBlocks(data, ref pos);
                }

                continue;
            }

            if (introducer != 0x2C)
            {
                throw new InvalidDataException($"Unexpected block 0x{introducer:X2} at {pos - 1}.");
            }

            int left = ReadUInt16(data, ref pos);
            int top = ReadUInt16(data, ref pos);
            int w = ReadUInt16(data, ref pos);
            int h = ReadUInt16(data, ref pos);
            byte imageFlags = data[pos++];

            if ((imageFlags & 0x40) != 0)
            {
                throw new InvalidDataException("Interlaced frames are not expected.");
            }

            if (left + w > width || top + h > height)
            {
                throw new InvalidDataException("A frame lies outside the canvas.");
            }

            byte[]? table = globalTable;
            if ((imageFlags & 0x80) != 0)
            {
                int size = 3 << ((imageFlags & 7) + 1);
                table = data.AsSpan(pos, size).ToArray();
                pos += size;
            }

            if (table is null)
            {
                throw new InvalidDataException("A frame has no colour table.");
            }

            int minCodeSize = data[pos++];
            byte[] compressed = ReadSubBlocks(data, ref pos);
            byte[] indices = Lzw(compressed, minCodeSize, w * h);

            byte[]? restore = disposal == 3 ? (byte[])canvas.Clone() : null;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int index = indices[(y * w) + x];
                    if (index == transparent)
                    {
                        continue;
                    }

                    if ((index * 3) + 2 >= table.Length)
                    {
                        throw new InvalidDataException("A pixel indexes past the colour table.");
                    }

                    int d = (((top + y) * width) + left + x) * 3;
                    canvas[d] = table[index * 3];
                    canvas[d + 1] = table[(index * 3) + 1];
                    canvas[d + 2] = table[(index * 3) + 2];
                }
            }

            frames.Add(new DecodedFrame((byte[])canvas.Clone(), delay, disposal, left, top, w, h,
                transparent >= 0));

            if (disposal == 2)
            {
                for (int y = top; y < top + h; y++)
                {
                    Array.Clear(canvas, ((y * width) + left) * 3, w * 3);
                }
            }
            else if (restore is not null)
            {
                canvas = restore;
            }

            delay = 0;
            disposal = 0;
            transparent = -1;
        }

        if (pos != data.Length)
        {
            throw new InvalidDataException("Bytes after the trailer.");
        }

        return new DecodedGif(width, height, loopCount, frames);
    }

    private static byte[] Lzw(byte[] data, int minCodeSize, int pixelCount)
    {
        if (minCodeSize is < 2 or > 8)
        {
            throw new InvalidDataException($"LZW minimum code size {minCodeSize}.");
        }

        int clear = 1 << minCodeSize;
        int end = clear + 1;
        int[] prefix = new int[4096];
        byte[] suffix = new byte[4096];
        byte[] firstOf = new byte[4096];
        int[] length = new int[4096];

        for (int i = 0; i < clear; i++)
        {
            suffix[i] = (byte)i;
            firstOf[i] = (byte)i;
            length[i] = 1;
        }

        byte[] output = new byte[pixelCount];
        int written = 0;
        int width = minCodeSize + 1;
        int next = end + 1;
        int previous = -1;
        int bitPos = 0;
        int totalBits = data.Length * 8;
        bool sawEnd = false;

        while (bitPos + width <= totalBits)
        {
            int code = 0;
            for (int b = 0; b < width; b++, bitPos++)
            {
                code |= ((data[bitPos >> 3] >> (bitPos & 7)) & 1) << b;
            }

            if (code == clear)
            {
                width = minCodeSize + 1;
                next = end + 1;
                previous = -1;
                continue;
            }

            if (code == end)
            {
                sawEnd = true;
                break;
            }

            if (previous < 0)
            {
                if (code >= clear)
                {
                    throw new InvalidDataException("The first code after a clear must be a literal.");
                }

                output[written++] = (byte)code;
                previous = code;
                continue;
            }

            byte first;
            if (code < next)
            {
                first = firstOf[code];
                written = Emit(code, prefix, suffix, length, output, written);
            }
            else if (code == next)
            {
                first = firstOf[previous];
                written = Emit(previous, prefix, suffix, length, output, written);
                output[written++] = first;
            }
            else
            {
                throw new InvalidDataException($"Code {code} ahead of the table ({next}).");
            }

            if (next < 4096)
            {
                prefix[next] = previous;
                suffix[next] = first;
                firstOf[next] = firstOf[previous];
                length[next] = length[previous] + 1;
                next++;
                if (next == 1 << width && width < 12)
                {
                    width++;
                }
            }

            previous = code;
        }

        if (!sawEnd)
        {
            throw new InvalidDataException("The image data has no end code.");
        }

        if (written != pixelCount)
        {
            throw new InvalidDataException($"Decoded {written} pixels, expected {pixelCount}.");
        }

        return output;
    }

    private static int Emit(int code, int[] prefix, byte[] suffix, int[] length, byte[] output, int written)
    {
        int n = length[code];
        if (written + n > output.Length)
        {
            throw new InvalidDataException("The image data runs past the frame.");
        }

        for (int i = written + n - 1; i >= written; i--)
        {
            output[i] = suffix[code];
            code = prefix[code];
        }

        return written + n;
    }

    private static byte[] ReadSubBlocks(byte[] data, ref int pos)
    {
        List<byte> bytes = [];
        while (true)
        {
            int size = data[pos++];
            if (size == 0)
            {
                return [.. bytes];
            }

            bytes.AddRange(data.AsSpan(pos, size));
            pos += size;
        }
    }

    private static void ExpectTerminator(byte[] data, ref int pos)
    {
        if (data[pos++] != 0)
        {
            throw new InvalidDataException("Missing block terminator.");
        }
    }

    private static int ReadUInt16(byte[] data, ref int pos)
    {
        int value = data[pos] | (data[pos + 1] << 8);
        pos += 2;
        return value;
    }
}

/// <summary>A decoded animation.</summary>
internal sealed record DecodedGif(int Width, int Height, int? LoopCount, IReadOnlyList<DecodedFrame> Frames);

/// <summary>One frame as composited onto the canvas, packed RGB, plus its control block and rectangle.</summary>
internal sealed record DecodedFrame(
    byte[] Rgb,
    int DelayCentiseconds,
    int Disposal,
    int Left,
    int Top,
    int Width,
    int Height,
    bool HasTransparency);
