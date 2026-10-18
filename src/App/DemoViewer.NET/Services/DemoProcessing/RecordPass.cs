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

    /// <summary>
    ///     Stamp ids on the row the pass reads. A pass naming any is held on a demo until each is on its row, and
    ///     runs again only when one of them moves; a pass naming none runs again on every new row.
    /// </summary>
    IReadOnlyList<string> InputStamps => [];

    /// <summary>The stamp behind one of <see cref="InputStamps" /> for the demo, or null while it is not written. The row's own by default.</summary>
    PackStamp? InputStamp(DemoCacheIndexEntry entry, string id) => entry.Stamp(id);

    /// <summary>Why the runner must not take the pass, or null. Asked on every run; a reason is reported once until it changes.</summary>
    string? Refusal() => null;

    /// <summary>True while an input the pass reads is switched off: the pass waits, unreported, until it is on again.</summary>
    bool InputsOff() => false;

    /// <summary>Whether the demo behind <paramref name="entry" /> needs the pass. Answer from the row and memory only.</summary>
    bool Wants(DemoCacheIndexEntry entry);

    /// <summary>Does the pass's work. A throw skips this pass for this demo for the session.</summary>
    void Run(DemoCacheRecord record, CancellationToken cancellationToken);
}

/// <summary>
///     Runs the record passes. Fed by the cache's own change events and by <see cref="RecheckAll" />; plans
///     and runs in one light queue job at a time, reading each wanted demo's record once, outside the store's
///     capacity-1 cache. A pass does not run twice on one row object: the store replaces the row on every
///     write, so a pass runs again exactly when the demo's row changed. A pass that names
///     <see cref="IRecordPass.InputStamps" /> is keyed on those stamps instead of the row. A pass with a
///     <see cref="IRecordPass.Refusal" /> or <see cref="IRecordPass.InputsOff" /> is skipped for the run; both are asked
///     again on the next run, so a pass held while another extension was off is taken once it comes on. A demo held at several paths is one
///     demo: it is visited, run and faulted once, through its primary path.
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

    // Keyed by the store's demo key, so every path of one demo shares them. A fault is also held under the
    // path it was visited by: that path's key changes once its file is hashed.
    private readonly HashSet<(string Pass, string Demo)> _faulted = new(PassDemoKeyComparer.Instance);
    private readonly Dictionary<(string Pass, string Demo), DemoCacheIndexEntry> _ranOn = new(PassDemoKeyComparer.Instance);
    private readonly Dictionary<(string Pass, string Demo), PackStamp[]> _ranOnInputs = new(PassDemoKeyComparer.Instance);
    private readonly Dictionary<string, string> _refused = new(StringComparer.Ordinal);
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
        _store.Changed += InputChanged;
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

    /// <summary>True when the pass threw on the demo, through any of its paths, earlier in the session.</summary>
    public bool IsFaulted(string passId, string path)
    {
        string demo = _store.DemoKeyOf(path);
        lock (_lock)
        {
            return _faulted.Contains((passId, demo)) || _faulted.Contains((passId, path));
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

        _store.Changed -= InputChanged;
    }

    /// <summary>
    ///     Something a pass reads changed for the demo at <paramref name="path" />, or for every demo when null.
    ///     No job while no record pass is on: a pass that comes on is handed the library by the re-check that follows.
    /// </summary>
    /// <param name="path">The demo, or null for all of them.</param>
    public void InputChanged(string? path)
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
                    batch.AddRange(_store.Contents.Select(e => e.Path));
                }

                HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
                batch = [.. batch.Where(p => seen.Add(_store.DemoKeyOf(p)))];

                List<IRecordPass> passes = Taken(_passes());
                if (passes.Count == 0)
                {
                    continue;
                }

                bool refreshed = false;
                using (_store.BeginBatch())
                {
                    for (int i = 0; i < batch.Count; i++)
                    {
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            refreshed |= Visit(batch[i], passes, token);
                        }
                        catch (OperationCanceledException)
                        {
                            // The demo it stopped on and the rest stay dirty. Rethrown so a preempted run is
                            // requeued; returning would end it Completed with the work left for an unrelated change.
                            lock (_lock)
                            {
                                _dirty.UnionWith(batch.Skip(i));
                                _runQueued = false;
                            }

                            throw;
                        }
                    }
                }

                if (refreshed)
                {
                    _store.SaveIndex();
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
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
        string demo = _store.DemoKeyOf(path);
        if (_store.TryGetPrimary(path) is not { } entry || Wanting(passes, demo, entry).Count == 0)
        {
            return false;
        }

        path = entry.Path;
        if (_store.TryLoadRecord(path, false) is not { } record)
        {
            return false;
        }

        bool refreshed = false;
        if ((entry.CtPlayers is null || entry.TPlayers is null) && record.Parse.IsPresent && _store.RefreshIndexRow(record))
        {
            refreshed = true;
            entry = _store.TryGetPrimary(path) ?? entry;
        }

        foreach (IRecordPass pass in Wanting(passes, demo, entry))
        {
            token.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (InputsOn(pass, entry) is { } inputs)
                {
                    _ranOnInputs[(pass.Id, demo)] = inputs;
                }
                else
                {
                    _ranOn[(pass.Id, demo)] = entry;
                }
            }

            try
            {
                pass.Run(record, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Stopped part-way: the pass has not run on this row, so the requeued run must ask it again.
                lock (_lock)
                {
                    _ranOnInputs.Remove((pass.Id, demo));
                    _ranOn.Remove((pass.Id, demo));
                }

                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lock (_lock)
                {
                    _faulted.Add((pass.Id, demo));
                    _faulted.Add((pass.Id, entry.Path));
                }

                Report(pass, path, ex);
            }
        }

        return refreshed;
    }

    private List<IRecordPass> Wanting(IReadOnlyList<IRecordPass> passes, string demo, DemoCacheIndexEntry entry)
    {
        List<IRecordPass> wanting = [];
        foreach (IRecordPass pass in passes)
        {
            PackStamp[]? inputs = InputsOn(pass, entry);
            if (pass.InputStamps.Count > 0 && inputs is null)
            {
                continue;
            }

            lock (_lock)
            {
                if (_faulted.Contains((pass.Id, demo)) || _faulted.Contains((pass.Id, entry.Path))
                    || (inputs is null
                        ? _ranOn.TryGetValue((pass.Id, demo), out DemoCacheIndexEntry? ran) && ReferenceEquals(ran, entry)
                        : _ranOnInputs.TryGetValue((pass.Id, demo), out PackStamp[]? ranInputs) && ranInputs.SequenceEqual(inputs)))
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
                    _faulted.Add((pass.Id, demo));
                    _faulted.Add((pass.Id, entry.Path));
                }

                Report(pass, entry.Path, ex);
                continue;
            }

            if (wants)
            {
                wanting.Add(pass);
            }
        }

        return wanting;
    }

    // Null for a pass that names no inputs, or while one of them is not on the row. Any stamp counts, failed or stale.
    private static PackStamp[]? InputsOn(IRecordPass pass, DemoCacheIndexEntry entry)
    {
        IReadOnlyList<string> ids = pass.InputStamps;
        if (ids.Count == 0)
        {
            return null;
        }

        PackStamp[] stamps = new PackStamp[ids.Count];
        for (int i = 0; i < ids.Count; i++)
        {
            if (pass.InputStamp(entry, ids[i]) is not { } stamp)
            {
                return null;
            }

            stamps[i] = stamp;
        }

        return stamps;
    }

    // The passes this run takes. A refusal is reported when its reason is new, so a pass is not reported on every run.
    private List<IRecordPass> Taken(IReadOnlyList<IRecordPass> passes)
    {
        List<IRecordPass> taken = [];
        foreach (IRecordPass pass in passes)
        {
            Exception? refusal;
            bool off = false;
            try
            {
                refusal = pass.Refusal() is { } reason ? new InvalidOperationException(reason) : null;
                off = refusal is null && pass.InputsOff();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                refusal = ex;
            }

            bool report;
            lock (_lock)
            {
                if (refusal is null)
                {
                    _refused.Remove(pass.Id);
                    report = false;
                }
                else
                {
                    report = !(_refused.TryGetValue(pass.Id, out string? last) && string.Equals(last, refusal.Message, StringComparison.Ordinal));
                    _refused[pass.Id] = refusal.Message;
                }
            }

            if (report)
            {
                Report(pass, null, refusal!);
            }
            else if (refusal is null && !off)
            {
                taken.Add(pass);
            }
        }

        return taken;
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
