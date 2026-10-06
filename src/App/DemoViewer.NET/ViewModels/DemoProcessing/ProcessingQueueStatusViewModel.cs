#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.ViewModels.DemoProcessing;

/// <summary>
///     Maps the global <see cref="IDemoProcessingQueue" /> onto a status-strip <see cref="StatusChipViewModel" />
///     and its flyout: the THIRD consumer of the shared <c>StatusChip</c>
///     idiom (alongside Live Sync and the Reel job): a persistent, stateful, background-activity indicator that
///     opens a <c>card-flyout</c> for detail + actions. Its <c>FlyoutContent</c> is this VM, so the app
///     <c>ViewLocator</c> resolves <c>Views/DemoProcessing/ProcessingQueueStatusView</c> for the body.
///     <para>
///         The VM owns no queue logic: it binds the queue's live <see cref="IDemoProcessingQueue.Items" />
///         (projected into presentation-only <see cref="DemoQueueRowViewModel" />s), reads its counts /
///         pause / background-enabled state, and forwards Pause/Resume, per-item remove and promote. It
///         refreshes on the queue's posted <see cref="IDemoProcessingQueue.Changed" /> event: no timer, no polling.
///     </para>
///     <para>
///         <see cref="Rows" /> holds running items, then queued ones in the order the queue reports it will start
///         them (<see cref="DemoQueueItem.StartRank" />), heavy lane before light. Finished items move to
///         <see cref="RecentRows" />, newest first, which outlives the queue's own history for the session.
///     </para>
///     <para>
///         <b>Theme mandate.</b> Holds no brushes: the chip dot re-themes via the shared state→token classes,
///         the label is the neutral <c>TextMid</c> token the shared <c>StatusChip</c> already renders.
///     </para>
/// </summary>
public sealed partial class ProcessingQueueStatusViewModel : ViewModelBase, IDisposable
{
    /// <summary>How many finished items Recent keeps.</summary>
    public const int RecentCap = 25;

    private readonly Dictionary<Guid, DemoQueueRowViewModel> _rowsById = [];
    private readonly Dictionary<Guid, long> _finishedAt = [];

    // Finished items pushed out of Recent by the cap while the queue still lists them.
    private readonly HashSet<Guid> _evicted = [];
    private long _finishCount;
    private bool _showRecent;
    private readonly JobKindRegistry _jobKinds;
    private readonly Action? _openSettings;
    private readonly IDemoProcessingQueue _queue;
    private bool _disposed;

    /// <summary>True when the persisted master switch is off (background fully disabled in Settings).</summary>
    [ObservableProperty]
    private bool _isBackgroundDisabled;

    /// <summary>True when nothing runs or waits (drives the queue view's empty-state text).</summary>
    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>True when nothing has finished this session.</summary>
    [ObservableProperty]
    private bool _isRecentEmpty = true;

    /// <summary>The Queue toggle's caption, with the live count.</summary>
    [ObservableProperty]
    private string _queueViewLabel = "Queue";

    /// <summary>The Recent toggle's caption, with the count kept.</summary>
    [ObservableProperty]
    private string _recentViewLabel = "Recent";

    /// <summary>True while background processing is transiently paused (the Pause/Resume toggle state).</summary>
    [ObservableProperty]
    private bool _isPaused;

    /// <summary>Demos waiting for a worker slot.</summary>
    [ObservableProperty]
    private int _queuedCount;

    // ── Flyout header / status line ───────────────────────────────────────────

    /// <summary>Demos being parsed right now.</summary>
    [ObservableProperty]
    private int _runningCount;

    /// <summary>The one-line status ("N running · M queued", plus a paused / disabled note).</summary>
    [ObservableProperty]
    private string _statusLine = "";

    /// <summary>
    ///     Constructs the mapper over the live queue. Seeds the chip + rows from the current state (never
    ///     blank) and tracks the queue's posted <see cref="IDemoProcessingQueue.Changed" /> event.
    /// </summary>
    /// <param name="queue">The global demo-processing queue singleton.</param>
    /// <param name="openSettings">
    ///     Opens the Settings screen (to the Background-processing section); null hides
    ///     the flyout's settings link (e.g. the designer / capture path).
    /// </param>
    /// <param name="jobKinds">Resolves each row's kind chip. Defaults to <see cref="JobKindRegistry.Default" />.</param>
    public ProcessingQueueStatusViewModel(IDemoProcessingQueue queue, Action? openSettings = null,
        JobKindRegistry? jobKinds = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
        _openSettings = openSettings;
        _jobKinds = jobKinds ?? JobKindRegistry.Default;

        Chip = new StatusChipViewModel
        {
            FlyoutContent = this
        };

        // Project the queue's live Items into row VMs and keep them in sync with the collection's own
        // add/remove/reset notifications (the queue reconciles Items by id on the post thread).
        _queue.Changed += OnQueueChanged;
        Refresh();
    }

