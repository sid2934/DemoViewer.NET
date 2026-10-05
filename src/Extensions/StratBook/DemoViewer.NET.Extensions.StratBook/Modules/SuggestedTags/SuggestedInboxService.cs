#region

using DemoViewer.NET.Extensions.StratBook;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;

/// <summary>One proposal as the library-wide Suggested section lists it.</summary>
/// <param name="DemoPath">The demo it was made on.</param>
/// <param name="Sha256">The hash its verdicts are keyed by.</param>
/// <param name="FileName">The demo's file name.</param>
/// <param name="Map">The demo's map, or null when the cache does not know it.</param>
/// <param name="ModifiedTicks">The demo file's time, newest first in the list.</param>
/// <param name="Entry">The proposal and its verdict.</param>
public sealed record SuggestedInboxItem(string DemoPath, string? Sha256, string FileName, string? Map, long ModifiedTicks, ProposalEntry Entry);

/// <summary>
///     Every demo's Suggested Tags proposals in one list, for the Strat Book's Suggested section. The first load reads
///     each built demo's proposals and verdicts as one queue item; after that a demo whose suggestions change is read
///     again on its own. Verdicts go through <see cref="SuggestedTagsService" />, the same path Review mode uses.
/// </summary>
public sealed class SuggestedInboxService : IDisposable
{
    private readonly IExtensionLibrary _library;
    private readonly object _gate = new();
    private readonly Action<Action> _post;
    private readonly IExtensionJobs? _jobs;
    private readonly Func<Action, Task> _run;
    private readonly SuggestedTagsService _suggestions;
    private Dictionary<string, IReadOnlyList<SuggestedInboxItem>> _byDemo = new(StringComparer.OrdinalIgnoreCase);
    private Task? _loading;

    /// <param name="suggestions">The engine: proposals, verdicts, accept, dismiss and restore.</param>
    /// <param name="library">The library: the maps and write times of the demos with proposals.</param>
    /// <param name="jobs">Where the library read runs; null runs it on <paramref name="run" />.</param>
    /// <param name="run">Runs work off the UI thread; defaults to <see cref="Task.Run(Action)" />.</param>
    /// <param name="post">UI-thread marshal for <see cref="Changed" />.</param>
    public SuggestedInboxService(SuggestedTagsService suggestions, IExtensionLibrary library, IExtensionJobs? jobs = null,
        Func<Action, Task>? run = null, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        ArgumentNullException.ThrowIfNull(library);
        _suggestions = suggestions;
        _library = library;
        _jobs = jobs;
        _run = run ?? Task.Run;
        _post = post ?? (a => a());
        _suggestions.Changed += OnSuggestionsChanged;
    }

    /// <summary>Every loaded proposal, newest demo first, then round and trigger order.</summary>
    public IReadOnlyList<SuggestedInboxItem> Items { get; private set; } = [];

    /// <summary>True once the first library read finished.</summary>
    public bool IsLoaded { get; private set; }

    public bool IsLoading
    {
        get
        {
            lock (_gate)
            {
                return _loading is { IsCompleted: false };
            }
        }
    }

    /// <summary>Pending proposals across the library, from the proposals' stamps alone: the rail badge.</summary>
    public int PendingCount => _suggestions.Proposals.PendingTotal();

    /// <summary>Raised on the UI thread when <see cref="Items" /> changed.</summary>
    public event Action? Changed;

    public void Dispose() => _suggestions.Changed -= OnSuggestionsChanged;

    /// <summary>Reads every demo with built proposals, as one user-requested queue item. A call while one runs joins it.</summary>
    public Task LoadAsync()
    {
        lock (_gate)
        {
            if (_loading is { IsCompleted: false } running)
            {
                return running;
            }

            _loading = _jobs is null
                ? _run(ReadAll)
                : _jobs.Enqueue(new JobRequest("Suggested tags: library", _ =>
                {
                    ReadAll();
                    return Task.CompletedTask;
                }, new JobOptions(StratBookJobKinds.SuggestionsInbox, JobPriority.UserRequested, "suggested-inbox"))).Completion;
            return _loading;
        }
    }

    /// <summary>Accepts as proposed into that demo's tag document. False when it is not pending or was not written.</summary>
    public bool Accept(SuggestedInboxItem item) =>
        After(item, _suggestions.Accept(item.DemoPath, item.Entry.Proposal.Id, null, item.Sha256));

    /// <summary>Dismisses: the proposal is not offered again until restored.</summary>
    public bool Dismiss(SuggestedInboxItem item) => After(item, _suggestions.Reject(item.DemoPath, item.Entry.Proposal.Id, item.Sha256));

    /// <summary>Offers a dismissed proposal again.</summary>
    public bool Restore(SuggestedInboxItem item) => After(item, _suggestions.Restore(item.DemoPath, item.Entry.Proposal.Id, item.Sha256));

    private bool After(SuggestedInboxItem item, bool written)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (written)
        {
            ReadDemo(item.DemoPath);
            Publish();
        }

        return written;
    }

    private void ReadAll()
    {
        Dictionary<string, IReadOnlyList<SuggestedInboxItem>> all = new(StringComparer.OrdinalIgnoreCase);
        foreach (DemoDataStamp stamp in _suggestions.Proposals.Stamps().Where(s => s.Fingerprint is not null))
        {
            if (_library.Find(stamp.DemoPath) is { } row)
            {
                all[row.FilePath] = Read(row);
            }
        }

        lock (_gate)
        {
            _byDemo = all;
        }

        IsLoaded = true;
        Publish();
    }

    private void ReadDemo(string path)
    {
        if (_library.Find(path) is not { } row)
        {
            return;
        }

        IReadOnlyList<SuggestedInboxItem> items = Read(row);
        lock (_gate)
        {
            Dictionary<string, IReadOnlyList<SuggestedInboxItem>> next = new(_byDemo, StringComparer.OrdinalIgnoreCase)
            {
                [path] = items
            };
            _byDemo = next;
        }
    }

    private IReadOnlyList<SuggestedInboxItem> Read(LibraryDemo row)
    {
        ProposalSet set = _suggestions.Load(row.FilePath, row.Sha256);
        string fileName = Path.GetFileName(row.FilePath);
        return [.. set.Entries.Select(e => new SuggestedInboxItem(row.FilePath, set.Sha256, fileName, row.MapName, row.Modified.Ticks, e))];
    }

    private void Publish()
    {
        List<SuggestedInboxItem> items;
        lock (_gate)
        {
            items =
            [
                .. _byDemo.Values.SelectMany(v => v)
                    .OrderByDescending(i => i.ModifiedTicks)
                    .ThenBy(i => i.DemoPath, StringComparer.Ordinal)
                    .ThenBy(i => i.Entry.Proposal.Round)
                    .ThenBy(i => i.Entry.Proposal.TriggerTick)
            ];
        }

        _post(() =>
        {
            Items = items;
            Changed?.Invoke();
        });
    }

    // Another surface (Review mode, the background sweep) changed a demo: read it again off the UI thread.
    private void OnSuggestionsChanged(string path)
    {
        if (!IsLoaded)
        {
            return;
        }

        _ = _run(() =>
        {
            ReadDemo(path);
            Publish();
        });
    }
}
