#region

using System.Threading.Channels;

#endregion

namespace DemoViewer.NET.Services.Diagnostics;

/// <summary>
///     A bounded, rolling, non-blocking file sink for the unified diagnostics logs, written under
///     <see cref="AppPaths.LogsDir" /> (a stable app-data location, not temp) so a copied diagnostics
///     report can attach recent history for a user-reported issue.
///     <para>
///         <b>Non-blocking:</b> producers call <see cref="Write" />, which drops a preformatted line
///         into a bounded channel and returns immediately: no file I/O on the caller's thread (which
///         may be the UI thread). A single background pump serializes writes to disk. The channel is
///         bounded and drops on overflow, so a stalled disk can never balloon memory.
///     </para>
///     <para>
///         <b>Bounded on disk:</b> the active <c>diagnostics.log</c> rolls to
///         <c>diagnostics.1.log</c> … once it passes the size cap; at most <c>FileMaxCount</c> rolled
///         files are retained. Both caps are read live. No filesystem on WASM → <see cref="TryCreate" />
///         returns <c>null</c> and the pillar simply skips file mirroring.
///     </para>
/// </summary>
public sealed class DiagnosticsFileLog : IDisposable
{
    private const string ActiveName = "diagnostics.log";
    private readonly string _activeFile;
    private readonly Channel<string> _channel;

    private readonly string _dir;
    private readonly Func<long> _maxBytes;
    private readonly Func<int> _maxFiles;
    private readonly Task _pump;

    private StreamWriter? _writer;
    private long _written;

    private DiagnosticsFileLog(string dir, Func<long> maxBytes, Func<int> maxFiles)
    {
        _dir = dir;
        _activeFile = Path.Combine(dir, ActiveName);
        _maxBytes = maxBytes;
        _maxFiles = maxFiles;
        // Bounded + drop-oldest so a stalled writer bounds memory; single reader = the pump.
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(16384)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Stops the pump and flushes. Idempotent; best-effort.</summary>
    public void Dispose()
    {
        _channel.Writer.TryComplete();
        try
        {
            _pump.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort drain on shutdown: never block teardown on a stuck disk.
        }

        _writer?.Dispose();
        _writer = null;
    }

    /// <summary>
    ///     Creates the sink, ensuring the logs directory exists. Returns <c>null</c> on WASM (no
    ///     filesystem) or if the directory can't be created, the caller then runs without file
    ///     mirroring. <paramref name="maxKilobytes" /> / <paramref name="maxFiles" /> are read live.
    /// </summary>
    public static DiagnosticsFileLog? TryCreate(Func<int> maxKilobytes, Func<int> maxFiles)
    {
        string? dir = AppPaths.EnsureLogsDirectory();
        if (dir is null)
        {
            return null;
        }

        // Public contract is kilobytes (floor 64 KiB); the internal seam works in raw bytes.
        return TryCreateInDirectory(dir, () => Math.Max(64, maxKilobytes()) * 1024L, maxFiles);
    }

    /// <summary>
    ///     Test seam (InternalsVisibleTo): create a sink writing into an explicit directory, bypassing
    ///     <see cref="AppPaths" />. Byte cap can be floored low here so tests can force a roll cheaply.
    /// </summary>
    internal static DiagnosticsFileLog? TryCreateInDirectory(string dir, Func<long> maxBytes, Func<int> maxFiles)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return new DiagnosticsFileLog(dir,
                () => Math.Max(1, maxBytes()),
                () => Math.Max(1, maxFiles()));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Queues a preformatted line for the background pump. Never blocks, never throws.</summary>
    public void Write(string line) => _channel.Writer.TryWrite(line);

    /// <summary>
    ///     Reads at most the last <paramref name="maxLines" /> lines of a log file for the copy-diagnostics
    ///     attachment. Opens with <see cref="FileShare.ReadWrite" /> so it succeeds even while THIS sink
    ///     holds the active file open for writing (the common case: a demo has been loaded). Best-effort:
    ///     returns an empty list on any error rather than throwing.
    /// </summary>
    public static List<string> ReadTail(string path, int maxLines)
    {
        try
        {
            List<string> all = [];
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(fs);
            while (reader.ReadLine() is { } line)
            {
                all.Add(line);
            }

            return all.Count <= maxLines ? all : all.GetRange(all.Count - maxLines, maxLines);
        }
        catch
        {
            return [];
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (string line in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    EnsureWriter();
                    _writer!.WriteLine(line);
                    _written += line.Length + Environment.NewLine.Length;
                    if (_written >= _maxBytes())
                    {
                        Roll();
                    }
                }
                catch
                {
                    // A single bad write (locked file, full disk) must not kill the pump: drop and go on.
                }
            }
        }
        catch
        {
            // Channel completion / shutdown.
        }
    }

    private void EnsureWriter()
    {
        if (_writer is not null)
        {
            return;
        }

        _written = File.Exists(_activeFile) ? new FileInfo(_activeFile).Length : 0;
        // AutoFlush so a crash keeps the lines leading up to it, the whole point of the file mirror.
        _writer = new StreamWriter(_activeFile, true)
        {
            AutoFlush = true
        };
    }

    // Runs only on the single pump thread, so no locking around the writer / file moves.
    private void Roll()
    {
        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;

        int keep = _maxFiles();
        try
        {
            string oldest = Path.Combine(_dir, $"diagnostics.{keep}.log");
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (int i = keep - 1; i >= 1; i--)
            {
                string src = Path.Combine(_dir, $"diagnostics.{i}.log");
                if (File.Exists(src))
                {
                    File.Move(src, Path.Combine(_dir, $"diagnostics.{i + 1}.log"), true);
                }
            }

            if (File.Exists(_activeFile))
            {
                File.Move(_activeFile, Path.Combine(_dir, "diagnostics.1.log"), true);
            }
        }
        catch
        {
            // If the roll fails (locked file), fall through and just keep appending to the active file;
            // EnsureWriter reopens it. Bounded-ish still, and logging never throws.
        }

        _written = 0;
    }
}
