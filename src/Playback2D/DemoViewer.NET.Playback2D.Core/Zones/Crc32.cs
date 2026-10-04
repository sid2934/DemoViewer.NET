#region

using System.Text;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Zones;

/// <summary>
///     The IEEE CRC-32 the baker stamps <c>zonesVersion</c> with, restated here so
///     <c>ZoneSet.EffectiveVersion</c> can fold the overlay bytes in without Core taking a package.
///     Formatted the way the baker formats it: the little-endian hash bytes as eight lowercase hex
///     digits, so a set with no overlay stamps byte-identical to what the file says.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] _table = BuildTable();

    /// <summary>The stamp for <c>zonesVersion ‖ overlay bytes</c>.</summary>
    /// <param name="zonesVersion">The baked stamp.</param>
    /// <param name="overlay">The overlay file's bytes.</param>
    public static string EffectiveVersion(string zonesVersion, ReadOnlySpan<byte> overlay)
    {
        uint crc = 0xFFFFFFFF;
        crc = Append(crc, Encoding.UTF8.GetBytes(zonesVersion));
        crc = Append(crc, overlay);
        crc ^= 0xFFFFFFFF;

        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)crc;
        bytes[1] = (byte)(crc >> 8);
        bytes[2] = (byte)(crc >> 16);
        bytes[3] = (byte)(crc >> 24);
        return Convert.ToHexStringLower(bytes);
    }

    private static uint Append(uint crc, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            crc = _table[(crc ^ bytes[i]) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
