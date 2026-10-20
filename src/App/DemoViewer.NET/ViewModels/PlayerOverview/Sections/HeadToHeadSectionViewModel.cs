#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.Players;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>
///     The subject against one opponent the user picks from everyone they ended a demo of the filter's window
///     against: the match record from the subject's <c>player_match</c> rows and the kills each way from the demos'
///     <c>player_event</c> facets, with one line per demo they met in. Both are read off the UI thread; a demo whose
///     facet is not written says so and adds nothing to the counts.
/// </summary>
public sealed partial class HeadToHeadSectionViewModel : PlayerOverviewSection
{
    private const string None = "–";
    private static readonly string EventStamp = StampedFacts.StampId(PlayerEventFacet.RulesetId);

    private readonly PlayerOverviewServices _services;
    private PlayerOverviewContext? _context;
    private int _load;
    // The kills last read and the pair they were read for: a re-read for the same pair shows them meanwhile.
    private (ulong Subject, ulong Opponent)? _pair;
    private IReadOnlyDictionary<string, IReadOnlyList<PlayerKill>> _kills = new Dictionary<string, IReadOnlyList<PlayerKill>>();
    // The facet stamp of each demo the last whole read covered, by content id: a refresh within them reads nothing.
    private Dictionary<string, PackStamp?> _readStamps = new(StringComparer.Ordinal);
    private bool _updating;
    private IReadOnlyList<PlayerMatch> _window = [];

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private string _deathsText = None;

    [ObservableProperty]
    private string _hsText = None;

    [ObservableProperty]
    private string _kdText = None;

    [ObservableProperty]
    private string _killsText = None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string _noteText = "";

    [ObservableProperty]
    private IReadOnlyList<OpponentItem> _opponents = [];

    [ObservableProperty]
    private string _recordText = None;

    [ObservableProperty]
    private IReadOnlyList<HeadToHeadRowItem> _rows = [];

    [ObservableProperty]
    private OpponentItem? _selectedOpponent;

    /// <param name="services">The directory, the subject's facet rows and the kills of each demo.</param>
    public HeadToHeadSectionViewModel(PlayerOverviewServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc />
    public override string Id => "headtohead";

    /// <inheritdoc />
    public override PlayerOverviewPageKind Page => PlayerOverviewPageKind.History;

    /// <inheritdoc />
    public override SectionSlot Slot => SectionSlot.Bottom;

    /// <summary>True when there is a line under the counts.</summary>
    public bool HasNote => NoteText.Length > 0;

    /// <summary>The latest read of the facets; completed when nothing is being read.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public override void Update(PlayerOverviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _window = [.. context.Window.Select(d => d.Match)];
        IReadOnlyList<PlayerOpponent> met = context.Player is { } player ? HeadToHead.Opponents(player.SteamId64, _window) : [];
        if (met.Count == 0)
        {
            _load++;
            IsVisible = false;
            Opponents = [];
            SelectedOpponent = null;
            Rows = [];
            return;
        }

        // The pick survives a refresh while the opponent is still met; otherwise the most often met.
        ulong? keep = SelectedOpponent?.SteamId64;
        List<OpponentItem> items = [.. met.Select(o => new OpponentItem(o))];
        _updating = true;
        try
        {
            Opponents = items;
            SelectedOpponent = items.Find(o => o.SteamId64 == keep) ?? items[0];
        }
        finally
        {
            _updating = false;
        }

        IsVisible = true;
        Refresh();
    }

    partial void OnSelectedOpponentChanged(OpponentItem? value)
    {
        if (!_updating && value is not null && _context is not null)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        int load = ++_load;
        if (_context is not { Player: { } player } context || SelectedOpponent is not { } opponent)
        {
            return;
        }

        IReadOnlyList<PlayerMatch> against = HeadToHead.Against(_window, _services.Directory.Matches(opponent.SteamId64));
        PlayerRows? stats = _services.Rows?.TryGet(player.SteamId64);
        if (_pair != (player.SteamId64, opponent.SteamId64))
        {
            _pair = null;
            _kills = new Dictionary<string, IReadOnlyList<PlayerKill>>();
            _readStamps = new Dictionary<string, PackStamp?>(StringComparer.Ordinal);
        }

        Show(player.SteamId64, opponent.SteamId64, against, stats, _kills, reading: _pair is null);
        if (_pair is not null && (stats is not null || _services.Rows is null) && against.All(IsRead))
        {
            Loading = Task.CompletedTask;
            return;
        }

        Loading = LoadAsync(player.SteamId64, opponent.SteamId64, against, stats, load);
    }

    private bool IsRead(PlayerMatch match) =>
        _readStamps.TryGetValue(match.Row.ContentId(), out PackStamp? stamp) && stamp == match.Row.Stamp(EventStamp);

    private async Task LoadAsync(ulong subject, ulong opponent, IReadOnlyList<PlayerMatch> against, PlayerRows? stats, int load)
    {
        IReadOnlyDictionary<string, IReadOnlyList<PlayerKill>> kills = new Dictionary<string, IReadOnlyList<PlayerKill>>();
        Dictionary<string, PackStamp?> stamps = new(StringComparer.Ordinal);
        foreach (PlayerMatch match in against)
        {
            stamps.TryAdd(match.Row.ContentId(), match.Row.Stamp(EventStamp));
        }

        try
        {
            Task<PlayerRows>? rows = stats is null ? _services.Rows?.LoadAsync(subject) : null;
            if (_services.Events is { } events)
            {
                kills = await events.LoadAsync([.. against.Select(m => m.Row)]);
            }

            if (rows is not null)
            {
                stats = await rows;
            }
        }
        catch (Exception)
        {
            // What did not read shows as not read; the next refresh tries again.
            stamps.Clear();
        }

        if (_load == load)
        {
            _pair = (subject, opponent);
            _kills = kills;
            _readStamps = stamps;
            Show(subject, opponent, against, stats, kills, reading: false);
        }
    }

