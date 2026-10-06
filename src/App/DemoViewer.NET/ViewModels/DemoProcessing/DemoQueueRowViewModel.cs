#region

using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.ViewModels.DemoProcessing;

/// <summary>
///     One row of the demo-processing-queue flyout. A thin, presentation-only
///     wrapper over the service-owned <see cref="DemoQueueItem" />: it holds no queue logic and adds nothing
///     to the DemoProcessing layer; it just projects the item's fields into display strings + the
///     class-driving flags the flyout binds, and forwards the per-row remove to the queue.
///     <para>
///         <b>Theme mandate.</b> The row carries <b>no brushes</b>. Its state maps onto the five shared
///         <c>Ellipse.dot.*</c> semantic states (Off/Working/Good/Degraded/Error) in
///         <c>Styles/Primitives.axaml</c> via bound <c>Classes.x</c> flags, so the state dot re-themes live
///         (the <c>StatusChip</c> pattern). The state <em>word</em> (<see cref="StateLabel" />) is the
///         accessible carrier; the dot is the redundant colour cue (WCAG 1.4.1).
///     </para>
/// </summary>
public sealed partial class DemoQueueRowViewModel : ViewModelBase, IDisposable
{
    private readonly DemoQueueItem _item;
    private readonly JobKindRegistry _jobKinds;
    private readonly IDemoProcessingQueue _queue;
    private bool _disposed;

    /// <summary>
    ///     Wraps <paramref name="item" /> for display and subscribes to its in-place state updates.
    ///     <paramref name="jobKinds" /> resolves the kind chip's label; defaults to <see cref="JobKindRegistry.Default" />.
    /// </summary>
    public DemoQueueRowViewModel(DemoQueueItem item, IDemoProcessingQueue queue, JobKindRegistry? jobKinds = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(queue);
        _item = item;
        _queue = queue;
        _jobKinds = jobKinds ?? JobKindRegistry.Default;
        _item.PropertyChanged += OnItemChanged;
    }

    /// <summary>The wrapped item's stable id: identity for the queue's reconcile + the remove key.</summary>
    public Guid Id => _item.Id;

    /// <summary>The queue item this row shows.</summary>
    public DemoQueueItem Item => _item;

    /// <summary>Running now.</summary>
    public bool IsRunning => _item.State == DemoQueueItemState.Running;

    /// <summary>Waiting to start.</summary>
    public bool IsQueued => _item.State == DemoQueueItemState.Queued;

    /// <summary>Ended: completed, failed, cancelled or rejected. The row belongs in Recent.</summary>
    public bool IsFinished => !IsQueued && !IsRunning;

    /// <summary>The user moved it to the top; the queue clears this when it starts.</summary>
    public bool IsPromoted => IsQueued && _item.Promoted;

    /// <summary>
    ///     Whether "Move to top" applies. An open already goes first, a compaction is due now, and a visit
    ///     parked on an open runs on that open's parse.
    /// </summary>
    public bool CanPromote => IsQueued && _item.Kind is not (QueueJobKind.DemoOpen or QueueJobKind.HeapCompaction)
                              && _item.Hold != DemoQueueHold.OnOpen;

    /// <summary>The menu entry, saying when a promoted item will still wait.</summary>
    public string PromoteLabel => _item.Hold switch
    {
        DemoQueueHold.Paused => "Move to top (waits: background work paused)",
        DemoQueueHold.BackgroundOff => "Move to top (waits: background processing off)",
        _ => "Move to top"
    };

    /// <summary>Only an active item can be removed; a finished one is history.</summary>
    public bool CanRemove => IsQueued || IsRunning;

    /// <summary>The remove entry's wording: a running item stops at its next step.</summary>
    public string RemoveLabel => IsRunning ? "Stop and remove" : "Remove from queue";

    /// <summary>A failed item's message, shown under it in Recent.</summary>
    public bool HasError => _item.State == DemoQueueItemState.Failed && !string.IsNullOrEmpty(_item.Error);

    /// <summary>File-name display (falls back to the path when the queue supplied no display name).</summary>
    public string DisplayText =>
        !string.IsNullOrEmpty(_item.DisplayName) ? _item.DisplayName! : SafeFileName(_item.Path);

    /// <summary>The row tooltip: the full path, or the title when the job is about no file.</summary>
    public string Path => string.IsNullOrEmpty(_item.Path) ? DisplayText : _item.Path;

    /// <summary>Comma-joined owning module tags (e.g. "library, highlights"); empty ⇒ the chip hides.</summary>
    public string Owners => _item.Owners;

    /// <summary>Owner chip for demo parses only; a job's kind chip already says who it is.</summary>
    public bool HasOwners => _item.Kind == QueueJobKind.DemoProcessing && !string.IsNullOrWhiteSpace(_item.Owners);

    /// <summary>The job kind's short chip; empty for a demo parse.</summary>
    public string KindLabel => _jobKinds.Label(_item.Kind, _item.ExtensionKind);

