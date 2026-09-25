#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Review;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Tags;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>
///     The Strat Record Panel (plan.md §3, strat-model.md §3.6): run / won / lost / aborted, split by Demo
///     Provenance Labels, a small-sample caution under eight, the failure breakdown, and a click on every
///     number that sends the runs behind it to the Review Queue and shows the Review tab. The data comes
///     from <see cref="StratEvidenceService" />; this view model orders it, words it and wires the clicks,
///     the way <see cref="StratRecordPane" /> is the wordless, UI-free shape and the Matrix's cell view
///     models are the wired shape of a <see cref="Services.Tags.TagQuery" /> pivot.
///     <para>
///         <b>Live from the Tag Store.</b> A tagged round is evidence the moment its document is saved, so
///         the panel rebuilds on every <see cref="TagStore.Changed" />, debounced like the Matrix, and never
///         needs a restart to reflect a tag just written. Without the strat's own <c>stratIds</c> index
///         column (strat-model.md §3.6, not yet built) every change rescans every tagged document, which is
///         the design's own stated cost until that column exists.
///     </para>
///     <para>
///         <b>Rebuild triggers.</b> <see cref="Configure" /> is called by the tab on every session change; it
///         rebuilds only when the open strat's id, side or revision actually differs from what the panel last
///         computed with, since a field edit that touches neither (a note, a step's text) cannot change who
///         won. A Tag Store change always rebuilds, because there is no cheap way yet to know it did not touch
///         this strat's evidence.
///     </para>
/// </summary>
public sealed partial class StratRecordPanelViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan _defaultDebounce = TimeSpan.FromMilliseconds(150);

    private readonly Action<Action> _post;
    private readonly TimeSpan _debounce;
    private readonly StratEvidenceService _evidence;
    private readonly Func<string, DemoCacheIndexEntry?> _indexBySha;
    private readonly ReviewQueue? _review;
    private readonly Func<string, bool>? _selectTab;
    private readonly TagStore _tags;

    private CancellationTokenSource? _cts;
    private StratDocument? _document;
    private bool _disposed;
    private Guid? _lastId;
    private int _lastRevision = -1;
    private string _lastSide = "";
    private int _sequence;

    [ObservableProperty]
    private string? _caution;

    [ObservableProperty]
    private bool _isComputing;

    [ObservableProperty]
    private string _statusLine = "";

    [ObservableProperty]
    private string? _unknownNote;

    [ObservableProperty]
    private string _winRateText = StratRecordPane.WinRateText(RecordSplit.Empty);

    /// <param name="evidence">Computes the record from the Tag Store.</param>
    /// <param name="tags">The store the panel listens to, for the live rebuild.</param>
    /// <param name="review">Where a number's clips go; null says there is none on this host.</param>
    /// <param name="indexBySha">Hash to library row (<see cref="DemoCacheStore.TryGetIndexBySha256" />), for a clip's path.</param>
    /// <param name="selectTab">Shows a tab by id, for the Review tab after a send; null stays on the Strat Book.</param>
    /// <param name="post">Marshals a finished rebuild onto the UI thread; defaults to synchronous.</param>
    /// <param name="debounce">How long a burst of Tag Store changes is folded; 150 ms by default.</param>
    public StratRecordPanelViewModel(
        StratEvidenceService evidence,
        TagStore tags,
        ReviewQueue? review = null,
        Func<string, DemoCacheIndexEntry?>? indexBySha = null,
        Func<string, bool>? selectTab = null,
        Action<Action>? post = null,
        TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(tags);
        _evidence = evidence;
        _tags = tags;
        _review = review;
        _indexBySha = indexBySha ?? (_ => null);
        _selectTab = selectTab;
        _post = post ?? (action => action());
        _debounce = debounce ?? _defaultDebounce;
        _tags.Changed += OnTagsChanged;
    }

    /// <summary>Whether a strat is open; the panel shows nothing without one.</summary>
    public bool HasStrat => _document is not null;

    /// <summary>The in-flight rebuild, so a caller (a test) can wait for a debounced Tag Store change to land.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>Whether <see cref="Caution" /> has something to say.</summary>
    public bool HasCaution => !string.IsNullOrEmpty(Caution);

    /// <summary>Whether <see cref="UnknownNote" /> has something to say.</summary>
    public bool HasUnknownNote => !string.IsNullOrEmpty(UnknownNote);

    /// <summary>Every run, won, lost, aborted and unknown, over the whole record.</summary>
    public StratRecordSplitRow Total { get; private set; } = EmptyRow("total", "All runs");

    /// <summary>The vocabulary's provenance labels in order, then any other value, then unlabeled.</summary>
    public ObservableCollection<StratRecordSplitRow> ByProvenance { get; } = [];

    /// <summary>Whether <see cref="ByProvenance" /> has a row to show.</summary>
    public bool HasProvenanceRows => ByProvenance.Count > 0;

    /// <summary>The shipped failure values in vocabulary order, then any other value; runs that did not win only.</summary>
    public ObservableCollection<StratRecordFailureRow> Failures { get; } = [];

    public bool HasFailures => Failures.Count > 0;

    /// <summary>Points the panel at the open strat; rebuilds when its identity, side or revision changed.</summary>
    /// <param name="document">The session's open document, or null when none is open.</param>
    public void Configure(StratDocument? document)
    {
        _document = document;
        OnPropertyChanged(nameof(HasStrat));

        if (document is null)
        {
            _lastId = null;
            _cts?.Cancel();
            Clear();
            return;
        }

        if (document.Id != _lastId || !string.Equals(document.Side, _lastSide, StringComparison.Ordinal)
                                    || document.Revision != _lastRevision)
        {
            Schedule(document);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tags.Changed -= OnTagsChanged;
        _cts?.Cancel();
        _cts?.Dispose();
    }

    // Any document in the store may carry this strat's evidence (strat-model.md §3.6's stated cost until
    // the stratIds index column exists), so every change rebuilds, debounced, for whichever strat is open.
    private void OnTagsChanged(string? sha256)
    {
        if (_document is { } document)
        {
            Schedule(document);
        }
    }

    private void Schedule(StratDocument document)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        int sequence = ++_sequence;
        IsComputing = true;
        Pending = RunAsync(document, sequence, _cts.Token);
    }

    private async Task RunAsync(StratDocument document, int sequence, CancellationToken token)
    {
        try
        {
            if (_debounce > TimeSpan.Zero)
            {
                await Task.Delay(_debounce, token).ConfigureAwait(false);
            }

            StratRecord record = await _evidence.ComputeAsync(document, token).ConfigureAwait(false);
            _post(() =>
            {
                if (sequence == _sequence && !_disposed)
                {
                    Apply(document, record);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // A newer request replaced this one; it clears IsComputing when it lands.
        }
        catch (Exception ex)
        {
            _post(() =>
            {
                if (sequence == _sequence)
                {
                    IsComputing = false;
                    StatusLine = $"could not compute the record: {ex.Message}";
                }
            });
        }
    }

    private void Apply(StratDocument document, StratRecord record)
    {
        _lastId = document.Id;
        _lastSide = document.Side;
        _lastRevision = document.Revision;
        IsComputing = false;

        StratRecordPane pane = StratRecordPane.From(record);
        Total = BuildRow("total", pane.Total);
        OnPropertyChanged(nameof(Total));

        ByProvenance.Clear();
        foreach (StratRecordRow row in pane.ByProvenance)
        {
            ByProvenance.Add(BuildRow("provenance", row));
        }

        Failures.Clear();
        foreach (StratFailureRow failure in pane.Failures)
        {
            Failures.Add(new StratRecordFailureRow(failure.Failure, failure.Count,
                Cell($"failure · {failure.Failure}", "clips", failure.Runs)));
        }

        Caution = pane.Caution;
        UnknownNote = pane.UnknownNote;
        WinRateText = StratRecordPane.WinRateText(pane.Total.Split);
        OnPropertyChanged(nameof(HasFailures));
        OnPropertyChanged(nameof(HasProvenanceRows));
    }

    private StratRecordSplitRow BuildRow(string section, StratRecordRow row) => new(
        row.Display,
        Cell($"{section} · {row.Display}", "run", row.Runs),
        Cell($"{section} · {row.Display}", "won", row.RunsWith(RunOutcome.Won)),
        Cell($"{section} · {row.Display}", "lost", row.RunsWith(RunOutcome.Lost)),
        Cell($"{section} · {row.Display}", "aborted", row.RunsWith(RunOutcome.Aborted)),
        Cell($"{section} · {row.Display}", "unknown", row.RunsWith(RunOutcome.Unknown)),
        StratRecordPane.WinRateText(row.Split));

    private StratRecordCountCell Cell(string scope, string label, IReadOnlyList<StratRun> runs) =>
        new(this, scope, label, runs);

    private static StratRecordSplitRow EmptyRow(string section, string display)
    {
        StratRecordCountCell Empty(string label) => new(null, $"{section} · {display}", label, []);
        return new StratRecordSplitRow(display, Empty("run"), Empty("won"), Empty("lost"), Empty("aborted"),
            Empty("unknown"), StratRecordPane.WinRateText(RecordSplit.Empty));
    }

    private void Clear()
    {
        Total = EmptyRow("total", "All runs");
        OnPropertyChanged(nameof(Total));
        ByProvenance.Clear();
        Failures.Clear();
        Caution = null;
        UnknownNote = null;
        WinRateText = StratRecordPane.WinRateText(RecordSplit.Empty);
        IsComputing = false;
        OnPropertyChanged(nameof(HasFailures));
        OnPropertyChanged(nameof(HasProvenanceRows));
    }

    /// <summary>
    ///     Sends the runs behind one number to the Review Queue under one title card, then shows the Review
    ///     tab. A run whose demo is not in the library is not queued as a dead link; the status line counts
    ///     it instead. Tick rate is left at 0 (the surface does not know it, the same "did not know it" case
    ///     <see cref="ReviewEntry" /> already carries for other callers), so a queued clip's length is unknown
    ///     until the reviewer opens it.
    /// </summary>
    /// <param name="scope">What the number is, for the section title ("total · All runs", "provenance · scrim").</param>
    /// <param name="label">Which count within the scope ("won", "aborted", the failure name).</param>
    /// <param name="runs">The runs behind it.</param>
    /// <returns>How many clips were added.</returns>
    internal int Open(string scope, string label, IReadOnlyList<StratRun> runs)
    {
        if (runs.Count == 0)
        {
            return 0;
        }

        if (_review is null)
        {
            StatusLine = "no Review Queue on this host";
            return 0;
        }

        List<ReviewEntry> clips = [];
        int missing = 0;
        foreach (StratRun run in runs)
        {
            if (ReviewQueue.FromTag(run.Ref, sha => _indexBySha(sha)?.Path) is { } clip)
            {
                clips.Add(clip);
            }
            else
            {
                missing++;
            }
        }

        string notInLibrary = missing == 0 ? "" : $" · {Plural(missing, "clip")} not queued: demo not in library";
        if (clips.Count == 0)
        {
            StatusLine = $"nothing queued: {(missing == 1 ? "the demo is" : "the demos are")} not in the library";
            return 0;
        }

        int added = _review.Add(clips, $"Strat record · {scope}", $"{Plural(runs.Count, "run")} · {label}");
        StatusLine = (added == 0 ? "already in Review" : $"{Plural(added, "clip")} sent to Review") + notInLibrary;
        _selectTab?.Invoke(ReviewQueueModule.TabId);
        return added;
    }

    private static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")}");
}

/// <summary>One row of the panel: run, won, lost, aborted and unknown counts over a scope, plus its win rate.</summary>
public sealed class StratRecordSplitRow(
    string display,
    StratRecordCountCell run,
    StratRecordCountCell won,
    StratRecordCountCell lost,
    StratRecordCountCell aborted,
    StratRecordCountCell unknown,
    string winRateText)
{
    public string Display { get; } = display;

    public StratRecordCountCell Run { get; } = run;

    public StratRecordCountCell Won { get; } = won;

    public StratRecordCountCell Lost { get; } = lost;

    public StratRecordCountCell Aborted { get; } = aborted;

    public StratRecordCountCell Unknown { get; } = unknown;

    public string WinRateText { get; } = winRateText;
}

/// <summary>One failure value, how many non-winning runs named it, and the clip count that opens them.</summary>
public sealed class StratRecordFailureRow(string failure, int count, StratRecordCountCell clips)
{
    public string Failure { get; } = failure;

    public int Count { get; } = count;

    public StratRecordCountCell Clips { get; } = clips;
}

/// <summary>One clickable number: the runs behind it, and the command a button binds to send them to Review.</summary>
public sealed partial class StratRecordCountCell : ViewModelBase
{
    private readonly StratRecordPanelViewModel? _owner;

    internal StratRecordCountCell(StratRecordPanelViewModel? owner, string scope, string label, IReadOnlyList<StratRun> runs)
    {
        _owner = owner;
        Scope = scope;
        Label = label;
        Runs = runs;
    }

    public string Scope { get; }

    public string Label { get; }

    public IReadOnlyList<StratRun> Runs { get; }

    public int Count => Runs.Count;

    public bool IsEmpty => Runs.Count == 0;

    public string CountText => Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>"3 · provenance · scrim · lost: send to Review".</summary>
    public string Tip => $"{Count} · {Scope} · {Label}: send to Review";

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open() => _owner?.Open(Scope, Label, Runs);

    private bool CanOpen() => _owner is not null && Runs.Count > 0;
}
