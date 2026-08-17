#region

using System.Globalization;
using Cs2DemoKit.Parser;
using Snappier;

#endregion

namespace DemoViewer.NET.DemoTrimmer;

/// <summary>A frame located directly in the raw file bytes, without going through the parser.</summary>
/// <param name="Offset">Absolute file offset of the first header byte.</param>
/// <param name="HeaderLength">Byte length of the three header varints.</param>
/// <param name="CommandId">EDemoCommands value with the compressed flag stripped.</param>
/// <param name="Tick">Frame tick.</param>
/// <param name="PayloadLength">Payload byte length as written (compressed length when compressed).</param>
/// <param name="Compressed">Whether the payload is Snappy-compressed.</param>
internal readonly record struct RawFrame(
    int Offset, int HeaderLength, int CommandId, int Tick, int PayloadLength, bool Compressed)
{
    public int PayloadStart => Offset + HeaderLength;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"cmd={CommandId} tick={Tick} payload={PayloadLength}B{(Compressed ? " (snappy)" : "")} @{Offset}");
}

/// <summary>
///     The three frames that live <b>after</b> <c>DEM_Stop</c> and are therefore invisible to
///     <see cref="ParsedDemo.Frames" /> — the parse loop breaks at <c>DEM_Stop</c>.
///     <para>
///         <b>Measured layout of both reference demos</b> (matchmaking 172 MiB, pro 318 MiB):
///         <c>… DEM_Packet@last · DEM_Stop@last · DEM_SpawnGroups@last · DEM_FileInfo@last · EOF</c>,
///         with the 16-byte file header's two int32s pointing at the last two. A trimmer that only
///         re-emits <see cref="ParsedDemo.Frames" /> silently drops all three and leaves both header
///         offsets zero — which is very likely fatal to the real CS2 client, so the trimmer reads them
///         straight out of the raw bytes instead.
///     </para>
/// </summary>
/// <param name="Stop">The <c>DEM_Stop</c> terminator, if present.</param>
/// <param name="SpawnGroups">The <c>DEM_SpawnGroups</c> frame the file header's bytes 12-15 point at.</param>
/// <param name="FileInfo">The <c>DEM_FileInfo</c> frame the file header's bytes 8-11 point at.</param>
internal sealed record DemoTail(RawFrame? Stop, RawFrame? SpawnGroups, RawFrame? FileInfo)
{
    /// <summary>Locates the tail frames in <paramref name="raw" />.</summary>
    public static DemoTail Read(byte[] raw, ParsedDemo demo)
    {
        RawFrame? stop = null;
        if (demo.Frames.Count > 0)
        {
            DemoFrame last = demo.Frames[^1];
            if (TryRead(raw, last.RawStart + last.RawLength, out RawFrame candidate)
                && candidate.CommandId == DemoFormat.CommandIdByName["DEM_Stop"])
            {
                stop = candidate;
            }
        }

        return new DemoTail(
            stop,
            ReadAt(raw, BitConverter.ToInt32(raw, 12), "DEM_SpawnGroups"),
            ReadAt(raw, BitConverter.ToInt32(raw, 8), "DEM_FileInfo"));
    }

    /// <summary>Decompresses (if needed) and returns the tail frame's payload bytes.</summary>
    public static byte[] Payload(byte[] raw, RawFrame frame) =>
        frame.Compressed
            ? Snappy.DecompressToArray(raw.AsSpan(frame.PayloadStart, frame.PayloadLength))
            : raw.AsSpan(frame.PayloadStart, frame.PayloadLength).ToArray();

    private static RawFrame? ReadAt(byte[] raw, int offset, string expectedCommand) =>
        TryRead(raw, offset, out RawFrame frame)
        && frame.CommandId == DemoFormat.CommandIdByName[expectedCommand]
            ? frame
            : null;

    /// <summary>Decodes the three header varints at <paramref name="offset" />.</summary>
    private static bool TryRead(ReadOnlySpan<byte> data, int offset, out RawFrame frame)
    {
        frame = default;
        if (offset < DemoFormat.FileHeaderLength || offset >= data.Length)
        {
            return false;
        }

        int cursor = offset;
        if (!TryReadVarint(data, ref cursor, out uint rawCommand)
            || !TryReadVarint(data, ref cursor, out uint tick)
            || !TryReadVarint(data, ref cursor, out uint size))
        {
            return false;
        }

        if (cursor + (long)size > data.Length)
        {
            return false;
        }

        frame = new RawFrame(
            offset, cursor - offset,
            (int)(rawCommand & ~DemoFormat.CompressedFlag), unchecked((int)tick),
            (int)size, (rawCommand & DemoFormat.CompressedFlag) != 0);
        return true;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int cursor, out uint value)
    {
        value = 0;
        int shift = 0;
        while (cursor < data.Length && shift <= 28)
        {
            byte b = data[cursor++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }
}