    private void Show(ulong subject, ulong opponent, IReadOnlyList<PlayerMatch> against, PlayerRows? stats,
        IReadOnlyDictionary<string, IReadOnlyList<PlayerKill>> kills, bool reading)
    {
        Dictionary<string, MatchOutcome?> outcomes = new(StringComparer.Ordinal);
        foreach (PlayerDemo demo in stats?.Demos ?? [])
        {
            outcomes.TryAdd(demo.Match.Row.ContentId(), demo.Outcome);
        }

        HeadToHead result = HeadToHead.Of(subject, opponent, against, m => outcomes.GetValueOrDefault(m.Row.ContentId()), kills);
        CountText = against.Count == 1 ? "1 match against" : string.Create(CultureInfo.InvariantCulture, $"{against.Count} matches against");
        RecordText = result.WithOutcome == 0
            ? None
            : result.Ties > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{result.Wins}-{result.Losses}-{result.Ties}")
                : string.Create(CultureInfo.InvariantCulture, $"{result.Wins}-{result.Losses}");
        bool counted = result.WithKills > 0;
        KillsText = counted ? result.Kills.ToString(CultureInfo.InvariantCulture) : None;
        DeathsText = counted ? result.Deaths.ToString(CultureInfo.InvariantCulture) : None;
        KdText = counted && result.Deaths > 0 ? ((double) result.Kills / result.Deaths).ToString("0.00", CultureInfo.InvariantCulture) : None;
        HsText = counted && result.Kills > 0 ? (100.0 * result.HeadshotKills / result.Kills).ToString("0'%'", CultureInfo.InvariantCulture) : None;
        int unwritten = against.Count(m => !PlayerEventCache.IsWritten(m.Row));
        NoteText = reading
            ? "Reading kills"
            : result.WithKills == against.Count
                ? ""
                : unwritten > 0
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"Kills from {result.WithKills} of {against.Count} matches; the library is computing the rest")
                    : string.Create(CultureInfo.InvariantCulture, $"Kills from {result.WithKills} of {against.Count} matches");
        Rows = [.. result.Matches.Select(m => new HeadToHeadRowItem(m, reading))];
    }
}

/// <summary>One account the subject played against, as the picker lists it.</summary>
public sealed class OpponentItem
{
    internal OpponentItem(PlayerOpponent opponent)
    {
        SteamId64 = opponent.SteamId64;
        string name = opponent.Name.Length > 0 ? DisplayText.Sanitize(opponent.Name) : opponent.SteamId64.ToString(CultureInfo.InvariantCulture);
        Title = opponent.Matches == 1 ? name + " (1 match)" : string.Create(CultureInfo.InvariantCulture, $"{name} ({opponent.Matches} matches)");
    }

    /// <summary>The opponent.</summary>
    public ulong SteamId64 { get; }

    /// <summary>The name and how many demos they met in.</summary>
    public string Title { get; }

    /// <inheritdoc />
    public override string ToString() => Title;
}

/// <summary>One demo the subject and the opponent met in, every cell formatted.</summary>
public sealed class HeadToHeadRowItem
{
    private const string None = "–";

    /// <param name="match">The demo and what happened between them.</param>
    /// <param name="reading">True while the demo's kills may still be read.</param>
    public HeadToHeadRowItem(HeadToHeadMatch match, bool reading)
    {
        Match = match ?? throw new ArgumentNullException(nameof(match));
        DateText = HistoryRowItem.DateOf(match.Match);
        MapText = DemoEntry.PrettifyMap(match.Match.Row.Map);
        IsWin = match.Outcome == MatchOutcome.Win;
        IsLoss = match.Outcome == MatchOutcome.Loss;
        ResultText = match.Outcome switch { MatchOutcome.Win => "W", MatchOutcome.Loss => "L", MatchOutcome.Tie => "T", _ => None };
        HasKills = match.HasKills;
        KillsText = string.Create(CultureInfo.InvariantCulture, $"{match.Kills}-{match.Deaths}");
        KillsNote = reading ? "reading" : PlayerEventCache.IsWritten(match.Match.Row) ? "not read" : "pending";
    }

    /// <summary>The demo and what happened between them.</summary>
    public HeadToHeadMatch Match { get; }

    /// <summary>The match time, local, "yyyy-MM-dd HH:mm".</summary>
    public string DateText { get; }

    /// <summary>The map, prettified.</summary>
    public string MapText { get; }

    /// <summary>"W", "L" or "T" for the subject.</summary>
    public string ResultText { get; }

    /// <summary>True for a win.</summary>
    public bool IsWin { get; }

    /// <summary>True for a loss.</summary>
    public bool IsLoss { get; }

    /// <summary>True when the kills cell holds the demo's counts.</summary>
    public bool HasKills { get; }

    /// <summary>Kills on the opponent and deaths to them, "3-1".</summary>
    public string KillsText { get; }

    /// <summary>Why the kills cell is empty: "pending" for a facet not yet written.</summary>
    public string KillsNote { get; }
}