    /// <summary>The status-strip chip this VM drives (added to <c>MainViewModel.Chips</c> while relevant).</summary>
    public StatusChipViewModel Chip { get; }

    /// <summary>Running items, then queued ones in start order (presentation-only wrappers over the queue items).</summary>
    public ObservableCollection<DemoQueueRowViewModel> Rows { get; } = [];

    /// <summary>Items that finished this session, newest first, at most <see cref="RecentCap" />.</summary>
    public ObservableCollection<DemoQueueRowViewModel> RecentRows { get; } = [];

    /// <summary>The live view shows (the default). Settable from the toggle.</summary>
    public bool IsQueueView
    {
        get => !_showRecent;
        set => SetView(!value);
    }

    /// <summary>Recent shows in place of the live view. Settable from the toggle.</summary>
    public bool IsRecentView
    {
        get => _showRecent;
        set => SetView(value);
    }

    /// <summary>Pause / Resume button caption, reflecting <see cref="IsPaused" />.</summary>
    public string PauseResumeLabel => IsPaused ? "Resume background work" : "Pause background work";

    /// <summary>Whether the flyout's "Background processing settings" link is shown (an opener was supplied).</summary>
    public bool CanOpenSettings => _openSettings is not null;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Changed -= OnQueueChanged;
        foreach (DemoQueueRowViewModel row in _rowsById.Values)
        {
            row.Dispose();
        }

