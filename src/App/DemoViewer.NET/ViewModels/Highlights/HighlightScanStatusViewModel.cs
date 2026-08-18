#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.ViewModels.Highlights;

/// <summary>
///     Library-wide highlight-scan progress as the <b>fourth</b> <c>StatusChip</c> consumer
///     (row 2) — the home the card grid's
///     <c>ScanQueueSummary</c> badge and its per-card scanning animation were re-assigned to. The design
///     system says verbatim that three consumers now share the control and <em>"a fourth should extend it,
///     not fork"</em>, so this owns a <see cref="StatusChipViewModel" /> whose <c>FlyoutContent</c> is this
///     VM and adds nothing to the shared control.
///     <para>
///         <b>Pure projection — safe to instantiate more than once.</b> Unlike
///         <see cref="ReelJobStatusViewModel" /> (which must be a single instance,
///         because the chip and the inline strip are two views of one <em>job</em>), this holds no job state
///         at all: every property is derived from the live <see cref="HighlightScanService" /> plus the
///         <see cref="DemoCacheStore" /> rows. Two instances cannot disagree, so the shell may build
///         its own at composition (so the chip exists before the Reels tab is first activated) without
///         creating a second source of truth.
///     </para>
///     <para>
///         <b>It also restores a lost entry point.</b> <c>HighlightScanService.RequestScan(path)</c> lost its
///         last UI caller when the card grid's per-demo staleness/failed badges went away (recorded in the
///         design system as a deliberate reduction). <see cref="RetryAllFailedCommand" /> calls it per failed
///         row, so a failed scan is recoverable again without a whole-library rescan.
///     </para>
/// </summary>
public sealed partial class HighlightScanStatusViewModel : ViewModelBase, IDisposable
{
    private readonly HighlightScanService _scanner;
    private readonly DemoCacheStore _store;

    private bool _disposed;

    /// <summary>How many rows still failed their last scan (the <c>Retry all failed</c> population).</summary>
    [ObservableProperty]
    private int _failedCount;

    /// <summary>True while the coordinator has outstanding highlight work (drives the pulse).</summary>
    [ObservableProperty]
    private bool _isScanning;

    /// <summary>How many demos are queued for a scan (Pending rows).</summary>
    [ObservableProperty]
    private int _queueDepth;

    /// <summary>
    ///     The demo believed to be scanning right now — the ONE thing the per-card animation carried that a
    ///     chip cannot: which demo. Null when nothing is in flight.
    /// </summary>
    [ObservableProperty]
    private string? _scanningName;

    /// <summary>
    ///     Rows whose cached highlights are stale (re-queued but still carrying their previous harvest).
    ///     Same rule the card grid used: <c>Pending &amp;&amp; Events.Count &gt; 0</c>. A stale row still
    ///     shows its old highlights everywhere, which is why the count is worth surfacing at all.
    /// </summary>
    [ObservableProperty]
    private int _staleCount;

    /// <summary>The flyout's one-line summary ("12 queued · scanning …", "Everything is indexed", …).</summary>
    [ObservableProperty]
    private string _statusLine = "";

    // ── Determinate batch progress (v0.6.0, item 12) ──────────────────────────
    // The scanner exposes only the REMAINING queue, so the batch size is tracked here: the peak of
    // (queued + in-flight) since the last idle. New requests joining mid-batch raise the peak, so
    // the bar never runs backwards; idle resets it for the next batch.

    /// <summary>Total demos in the current scan batch (the peak backlog since last idle).</summary>
    [ObservableProperty]
    private int _batchTotal;

    /// <summary>Demos completed in the current batch (<see cref="BatchTotal" /> − remaining).</summary>
    [ObservableProperty]
    private int _batchDone;

    /// <summary>
    ///     Gates the "N of M" line + bar: meaningful only while work remains AND the batch has more
    ///     than one demo (a single-demo scan gets adequate feedback from the pulsing dot).
    /// </summary>
    public bool HasBatchProgress => BatchTotal > 1 && (IsScanning || QueueDepth > 0);

    /// <summary>"N of M scanned" for the flyout.</summary>
    public string BatchProgressText => $"{BatchDone} of {BatchTotal} scanned";

    /// <summary>
    ///     Builds the projection over the live scanner + cache and seeds it immediately (never blank), then
    ///     tracks both change sources.
    /// </summary>
    /// <param name="scanner">The library-wide highlight scan/backfill service.</param>
    /// <param name="store">The highlights cache the scanner writes into (row states live here, not on the service).</param>
    public HighlightScanStatusViewModel(HighlightScanService scanner, DemoCacheStore store)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(store);
        _scanner = scanner;
        _store = store;

        Chip = new StatusChipViewModel
        {
            FlyoutContent = this
        };

