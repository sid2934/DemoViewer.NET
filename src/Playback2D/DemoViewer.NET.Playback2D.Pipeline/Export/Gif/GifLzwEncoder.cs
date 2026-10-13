namespace DemoViewer.NET.Playback2D.Pipeline.Export.Gif;

/// <summary>
///     The GIF flavour of LZW: variable-width codes packed LSB first, a clear code when the 4096-entry
///     table fills, and the output cut into data sub-blocks of at most 255 bytes.
///     <para>
///         Reusable across frames so a long animation does not allocate a hash table per frame. Not
///         thread safe.
///     </para>
/// </summary>
internal sealed class GifLzwEncoder
{
    private const int MaxCodeWidth = 12;
    private const int TableLimit = 1 << MaxCodeWidth;

    // Open addressing at under half load: at most 4096 - 258 live entries in 8192 slots.
    private const int HashBits = 13;
    private const int HashSize = 1 << HashBits;
    private const int HashMask = HashSize - 1;

    private readonly byte[] _block = new byte[255];
    private readonly short[] _codes = new short[HashSize];
    private readonly int[] _keys = new int[HashSize];

    private int _bitBuffer;
    private int _bitCount;
    private int _blockLength;
    private Stream _output = Stream.Null;

    /// <summary>
    ///     Writes the LZW minimum code size byte, the compressed <paramref name="indices" /> as data
    ///     sub-blocks, and the zero-length block terminator.
    /// </summary>
    /// <param name="indices">Palette indices, each below <c>1 &lt;&lt; minCodeSize</c>. Must not be empty.</param>
    /// <param name="minCodeSize">2 to 8; 8 for a 256-entry colour table.</param>
    /// <param name="output">Receives the image data.</param>
    public void Encode(ReadOnlySpan<byte> indices, int minCodeSize, Stream output)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minCodeSize, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minCodeSize, 8);

        if (indices.IsEmpty)
        {
            throw new ArgumentException("A GIF image needs at least one pixel.", nameof(indices));
        }

        _output = output;
        _bitBuffer = 0;
        _bitCount = 0;
        _blockLength = 0;
        output.WriteByte((byte)minCodeSize);

        int clearCode = 1 << minCodeSize;
        int endCode = clearCode + 1;
        int width = minCodeSize + 1;
        int next = endCode + 1;
        Array.Clear(_keys);

        // The width grows when the code count passes 1 << width, judged before the entry for the code
        // just written is added. That is where the decoder, one entry behind, grows too.
        WriteCode(clearCode, width);

        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            int symbol = indices[i];
            int key = ((prefix << 8) | symbol) + 1;
            int slot = Slot(key);
            bool found = false;

            while (_keys[slot] != 0)
            {
                if (_keys[slot] == key)
                {
                    found = true;
                    break;
                }

                slot = (slot + 1) & HashMask;
            }

            if (found)
            {
                prefix = _codes[slot];
                continue;
            }

            WriteCode(prefix, width);
            width = Grow(width, next);
            prefix = symbol;

            if (next < TableLimit)
            {
                _keys[slot] = key;
                _codes[slot] = (short)next;
                next++;
            }
            else
            {
                WriteCode(clearCode, width);
                Array.Clear(_keys);
                width = minCodeSize + 1;
                next = endCode + 1;
            }
        }

        WriteCode(prefix, width);
        width = Grow(width, next);
        WriteCode(endCode, width);

        if (_bitCount > 0)
        {
            AppendByte((byte)_bitBuffer);
        }

        FlushBlock();
        output.WriteByte(0);
        _output = Stream.Null;
    }

    private static int Grow(int width, int next) =>
        next > (1 << width) - 1 && width < MaxCodeWidth ? width + 1 : width;

    private static int Slot(int key) => (int)((uint)key * 0x9E3779B1u >> (32 - HashBits));

    private void WriteCode(int code, int width)
    {
        _bitBuffer |= code << _bitCount;
        _bitCount += width;

        while (_bitCount >= 8)
        {
            AppendByte((byte)_bitBuffer);
            _bitBuffer >>= 8;
            _bitCount -= 8;
        }
    }

    private void AppendByte(byte value)
    {
        _block[_blockLength++] = value;
        if (_blockLength == _block.Length)
        {
            FlushBlock();
        }
    }

    private void FlushBlock()
    {
        if (_blockLength == 0)
        {
            return;
        }

        _output.WriteByte((byte)_blockLength);
        _output.Write(_block, 0, _blockLength);
        _blockLength = 0;
    }
}