        _rowsById.Clear();
        Rows.Clear();
        RecentRows.Clear();
    }

    // A checked toggle clicked again unchecks itself; re-raise so it shows the view that is still on.
    private void SetView(bool recent)
    {
        _showRecent = recent;
        OnPropertyChanged(nameof(IsQueueView));
        OnPropertyChanged(nameof(IsRecentView));
    }

    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(PauseResumeLabel));

    /// <summary>
    ///     Transiently pauses or resumes background processing (NOT persisted; the app starts
    ///     un-paused). Foreground opens are unaffected. The queue's <c>Changed</c> event re-syncs this VM.
    /// </summary>
    [RelayCommand]
    private void TogglePause()
    {
        if (_queue.IsPaused)
        {
            _queue.Resume();
        }
        else
        {
            _queue.Pause();
        }

        // Refresh eagerly too: Pause/Resume post Changed, but reflecting immediately keeps the button label
        // in step with the click even before the posted event drains.
        Refresh();
    }

    /// <summary>Opens the Settings screen so the user can change the persisted queue defaults.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenSettings))]
    private void OpenSettings() => _openSettings?.Invoke();

    // ── Queue → UI mapping ────────────────────────────────────────────────────

    private void OnQueueChanged() => Refresh();

    /// <summary>
    ///     Re-reads the queue's counts / pause / background state and maps them onto the chip + header.
    ///     Called at construction, on every posted <c>Changed</c>, and eagerly after a Pause/Resume click.
    /// </summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        SyncRows();
        RunningCount = _queue.RunningCount;
        QueuedCount = _queue.QueuedCount;
        IsPaused = _queue.IsPaused;
        IsBackgroundDisabled = !_queue.BackgroundEnabled;
        StatusLine = BuildStatusLine();
        MapChip();
    }

    private string BuildStatusLine()
    {
        string counts = string.Format(
            CultureInfo.InvariantCulture, "{0} running · {1} queued", RunningCount, QueuedCount);
        if (IsPaused)
        {
            return counts + " · paused: background work held";
        }

        if (IsBackgroundDisabled)
        {
            return counts + " · background disabled";
        }

        return counts;
    }

    private void MapChip()
    {
        if (IsPaused)
        {
            SetChip(StatusChipDotState.Off, false, "Background paused");
        }
        else if (RunningCount > 0)
        {
            SetChip(StatusChipDotState.Working, true,
                string.Format(CultureInfo.InvariantCulture, "Processing {0}", RunningCount));
        }
        else if (QueuedCount > 0)
        {
            SetChip(StatusChipDotState.Working, false,
                string.Format(CultureInfo.InvariantCulture, "{0} queued", QueuedCount));
        }
        else
        {
            // Idle: the shell hides the chip in this state, but keep the mapping coherent.
            SetChip(StatusChipDotState.Off, false, "Queue idle");
        }

        Chip.Tooltip = StatusLine;
    }

    private void SetChip(StatusChipDotState dot, bool pulsing, string label)
    {
        Chip.DotState = dot;
        Chip.IsPulsing = pulsing;
        Chip.Label = label;
    }

    // Rows and RecentRows follow Items by id, once per Changed: the queue raises it after each mirror update,
    // and a sync per item would re-sort on half-updated ranks. Rows move rather than rebuild, so a row keeps
    // its open context menu through the progress reports that change the queue many times a second.
    private void SyncRows()
    {
        if (_disposed)
        {
            return;
        }

        HashSet<Guid> present = [];
        List<(DemoQueueRowViewModel Row, int Index)> live = [];
        int index = 0;
        foreach (DemoQueueItem item in _queue.Items)
        {
            present.Add(item.Id);
            if (_evicted.Contains(item.Id))
            {
                index++;
                continue;
            }

            DemoQueueRowViewModel row = RowFor(item);
            if (row.IsFinished)
            {
                if (!_finishedAt.ContainsKey(item.Id))
                {
                    _finishedAt[item.Id] = ++_finishCount;
                }
            }
            else
            {
                live.Add((row, index));
            }

            index++;
        }

        // A live item that left Items unfinished (a completed save, which the queue drops) is not history.
        foreach (Guid id in _rowsById.Keys.Where(id => !present.Contains(id) && !_finishedAt.ContainsKey(id)).ToList())
        {
            DropRow(id);
        }

        Arrange(Rows, live
            .OrderBy(x => x.Row.IsRunning ? 0 : 1)
            .ThenBy(x => x.Row.Item.Light)
            .ThenBy(x => x.Row.Item.StartRank ?? int.MaxValue)
            .ThenBy(x => x.Index)
            .Select(x => x.Row)
            .ToList());

        List<DemoQueueRowViewModel> recent = _finishedAt.Keys
            .Select(id => _rowsById[id])
            .OrderByDescending(r => r.Item.EndedSeq)
            .ThenByDescending(r => _finishedAt[r.Id])
            .ToList();
        _evicted.RemoveWhere(id => !present.Contains(id));
        foreach (DemoQueueRowViewModel old in recent.Skip(RecentCap))
        {
            DropRow(old.Id);
            if (present.Contains(old.Id))
            {
                _evicted.Add(old.Id);
            }
        }

        Arrange(RecentRows, recent.Take(RecentCap).ToList());

        IsEmpty = Rows.Count == 0;
        IsRecentEmpty = RecentRows.Count == 0;
        QueueViewLabel = Rows.Count == 0 ? "Queue" : string.Format(CultureInfo.InvariantCulture, "Queue ({0})", Rows.Count);
        RecentViewLabel = RecentRows.Count == 0
            ? "Recent"
            : string.Format(CultureInfo.InvariantCulture, "Recent ({0})", RecentRows.Count);
    }

    private DemoQueueRowViewModel RowFor(DemoQueueItem item)
    {
        if (!_rowsById.TryGetValue(item.Id, out DemoQueueRowViewModel? row))
        {
            row = new DemoQueueRowViewModel(item, _queue, _jobKinds);
            _rowsById[item.Id] = row;
        }

        return row;
    }

    private void DropRow(Guid id)
    {
        if (_rowsById.Remove(id, out DemoQueueRowViewModel? row))
        {
            Rows.Remove(row);
            RecentRows.Remove(row);
            row.Dispose();
        }

        _finishedAt.Remove(id);
    }

    // Makes target equal desired with moves, inserts and removes, never a reset.
    private static void Arrange(ObservableCollection<DemoQueueRowViewModel> target, List<DemoQueueRowViewModel> desired)
    {
        HashSet<DemoQueueRowViewModel> wanted = [.. desired];
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (int i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i]))
            {
                continue;
            }

            int at = target.IndexOf(desired[i]);
            if (at >= 0)
            {
                target.Move(at, i);
            }
            else
            {
                target.Insert(i, desired[i]);
            }
        }
    }
}
