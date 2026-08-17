#region

using System.Collections.Frozen;
using System.Reflection;
using Google.Protobuf.Reflection;

#endregion

namespace DemoViewer.NET.DemoTrimmer;

/// <summary>
///     Container-level facts about the CS2 <c>.dem</c> wire format that a demo <em>writer</em> needs
///     and the (read-only) parser does not expose.
///     <para>
///         <b>File header (16 bytes), verified by probing both reference demos:</b>
///         <list type="bullet">
///             <item>bytes 0-7: ASCII magic <c>PBDEMS2\0</c></item>
///             <item>bytes 8-11: int32LE — absolute file offset of the <c>DEM_FileInfo</c> frame</item>
///             <item>bytes 12-15: int32LE — absolute file offset of the <c>DEM_SpawnGroups</c> frame</item>
///         </list>
///         Both demos put <c>DEM_SpawnGroups</c> then <c>DEM_FileInfo</c> as the last two frames and
///         end the file there — neither contains a <c>DEM_Stop</c> frame at all.
///     </para>
/// </summary>
internal static class DemoFormat
{
    /// <summary>Bytes before the first frame.</summary>
    public const int FileHeaderLength = 16;

    /// <summary><c>DEM_IsCompressed</c> — bit 6 of the frame's command varint.</summary>
    public const uint CompressedFlag = 64u;

    /// <summary>Frame command name → <c>EDemoCommands</c> integer value (reverse of the parser's cache).</summary>
    public static readonly FrozenDictionary<string, int> CommandIdByName = BuildCommandIds();

    /// <summary>
    ///     Frame commands that carry the delta-encoded game stream. These are what the contiguity rule
    ///     applies to, and what the setup-frame collector must NOT pick up from before the entry point.
    /// </summary>
    /// <remarks>
    ///     <c>DEM_Recovery</c> is included conservatively: the pro demo carries 20 of them scattered
    ///     through the stream (not in the signon run), so replaying pre-entry ones after a checkpoint
    ///     jump would be feeding the client stale mid-stream state. In-window ones are kept verbatim.
    /// </remarks>
    public static readonly FrozenSet<string> StreamFrameCommands = new[]
    {
        "DEM_Packet", "DEM_FullPacket", "DEM_AnimationData", "DEM_AnimationHeader",
        "DEM_UserCmd", "DEM_Recovery"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Writes an unsigned LEB128 varint — the frame-header encoding for cmd / tick / size.</summary>
    public static int WriteVarint(Stream stream, uint value)
    {
        int written = 0;
        while (value >= 0x80u)
        {
            stream.WriteByte((byte)(value | 0x80u));
            value >>= 7;
            written++;
        }

        stream.WriteByte((byte)value);
        return written + 1;
    }

    /// <summary>
    ///     Writes a complete frame: three header varints then the payload.
    ///     <paramref name="tick" /> is written as its unsigned two's-complement form, so the
    ///     pre-recording sentinel <c>-1</c> round-trips as the original's <c>ff ff ff ff 0f</c>.
    /// </summary>
    public static int WriteFrame(Stream stream, int commandId, int tick, bool compressed, ReadOnlySpan<byte> payload)
    {
        int n = WriteVarint(stream, (uint)commandId | (compressed ? CompressedFlag : 0u));
        n += WriteVarint(stream, unchecked((uint)tick));
        n += WriteVarint(stream, (uint)payload.Length);
        stream.Write(payload);
        return n + payload.Length;
    }

    /// <summary>Writes the 16-byte file header with the two frame offsets (0 when the frame is absent).</summary>
    public static void WriteFileHeader(Stream stream, int fileInfoOffset, int spawnGroupsOffset)
    {
        Span<byte> header = stackalloc byte[FileHeaderLength];
        "PBDEMS2\0"u8.CopyTo(header);
        BitConverter.TryWriteBytes(header[8..12], fileInfoOffset);
        BitConverter.TryWriteBytes(header[12..16], spawnGroupsOffset);
        stream.Write(header);
    }

    private static FrozenDictionary<string, int> BuildCommandIds()
    {
        Dictionary<string, int> map = new(StringComparer.Ordinal);
        foreach (FieldInfo field in typeof(EDemoCommands).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            string? protoName = field.GetCustomAttribute<OriginalNameAttribute>()?.Name;
            if (protoName is not null)
            {
                map.TryAdd(protoName, (int)field.GetValue(null)!);
            }
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
