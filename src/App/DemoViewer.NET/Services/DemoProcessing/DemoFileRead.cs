#region

using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>A content hash taken from a whole-file read of a demo that was being read anyway.</summary>
/// <param name="Path">The file read.</param>
/// <param name="Sha256">Lowercase-hex SHA-256 of the bytes read.</param>
/// <param name="Fingerprint">The fingerprint taken in the same read.</param>
/// <param name="Stat">The file's size and write time when read.</param>
public sealed record DemoContentRead(string Path, string Sha256, DemoContentFingerprint? Fingerprint, FileStat Stat);

/// <summary>
///     How the queue reads a demo it parses. A settled local file is memory-mapped. A file on a network mount,
///     or one that may still be written, is read into memory on a thread of its own, which is abandoned when it
///     stops delivering bytes: a mapped view of a dropped network file faults rather than throws, and a read
///     that never answers would hold the heavy slot for good. When the caller wants the content hash it is
///     taken from that same read, so a network demo is read once.
/// </summary>
/// <param name="time">The clock the settle window is measured against.</param>
/// <param name="wantsHash">True for a path whose content hash nothing has confirmed; null wants none.</param>
/// <param name="contentRead">Takes the hash of each whole file read for a settled file; null takes none.</param>
public sealed class DemoFileRead(TimeProvider time, Func<string, bool>? wantsHash, Action<DemoContentRead>? contentRead)
{
    /// <summary>How long a read may deliver no bytes before it is abandoned.</summary>
    public static readonly TimeSpan NoProgress = TimeSpan.FromSeconds(60);

    private const int Chunk = 1 << 20;

    /// <summary>True when the read reports hashes and the hash of this path is wanted.</summary>
    /// <param name="path">A demo path.</param>
    public bool WantsHash(string path) => contentRead is not null && wantsHash?.Invoke(path) == true;

    /// <summary>
    ///     The bytes to parse <paramref name="path" /> from, or null when the file is to be mapped. Reports the
    ///     content hash when it is wanted and the file is settled, from the bytes read or, for a mapped file,
    ///     from a local read before it is mapped.
    /// </summary>
    /// <param name="path">The demo to read.</param>
    /// <param name="cancellationToken">Abandons the read.</param>
    /// <exception cref="IOException">The read failed or delivered no bytes for <see cref="NoProgress" />.</exception>
    public byte[]? Prepare(string path, CancellationToken cancellationToken)
    {
        bool want = WantsHash(path);
        if (OperatingSystem.IsBrowser())
        {
            return File.ReadAllBytes(path);
        }

        bool settled = MappedParsePolicy.IsSettled(path, time, MappedParsePolicy.StatFile);
        if (settled && !NetworkMounts.IsNetwork(path))
        {
            if (want)
            {
                FileStat stat = MappedParsePolicy.StatFile(path);
                (string? sha, DemoContentFingerprint? fingerprint) = DemoContentFingerprint.TryComputeWithContentHash(path, time);
                if (sha is not null)
                {
                    contentRead!(new DemoContentRead(path, sha, fingerprint, stat));
                }
            }

            return null;
        }

        FileStat before = MappedParsePolicy.StatFile(path);
        byte[] bytes = ReadAll(path, NoProgress, cancellationToken);
        if (want && settled && bytes.LongLength == before.Length && MappedParsePolicy.StatFile(path) == before)
        {
            using MemoryStream stream = new(bytes, false);
            (string sha, DemoContentFingerprint fingerprint) =
                DemoContentFingerprint.ComputeWithContentHash(stream, DemoContentFingerprint.DefaultWindowBytes);
            contentRead!(new DemoContentRead(path, sha, fingerprint, before));
        }

        return bytes;
    }

    /// <summary>
    ///     Reads a whole file on a thread of its own. The caller waits only while bytes keep arriving: a read
    ///     that delivers nothing for <paramref name="noProgress" /> is abandoned and its thread left to finish.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <param name="noProgress">How long the read may deliver no bytes.</param>
    /// <param name="cancellationToken">Abandons the read.</param>
    /// <exception cref="IOException">The read failed, the file changed length, or the read stalled.</exception>
    public static byte[] ReadAll(string path, TimeSpan noProgress, CancellationToken cancellationToken)
    {
        long done = 0;
        int abandoned = 0;
        Task<byte[]> read = Task.Factory.StartNew(() =>
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1,
                FileOptions.SequentialScan);
            long length = stream.Length;
            if (length > Array.MaxLength)
            {
                throw new IOException($"{path} is too large to read into memory");
            }

            byte[] buffer = GC.AllocateUninitializedArray<byte>((int)length);
            int offset = 0;
            while (offset < buffer.Length && Volatile.Read(ref abandoned) == 0)
            {
                int got = stream.Read(buffer, offset, Math.Min(Chunk, buffer.Length - offset));
                if (got == 0)
                {
                    throw new IOException($"{path} shrank while it was read");
                }

                offset += got;
                Interlocked.Exchange(ref done, offset);
            }

            return buffer;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        long seen = -1;
        DateTime lastProgress = DateTime.UtcNow;
        TimeSpan poll = noProgress < TimeSpan.FromSeconds(1) ? noProgress : TimeSpan.FromSeconds(1);
        try
        {
            while (Task.WaitAny(new Task[] { read }, (int)poll.TotalMilliseconds, cancellationToken) < 0)
            {
                long now = Interlocked.Read(ref done);
                if (now != seen)
                {
                    seen = now;
                    lastProgress = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastProgress >= noProgress)
                {
                    throw new IOException($"{path} delivered no bytes in {noProgress.TotalSeconds:0.#} s");
                }
            }
        }
        catch (Exception)
        {
            Volatile.Write(ref abandoned, 1);
            _ = read.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            throw;
        }

        try
        {
            return read.GetAwaiter().GetResult();
        }
        catch (UnauthorizedAccessException e)
        {
            throw new IOException(e.Message, e);
        }
    }
}

/// <summary>Whether a path is on a network mount, asked of the mount table and remembered per folder.</summary>
internal static class NetworkMounts
{
    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Answer = TimeSpan.FromSeconds(5);
    private static readonly Dictionary<string, (bool Network, DateTime At)> _byFolder = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     True for a path on a network mount, or one whose mount does not answer in time: such a path is read
    ///     the guarded way.
    /// </summary>
    /// <param name="path">A file path.</param>
    public static bool IsNetwork(string path)
    {
        if (OperatingSystem.IsWindows() && path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? path;
        lock (_byFolder)
        {
            if (_byFolder.TryGetValue(folder, out (bool Network, DateTime At) known) && DateTime.UtcNow - known.At < Remember)
            {
                return known.Network;
            }
        }

        // The drive type is a statfs of the mount, which a dead network mount may never answer.
        Task<bool> ask = Task.Run(() => Lookup(folder));
        bool network = !ask.Wait(Answer) || ask.Result;
        lock (_byFolder)
        {
            _byFolder[folder] = (network, DateTime.UtcNow);
        }

        return network;
    }

    private static bool Lookup(string folder)
    {
        try
        {
            DriveInfo? mount = DriveInfo.GetDrives()
                .Where(d => Under(folder, d.RootDirectory.FullName))
                .MaxBy(d => d.RootDirectory.FullName.Length);
            return mount?.DriveType == DriveType.Network;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Under(string folder, string root)
    {
        StringComparison comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string withSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return string.Equals(folder, root.TrimEnd(Path.DirectorySeparatorChar), comparison)
               || folder.StartsWith(withSeparator, comparison);
    }
}
