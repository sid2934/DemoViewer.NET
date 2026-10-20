#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Controls.Stats;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>
///     The subject beside a second player the user picks from the search's candidates: the summary tiles and the
///     stat categories of both, each pooled over their own demos that pass the tab's filter. The second player's
///     rows come from <see cref="PlayerRowCache" />, read off the UI thread. A dot marks the better of two values
///     only where they compare across samples of different sizes: a rate, a share or a banded ratio, never a
///     raw count.
/// </summary>
public sealed partial class CompareSectionViewModel : PlayerOverviewSection
{
    private const string None = "–";

    private readonly PlayerSearch _search;
    private readonly PlayerOverviewServices _services;
    private bool _clearingSearch;
    private PlayerOverviewContext? _context;
    private int _load;
    private PlayerRows? _rows;

    [ObservableProperty]
    private IReadOnlyList<CompareCategoryItem> _categories = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string _noteText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOther))]
    private ulong? _otherSteamId64;

    [ObservableProperty]
    private string _otherName = "";

    [ObservableProperty]
    private string _otherScope = "";

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchMessage))]
    private string _searchMessage = "";

    [ObservableProperty]
    private string _subjectName = "";

    [ObservableProperty]
    private string _subjectScope = "";

    [ObservableProperty]
    private IReadOnlyList<CompareStatItem> _tiles = [];

    /// <param name="services">The directory to search and the stats of every account.</param>
    public CompareSectionViewModel(PlayerOverviewServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _search = new PlayerSearch(services.Directory);
    }

    /// <inheritdoc />
    public override string Id => "compare";

    /// <summary>The players the search text matches, the subject left out.</summary>
    public ObservableCollection<PlayerCandidateItem> Candidates { get; } = [];

    /// <summary>True when the candidate list shows.</summary>
    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>True when the search box has something to say besides candidates.</summary>
    public bool HasSearchMessage => SearchMessage.Length > 0;

    /// <summary>True once a second player is picked.</summary>
    public bool HasOther => OtherSteamId64 is not null;

    /// <summary>True when there is a line under the columns.</summary>
    public bool HasNote => NoteText.Length > 0;

    /// <summary>The latest read of the second player's stats; completed when nothing is being read.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public override void Update(PlayerOverviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        if (context.Player is not { } player || _services.Rows is null)
        {
            _load++;
            IsVisible = false;
            Tiles = [];
            Categories = [];
            return;
        }

        SubjectName = NameOf(player);
        if (OtherSteamId64 == player.SteamId64)
        {
            Clear();
        }

        IsVisible = true;
        Refresh();
    }

    /// <summary>Compares the subject with <paramref name="steamId64" />.</summary>
    /// <param name="steamId64">An account in the library other than the subject.</param>
    public void Pick(ulong steamId64)
    {
        if (_services.Directory.Get(steamId64) is not { } entry || steamId64 == _context?.Player?.SteamId64)
        {
            return;
        }

        ClearSearch();
        OtherSteamId64 = steamId64;
        OtherName = NameOf(entry);
        if (_rows?.SteamId64 != steamId64)
        {
            _rows = null;
        }

        Refresh();
    }

    /// <summary>Picks <paramref name="candidate" /> and clears the search.</summary>
    [RelayCommand]
    private void PickCandidate(PlayerCandidateItem? candidate)
    {
        if (candidate is not null)
        {
            Pick(candidate.SteamId64);
        }
    }

    /// <summary>Picks the only candidate.</summary>
    [RelayCommand]
    private void SubmitSearch()
    {
        if (Candidates.Count == 1)
        {
            Pick(Candidates[0].SteamId64);
        }
    }

    /// <summary>Drops the second player.</summary>
    [RelayCommand]
    private void ClearOther()
    {
        Clear();
        Refresh();
    }

    /// <summary>Opens the second player's own overview.</summary>
    [RelayCommand]
    private void OpenOther()
    {
        if (OtherSteamId64 is { } id)
        {
            _services.OpenPlayer?.Invoke(id);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_clearingSearch)
        {
            return;
        }

        ulong? subject = _context?.Player?.SteamId64;
        PlayerSearchResult result = _search.Search(value);
        Candidates.Clear();
        foreach (PlayerEntry entry in result.Candidates)
        {
            if (entry.SteamId64 != subject)
            {
                Candidates.Add(PlayerCandidateItem.From(entry));
            }
        }

        OnPropertyChanged(nameof(HasCandidates));
        if (result.SteamId64 is { } id && !result.IsFlagged && id != subject && _services.Directory.Get(id) is not null)
        {
            Pick(id);
            return;
        }

        SearchMessage = result.Notice is { } notice
            ? $"Steam vanity URLs: {notice}."
            : Candidates.Count == 0 && value.Trim().Length > 0
                ? "No other player in this library matches."
                : "";
    }

    private void ClearSearch()
    {
        _clearingSearch = true;
        SearchText = "";
        _clearingSearch = false;
        Candidates.Clear();
        SearchMessage = "";
        OnPropertyChanged(nameof(HasCandidates));
    }

    private void Clear()
    {
        _load++;
        OtherSteamId64 = null;
        OtherName = OtherScope = "";
        _rows = null;
    }

    private void Refresh()
    {
        int load = ++_load;
        if (_context is not { Player: not null } context)
        {
            return;
        }

        if (OtherSteamId64 is not { } other || _services.Rows is not { } cache)
        {
            Show(context, null, false);
            return;
        }

        // Rows the index has moved past stay on screen while the new ones are read.
        if (cache.TryGet(other) is { } held)
        {
            _rows = held;
            Show(context, held, false);
            Loading = Task.CompletedTask;
            return;
        }

        Show(context, _rows, _rows is null);
        Loading = LoadAsync(cache, other, load);
    }

    private async Task LoadAsync(PlayerRowCache cache, ulong other, int load)
    {
        PlayerRows? rows = null;
        try
        {
            rows = await cache.LoadAsync(other);
        }
        catch (Exception)
        {
            // Stats that will not read show as none; the next refresh tries again.
        }

        if (_load != load || _context is not { } context)
        {
            return;
        }

        _rows = rows ?? _rows;
        Show(context, _rows, false);
    }

    private void Show(PlayerOverviewContext context, PlayerRows? rows, bool reading)
    {
        if (OtherSteamId64 is null)
        {
            Tiles = [];
            Categories = [];
            SubjectScope = OtherScope = "";
            NoteText = "Search a player to compare them with this one over the same filter.";
            return;
        }

        PlayerAggregate subject = PlayerAggregate.Of(context.Window);
        SubjectScope = ScopeOf(subject);
        IReadOnlyList<PlayerDemo> otherWindow = rows is null ? [] : PlayerAggregate.Window(rows.Demos, context.Filter);
        PlayerAggregate other = PlayerAggregate.Of(otherWindow);
        OtherScope = reading ? "reading" : ScopeOf(other);

        bool hasOther = rows is not null && other.WithStats > 0;
        SummaryTileItem[] mine = SummarySectionViewModel.Build(subject, null);
        SummaryTileItem[]? theirs = hasOther ? SummarySectionViewModel.Build(other, null) : null;
        Tiles = [.. mine.Select((t, i) => CompareStatItem.Of(t, theirs?[i]))];

        IReadOnlyList<IReadOnlyDictionary<string, double>> mineRows = StatsOf(context.Window);
        IReadOnlyList<IReadOnlyDictionary<string, double>> theirRows = StatsOf(otherWindow);
        Categories =
        [
            .. CategoriesSectionViewModel.All.Select(c => new CompareCategoryItem(c.Title,
            [
                .. c.Entries.Select(e => CompareStatItem.Of(e, subject, mineRows, hasOther ? other : null, theirRows))
            ]))
        ];

        NoteText = reading
            ? "Reading stats"
            : rows is null
                ? "Their stats did not read."
                : otherWindow.Count == 0
                    ? context.IsFiltered ? "None of their matches passes the filter." : "None of their matches is in this library."
                    : other.WithStats == 0
                        ? "None of their matches in the window has stats yet."
                        : "";
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, double>> StatsOf(IReadOnlyList<PlayerDemo> window) =>
        [.. window.Select(d => d.Stats).OfType<IReadOnlyDictionary<string, double>>()];

    private static string ScopeOf(PlayerAggregate pool) =>
        pool.WithStats == 1 ? "1 match" : string.Create(CultureInfo.InvariantCulture, $"{pool.WithStats} matches");

    private static string NameOf(PlayerEntry entry) =>
        entry.Name.Length > 0 ? DisplayText.Sanitize(entry.Name) : entry.SteamId64.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One category of the comparison.</summary>
/// <param name="Title">"Opening duels".</param>
/// <param name="Stats">Its columns, in order.</param>
public sealed record CompareCategoryItem(string Title, IReadOnlyList<CompareStatItem> Stats);

/// <summary>One stat for both players, formatted.</summary>
/// <param name="Key">The catalogue key or tile id.</param>
/// <param name="Label">The caption.</param>
/// <param name="Tooltip">What the stat measures.</param>
/// <param name="Subject">The subject's value.</param>
/// <param name="Other">The second player's value; an en dash before one is picked or without stats.</param>
/// <param name="Lead">1 when the subject's is the better, -1 when the second player's is, 0 when neither is judged.</param>
public sealed record CompareStatItem(string Key, string Label, string Tooltip, CompareCell Subject, CompareCell Other, int Lead)
{
    private static readonly CompareCell Empty = new("–", null, null, "");

    /// <summary>True when the subject's value is the better.</summary>
    public bool SubjectLeads => Lead > 0;

    /// <summary>True when the second player's value is the better.</summary>
    public bool OtherLeads => Lead < 0;

    // A tile with a band compares by value: every banded tile is a ratio. Counts carry no band and no lead.
    internal static CompareStatItem Of(SummaryTileItem mine, SummaryTileItem? theirs)
    {
        CompareCell subject = new(mine.Text, mine.Value, mine.Scale, "");
        CompareCell other = theirs is null ? Empty : new CompareCell(theirs.Text, theirs.Value, theirs.Scale, "");
        int lead = mine.Scale is { } scale && theirs?.Scale is not null ? LeadOf(mine.Value, theirs.Value, scale.Polarity) : 0;
        return new CompareStatItem(mine.Key, mine.Label, mine.Tooltip, subject, other, lead);
    }

    // A count compares by its rate, which allows for the two samples' sizes; a column without a rate only when banded.
    internal static CompareStatItem Of(CategoriesSectionViewModel.Entry entry, PlayerAggregate mine,
        IReadOnlyList<IReadOnlyDictionary<string, double>> mineRows, PlayerAggregate? theirs,
        IReadOnlyList<IReadOnlyDictionary<string, double>> theirRows)
    {
        CategoryStatItem a = entry.Build(mine, mineRows);
        CategoryStatItem? b = theirs is null ? null : entry.Build(theirs, theirRows);
        CompareCell subject = new(a.Text, a.Value, a.Scale, a.Detail);
        CompareCell other = b is null ? Empty : new CompareCell(b.Text, b.Value, b.Scale, b.Detail);
        int lead = 0;
        if (b is not null)
        {
            StatPolarity? polarity = ColumnCatalogue.Resolve(entry.Key).Scale?.Polarity;
            if (entry.Rate is { } rate && polarity is { } p)
            {
                lead = LeadOf(a.Value is { } x ? rate.Of(x, mineRows) : null, b.Value is { } y ? rate.Of(y, theirRows) : null, p);
            }
            else if (entry.Rate is null && a.Scale is { } scale && b.Scale is not null)
            {
                lead = LeadOf(a.Value, b.Value, scale.Polarity);
            }
        }

        return new CompareStatItem(entry.Key, a.Label, a.Tooltip, subject, other, lead);
    }

    private static int LeadOf(double? mine, double? theirs, StatPolarity polarity)
    {
        if (mine is not { } x || theirs is not { } y || polarity == StatPolarity.Neutral || x.Equals(y))
        {
            return 0;
        }

        return (x > y) == (polarity == StatPolarity.HigherIsBetter) ? 1 : -1;
    }
}

/// <summary>One player's side of a compared stat.</summary>
/// <param name="Text">The value, formatted; an en dash when it does not pool.</param>
/// <param name="Value">The value, for the colour.</param>
/// <param name="Scale">The catalogue's fixed band, or null for a count or too thin a sample.</param>
/// <param name="Detail">The rate beside a count ("0.12 per round"), or empty.</param>
public sealed record CompareCell(string Text, double? Value, StatScale? Scale, string Detail)
{
    /// <summary>True when there is a rate to show.</summary>
    public bool HasDetail => Detail.Length > 0;
}
