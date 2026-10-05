#region

using System.Runtime.CompilerServices;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     Work over what the demo cache already holds for a demo, run without reading the demo file. Asked about
///     a demo when its index row changes and on a re-check of the library; run with the demo's record, read
///     once for every record pass that wants it.
/// </summary>
public interface IRecordPass
{
    /// <summary>Unique among the record passes.</summary>
    string Id { get; }

    /// <summary>Who the pass belongs to, for fault reports. The pass's own id unless an extension owns it.</summary>
    string Owner => Id;

    /// <summary>Whether the demo behind <paramref name="entry" /> needs the pass. Answer from the row and memory only.</summary>
    bool Wants(DemoCacheIndexEntry entry);

    /// <summary>Does the pass's work. A throw skips this pass for this demo for the session.</summary>
    void Run(DemoCacheRecord record, CancellationToken cancellationToken);
}

/// <summary>
///     Runs the record passes. Fed by the cache's own change events and by <see cref="RecheckAll" />; plans
///     and runs in one light queue job at a time, reading each wanted demo's record once, outside the store's
///     capacity-1 cache. A pass does not run twice on one row object: the store replaces the row on every
///     write, so a pass runs again exactly when the demo's row changed.
///     <para>
///         A row written by an index older than version 3 carries no side players. When a pass reads such a
///         demo's record anyway, the row is refreshed from it, so the index fills in as record passes go.
///     </para>
/// </summary>
public sealed class RecordPassRunner : IDisposable
{
    /// <summary>The queue owner of the record pass runs.</summary>
    public const string Owner = "records";

    private const string RunKey = "records.run";
    private const string RunTitle = "Reading cached demos";

