#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>
///     The Strat Book tab (strat-model.md §3.11): the book selector over Team Identity's teams plus <c>me</c>, the
///     map and side filters, the strat list from the store's index, and the editor over the one open
///     <see cref="StratSession" />.
///     <para>
///         <b>Commits follow the tab.</b> Leaving the tab, a demo swap, opening another strat, another book and
///         shutdown each commit the open strat's pending edits (§3.8's triggers); the session's own timers cover the
///         30 s idle rule and the working copy between.
///     </para>
///     <para>
///         Delegate-injected (the Highlights precedent): the composition root supplies the store, Team Identity and
///         the UI-thread post; the VM references no shell. Team names are raw in the service and sanitized here, at
///         the render boundary, like every player name.
///     </para>
/// </summary>
public sealed partial class StratBookTabViewModel : ViewModelBase, IWorkspaceTabViewModel, IDisposable
{
    /// <summary>The map filter's "no filter" entry.</summary>
    public const string AllMaps = "all maps";

    /// <summary>The side filter's "no filter" entry.</summary>
    public const string AllSides = "all sides";

    private readonly Action<Action> _post;
    private readonly StratStore _store;
    private readonly TeamIdentityService? _teams;

    private IModuleContext? _context;
    private bool _disposed;
    private bool _refreshing;

    [ObservableProperty]
    private string _listLine = "";

    [ObservableProperty]
    private string _selectedMap = AllMaps;

    [ObservableProperty]
    private StratOwnerOption? _selectedOwner;

    [ObservableProperty]
    private string _selectedSide = AllSides;

    [ObservableProperty]
    private StratListRow? _selectedStrat;

    /// <param name="store">The strat store.</param>
    /// <param name="teams">Team Identity, for the books; null offers the <c>me</c> book alone.</param>
    /// <param name="post">Marshals the session's timers onto the UI thread; defaults to synchronous.</param>
    /// <param name="isBrowser">Whether the host is the WASM head; null reads the runtime.</param>
    public StratBookTabViewModel(StratStore store, TeamIdentityService? teams = null, Action<Action>? post = null, bool? isBrowser = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _teams = teams;
        _post = post ?? (action => action());
        IsBrowser = isBrowser ?? OperatingSystem.IsBrowser();
        Session = new StratSession(store, _post, () => IsBrowser, () => DateTime.UtcNow);
        Editor = new StratEditorViewModel(Session);

        Session.Changed += OnSessionChanged;
        _store.Changed += OnStoreChanged;
        if (_teams is not null)
        {
            _teams.Changed += RefreshOwners;
        }

        RefreshOwners();
    }

    /// <summary>The line the tab shows on the browser host.</summary>
    public static string BrowserNote => "session only: this browser tab forgets strats when it reloads";

    /// <summary>True on the WASM head.</summary>
    public bool IsBrowser { get; }

    /// <summary>The one live strat. Step Authoring's canvas edits through it too (overview correction 18).</summary>
    public StratSession Session { get; }

    public StratEditorViewModel Editor { get; }

    /// <summary>Every book: <c>me</c>, then each visible team.</summary>
    public ObservableCollection<StratOwnerOption> Owners { get; } = [];

    public ObservableCollection<string> MapOptions { get; } = [];

    public static IReadOnlyList<string> SideOptions { get; } = [AllSides, StratVocabulary.SideT, StratVocabulary.SideCt];

    public ObservableCollection<StratListRow> Strats { get; } = [];

    public bool HasStrats => Strats.Count > 0;

    public bool HasOpenStrat => Session.Document is not null;

    public bool CanUndo => Session.CanUndo;

    public bool CanRedo => Session.CanRedo;

    public bool HasPending => Session.HasPending;

    public string StatusLine => Session.StatusText;

    /// <inheritdoc />
    public void OnActivated(IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_context is not null)
        {
            _context.DemoReset -= OnDemoReset;
        }

        _context = context;
        _context.DemoReset += OnDemoReset;

        // The open demo's map is the likely one to author for; a filter the user chose is kept.
        if (SelectedMap == AllMaps && !string.IsNullOrEmpty(context.MapName))
        {
            RefreshMaps(context.MapName);
            SelectedMap = context.MapName.ToLowerInvariant();
        }

