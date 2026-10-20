#region

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.Identity;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.PlayerOverview.Sections;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview;

/// <summary>
///     The Player Overview tab: one player across the whole library. The tab owns who the page is about (the
///     subject), the page-wide empty states and the inner tabs; everything else is a
///     <see cref="PlayerOverviewSection" /> it hosts on the <see cref="PlayerOverviewPage" /> the section names, or
///     above the tabs when the section is persistent.
///     <para>
///         Subject rules. With no restored subject the tab opens on the active Me account. A change to the user's
///         accounts moves the tab to the new active account unless the user opened a player themselves (the
///         subject is pinned), and a pinned subject that stops being one of the user's accounts falls back to the
///         new active account or to nobody.
///     </para>
///     <para>
///         The tab reads only the index: who is in which demo comes from <see cref="PlayerDirectory" />, and whether a
///         demo has its player stats from the row's <c>facts:player_match</c> stamp. It never runs the engine. Work is
///         deferred while the tab is hidden and done once when it is shown again.
///     </para>
///     <para>
///         The tab also reads the subject's stats and rank rows, through <see cref="PlayerRowCache" /> and
///         <see cref="PlayerRankCache" /> off the UI thread, and
///         owns the filter bar: every section renders the one filtered set in its context, so a filter change
///         recomputes every panel from the rows already read and touches no file.
///     </para>
/// </summary>
public sealed partial class PlayerOverviewTabViewModel : ViewModelBase, IWorkspaceTabViewModel, IDisposable
{
    /// <summary>The tab's feature id.</summary>
    public const string FeatureId = "tab.playeroverview";

    private static readonly string FacetStamp = StampedFacts.StampId(PlayerMatchFacet.RulesetId);

    private readonly Action<Action> _post;
    private readonly IDemoProcessingQueue? _queue;
    private int _load;
    private ulong? _optionsFor;
    private readonly PlayerSearch _search;
    private readonly PlayerOverviewServices _services;
    private bool _active;
    private PlayerOverviewContext? _context;
    private bool _disposed;
    private bool _pinned;
    private bool _queueRefreshQueued;
    private bool _clearingSearch;
    private bool _refreshQueued;
    private PlayerRanks? _ranks;
    private PlayerRows? _rows;
    private bool _stale = true;
    private ulong? _subject;
    private bool _subjectWasMine;

    /// <summary>The message under the empty state's or the banner's title.</summary>
    [ObservableProperty]
    private string _detail = "";

    /// <summary>The inner tab on show.</summary>
    [ObservableProperty]
    private PlayerOverviewPage _selectedPage;

    /// <summary>The processing chip's caption while the queue is computing the subject's stats, else empty.</summary>
    [ObservableProperty]
    private string _queueText = "";

    /// <summary>The text in the search box: a name, or a SteamID in any form Steam shows one.</summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>Under the search box: why there are no candidates, or the offline note for a vanity URL.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchMessage))]
    private string _searchMessage = "";

    /// <summary>True while the subject's facets are read for the first time; a later re-read refreshes in place.</summary>
    [ObservableProperty]
    private bool _isReadingStats;