        // BOTH sources matter and neither implies the other: ScanProgressChanged fires on queue/lifecycle
        // moves, Changed fires when a row's ScanState or Events are rewritten. Subscribing to only the first
        // left the stale/failed counts frozen after a background harvest completed.
        _scanner.ScanProgressChanged += Refresh;
        _store.Changed += OnCacheChanged;
        Refresh();
    }

    /// <summary>The status-strip chip this VM drives (the shell adds it to <c>MainViewModel.Chips</c>).</summary>
    public StatusChipViewModel Chip { get; }

    /// <summary>True when at least one row failed — gates the flyout's <c>Retry all failed</c> action.</summary>
    public bool HasFailed => FailedCount > 0;

    /// <summary>True when at least one row is carrying a stale harvest.</summary>
    public bool HasStale => StaleCount > 0;

    /// <summary>True while a specific demo is believed to be in flight (gates the "◐ scanning …" line).</summary>
    public bool HasScanningName => !string.IsNullOrEmpty(ScanningName);

    /// <summary>
    ///     Whether the chip is worth showing at all — the presence rule the shell reconciles against
    ///     (mirroring <c>ReconcileQueueChip</c>). An idle, fully-indexed library adds no strip clutter.
    /// </summary>
    public bool IsRelevant => QueueDepth > 0 || IsScanning || FailedCount > 0;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scanner.ScanProgressChanged -= Refresh;
        _store.Changed -= OnCacheChanged;
    }

    /// <summary>
    ///     Re-queues every failed row at user priority (the aggregate action). Per-row
    ///     <c>RequestScan</c> rather than <c>RescanAll</c>: a user retrying three broken demos has not asked
    ///     to re-harvest the seven hundred that worked.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasFailed))]
    private void RetryAllFailed()
    {
        // Materialise BEFORE calling RequestScan: it Updates each row (Pending), and the store raises
        // Changed, so enumerating Rows lazily while mutating rows is a modification-during-enumeration bug
        // waiting for the first user with two failed demos.
        List<string> failed =
        [
            .. _store.Index.Where(r => r.AnalysisState == DemoAnalysisState.Failed).Select(r => r.Path)
        ];
        foreach (string path in failed)
        {
            _scanner.RequestScan(path);
        }
    }

    /// <summary>Whole-library rescan — the flyout's copy of the dashboard/picker action.</summary>
    [RelayCommand]
    private void RescanAll() => _scanner.RescanAll();

    // Library-wide counts move for ANY demo, so the changed path is accepted and ignored.
    private void OnCacheChanged(string? changedPath) => Refresh();

    private void Refresh()
    {
        // The backlog is DERIVED now — the scanner owns the rule (fingerprint + tier state), so asking it is
        // the only way these counts and its own queue can never disagree.
        IReadOnlyList<string> queued = _scanner.PendingPaths();
        IReadOnlyList<DemoCacheIndexEntry> rows = _store.Index;
        Dictionary<string, DemoCacheIndexEntry> byPath = new(StringComparer.OrdinalIgnoreCase);
        foreach (DemoCacheIndexEntry entry in rows)
        {
            byPath.TryAdd(entry.Path, entry);
        }

        QueueDepth = queued.Count;
        FailedCount = rows.Count(r => r.AnalysisState == DemoAnalysisState.Failed);
        // Stale = queued but still carrying a previous harvest — the re-queued-yet-showing-results case. Same
        // rule as before; it just reads highlight COUNT off the index instead of a Pending flag.
        StaleCount = queued.Count(p => byPath.TryGetValue(p, out DemoCacheIndexEntry? e) && e.HighlightCount > 0);
        IsScanning = _scanner.IsScanning;

        // The in-flight demo, inferred exactly as the card grid inferred it: the newest Pending row while a
        // drain is running. The service exposes no "currently decoding" path, and inventing one would mean
        // touching the scan service for a cosmetic label.
        // PendingPaths is already newest-first, which is the order the queue drains in.
        ScanningName = IsScanning && queued.Count > 0 ? Path.GetFileName(queued[0]) : null;

        // Batch progress (v0.6.0): remaining = queue + the in-flight demo; the peak since last idle
        // is the batch size. Idle (nothing queued or in flight) closes the batch.
        int remaining = QueueDepth + (IsScanning ? 1 : 0);
        if (remaining == 0)
        {
            BatchTotal = 0;
            BatchDone = 0;
        }
        else
        {
            BatchTotal = Math.Max(BatchTotal, remaining);
            BatchDone = BatchTotal - remaining;
        }

        StatusLine = BuildStatusLine();
        Chip.Label = BuildChipLabel();
        Chip.Tooltip = StatusLine;
        Chip.IsPulsing = IsScanning;
        Chip.DotState = IsScanning ? StatusChipDotState.Working
            : FailedCount > 0 ? StatusChipDotState.Error
            : QueueDepth > 0 ? StatusChipDotState.Working
            : StatusChipDotState.Off;

        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(HasStale));
        OnPropertyChanged(nameof(HasScanningName));
        OnPropertyChanged(nameof(IsRelevant));
        OnPropertyChanged(nameof(HasBatchProgress));
        OnPropertyChanged(nameof(BatchProgressText));
        RetryAllFailedCommand.NotifyCanExecuteChanged();
    }

    // The chip LABEL carries the state in words — the dot is a redundant colour cue (WCAG 1.4.1, and the
    // StatusChip contrast contract forbids tinting the label to signal state).
    private string BuildChipLabel()
    {
        if (IsScanning)
        {
            return QueueDepth > 0 ? $"Highlights · scanning ({QueueDepth} left)" : "Highlights · scanning";
        }

        if (QueueDepth > 0)
        {
            return $"Highlights · {QueueDepth} queued";
        }

        return FailedCount > 0 ? $"Highlights · {FailedCount} failed" : "Highlights · idle";
    }

    private string BuildStatusLine()
    {
        if (QueueDepth == 0 && FailedCount == 0)
        {
            return "Every demo in the cache has been scanned for highlights.";
        }

        List<string> parts = [];
        if (QueueDepth > 0)
        {
            parts.Add($"{QueueDepth} queued");
        }

        if (IsScanning)
        {
            parts.Add("scanning now");
        }

        if (FailedCount > 0)
        {
            parts.Add($"{FailedCount} failed");
        }

        return string.Join(" · ", parts);
    }
}