        RefreshList();
    }

    /// <inheritdoc />
    public void OnDeactivated()
    {
        if (_context is not null)
        {
            _context.DemoReset -= OnDemoReset;
            _context = null;
        }

        Session.Commit();
    }

    /// <summary>
    ///     Shutdown, called by the shell: commits the open strat, leaves anything the store refused in the working
    ///     copy for the next session, and writes the deferred index.
    /// </summary>
    public void Shutdown()
    {
        Session.Close();
        _store.SaveIndex();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_context is not null)
        {
            _context.DemoReset -= OnDemoReset;
        }

        if (_teams is not null)
        {
            _teams.Changed -= RefreshOwners;
        }

        _store.Changed -= OnStoreChanged;
        Session.Changed -= OnSessionChanged;
        Session.Dispose();
    }

    // ── Actions ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A new strat in the selected book, on the filtered map (else the open demo's), on the filtered side (else
    ///     T), committed as revision 1 and opened.
    /// </summary>
    [RelayCommand]
    private void NewStrat()
    {
        if (SelectedOwner is not { } owner)
        {
            return;
        }

        string? map = SelectedMap != AllMaps ? SelectedMap : _context?.MapName;
        if (string.IsNullOrWhiteSpace(map))
        {
            ListLine = "choose a map for the new strat";
            return;
        }

        string side = SelectedSide == StratVocabulary.SideCt ? StratVocabulary.SideCt : StratVocabulary.SideT;
        StratDocument created = _store.Create(owner.Owner, map, side, side == StratVocabulary.SideCt ? "setup" : "default", "New strat");
        if (created.Revision == 0)
        {
            ListLine = "strat could not be saved";
            return;
        }

        RefreshList();
        SelectedStrat = Strats.FirstOrDefault(r => r.Id == created.Id);
    }

    /// <summary>Moves the selected strat to its folder's <c>.trash/</c> (decision 10), committing its edits first.</summary>
    [RelayCommand]
    private void DeleteStrat()
    {
        if (SelectedStrat is not { } row)
        {
            return;
        }

        if (Session.Document?.Id == row.Id)
        {
            Session.Close();
        }

        SelectedStrat = null;
        if (!_store.Delete(row.Id))
        {
            ListLine = "strat could not be deleted";
        }

        RefreshList();
    }

    /// <summary>The explicit save: a commit of the pending edits.</summary>
    [RelayCommand]
    private void Save() => Session.Commit();

    [RelayCommand]
    private void Undo() => Session.Undo();

    [RelayCommand]
    private void Redo() => Session.Redo();

    // ── Selection ────────────────────────────────────────────────────────────────────────────────

    partial void OnSelectedOwnerChanged(StratOwnerOption? value)
    {
        if (_refreshing)
        {
            return;
        }

        // Another book: the open strat is not in it.
        SelectedStrat = null;
        RefreshList();
    }

    partial void OnSelectedMapChanged(string value)
    {
        if (!_refreshing)
        {
            RefreshList();
        }
    }

    partial void OnSelectedSideChanged(string value)
    {
        if (!_refreshing)
        {
            RefreshList();
        }
    }

    partial void OnSelectedStratChanged(StratListRow? value)
    {
        if (_refreshing || value?.Id == Session.Document?.Id)
        {
            return;
        }

        if (value is null)
        {
            Session.Close();
            return;
        }

        StratLoadResult result = Session.Open(value.Id);
        if (result.Document is null)
        {
            ListLine = "that strat could not be opened";
            return;
        }

        ConfigureEditor();
    }

    // ── Projection ───────────────────────────────────────────────────────────────────────────────

    private void OnDemoReset() => Session.Commit();

    private void OnSessionChanged()
    {
        Editor.Project();
        OnPropertyChanged(nameof(HasOpenStrat));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(StatusLine));
    }

    // The open strat's own working-copy writes land here every half second while it is edited; they change
    // its list row and nothing the editor offers, so only another strat's change reconfigures the editor.
    private void OnStoreChanged(Guid? id) => RefreshList(id is null || id != Session.Document?.Id);

    // Books are "me" plus every visible team, "us" first among the teams; a book whose team went away is
    // dropped and the selection falls back to the default, "us" when a team is marked, else "me".
    private void RefreshOwners()
    {
        StratOwner? keep = SelectedOwner?.Owner;
        List<StratOwnerOption> options = [new(StratOwner.Me(), "me")];
        if (_teams is not null)
        {
            options.AddRange(_teams.Teams
                .OrderByDescending(t => t.IsUs)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => new StratOwnerOption(StratOwner.Team(t.Id), DisplayText.Sanitize(t.Name) + (t.IsUs ? " (us)" : ""))));
        }

        _refreshing = true;
        try
        {
            Owners.Clear();
            foreach (StratOwnerOption option in options)
            {
                Owners.Add(option);
            }

            StratOwner fallback = _teams?.Us is { } us ? StratOwner.Team(us.Id) : StratOwner.Me();
            SelectedOwner = Owners.FirstOrDefault(o => keep is not null && o.Owner.Equals(keep))
                            ?? Owners.FirstOrDefault(o => o.Owner.Equals(fallback))
                            ?? Owners[0];
        }
        finally
        {
            _refreshing = false;
        }

        if (Session.Document is { } open && !open.Owner.Equals(SelectedOwner?.Owner))
        {
            SelectedStrat = null;
        }

        RefreshList();
    }

    private void RefreshMaps(string? extra)
    {
        SortedSet<string> maps = new(StringComparer.Ordinal);
        maps.UnionWith(CanonicalPlaces.Maps);
        maps.UnionWith(_store.Index.Select(e => e.Map));
        if (!string.IsNullOrEmpty(extra))
        {
            maps.Add(extra.ToLowerInvariant());
        }

        List<string> options = [AllMaps, .. maps];
        if (MapOptions.SequenceEqual(options))
        {
            return;
        }

        string keep = SelectedMap;
        _refreshing = true;
        try
        {
            MapOptions.Clear();
            foreach (string option in options)
            {
                MapOptions.Add(option);
            }

            SelectedMap = MapOptions.Contains(keep) ? keep : AllMaps;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshList(bool configureEditor = true)
    {
        if (_disposed)
        {
            return;
        }

        RefreshMaps(_context?.MapName);
        Guid? keep = SelectedStrat?.Id ?? Session.Document?.Id;
        IReadOnlyList<StratIndexEntry> rows = SelectedOwner is { } owner
            ? _store.Query(owner.Owner, SelectedMap == AllMaps ? null : SelectedMap, SelectedSide == AllSides ? null : SelectedSide, null)
            : [];

        _refreshing = true;
        try
        {
            Strats.Clear();
            foreach (StratIndexEntry row in rows)
            {
                Strats.Add(new StratListRow(row));
            }

            SelectedStrat = keep is { } id ? Strats.FirstOrDefault(r => r.Id == id) : null;
        }
        finally
        {
            _refreshing = false;
        }

        ListLine = rows.Count == 0
            ? SelectedOwner is null ? "" : "no strats in this book yet"
            : string.Create(CultureInfo.InvariantCulture, $"{rows.Count} strat{(rows.Count == 1 ? "" : "s")}");
        OnPropertyChanged(nameof(HasStrats));

        // The open strat's branch targets are the book's strats on its map, which a commit elsewhere changes.
        if (configureEditor && Session.Document is not null)
        {
            ConfigureEditor();
        }
    }

    private void ConfigureEditor()
    {
        if (Session.Document is not { } document)
        {
            return;
        }

        CalloutResolver places = CalloutResolver.For(document.Map, null, _store.LoadCallouts(document.Owner, document.Map));
        List<StratTargetOption> targets =
        [
            .. _store.Query(document.Owner, document.Map, null, null)
                .Select(e => new StratTargetOption(e.Id, e.Id == document.Id ? "this strat" : e.Name))
        ];
        Editor.Configure(places, PinsFor(document.Owner), targets);
    }

    // The latest roster's members for a team, by last-seen name; the confirmed me accounts for the me book.
    private List<StratPinOption> PinsFor(StratOwner owner)
    {
        if (_teams is null)
        {
            return [];
        }

        if (!owner.IsTeam)
        {
            return [.. _teams.MyAccounts.Select(id => new StratPinOption(id, id))];
        }

        Team? team = _teams.AllTeams.FirstOrDefault(t => t.Id == owner.TeamId);
        Roster? latest = team?.Rosters.OrderBy(r => r.Since).LastOrDefault();
        if (team is null || latest is null)
        {
            return [];
        }

        return
        [
            .. _teams.MembersOf(team.Id, latest.Id)
                .OrderByDescending(m => m.Value.Count)
                .ThenBy(m => m.Key, StringComparer.Ordinal)
                .Select(m => new StratPinOption(m.Key, DisplayText.Sanitize(m.Value.LastName)))
        ];
    }
}

/// <summary>A book the selector offers.</summary>
public sealed record StratOwnerOption(StratOwner Owner, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One strat in the list, from its index row.</summary>
public sealed class StratListRow(StratIndexEntry entry)
{
    public Guid Id { get; } = entry.Id;

    public string Name { get; } = entry.Name;

    public string Summary { get; } = string.Create(CultureInfo.InvariantCulture,
        $"{entry.Map} · {entry.Side} · {entry.Type}{(entry.TargetSite is null ? "" : " " + entry.TargetSite)} · {entry.Status} · {entry.StepCount} step{(entry.StepCount == 1 ? "" : "s")} · rev {entry.Revision}");
}