    /// <summary>What the page can show for its subject.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowBanner), nameof(ShowSections), nameof(CanOpenIdentity),
        nameof(CanOpenProcessing), nameof(ShowQueueChip))]
    private PlayerOverviewState _state = PlayerOverviewState.NoSubject;

    /// <summary>The empty state's or the banner's title.</summary>
    [ObservableProperty]
    private string _title = "";

    /// <param name="services">What the tab and its sections reach.</param>
    /// <param name="queue">The processing queue, for the paused and switched-off states; null reads as always running.</param>
    /// <param name="post">Runs a refresh later on the UI thread, so a burst of changes refreshes once; inline when null.</param>
    public PlayerOverviewTabViewModel(PlayerOverviewServices services, IDemoProcessingQueue? queue = null, Action<Action>? post = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _queue = queue;
        _post = post ?? (action => action());
        _search = new PlayerSearch(services.Directory);
        Filter = new PlayerOverviewFilterViewModel();
        Sections = PlayerOverviewSections.Create(services with { OpenPlayer = OpenPlayer }, Filter);
        Persistent = [.. Sections.Where(s => s.IsPersistent)];
        // History's table needs about 900px of its own before Teammates and Head-to-head can sit beside it.
        Pages =
        [
            new PlayerOverviewPage(PlayerOverviewPageKind.Overview, Sections),
            new PlayerOverviewPage(PlayerOverviewPageKind.History, Sections, 3, 2, 1700),
            new PlayerOverviewPage(PlayerOverviewPageKind.Stats, Sections, 3, 2, 1300),
            new PlayerOverviewPage(PlayerOverviewPageKind.Achievements, Sections)
        ];
        _selectedPage = Pages[0];
        _selectedPage.IsActive = true;

        SetSubject(ParseId(services.Identity.ActiveSteamId64), false);

        services.Directory.Changed += OnDirectoryChanged;
        services.Identity.Changed += OnIdentityChanged;
        Filter.Changed += OnFilterChanged;
        if (queue is not null)
        {
            queue.Changed += OnQueueChanged;
        }
    }

    /// <summary>The filter bar every section's window follows.</summary>
    public PlayerOverviewFilterViewModel Filter { get; }

    /// <summary>The latest read of the subject's facets and records; completed when nothing is being read.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Every section, in page order.</summary>
    public IReadOnlyList<PlayerOverviewSection> Sections { get; }

    /// <summary>The sections above the inner tabs, shown on every page.</summary>
    public IReadOnlyList<PlayerOverviewSection> Persistent { get; }

    /// <summary>The inner tabs, in strip order.</summary>
    public IReadOnlyList<PlayerOverviewPage> Pages { get; }

    /// <summary>The players the search text matches, most demos first.</summary>
    public ObservableCollection<PlayerCandidateItem> Candidates { get; } = [];

    /// <summary>True when the search box has something to say besides candidates.</summary>
    public bool HasSearchMessage => SearchMessage.Length > 0;

    /// <summary>True when the candidate list shows.</summary>
    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>The user's Me accounts for the header switcher, the active one marked.</summary>
    public ObservableCollection<MeAccountItem> MeAccounts { get; } = [];

    /// <summary>True when the user has at least one Me account.</summary>
    public bool HasMeAccounts => MeAccounts.Count > 0;

    /// <summary>The account the page is about, or null.</summary>
    public ulong? SubjectSteamId64 => _subject;

    /// <summary>True when the user opened the subject; false while the tab follows the active Me account.</summary>
    public bool IsPinned => _pinned;

    /// <summary>What the sections last rendered; null before the first refresh.</summary>
    public PlayerOverviewContext? Context => _context;

    /// <summary>True when the page shows a full-page empty state instead of sections.</summary>
    public bool ShowEmpty => State is PlayerOverviewState.NoPlayers or PlayerOverviewState.NoSubject or PlayerOverviewState.SubjectNotFound;

    /// <summary>True when the sections show.</summary>
    public bool ShowSections => !ShowEmpty;

    /// <summary>True when the sections show under a line saying some demos have no stats yet.</summary>
    public bool ShowBanner => State is PlayerOverviewState.Computing or PlayerOverviewState.ProcessingOff or PlayerOverviewState.Paused;

    /// <summary>True when the state's action is setting who the user is.</summary>
    public bool CanOpenIdentity => State == PlayerOverviewState.NoSubject;

    /// <summary>True when the state's action is switching background processing on.</summary>
    public bool CanOpenProcessing => State == PlayerOverviewState.ProcessingOff;

    /// <summary>True when the processing chip shows.</summary>
    public bool ShowQueueChip => State == PlayerOverviewState.Computing && QueueText.Length > 0;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _services.Directory.Changed -= OnDirectoryChanged;
        _services.Identity.Changed -= OnIdentityChanged;
        Filter.Changed -= OnFilterChanged;
        if (_queue is not null)
        {
            _queue.Changed -= OnQueueChanged;
        }
    }

    /// <inheritdoc />
    public void OnActivated(IModuleContext context)
    {
        _active = true;
        if (_stale)
        {
            Refresh();
        }
    }

    /// <inheritdoc />
    public void OnDeactivated() => _active = false;

    /// <inheritdoc />
    public object? SnapshotState() =>
        new PlayerOverviewSession(_subject?.ToString(CultureInfo.InvariantCulture), _pinned, SelectedPage.Kind.ToString());

    /// <summary>
    ///     Reopens the player the user had open. A subject that only followed the active account is not restored:
    ///     the tab follows whichever account is active now. A blob of another shape restores nothing.
    /// </summary>
    public void RestoreState(object? state)
    {
        if (state is not JsonElement { ValueKind: JsonValueKind.Object } json)
        {
            return;
        }

        PlayerOverviewSession? session;
        try
        {
            session = json.Deserialize<PlayerOverviewSession>();
        }
        catch (JsonException)
        {
            return;
        }

        if (Enum.TryParse(session?.Page, out PlayerOverviewPageKind page))
        {
            ShowPage(page);
        }

        if (session is { Pinned: true } && ParseId(session.SubjectSteamId64) is { } id)
        {
            SetSubject(id, true);
            Invalidate();
        }
    }

    /// <summary>Shows <paramref name="steamId64" />. Opening anyone but the active account pins them for the session.</summary>
    /// <param name="steamId64">The account.</param>
    public void OpenPlayer(ulong steamId64)
    {
        if (steamId64 == 0)
        {
            return;
        }

        SetSubject(steamId64, steamId64 != ParseId(_services.Identity.ActiveSteamId64));
        Invalidate();
    }

    /// <summary>Called by the view with its measured width; each page decides whether its rail fits.</summary>
    public void SetViewportWidth(double width)
    {
        foreach (PlayerOverviewPage page in Pages)
        {
            page.SetWidth(width);
        }
    }

    /// <summary>Shows the inner tab <paramref name="kind" />.</summary>
    public void ShowPage(PlayerOverviewPageKind kind) => SelectedPage = Pages.First(p => p.Kind == kind);

    // A null from the strip (its items rebuilt) keeps the page that was showing; the subject never moves the page.
    partial void OnSelectedPageChanging(PlayerOverviewPage? oldValue, PlayerOverviewPage newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
        }
    }

    partial void OnSelectedPageChanged(PlayerOverviewPage value)
    {
        if (value is null)
        {
            SelectedPage = Pages[0];
            return;
        }

        value.IsActive = true;
    }

    /// <summary>Opens the only candidate, or the account the text names when it is not a bare account id.</summary>
    [RelayCommand]
    private void SubmitSearch()
    {
        PlayerSearchResult result = _search.Search(SearchText);
        if (result.Candidates.Count == 1)
        {
            OpenFromSearch(result.Candidates[0].SteamId64);
        }
        else if (result.SteamId64 is { } id && !result.IsFlagged)
        {
            OpenFromSearch(id);
        }
    }

    /// <summary>Opens <paramref name="candidate" /> and clears the search.</summary>
    [RelayCommand]
    private void OpenCandidate(PlayerCandidateItem? candidate)
    {
        if (candidate is not null)
        {
            OpenFromSearch(candidate.SteamId64);
        }
    }

    /// <summary>Makes <paramref name="account" /> the active Me account and shows it.</summary>
    [RelayCommand]
    private void SwitchAccount(MeAccountItem? account)
    {
        if (account is null || ParseId(account.SteamId64) is not { } id)
        {
            return;
        }

        _services.Identity.SetActive(account.SteamId64);
        SetSubject(id, false);
        Invalidate();
    }

    /// <summary>Opens Settings at Identity.</summary>
    [RelayCommand]
    private void OpenIdentitySettings() => _services.OpenSettings("SectionIdentity");

    /// <summary>Opens Settings at Background Processing.</summary>
    [RelayCommand]
    private void OpenProcessingSettings() => _services.OpenSettings("SectionBackgroundProcessing");

    partial void OnSearchTextChanged(string value)
    {
        if (_clearingSearch)
        {
            return;
        }

        PlayerSearchResult result = _search.Search(value);
        Candidates.Clear();
        foreach (PlayerEntry entry in result.Candidates)
        {
            Candidates.Add(PlayerCandidateItem.From(entry));
        }

        OnPropertyChanged(nameof(HasCandidates));
        if (result.SteamId64 is { } id && !result.IsFlagged)
        {
            OpenFromSearch(id);
            return;
        }

        SearchMessage = result.Notice is { } notice
            ? $"Steam vanity URLs: {notice}."
            : result.Candidates.Count == 0 && value.Trim().Length > 0
                ? "No player in this library matches."
                : "";
    }

    private void OpenFromSearch(ulong steamId64)
    {
        _clearingSearch = true;
        SearchText = "";
        _clearingSearch = false;
        Candidates.Clear();
        SearchMessage = "";
        OnPropertyChanged(nameof(HasCandidates));
        OpenPlayer(steamId64);
    }

    private void SetSubject(ulong? steamId64, bool pinned)
    {
        _subject = steamId64;
        _pinned = steamId64 is not null && pinned;
        _subjectWasMine = steamId64 is { } id && _services.Identity.IsMine(id);
    }

    private void OnIdentityChanged()
    {
        bool stillMine = _subject is { } id && _services.Identity.IsMine(id);
        if (!_pinned || (_subjectWasMine && !stillMine))
        {
            SetSubject(ParseId(_services.Identity.ActiveSteamId64), false);
        }
        else
        {
            _subjectWasMine = stillMine;
        }

        Invalidate();
    }

    private void OnDirectoryChanged() => Invalidate();

    // The rows are read already: a new window is a recompute over them, with no directory or file read.
    private void OnFilterChanged()
    {
        if (_context is { } context)
        {
            Show(context with { Filter = Filter.Filter, Window = PlayerAggregate.Window(context.Demos, Filter.Filter) });
        }
    }

    private void OnQueueChanged()
    {
        if (_queueRefreshQueued || !_active || _context is null)
        {
            return;
        }

        _queueRefreshQueued = true;
        _post(() =>
        {
            _queueRefreshQueued = false;
            if (!_disposed && _context is { } context)
            {
                ApplyState(context);
            }
        });
    }

    // Marks the page out of date and refreshes it once on the next post while it is shown.
    private void Invalidate()
    {
        _stale = true;
        if (_refreshQueued || !_active)
        {
            return;
        }

        _refreshQueued = true;
        _post(() =>
        {
            _refreshQueued = false;
            if (!_disposed && _active && _stale)
            {
                Refresh();
            }
        });
    }

    private void Refresh()
    {
        _stale = false;
        // A read still running for an earlier refresh finishes into the cache and is not shown.
        int load = ++_load;
        PlayerDirectory directory = _services.Directory;
        PlayerEntry? player = _subject is { } id ? directory.Get(id) : null;
        IReadOnlyList<PlayerMatch> matches = player is null ? [] : directory.Matches(player.SteamId64);
        int withStats = 0;
        foreach (PlayerMatch match in matches)
        {
            if (match.Row.Stamp(FacetStamp) is { State: DemoAnalysisState.Indexed })
            {
                withStats++;
            }
        }

        // A new subject starts unfiltered; the same subject keeps the user's selection.
        Filter.SetOptions(matches, reset: _optionsFor != _subject);
        _optionsFor = _subject;

        PlayerRowCache? cache = _services.Rows;
        if (player is null || _rows?.SteamId64 != player.SteamId64)
        {
            _rows = null;
        }

        bool reading = false;
        if (player is not null && cache is not null)
        {
            if (cache.TryGet(player.SteamId64) is { } rows)
            {
                _rows = rows;
            }
            else
            {
                reading = true;
            }
        }

        PlayerRankCache? ranks = _services.Ranks;
        if (player is null || _ranks?.SteamId64 != player.SteamId64)
        {
            _ranks = null;
        }

        bool ranking = false;
        if (player is not null && ranks is not null)
        {
            if (ranks.TryGet(player.SteamId64) is { } held)
            {
                _ranks = held;
            }
            else
            {
                ranking = true;
            }
        }

        // A demo finishing in the background drops the subject from the caches; the stats read before stay on
        // show while the re-read runs, so the page does not flash a loading state for every demo the queue does.
        bool firstRead = reading && _rows is null;
        PlayerOverviewContext context = Compose(new PlayerOverviewContext(_subject, player, matches, withStats, _subjectWasMine,
            _services, [], Filter.Filter, [], firstRead, _ranks, ranking && _ranks is null));
        RebuildMeAccounts();
        Pages.First(p => p.Kind == PlayerOverviewPageKind.History).Count = player is null ? null : matches.Count;
        ApplyState(context);
        Show(context);
        IsReadingStats = firstRead;

        Task stats = reading ? LoadAsync(cache!, player!.SteamId64, load) : Task.CompletedTask;
        Task records = ranking ? LoadRanksAsync(ranks!, player!.SteamId64, load) : Task.CompletedTask;
        Loading = Task.WhenAll(stats, records);
    }

    private async Task LoadAsync(PlayerRowCache cache, ulong steamId64, int load)
    {
        PlayerRows? rows = null;
        try
        {
            rows = await cache.LoadAsync(steamId64);
        }
        catch (Exception)
        {
            // A sidecar that will not read leaves its demo "stats not read"; the next refresh tries again.
        }

        if (_load != load || _disposed || _context is not { } context)
        {
            return;
        }

        _rows = rows ?? _rows;
        IsReadingStats = false;
        Show(Compose(context with { IsReadingStats = false }));
    }

    private async Task LoadRanksAsync(PlayerRankCache cache, ulong steamId64, int load)
    {
        PlayerRanks? ranks = null;
        try
        {
            ranks = await cache.LoadAsync(steamId64);
        }
        catch (Exception)
        {
            // A record that will not read leaves its rank empty; the next refresh tries again.
        }

        if (_load != load || _disposed || _context is not { } context)
        {
            return;
        }

        _ranks = ranks ?? _ranks;
        Show(context with { Ranks = _ranks, IsReadingRanks = false });
    }

    // Pairs each demo with the stats last read for it, kept only while its facet stamp has not moved, and
    // windows the result by the filter.
    private PlayerOverviewContext Compose(PlayerOverviewContext context)
    {
        Dictionary<string, PlayerDemo>? read = null;
        if (_rows is { } rows)
        {
            read = new Dictionary<string, PlayerDemo>(rows.Demos.Count, StringComparer.Ordinal);
            foreach (PlayerDemo demo in rows.Demos)
            {
                read[ContentIdOf(demo.Match.Row)] = demo;
            }
        }

        List<PlayerDemo> demos = new(context.Matches.Count);
        foreach (PlayerMatch match in context.Matches)
        {
            demos.Add(read is not null
                      && read.TryGetValue(ContentIdOf(match.Row), out PlayerDemo? held)
                      && held.HasFacet
                      && match.Row.Stamp(FacetStamp) is { State: DemoAnalysisState.Indexed } stamp
                      && held.Match.Row.Stamp(FacetStamp) == stamp
                ? held with { Match = match }
                : new PlayerDemo(match, false, null));
        }

        return context with { Demos = demos, Window = PlayerAggregate.Window(demos, context.Filter) };
    }

    private void Show(PlayerOverviewContext context)
    {
        _context = context;
        foreach (PlayerOverviewSection section in Sections)
        {
            section.Update(context);
        }
    }

    private static string ContentIdOf(DemoCacheIndexEntry row) => string.IsNullOrEmpty(row.Sha256) ? row.Path : row.Sha256;

    private void ApplyState(PlayerOverviewContext context)
    {
        int total = context.Matches.Count;
        int pending = total - context.WithStats;
        string queueText = "";
        PlayerOverviewState state;
        if (_services.Directory.Players.Count == 0)
        {
            state = PlayerOverviewState.NoPlayers;
            Title = "No players yet";
            Detail = "This library has no demos with SteamIDs. Players show up here once a demo that names them is indexed.";
        }
        else if (context.SteamId64 is not { } id)
        {
            state = PlayerOverviewState.NoSubject;
            Title = "No player open";
            Detail = "Search a player or set who you are.";
        }
        else if (context.Player is null)
        {
            state = PlayerOverviewState.SubjectNotFound;
            Title = "Not in this library";
            Detail = string.Create(CultureInfo.InvariantCulture, $"No demo in this library has the player {id}.");
        }
        else if (pending == 0)
        {
            state = PlayerOverviewState.Ready;
            Title = "";
            Detail = "";
        }
        else if (_queue is { BackgroundEnabled: false })
        {
            state = PlayerOverviewState.ProcessingOff;
            Title = "Background processing is off";
            Detail = string.Create(CultureInfo.InvariantCulture,
                $"{Demos(pending)} {(pending == 1 ? "has" : "have")} no player stats. Switch background processing on to compute them.");
        }
        else if (_queue is { IsPaused: true })
        {
            state = PlayerOverviewState.Paused;
            Title = StatsFor(context.WithStats, total, pending);
            Detail = "Background processing is paused; the rest are computed when it resumes.";
        }
        else
        {
            state = PlayerOverviewState.Computing;
            Title = StatsFor(context.WithStats, total, pending);
            Detail = "The library is computing the rest.";
            int inQueue = _queue is null ? 0 : _queue.QueuedCount + _queue.RunningCount;
            if (inQueue > 0)
            {
                queueText = string.Create(CultureInfo.InvariantCulture, $"Processing · {inQueue} in queue");
            }
        }

        QueueText = queueText;
        State = state;
        OnPropertyChanged(nameof(ShowQueueChip));
    }

    private void RebuildMeAccounts()
    {
        string? active = _services.Identity.ActiveSteamId64;
        MeAccounts.Clear();
        foreach (IdentityAccount account in _services.Identity.Accounts)
        {
            MeAccounts.Add(MeAccountItem.From(account, _services.Directory, active));
        }

        OnPropertyChanged(nameof(HasMeAccounts));
    }

    private static string StatsFor(int withStats, int total, int pending) =>
        string.Create(CultureInfo.InvariantCulture, $"Stats for {withStats} of {Demos(total)} · {pending} remaining");

    private static string Demos(int count) => count == 1 ? "1 demo" : string.Create(CultureInfo.InvariantCulture, $"{count} demos");

    private static ulong? ParseId(string? steamId64) =>
        ulong.TryParse(steamId64, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && id != 0 ? id : null;
}
