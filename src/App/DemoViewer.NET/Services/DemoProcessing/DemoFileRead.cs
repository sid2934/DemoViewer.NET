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
    public static byte[] ReadAll(string path, TimeSpan noProgress, CancellationToken cancellationToken) =>
        Watched(path, noProgress, stream =>
        {
            long length = stream.Length;
            if (length > Array.MaxLength)
            {
                throw new IOException($"{path} is too large to read into memory");
            }

            byte[] buffer = GC.AllocateUninitializedArray<byte>((int)length);
            int offset = 0;
            while (offset < buffer.Length)
            {
                int got = stream.Read(buffer, offset, Math.Min(Chunk, buffer.Length - offset));
                if (got == 0)
                {
                    throw new IOException($"{path} shrank while it was read");
                }

                offset += got;
            }

            return buffer;
        }, cancellationToken);

    /// <summary>
    ///     The content hash and fingerprint of a settled file from one streaming read, guarded as
    ///     <see cref="ReadAll" /> is. Both null when the file may still be written, cannot be read, changed
    ///     while it was read, or the read stalled.
    /// </summary>
    /// <param name="path">The file to hash.</param>
    /// <param name="time">The clock the settle window is measured against.</param>
    /// <param name="windowBytes">The fingerprint window.</param>
    /// <param name="noProgress">How long the read may deliver no bytes.</param>
    /// <param name="cancellationToken">Abandons the read.</param>
    public static (string? Sha256, DemoContentFingerprint? Fingerprint) TryHash(string path, TimeProvider time,
        int windowBytes, TimeSpan noProgress, CancellationToken cancellationToken)
    {
        try
        {
            if (!MappedParsePolicy.IsSettled(path, time, MappedParsePolicy.StatFile))
            {
                return (null, null);
            }

            FileStat before = MappedParsePolicy.StatFile(path);
            (string sha, DemoContentFingerprint fingerprint) = Watched(path, noProgress,
                stream => DemoContentFingerprint.ComputeWithContentHash(stream, windowBytes), cancellationToken);
            return MappedParsePolicy.StatFile(path) == before && fingerprint.Size == before.Length
                ? (sha, fingerprint)
                : (null, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    // Runs body over the open file on a thread of its own and waits while it keeps reading.
    private static T Watched<T>(string path, TimeSpan noProgress, Func<Stream, T> body, CancellationToken cancellationToken)
    {
        ProgressStream? watched = null;
        Task<T> read = Task.Factory.StartNew(() =>
        {
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1,
                FileOptions.SequentialScan);
            using ProgressStream stream = new(file);
            Volatile.Write(ref watched, stream);
            return body(stream);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        long seen = -1;
        DateTime lastProgress = DateTime.UtcNow;
        TimeSpan poll = noProgress < TimeSpan.FromSeconds(1) ? noProgress : TimeSpan.FromSeconds(1);
        try
        {
            while (Task.WaitAny(new Task[] { read }, (int)poll.TotalMilliseconds, cancellationToken) < 0)
            {
                long now = Volatile.Read(ref watched)?.Done ?? 0;
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
            Volatile.Read(ref watched)?.Abandon();
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

    // Counts the bytes read for the watchdog, and stops a read the caller gave up on at its next call.
    private sealed class ProgressStream(Stream inner) : Stream
    {
        private long _done;
        private int _abandoned;

        public long Done => Interlocked.Read(ref _done);

        public void Abandon() => Volatile.Write(ref _abandoned, 1);

        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Volatile.Read(ref _abandoned) != 0)
            {
                throw new IOException("the read was abandoned");
            }

            int got = inner.Read(buffer, offset, count);
            Interlocked.Add(ref _done, got);
            return got;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
