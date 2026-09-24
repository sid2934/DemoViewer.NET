#region

using System.Security.Cryptography;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline;

/// <summary>
///     The one demo content key: lowercase-hex SHA-256 of the <c>.dem</c> bytes.
///     <para>
///         Every store that must survive a moved, renamed or re-downloaded demo joins on this value: the
///         annotation sidecar's <c>demo.sha256</c>, the graph-breakpoint file's map key, the library's
///         copy dedup and the cache index row. Three copies of the same three lines used to live beside
///         each of them; they now forward here, so the value on disk cannot drift between consumers.
///     </para>
///     <para>
///         The file overload streams. A demo is routinely half a gigabyte and the library hashes every
///         one of them, so reading a file into memory to hash it is never acceptable here even though
///         the parser itself does hold the bytes; a caller that already has them uses the span overload.
///     </para>
/// </summary>
public static class DemoContentHash
{
    // 64 KiB matches the annotation store's original read size; SequentialScan lets the OS read ahead,
    // which is what makes a warm 500 MiB file hash in well under a second.
    private const int BufferSize = 1 << 16;

    /// <summary>Hashes a file's bytes, streaming. Throws like any file read would; see <see cref="TryCompute" />.</summary>
    /// <param name="path">The file to hash.</param>
    public static string Compute(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Hashes bytes already in memory (the open demo's raw buffer).</summary>
    /// <param name="bytes">The demo's bytes.</param>
    public static string Compute(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    ///     <see cref="Compute(string)" /> for callers whose persistence is best-effort: null instead of an
    ///     exception when the file cannot be read, so a locked or vanished demo degrades to "no key"
    ///     rather than failing the pass that asked.
    /// </summary>
    /// <param name="path">The file to hash.</param>
    public static string? TryCompute(string path)
    {
        try
        {
            return Compute(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
