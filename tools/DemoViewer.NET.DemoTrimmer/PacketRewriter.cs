#region

using Cs2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.DemoTrimmer;

/// <summary>Counters describing one inner-message strip pass.</summary>
/// <param name="Kept">Inner messages re-emitted.</param>
/// <param name="Dropped">Inner messages removed.</param>
/// <param name="DroppedBytes">Sum of the dropped messages' payload byte counts.</param>
internal readonly record struct StripStats(int Kept, int Dropped, long DroppedBytes)
{
    public static StripStats operator +(StripStats a, StripStats b) =>
        new(a.Kept + b.Kept, a.Dropped + b.Dropped, a.DroppedBytes + b.DroppedBytes);
}

/// <summary>Outcome of one encoder-identity comparison over a single packet bitstream.</summary>
internal enum IdentityOutcome
{
    /// <summary>Re-encoded bytes are bit-for-bit identical to the original, same length.</summary>
    Exact,

    /// <summary>
    ///     Re-encoded bytes match the original over every byte we produced, and the original carried
    ///     extra trailing bytes (recorder padding past the last message). Benign.
    /// </summary>
    ExactPrefixShorter,

    /// <summary>Re-encoded bytes diverge from the original inside the message region. A real defect.</summary>
    Mismatch
}

/// <summary>
///     Re-encodes a <c>CDemoPacket.data</c> inner-message bitstream, optionally omitting messages by
///     net-message type id.
///     <para>
///         The read side deliberately mirrors <c>DemoParser.ParseInnerMessages</c> exactly, including
///         its <c>size &lt;= 0 || size &gt; RemainingBytes</c> end-of-stream guard, so the set of
///         messages we re-emit is precisely the set the parser sees.
///     </para>
/// </summary>
internal static class PacketRewriter
{
    /// <summary><c>svc_UserCmds</c> — the per-tick recorded player-input stream (netmessages.proto).</summary>
    public const int SvcUserCmdsTypeId = 76;

    /// <summary>
    ///     Re-encodes <paramref name="data" />, dropping every message whose type id is in
    ///     <paramref name="dropTypeIds" />. Passing an empty set produces a pure round trip, which is
    ///     what <see cref="CheckEncoderIdentity" /> asserts on.
    /// </summary>
    public static byte[] Rewrite(ReadOnlySpan<byte> data, IReadOnlySet<int> dropTypeIds, out StripStats stats) =>
        Rewrite(data, dropTypeIds, out stats, out _);

    /// <summary>
    ///     <see cref="Rewrite(ReadOnlySpan{byte},IReadOnlySet{int},out StripStats)" /> plus the exact
    ///     <paramref name="bitLength" /> written. The byte array is zero-padded past that point, so only
    ///     the first <paramref name="bitLength" /> bits are meaningful.
    /// </summary>
    public static byte[] Rewrite(
        ReadOnlySpan<byte> data, IReadOnlySet<int> dropTypeIds, out StripStats stats, out int bitLength)
    {
        BitBuffer reader = new(data);
        BitStreamWriter writer = new(data.Length + 16);
        int kept = 0, dropped = 0;
        long droppedBytes = 0;

        while (reader.RemainingBits > 0)
        {
            uint typeId = reader.ReadUBitVar();
            uint size = reader.ReadUVarInt32();

            // Same guard as DemoParser.ParseInnerMessages: zero/oversized size means we have run into
            // the recorder's trailing bit padding (or a malformed tail) — stop, don't re-emit it.
            if (size == 0 || size > (uint)reader.RemainingBytes)
            {
                break;
            }

            byte[] payload = reader.ReadBytes((int)size);
            if (dropTypeIds.Contains((int)typeId))
            {
                dropped++;
                droppedBytes += size;
                continue;
            }

            writer.WriteUBitVar(typeId);
            writer.WriteUVarInt32(size);
            writer.WriteBytes(payload);
            kept++;
        }

        stats = new StripStats(kept, dropped, droppedBytes);
        bitLength = writer.BitLength;
        return writer.ToArray();
    }

    /// <summary>
    ///     Runs the encoder with an empty drop set and compares the result against the original bytes.
    ///     This is the gate that separates "the bit writer is wrong" from "dropping messages broke
    ///     playback" — without it a V3 failure is uninterpretable.
    ///     <para>
    ///         The comparison is <b>bit</b>-exact over the bits we produced, not byte-exact over the whole
    ///         buffer: the recorder's <c>CDemoPacket.data</c> length is byte-rounded, so the tail past the
    ///         last message is padding that the encoder is not expected to reproduce.
    ///     </para>
    /// </summary>
    public static IdentityOutcome CheckEncoderIdentity(ReadOnlySpan<byte> original, out int firstDivergentByte)
    {
        firstDivergentByte = -1;
        byte[] reencoded = Rewrite(original, EmptyDropSet, out _, out int bitLength);

        int fullBytes = bitLength >> 3;
        int remainderBits = bitLength & 7;
        if (fullBytes + (remainderBits > 0 ? 1 : 0) > original.Length)
        {
            // More bits out than in can only mean we invented data.
            firstDivergentByte = original.Length;
            return IdentityOutcome.Mismatch;
        }

        for (int i = 0; i < fullBytes; i++)
        {
            if (reencoded[i] != original[i])
            {
                firstDivergentByte = i;
                return IdentityOutcome.Mismatch;
            }
        }

        if (remainderBits > 0)
        {
            int mask = (1 << remainderBits) - 1;
            if ((reencoded[fullBytes] & mask) != (original[fullBytes] & mask))
            {
                firstDivergentByte = fullBytes;
                return IdentityOutcome.Mismatch;
            }
        }

        return reencoded.Length == original.Length && remainderBits == 0
            ? IdentityOutcome.Exact
            : IdentityOutcome.ExactPrefixShorter;
    }

    private static readonly HashSet<int> EmptyDropSet = [];
}
