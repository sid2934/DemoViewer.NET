#region

using System.Buffers;
using System.Security.Cryptography;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

/// <summary>
///     A cheap candidate identity for a demo file: its size plus the SHA-256 of its first and last
///     <see cref="WindowBytes" /> bytes. Reading two windows costs a few MiB where <see cref="DemoContentHash" />
///     reads the whole file, which is what lets a copy on a second mount be recognised without a full read.
///     <para>
///         A match is a candidate, never an identity: only <see cref="DemoContentHash" /> agreeing confirms
///         that two files hold the same demo. Two fingerprints taken with different windows never match.
///     </para>
/// </summary>
/// <param name="Size">The file's length in bytes.</param>
/// <param name="WindowBytes">The window both hashes were taken over.</param>
/// <param name="Head">Lowercase-hex SHA-256 of the first <c>min(WindowBytes, Size)</c> bytes.</param>
/// <param name="Tail">Lowercase-hex SHA-256 of the last <c>min(WindowBytes, Size)</c> bytes.</param>
public sealed record DemoContentFingerprint(long Size, int WindowBytes, string Head, string Tail)
{
    /// <summary>The window every fingerprint is taken with unless a caller says otherwise.</summary>
    public const int DefaultWindowBytes = 4 << 20;

    private const int BufferSize = 1 << 16;

    /// <summary>
    ///     The fingerprint of a settled file, or null when the file may still be written, cannot be read,
    ///     or changed while it was read.
    /// </summary>
    /// <param name="path">The file to fingerprint.</param>
    /// <param name="time">The clock the settle window is measured against.</param>
    /// <param name="stat">Reads a file's length and write time; <see cref="MappedParsePolicy.StatFile" /> when null.</param>
    /// <param name="windowBytes">The window to hash at each end.</param>
    public static DemoContentFingerprint? TryCompute(string path, TimeProvider time,
        Func<string, FileStat>? stat = null, int windowBytes = DefaultWindowBytes)
    {
        stat ??= MappedParsePolicy.StatFile;
        try
        {
            if (!MappedParsePolicy.IsSettled(path, time, stat))
            {
                return null;
            }

            FileStat before = stat(path);
            DemoContentFingerprint fingerprint;
            // No read-ahead: on a network mount the bytes between the two windows are what this avoids.
            using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0,
                       FileOptions.RandomAccess))
            {
                fingerprint = Compute(stream, windowBytes);
            }

            return stat(path) == before && fingerprint.Size == before.Length ? fingerprint : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     <see cref="DemoContentHash.Compute(string)" /> and the fingerprint in one streaming read. Both are
    ///     null when the file cannot be read or changed length during the read. The fingerprint alone is
    ///     null when the file is not settled or its write time moved, since windows taken mid-write
    ///     describe bytes that are about to change.
    /// </summary>
    /// <param name="path">The file to hash.</param>
    /// <param name="time">The clock the settle window is measured against.</param>
    /// <param name="stat">Reads a file's length and write time; <see cref="MappedParsePolicy.StatFile" /> when null.</param>
    /// <param name="windowBytes">The window to hash at each end.</param>
    public static (string? Sha256, DemoContentFingerprint? Fingerprint) TryComputeWithContentHash(string path,
        TimeProvider time, Func<string, FileStat>? stat = null, int windowBytes = DefaultWindowBytes)
    {
        stat ??= MappedParsePolicy.StatFile;
        try
        {
            bool settled = MappedParsePolicy.IsSettled(path, time, stat);
            FileStat before = stat(path);
            (string Sha256, DemoContentFingerprint Fingerprint) both;
            using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                       FileOptions.SequentialScan))
            {
                both = ComputeWithContentHash(stream, windowBytes);
            }

            bool steady = settled && stat(path) == before && both.Fingerprint.Size == before.Length;
            return (both.Sha256, steady ? both.Fingerprint : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    /// <summary>
    ///     Fingerprints a seekable stream from its current length, reading only the two windows (the whole
    ///     stream when it is shorter than two windows).
    /// </summary>
    /// <param name="stream">A readable, seekable stream positioned anywhere.</param>
    /// <param name="windowBytes">The window to hash at each end.</param>
    /// <exception cref="EndOfStreamException">The stream ended before its reported length.</exception>
    internal static DemoContentFingerprint Compute(Stream stream, int windowBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowBytes);
        long size = stream.Length;
        Windows windows = new(size, windowBytes);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            stream.Position = 0;
            ReadRange(stream, buffer, 0, windows.HeadEnd, windows, null);
            long resume = Math.Max(windows.HeadEnd, windows.TailStart);
            stream.Position = resume;
            ReadRange(stream, buffer, resume, size, windows, null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return windows.Finish();
    }

    /// <summary>The full content hash and the fingerprint from one sequential read of the whole stream.</summary>
    /// <param name="stream">A readable, seekable stream positioned anywhere.</param>
    /// <param name="windowBytes">The window to hash at each end.</param>
    /// <exception cref="EndOfStreamException">The stream ended before its reported length.</exception>
    /// <exception cref="IOException">The stream grew while it was read.</exception>
    internal static (string Sha256, DemoContentFingerprint Fingerprint) ComputeWithContentHash(Stream stream,
        int windowBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowBytes);
        long size = stream.Length;
        Windows windows = new(size, windowBytes);
        using IncrementalHash full = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            stream.Position = 0;
            ReadRange(stream, buffer, 0, size, windows, full);
            if (stream.Read(buffer, 0, 1) != 0)
            {
                throw new IOException("the file grew while it was hashed");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (Convert.ToHexStringLower(full.GetHashAndReset()), windows.Finish());
    }

    private static void ReadRange(Stream stream, byte[] buffer, long from, long to, Windows windows,
        IncrementalHash? full)
    {
        long offset = from;
        while (offset < to)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, to - offset));
            if (read == 0)
            {
                throw new EndOfStreamException("the file shrank while it was read");
            }

            ReadOnlySpan<byte> chunk = buffer.AsSpan(0, read);
            windows.Feed(offset, chunk);
            full?.AppendData(chunk);
            offset += read;
        }
    }

    // Routes each byte of one forward read to the head hash, the tail hash, or both when the windows overlap.
    private sealed class Windows(long size, int windowBytes)
    {
        private readonly IncrementalHash _head = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly IncrementalHash _tail = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public long HeadEnd { get; } = Math.Min(windowBytes, size);
        public long TailStart { get; } = Math.Max(0, size - windowBytes);

        public void Feed(long offset, ReadOnlySpan<byte> chunk)
        {
            if (offset < HeadEnd)
            {
                _head.AppendData(chunk[..(int)Math.Min(chunk.Length, HeadEnd - offset)]);
            }

            if (offset + chunk.Length > TailStart)
            {
                _tail.AppendData(chunk[(int)Math.Max(0, TailStart - offset)..]);
            }
        }

        public DemoContentFingerprint Finish()
        {
            using (_head)
            using (_tail)
            {
                return new DemoContentFingerprint(size, windowBytes,
                    Convert.ToHexStringLower(_head.GetHashAndReset()),
                    Convert.ToHexStringLower(_tail.GetHashAndReset()));
            }
        }
    }
}
