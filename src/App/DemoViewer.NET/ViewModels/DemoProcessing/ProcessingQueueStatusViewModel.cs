#region

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.ViewModels.DemoProcessing;

/// <summary>
///     Maps the global <see cref="IDemoProcessingQueue" /> onto a status-strip <see cref="StatusChipViewModel" />
///     and its flyout — the THIRD consumer of the shared <c>StatusChip</c>
///     idiom (alongside Live Sync and the Reel job): a persistent, stateful, background-activity indicator that
///     opens a <c>card-flyout</c> for detail + actions. Its <c>FlyoutContent</c> is this VM, so the app
///     <c>ViewLocator</c> resolves <c>Views/DemoProcessing/ProcessingQueueStatusView</c> for the body.
///     <para>
///         The VM owns no queue logic — it binds the queue's live <see cref="IDemoProcessingQueue.Items" />
///         (projected into presentation-only <see cref="DemoQueueRowViewModel" />s), reads its counts /
///         pause / background-enabled state, and forwards Pause/Resume + per-item remove. It refreshes on the
///         queue's posted <see cref="IDemoProcessingQueue.Changed" /> event — no timer, no polling.
///     </para>
///     <para>
///         <b>Theme mandate.</b> Holds no brushes: the chip dot re-themes via the shared state→token classes,
///         the label is the neutral <c>TextMid</c> token the shared <c>StatusChip</c> already renders.
///     </para>
/// </summary>
public sealed partial class ProcessingQueueStatusViewModel : ViewModelBase, IDisposable
{
    private readonly INotifyCollectionChanged _itemsIncc;
    private readonly Action? _openSettings;
    private readonly IDemoProcessingQueue _queue;
    private bool _disposed;

    /// <summary>True when the persisted master switch is off (background fully disabled in Settings).</summary>
    [ObservableProperty]
    private bool _isBackgroundDisabled;

    /// <summary>True when the queue holds no items (drives the flyout's empty-state text).</summary>
    [ObservableProperty]
    private bool _isEmpty = true;

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
    public ProcessingQueueStatusViewModel(IDemoProcessingQueue queue, Action? openSettings = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
        _openSettings = openSettings;

        Chip = new StatusChipViewModel
        {
            FlyoutContent = this
        };

        // Project the queue's live Items into row VMs and keep them in sync with the collection's own
        // add/remove/reset notifications (the queue reconciles Items by id on the post thread).
        _itemsIncc = _queue.Items;
        _itemsIncc.CollectionChanged += OnItemsChanged;
        foreach (DemoQueueItem item in _queue.Items)
        {
            Rows.Add(new DemoQueueRowViewModel(item, _queue));
        }

        _queue.Changed += OnQueueChanged;
        Refresh();
    }

    /// <summary>The status-strip chip this VM drives (added to <c>MainViewModel.Chips</c> while relevant).</summary>
    public StatusChipViewModel Chip { get; }

    /// <summary>The live queue rows the flyout list binds (presentation-only wrappers over the queue items).</summary>
    public ObservableCollection<DemoQueueRowViewModel> Rows { get; } = [];

    /// <summary>Pause / Resume button caption, reflecting <see cref="IsPaused" />.</summary>
    public string PauseResumeLabel => IsPaused ? "Resume background" : "Pause background";

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
        _itemsIncc.CollectionChanged -= OnItemsChanged;
        foreach (DemoQueueRowViewModel row in Rows)
        {
            row.Dispose();
        }

        Rows.Clear();
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

        RunningCount = _queue.RunningCount;
        QueuedCount = _queue.QueuedCount;
        IsPaused = _queue.IsPaused;
        IsBackgroundDisabled = !_queue.BackgroundEnabled;
        IsEmpty = Rows.Count == 0;
        StatusLine = BuildStatusLine();
        MapChip();
    }

    private string BuildStatusLine()
    {
        string counts = string.Format(
            CultureInfo.InvariantCulture, "{0} running · {1} queued", RunningCount, QueuedCount);
        if (IsPaused)
        {
            return counts + " · paused";
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
            SetChip(StatusChipDotState.Off, false, "Queue paused");
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
            // Idle — the shell hides the chip in this state, but keep the mapping coherent.
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

    // Keep Rows in lockstep with the queue's Items collection. The queue adds/removes items (and updates
    // existing ones in place, which each row observes itself), so Add/Remove/Reset is the full surface.
    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                int insertAt = e.NewStartingIndex >= 0 && e.NewStartingIndex <= Rows.Count
                    ? e.NewStartingIndex
                    : Rows.Count;
                foreach (DemoQueueItem item in e.NewItems)
                {
                    Rows.Insert(Math.Min(insertAt, Rows.Count), new DemoQueueRowViewModel(item, _queue));
                    insertAt++;
                }

                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (DemoQueueItem item in e.OldItems)
                {
                    RemoveRowFor(item.Id);
                }

                break;

            case NotifyCollectionChangedAction.Replace:
                RebuildRows();
                break;

            case NotifyCollectionChangedAction.Reset:
                RebuildRows();
                break;

            default:
                RebuildRows();
                break;
        }

        IsEmpty = Rows.Count == 0;
        Refresh();
    }

    private void RemoveRowFor(Guid id)
    {
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (Rows[i].Id == id)
            {
                Rows[i].Dispose();
                Rows.RemoveAt(i);
            }
        }
    }

    private void RebuildRows()
    {
        foreach (DemoQueueRowViewModel row in Rows)
        {
            row.Dispose();
        }

        Rows.Clear();
        foreach (DemoQueueItem item in _queue.Items)
        {
            Rows.Add(new DemoQueueRowViewModel(item, _queue));
        }
    }
}
