#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Tags;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>
///     Strat Version History (plan.md §3, strat-model.md §3.8): the append-only diff log as a pane, newest entry
///     first, each entry phrased against the document as it stood just before it, with the record
///     (<see cref="StratEvidenceService" />) split either side of the entry's revision. The data is
///     <see cref="StratHistoryPane" />; this view model reads the store's log, computes the record the same way
///     the Strat Record Panel does, and wires the live rebuild, so "every save is a diff and the record
///     splits" (the item's own done line) needs nothing further from the store or the session.
///     <para>
///         <b>Live from the store and the Tag Store.</b> A commit appends to the log the moment it lands, so
///         <see cref="Configure" /> is called by the tab on every session change and rebuilds when the open
///         strat's id or revision actually differ from what the pane last showed. A Tag Store change also
///         rebuilds, debounced like the Record Panel, because retagging an old demo can move it either side of
///         a past entry's split without changing the strat itself.
///     </para>
///     <para>
///         <b>No <c>stratIds</c> index column yet.</b> The record read is the Record Panel's own
///         <see cref="StratEvidenceService" />, so it carries the same rescan-every-change cost until that
///         column exists (strat-model.md §3.6); this panel adds no cost of its own beyond one more scan.
///     </para>
/// </summary>
public sealed partial class StratHistoryPanelViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan _defaultDebounce = TimeSpan.FromMilliseconds(150);

    private readonly CalloutResolverSource _callouts;
    private readonly TimeSpan _debounce;
    private readonly StratEvidenceService _evidence;
    private readonly Action<Action> _post;
    private readonly StratStore _store;
    private readonly TagStore _tags;

    private CancellationTokenSource? _cts;
    private StratDocument? _document;
    private bool _disposed;
    private Guid? _lastId;
    private int _lastRevision = -1;
    private int _sequence;

    [ObservableProperty]
    private bool _isComputing;

    [ObservableProperty]
    private string _statusLine = "";

    /// <param name="store">Reads the open strat's history log (<see cref="StratStore.History" />).</param>
    /// <param name="evidence">Computes the record the split reads; the same instance the Record Panel uses.</param>
    /// <param name="tags">The store the pane listens to, for the live rebuild.</param>
    /// <param name="callouts">Resolves places in an entry's words to the book's own callouts.</param>
    /// <param name="post">Marshals a finished rebuild onto the UI thread; defaults to synchronous.</param>
    /// <param name="debounce">How long a burst of Tag Store changes is folded; 150 ms by default.</param>
    public StratHistoryPanelViewModel(StratStore store, StratEvidenceService evidence, TagStore tags,
        CalloutResolverSource callouts, Action<Action>? post = null, TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(callouts);
        _store = store;
        _evidence = evidence;
        _tags = tags;
        _callouts = callouts;
        _post = post ?? (action => action());
        _debounce = debounce ?? _defaultDebounce;
        _tags.Changed += OnTagsChanged;
    }

    /// <summary>Whether a strat is open; the pane shows nothing without one.</summary>
    public bool HasStrat => _document is not null;

    /// <summary>The in-flight rebuild, so a test can wait for a debounced Tag Store change to land.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>One row per history entry, newest first.</summary>
    public ObservableCollection<StratHistoryEntryRow> Entries { get; } = [];

    public bool HasEntries => Entries.Count > 0;

    /// <summary>Points the pane at the open strat; rebuilds when its identity or revision changed.</summary>
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

        if (document.Id != _lastId || document.Revision != _lastRevision)
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

    // Retagging an old demo can move it either side of a past entry's split (§3.8), so any Tag Store
    // change rebuilds the whole pane for whichever strat is open, the Record Panel's own rule.
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

            IReadOnlyList<HistoryEntry> log = _store.History(document.Id);
            StratRecord record = await _evidence.ComputeAsync(document, token).ConfigureAwait(false);
            CalloutResolver resolver = _callouts.For(document.Owner, document.Map);
            IReadOnlyList<StratHistoryRow> rows = StratHistoryPane.Rows(log, resolver, record);
            _post(() =>
            {
                if (sequence == _sequence && !_disposed)
                {
                    Apply(document, rows);
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
                    StatusLine = $"could not compute the history: {ex.Message}";
                }
            });
        }
    }

    private void Apply(StratDocument document, IReadOnlyList<StratHistoryRow> rows)
    {
        _lastId = document.Id;
        _lastRevision = document.Revision;
        IsComputing = false;
        StatusLine = "";

        Entries.Clear();
        foreach (StratHistoryRow row in rows)
        {
            Entries.Add(new StratHistoryEntryRow(row));
        }

        OnPropertyChanged(nameof(HasEntries));
    }

    private void Clear()
    {
        Entries.Clear();
        IsComputing = false;
        StatusLine = "";
        OnPropertyChanged(nameof(HasEntries));
    }
}

/// <summary>One row of the pane: an entry's revision, when, its words, and the record either side of it.</summary>
public sealed class StratHistoryEntryRow
{
    internal StratHistoryEntryRow(StratHistoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Revision = row.Revision;
        WhenText = row.AtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Summary = row.Summary;
        Changes = row.Changes;
        HasSplit = row.Before is not null && row.After is not null;
        BeforeText = row.Before is { } before ? SplitLine(before) : "";
        AfterText = row.After is { } after ? SplitLine(after) : "";
    }

    public int Revision { get; }

    public string RevisionText => "rev " + Revision.ToString(CultureInfo.InvariantCulture);

    public string WhenText { get; }

    /// <summary>The stored summary, or the entry's ops joined when the commit left none.</summary>
    public string Summary { get; }

    /// <summary>One line per op, the diff itself; shown under the summary when it says more than one thing.</summary>
    public IReadOnlyList<string> Changes { get; }

    /// <summary>True when the itemized lines are worth showing beside the one-line summary.</summary>
    public bool ShowChangeList => Changes.Count > 1;

    /// <summary>Whether a record was given, so <see cref="BeforeText" />/<see cref="AfterText" /> have something to say.</summary>
    public bool HasSplit { get; }

    /// <summary>Runs tagged at a revision below this entry's, worded like the Record Panel's rows.</summary>
    public string BeforeText { get; }

    /// <summary>Runs tagged at this entry's revision or later.</summary>
    public string AfterText { get; }

    // "3 runs · 2 won · 1 lost · 67%"; "no runs" when the side is empty.
    private static string SplitLine(RecordSplit split)
    {
        if (split.Run == 0)
        {
            return "no runs";
        }

        List<string> parts = [Plural(split.Run, "run")];
        if (split.Won > 0)
        {
            parts.Add($"{split.Won} won");
        }

        if (split.Lost > 0)
        {
            parts.Add($"{split.Lost} lost");
        }

        if (split.Aborted > 0)
        {
            parts.Add($"{split.Aborted} aborted");
        }

        if (split.Unknown > 0)
        {
            parts.Add($"{split.Unknown} unknown");
        }

        parts.Add(StratRecordPane.WinRateText(split));
        return string.Join(" · ", parts);
    }

    private static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")}");
}
