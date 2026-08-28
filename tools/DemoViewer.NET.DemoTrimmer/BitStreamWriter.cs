namespace DemoViewer.NET.DemoTrimmer;

/// <summary>
///     LSB-first bit writer — the exact inverse of the parser's <c>BitBuffer</c> reader.
///     <para>
///         Only the three primitives the CDemoPacket inner-message framing uses are implemented:
///         <c>UBitVar</c> (type id), <c>UVarInt32</c> (payload byte count) and a raw byte run.
///         Everything else in a demo packet is opaque payload we copy through untouched, so no
///         field-level bit encoders are needed.
///     </para>
///     <para>
///         <b>Why a new writer:</b> <c>BitBuffer</c> is a protected, read-only <c>ref struct</c>.
///         The encodings below are derived directly from its <c>ReadUBitVar</c> /
///         <c>ReadUVarInt32</c> / <c>ReadBytes</c> implementations, and
///         <see cref="PacketRewriter.CheckEncoderIdentity" /> proves the round trip on real data
///         before any message is dropped.
///     </para>
/// </summary>
internal sealed class BitStreamWriter
{
    private byte[] _buf;

    public BitStreamWriter(int initialByteCapacity = 4096) => _buf = new byte[Math.Max(16, initialByteCapacity)];

    /// <summary>Total bits written so far.</summary>
    public int BitLength { get; private set; }

    /// <summary>
    ///     Writes <paramref name="numBits" /> low bits of <paramref name="value" />, least-significant
    ///     bit first — matching <c>BitBuffer.ReadUBits</c>.
    /// </summary>
    public void WriteUBits(uint value, int numBits)
    {
        if (numBits is <= 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(numBits), numBits, "numBits must be in 1..32.");
        }

        // Mask off bits above numBits so a caller's stray high bits can never corrupt the next field.
        if (numBits < 32)
        {
            value &= (1u << numBits) - 1u;
        }

        EnsureBits(numBits);
        while (numBits > 0)
        {
            int byteIdx = BitLength >> 3;
            int bitOff = BitLength & 7;
            int take = Math.Min(8 - bitOff, numBits);
            uint chunk = value & (1u << take) - 1u;
            // The buffer is always zero-filled ahead of _bitPos (fresh arrays, and growth copies into
            // a fresh array), so OR-ing is equivalent to assignment for the untouched high bits.
            _buf[byteIdx] |= (byte)(chunk << bitOff);
            value >>= take;
            numBits -= take;
            BitLength += take;
        }
    }

    /// <summary>Writes one byte as 8 bits (LSB-first), mirroring <c>BitBuffer.ReadByte</c>.</summary>
    public void WriteByte(byte value) => WriteUBits(value, 8);

    /// <summary>
    ///     Writes a run of bytes, mirroring <c>BitBuffer.ReadBytes</c> (one byte at a time,
    ///     LSB-first). Uses a block copy when the cursor happens to be byte-aligned.
    /// </summary>
    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        EnsureBits(bytes.Length * 8);
        if ((BitLength & 7) == 0)
        {
            bytes.CopyTo(_buf.AsSpan(BitLength >> 3, bytes.Length));
            BitLength += bytes.Length * 8;
            return;
        }

        foreach (byte b in bytes)
        {
            WriteByte(b);
        }
    }

    /// <summary>
    ///     Writes Source's variable-width unsigned int. Inverse of <c>BitBuffer.ReadUBitVar</c>:
    ///     a 6-bit seed whose bits 4-5 select 0 / 4 / 8 / 28 extra bits, with the low 4 bits of the
    ///     value carried in the seed. Always emits the shortest form, which is what the recorder does
    ///     (proven by <see cref="PacketRewriter.CheckEncoderIdentity" />).
    /// </summary>
    public void WriteUBitVar(uint value)
    {
        switch (value)
        {
            case < 16u:
                WriteUBits(value, 6);
                break;
            case < 1u << 8:
                WriteUBits(16u | value & 15u, 6);
                WriteUBits(value >> 4, 4);
                break;
            case < 1u << 12:
                WriteUBits(32u | value & 15u, 6);
                WriteUBits(value >> 4, 8);
                break;
            default:
                WriteUBits(48u | value & 15u, 6);
                WriteUBits(value >> 4, 28);
                break;
        }
    }

    /// <summary>Writes a protobuf-style unsigned LEB128 varint. Inverse of <c>BitBuffer.ReadUVarInt32</c>.</summary>
    public void WriteUVarInt32(uint value)
    {
        while (value >= 0x80u)
        {
            WriteByte((byte)(value | 0x80u));
            value >>= 7;
        }

        WriteByte((byte)value);
    }

    /// <summary>
    ///     Returns the written bits as bytes. A trailing partial byte is zero-padded in its high bits —
    ///     the reader stops there because a zero type id decodes to a zero size, which its
    ///     <c>size &lt;= 0</c> guard treats as end-of-stream (same as the original recorder's padding).
    /// </summary>
    public byte[] ToArray()
    {
        int byteLength = BitLength + 7 >> 3;
        byte[] result = new byte[byteLength];
        _buf.AsSpan(0, byteLength).CopyTo(result);
        return result;
    }

    private void EnsureBits(int extraBits)
    {
        // +1 byte of slack so the partial-byte OR at the very end never indexes past the array.
        int neededBytes = (BitLength + extraBits + 7 >> 3) + 1;
        if (neededBytes <= _buf.Length)
        {
            return;
        }

        int newLength = Math.Max(neededBytes, _buf.Length * 2);
        byte[] grown = new byte[newLength]; // zero-filled: keeps the OR-append invariant
        _buf.AsSpan(0, BitLength + 7 >> 3).CopyTo(grown);
        _buf = grown;
    }
}