    private readonly object _lock = new();
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Pass, string Path)> _faulted = [];
    private readonly Dictionary<(string Pass, string Path), DemoCacheIndexEntry> _ranOn = [];
    private readonly Func<IReadOnlyList<IRecordPass>> _passes;
    private readonly IDemoProcessingQueue? _queue;
    private readonly DemoCacheStore _store;
    private bool _recheckAll;
    private bool _runQueued;
    private bool _disposed;

    /// <param name="passes">Read on every run, so a pass whose extension came on is seen on the next run.</param>
    /// <param name="store">The demo cache: the rows asked about and the records read.</param>
    /// <param name="queue">The processing queue; null runs on the pool.</param>
    public RecordPassRunner(Func<IReadOnlyList<IRecordPass>> passes, DemoCacheStore store, IDemoProcessingQueue? queue)
    {
        _passes = passes ?? throw new ArgumentNullException(nameof(passes));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _queue = queue;
        _store.Changed += OnStoreChanged;
    }

    /// <summary>Told when a pass throws, with its owner and the demo when there is one.</summary>
    public Action<IRecordPass, string?, Exception>? Faulted { get; set; }

    /// <summary>Asks every record pass about every demo, off the UI thread.</summary>
    public void RecheckAll()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _recheckAll = true;
        }

        Schedule();
    }

    /// <summary>Asks every record pass about one demo.</summary>
    /// <param name="path">The demo.</param>
    public void DemoChanged(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _dirty.Add(path);
        }

        Schedule();
    }

    /// <summary>True when the pass threw on the demo earlier in the session.</summary>
    public bool IsFaulted(string passId, string path)
    {
        lock (_lock)
        {
            return _faulted.Contains((passId, path));
        }
    }

    /// <summary>Detaches from the store; nothing runs after this.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _dirty.Clear();
        }

        _store.Changed -= OnStoreChanged;
    }

    // No job for a change while no record pass is on: a pass that comes on is handed the library by the
    // re-check that follows.
    private void OnStoreChanged(string? path)
    {
        if (!AnyPass())
        {
            return;
        }

        if (path is null)
        {
            RecheckAll();
        }
        else
        {
            DemoChanged(path);
        }
    }

    private bool AnyPass()
    {
        try
        {
            return _passes().Count > 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true;
        }
    }

    private void Schedule()
    {
        lock (_lock)
        {
            if (_runQueued)
            {
                return;
            }

            _runQueued = true;
        }

        // A run the user removes before it starts never drains; the next change must still be able to queue one.
        StrongBox<bool> started = new(false);
        _ = QueueWork.Run(_queue, QueueJobKind.RecordPass, RunTitle, Owner, token =>
            {
                started.Value = true;
                Drain(token);
            }, key: RunKey, preemptible: true)
            .ContinueWith(_ =>
            {
                if (!started.Value)
                {
                    lock (_lock)
                    {
                        _runQueued = false;
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // Loops until nothing is dirty, so a change made while a batch ran is taken by this job.
    private void Drain(CancellationToken token)
    {
        try
        {
            while (true)
            {
                List<string> batch;
                bool recheck;
                lock (_lock)
                {
                    recheck = _recheckAll;
                    _recheckAll = false;
                    if (_disposed || (!recheck && _dirty.Count == 0))
                    {
                        _runQueued = false;
                        return;
                    }

                    batch = [.. _dirty];
                    _dirty.Clear();
                }

                if (recheck)
                {
                    HashSet<string> seen = new(batch, StringComparer.OrdinalIgnoreCase);
                    batch.AddRange(_store.Index.Select(e => e.Path).Where(seen.Add));
                }

                IReadOnlyList<IRecordPass> passes = _passes();
                if (passes.Count == 0)
                {
                    continue;
                }

                bool refreshed = false;
                using (_store.BeginBatch())
                {
                    for (int i = 0; i < batch.Count; i++)
                    {
                        if (token.IsCancellationRequested)
                        {
                            lock (_lock)
                            {
                                _dirty.UnionWith(batch.Skip(i));
                                _runQueued = false;
                            }

                            return;
                        }

                        refreshed |= Visit(batch[i], passes, token);
                    }
                }

                if (refreshed)
                {
                    _store.SaveIndex();
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_lock)
            {
                _runQueued = false;
            }

            Report(null, null, ex);
        }
    }

    // One demo: the passes that want its current row, one record read, each pass asked again on the row the
    // read may have refreshed. True when the row was refreshed.
    private bool Visit(string path, IReadOnlyList<IRecordPass> passes, CancellationToken token)
    {
        if (_store.TryGetIndex(path) is not { } entry || Wanting(passes, path, entry).Count == 0)
        {
            return false;
        }

        if (_store.TryLoadRecord(path, false) is not { } record)
        {
            return false;
        }

        bool refreshed = false;
        if ((entry.CtPlayers is null || entry.TPlayers is null) && record.Parse.IsPresent && _store.RefreshIndexRow(record))
        {
            refreshed = true;
            entry = _store.TryGetIndex(path) ?? entry;
        }

        foreach (IRecordPass pass in Wanting(passes, path, entry))
        {
            token.ThrowIfCancellationRequested();
            lock (_lock)
            {
                _ranOn[(pass.Id, path)] = entry;
            }

            try
            {
                pass.Run(record, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lock (_lock)
                {
                    _faulted.Add((pass.Id, path));
                }

                Report(pass, path, ex);
            }
        }

        return refreshed;
    }

    private List<IRecordPass> Wanting(IReadOnlyList<IRecordPass> passes, string path, DemoCacheIndexEntry entry)
    {
        List<IRecordPass> wanting = [];
        foreach (IRecordPass pass in passes)
        {
            lock (_lock)
            {
                if (_faulted.Contains((pass.Id, path))
                    || (_ranOn.TryGetValue((pass.Id, path), out DemoCacheIndexEntry? ran) && ReferenceEquals(ran, entry)))
                {
                    continue;
                }
            }

            bool wants;
            try
            {
                wants = pass.Wants(entry);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lock (_lock)
                {
                    _faulted.Add((pass.Id, path));
                }

                Report(pass, path, ex);
                continue;
            }

            if (wants)
            {
                wanting.Add(pass);
            }
        }

        return wanting;
    }

    private void Report(IRecordPass? pass, string? path, Exception exception)
    {
        if (pass is null)
        {
            return;
        }

        try
        {
            Faulted?.Invoke(pass, path, exception);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing to fall back to.
        }
    }
}