    /// <summary>True for every kind but a demo parse.</summary>
    public bool HasKind => _item.Kind != QueueJobKind.DemoProcessing;

    /// <summary>Fraction done, 0 to 1.</summary>
    public double ProgressValue => _item.Progress ?? 0;

    /// <summary>A running item that reports progress shows the bar.</summary>
    public bool HasProgress => _item.State == DemoQueueItemState.Running && _item.Progress is not null;

    /// <summary>The running job's latest detail ("48 of 366 demos").</summary>
    public string Detail => _item.Detail ?? "";

    /// <summary>Shown while running, and while an open waits (it says what for).</summary>
    public bool HasDetail => (_item.State == DemoQueueItemState.Running
                              || (_item.State == DemoQueueItemState.Queued && _item.Kind == QueueJobKind.DemoOpen))
                             && !string.IsNullOrEmpty(_item.Detail);

    /// <summary>Short priority label, shown only when the item is elevated above routine background work.</summary>
    public string PriorityLabel => _item.Priority switch
    {
        DemoJobPriority.Foreground => "opening",
        DemoJobPriority.UserRequested => "manual",
        _ => ""
    };

    /// <summary>True for UserRequested/Foreground: routine Background work and an open, whose kind chip says it, show none.</summary>
    public bool HasElevatedPriority => _item.Priority != DemoJobPriority.Background && _item.Kind != QueueJobKind.DemoOpen;

    /// <summary>The lifecycle word: the accessible carrier of state (the dot is the redundant colour cue).</summary>
    public string StateLabel => _item.State switch
    {
        DemoQueueItemState.Queued => "Queued",
        DemoQueueItemState.Running => "Running",
        DemoQueueItemState.Completed => "Done",
        DemoQueueItemState.Failed => "Failed",
        DemoQueueItemState.Cancelled => "Cancelled",
        DemoQueueItemState.Rejected => "Rejected",
        _ => _item.State.ToString()
    };

    /// <summary>The failure message, surfaced as the state tooltip when <see cref="IsStateError" />.</summary>
    public string? Error => _item.Error;

    // ── Shared Ellipse.dot.* state flags (Styles/Primitives.axaml): bound to Classes.x, never a brush ──

    /// <summary>Cancelled: the dim/idle <c>TextDim</c> dot.</summary>
    public bool IsStateOff => _item.State is DemoQueueItemState.Cancelled;

    /// <summary>Queued or Running: the <c>AccentInteractive</c> dot (steady = queued, pulsing = running).</summary>
    public bool IsStateWorking => _item.State is DemoQueueItemState.Queued or DemoQueueItemState.Running;

    /// <summary>Completed: the <c>StatPositive</c> dot.</summary>
    public bool IsStateGood => _item.State is DemoQueueItemState.Completed;

    /// <summary>Rejected (queue full): the <c>AccentCaution</c> dot; the durable backlog re-feeds it later.</summary>
    public bool IsStateDegraded => _item.State is DemoQueueItemState.Rejected;

    /// <summary>Failed: the <c>AccentError</c> dot.</summary>
    public bool IsStateError => _item.State is DemoQueueItemState.Failed;

    /// <summary>Running only: the dot runs the subtle opacity pulse (in-flight parse).</summary>
    public bool IsPulsing => _item.State is DemoQueueItemState.Running;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _item.PropertyChanged -= OnItemChanged;
    }

    /// <summary>The user (UI) removes THIS item from the queue (any item, any owner); a running job stops at its next step.</summary>
    [RelayCommand]
    private void Remove() => _queue.RemoveByUser(_item.Id);

    /// <summary>Starts this item next in its lane once the running one there finishes. Stops nothing.</summary>
    [RelayCommand]
    private void Promote() => _queue.Promote(_item.Id);

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Any field the queue mutates in place (State, Priority, Owners, DisplayName, Error, ...) → re-raise the
        // whole projected surface. The set is tiny, so a blanket re-raise is simpler and cheaper than mapping
        // each source property to its derived ones.
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(Owners));
        OnPropertyChanged(nameof(HasOwners));
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(HasKind));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(PriorityLabel));
        OnPropertyChanged(nameof(HasElevatedPriority));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(IsStateOff));
        OnPropertyChanged(nameof(IsStateWorking));
        OnPropertyChanged(nameof(IsStateGood));
        OnPropertyChanged(nameof(IsStateDegraded));
        OnPropertyChanged(nameof(IsStateError));
        OnPropertyChanged(nameof(IsPulsing));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(IsPromoted));
        OnPropertyChanged(nameof(CanPromote));
        OnPropertyChanged(nameof(PromoteLabel));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(RemoveLabel));
        OnPropertyChanged(nameof(HasError));
    }

    private static string SafeFileName(string path)
    {
        try
        {
            string name = System.IO.Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch (ArgumentException)
        {
            // A path with invalid chars: show it verbatim rather than throw at the render boundary.
            return path;
        }
    }
}
