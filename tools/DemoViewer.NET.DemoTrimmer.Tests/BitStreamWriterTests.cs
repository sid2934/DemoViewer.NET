#region

using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.DemoTrimmer.Tests;

/// <summary>
///     Pure round-trip tests for the bit writer against the parser's own <c>BitBuffer</c> reader.
///     These need no demo file: if the writer is wrong, every V3 (svc_UserCmds stripped) candidate is
///     garbage, so this is the cheapest place to catch it.
///     <para>
///         <c>BitBuffer</c> is a <c>ref struct</c> and cannot live across an <c>await</c>, so every
///         decode drains into a plain array first and the assertions run afterwards.
///     </para>
/// </summary>
public sealed class BitStreamWriterTests
{
    [Test]
    public async Task WriteUBitVar_RoundTripsThroughBitBuffer()
    {
        // One value from each of ReadUBitVar's four branches, plus the boundaries between them.
        uint[] values = [0, 1, 15, 16, 17, 200, 255, 256, 1000, 4095, 4096, 70_000, 1u << 20, uint.MaxValue];

        BitStreamWriter writer = new();
        foreach (uint value in values)
        {
            writer.WriteUBitVar(value);
        }

        uint[] decoded = DecodeUBitVars(writer.ToArray(), values.Length);
        await Assert.That(decoded).IsEquivalentTo(values);
    }

    [Test]
    public async Task WriteUVarInt32_RoundTripsThroughBitBuffer()
    {
        uint[] values = [0, 1, 127, 128, 300, 16_383, 16_384, 1_000_000, uint.MaxValue];

        BitStreamWriter writer = new();
        foreach (uint value in values)
        {
            writer.WriteUVarInt32(value);
        }

        uint[] decoded = DecodeUVarInts(writer.ToArray(), values.Length);
        await Assert.That(decoded).IsEquivalentTo(values);
    }

    [Test]
    public async Task WriteBytes_RoundTripsFromAnUnalignedBitPosition()
    {
        // The realistic case: a 6-bit UBitVar leaves the cursor mid-byte before the payload run.
        byte[] payload = [.. Enumerable.Range(0, 257).Select(i => (byte)(i * 7))];

        BitStreamWriter writer = new();
        writer.WriteUBitVar(9); // 6 bits — deliberately not byte-aligned
        writer.WriteUVarInt32((uint)payload.Length);
        writer.WriteBytes(payload);

        (uint typeId, uint size, byte[] bytes) = DecodeOneMessage(writer.ToArray());
        await Assert.That(typeId).IsEqualTo(9u);
        await Assert.That(size).IsEqualTo((uint)payload.Length);
        await Assert.That(bytes).IsEquivalentTo(payload);
    }

    [Test]
    public async Task Rewrite_WithEmptyDropSet_IsIdentityOnASynthesizedBitstream()
    {
        BitStreamWriter writer = new();
        foreach ((uint typeId, byte[] payload) in SampleMessages())
        {
            writer.WriteUBitVar(typeId);
            writer.WriteUVarInt32((uint)payload.Length);
            writer.WriteBytes(payload);
        }

        // Exact vs ExactPrefixShorter only reflects whether the stream happens to end on a byte
        // boundary; both mean every bit we produced matches the original. Mismatch is the defect.
        await Assert.That(PacketRewriter.CheckEncoderIdentity(writer.ToArray(), out _))
            .IsNotEqualTo(IdentityOutcome.Mismatch);
    }

    [Test]
    public async Task Rewrite_DropsOnlyTheRequestedTypeId()
    {
        BitStreamWriter writer = new();
        foreach ((uint typeId, byte[] payload) in SampleMessages())
        {
            writer.WriteUBitVar(typeId);
            writer.WriteUVarInt32((uint)payload.Length);
            writer.WriteBytes(payload);
        }

        byte[] stripped = PacketRewriter.Rewrite(
            writer.ToArray(), new HashSet<int>
            {
                PacketRewriter.SvcUserCmdsTypeId
            }, out StripStats stats);

        await Assert.That(stats.Kept).IsEqualTo(2);
        await Assert.That(stats.Dropped).IsEqualTo(1);
        await Assert.That(stats.DroppedBytes).IsEqualTo(4L);

        List<(uint TypeId, uint Size, byte[] Bytes)> survivors = DecodeMessages(stripped, 2);
        await Assert.That(survivors[0].TypeId).IsEqualTo(40u);
        await Assert.That(survivors[0].Bytes).IsEquivalentTo(new byte[]
        {
            1, 2, 3
        });
        await Assert.That(survivors[1].TypeId).IsEqualTo(55u);
        await Assert.That(survivors[1].Bytes).IsEquivalentTo(Enumerable.Repeat((byte)0xAB, 300).ToArray());
    }

    private static (uint TypeId, byte[] Payload)[] SampleMessages() =>
    [
        (40, [1, 2, 3]),
        (PacketRewriter.SvcUserCmdsTypeId, [9, 9, 9, 9]),
        (55, [.. Enumerable.Repeat((byte)0xAB, 300)])
    ];

    private static uint[] DecodeUBitVars(byte[] encoded, int count)
    {
        uint[] result = new uint[count];
        BitBuffer reader = new(encoded);
        for (int i = 0; i < count; i++)
        {
            result[i] = reader.ReadUBitVar();
        }

        return result;
    }

    private static uint[] DecodeUVarInts(byte[] encoded, int count)
    {
        uint[] result = new uint[count];
        BitBuffer reader = new(encoded);
        for (int i = 0; i < count; i++)
        {
            result[i] = reader.ReadUVarInt32();
        }

        return result;
    }

    private static (uint TypeId, uint Size, byte[] Bytes) DecodeOneMessage(byte[] encoded) =>
        DecodeMessages(encoded, 1)[0];

    private static List<(uint TypeId, uint Size, byte[] Bytes)> DecodeMessages(byte[] encoded, int count)
    {
        List<(uint, uint, byte[])> messages = [];
        BitBuffer reader = new(encoded);
        for (int i = 0; i < count; i++)
        {
            uint typeId = reader.ReadUBitVar();
            uint size = reader.ReadUVarInt32();
            messages.Add((typeId, size, reader.ReadBytes((int)size)));
        }

        return messages;
    }
}
