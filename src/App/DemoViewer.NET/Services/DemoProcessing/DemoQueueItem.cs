#region

using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     The UI-bindable mirror of one queue entry. Created once per entry id
///     and updated in place by the queue's reconcile (on the post/UI thread) so the bound list keeps
///     item identity and selection across state changes. The queue's AUTHORITATIVE, thread-safe state
///     lives in its internal entry list under a lock. This is a projection for binding only.
/// </summary>
public partial class DemoQueueItem : ObservableObject
{
    /// <summary>Human label: file name when available, else the path.</summary>
    [ObservableProperty]
    private string? _displayName;

    /// <summary>Failure message when <see cref="State" /> is <see cref="DemoQueueItemState.Failed" />.</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>Comma-joined owning module tags (e.g. "library, highlights").</summary>
    [ObservableProperty]
    private string _owners = "";

    /// <summary>The highest priority any owner requested this at.</summary>
    [ObservableProperty]
    private DemoJobPriority _priority;

    /// <summary>Lifecycle state (drives the badge).</summary>
    [ObservableProperty]
    private DemoQueueItemState _state;

    /// <summary>What the item does.</summary>
    [ObservableProperty]
    private QueueJobKind _kind;

    /// <summary>For <see cref="QueueJobKind.Extension" />, the extension's kind id.</summary>
    [ObservableProperty]
    private string? _extensionKind;

    /// <summary>Fraction done, 0 to 1, or null when the job does not know.</summary>
    [ObservableProperty]
    private double? _progress;

    /// <summary>The job's latest progress detail, or null.</summary>
    [ObservableProperty]
    private string? _detail;

    /// <summary>Stable item identity (the <see cref="IDemoProcessingQueue.RemoveByUser" /> key).</summary>
    public required Guid Id { get; init; }

    /// <summary>The .dem path (identity for coalescing), or the file a job is about, or empty.</summary>
    public required string Path { get; init; }
}
