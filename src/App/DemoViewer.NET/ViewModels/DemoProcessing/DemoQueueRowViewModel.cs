#region

using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.ViewModels.DemoProcessing;

/// <summary>
///     One row of the demo-processing-queue flyout. A thin, presentation-only
///     wrapper over the service-owned <see cref="DemoQueueItem" /> — it holds no queue logic and adds nothing
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
    private readonly IDemoProcessingQueue _queue;
    private bool _disposed;

    /// <summary>Wraps <paramref name="item" /> for display and subscribes to its in-place state updates.</summary>
    public DemoQueueRowViewModel(DemoQueueItem item, IDemoProcessingQueue queue)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(queue);
        _item = item;
        _queue = queue;
        _item.PropertyChanged += OnItemChanged;
    }

    /// <summary>The wrapped item's stable id — identity for the queue's reconcile + the remove key.</summary>
    public Guid Id => _item.Id;

    /// <summary>File-name display (falls back to the path when the queue supplied no display name).</summary>
    public string DisplayText =>
        !string.IsNullOrEmpty(_item.DisplayName) ? _item.DisplayName! : SafeFileName(_item.Path);

    /// <summary>The full path — the row tooltip, so a trimmed name is still identifiable.</summary>
    public string Path => _item.Path;

    /// <summary>Comma-joined owning module tags (e.g. "library, highlights"); empty ⇒ the chip hides.</summary>
    public string Owners => _item.Owners;

    /// <summary>True when any owner tag is present (drives the owner chip's visibility).</summary>
    public bool HasOwners => !string.IsNullOrWhiteSpace(_item.Owners);

    /// <summary>Short priority label, shown only when the item is elevated above routine background work.</summary>
    public string PriorityLabel => _item.Priority switch
    {
        DemoJobPriority.Foreground => "opening",
        DemoJobPriority.UserRequested => "manual",
        _ => ""
    };

    /// <summary>True for UserRequested/Foreground — routine Background work shows no priority chip (noise).</summary>
    public bool HasElevatedPriority => _item.Priority != DemoJobPriority.Background;

    /// <summary>The lifecycle word — the accessible carrier of state (the dot is the redundant colour cue).</summary>
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

    // ── Shared Ellipse.dot.* state flags (Styles/Primitives.axaml) — bound to Classes.x, never a brush ──

    /// <summary>Cancelled — the dim/idle <c>TextDim</c> dot.</summary>
    public bool IsStateOff => _item.State is DemoQueueItemState.Cancelled;

    /// <summary>Queued or Running — the <c>AccentInteractive</c> dot (steady = queued, pulsing = running).</summary>
    public bool IsStateWorking => _item.State is DemoQueueItemState.Queued or DemoQueueItemState.Running;

    /// <summary>Completed — the <c>StatPositive</c> dot.</summary>
    public bool IsStateGood => _item.State is DemoQueueItemState.Completed;

    /// <summary>Rejected (queue full) — the <c>AccentCaution</c> dot; the durable backlog re-feeds it later.</summary>
    public bool IsStateDegraded => _item.State is DemoQueueItemState.Rejected;

    /// <summary>Failed — the <c>AccentError</c> dot.</summary>
    public bool IsStateError => _item.State is DemoQueueItemState.Failed;

    /// <summary>Running only — the dot runs the subtle opacity pulse (in-flight parse).</summary>
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

    /// <summary>The user (UI) removes THIS item from the queue (any item, any owner).</summary>
    [RelayCommand]
    private void Remove() => _queue.RemoveByUser(_item.Id);

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Any field the queue mutates in place (State, Priority, Owners, DisplayName, Error) → re-raise the
        // whole projected surface. The set is tiny, so a blanket re-raise is simpler and cheaper than mapping
        // each source property to its derived ones.
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(Owners));
        OnPropertyChanged(nameof(HasOwners));
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
            // A path with invalid chars — show it verbatim rather than throw at the render boundary.
            return path;
        }
    }
}
