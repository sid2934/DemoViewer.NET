#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.MatchMeta;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>
///     The subject's demos in the filter's window, newest match first, one row each: when, where, how it ended
///     and the subject's line from its <c>player_match</c> facet. The rows come from the index at once; the stats
///     arrive when the tab has read the facets through <see cref="PlayerRowCache" />, off the UI thread. A demo
///     whose facet is not written says so in its stat cells and never shows zeros.
/// </summary>
public sealed partial class HistorySectionViewModel : PlayerOverviewSection
{
    private static readonly string FacetStamp = StampedFacts.StampId(PlayerMatchFacet.RulesetId);

    private readonly PlayerOverviewServices _services;

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private IReadOnlyList<HistoryRowItem> _rows = [];

    /// <param name="services">The directory that opens demos and the shell's open and focus actions.</param>
    public HistorySectionViewModel(PlayerOverviewServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc />
    public override string Id => "history";

    /// <inheritdoc />
    public override void Update(PlayerOverviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Player is not { } player)
        {
            IsVisible = false;
            Rows = [];
            return;
        }

        bool reading = context.IsReadingStats;
        List<HistoryRowItem> rows = new(context.Window.Count);
        foreach (PlayerDemo demo in context.Window)
        {
            HistoryStats state;
            if (demo.Match.Row.Stamp(FacetStamp) is not { State: DemoAnalysisState.Indexed })
            {
                state = HistoryStats.Pending;
            }
            else if (demo.HasFacet)
            {
                state = demo.Stats is null ? HistoryStats.NoRow : HistoryStats.Ready;
            }
            else
            {
                state = reading ? HistoryStats.Loading : HistoryStats.Unread;
            }

            rows.Add(new HistoryRowItem(demo.Match, player.SteamId64, state, demo.Stats));
        }

        Rows = rows;
        int all = context.Matches.Count;
        CountText = rows.Count != all
            ? string.Create(CultureInfo.InvariantCulture, $"{rows.Count} of {all} matches")
            : all == 1 ? "1 match" : string.Create(CultureInfo.InvariantCulture, $"{all} matches");
        IsVisible = true;
    }

    /// <summary>Opens the row's demo from the first of its paths on disk, or marks the file missing.</summary>
    [RelayCommand]
    private void OpenDemo(HistoryRowItem? row)
    {
        if (row is not null && PathOf(row) is { } path)
        {
            _services.OpenDemo(path);
        }
    }

    /// <summary>Opens the row's demo and shows the subject in Stats, or marks the file missing.</summary>
    [RelayCommand]
    private void FocusInStats(HistoryRowItem? row)
    {
        if (row is not null && PathOf(row) is { } path)
        {
            _services.FocusPlayerInStats(path, row.SteamId64);
        }
    }

    // Checked on click rather than per refresh: a thousand rows would be a thousand file probes on the UI thread.
    private string? PathOf(HistoryRowItem row)
    {
        string? path = _services.Directory.OpenPath(row.Match);
        row.IsFileMissing = path is null;
        return path;
    }
}

/// <summary>What a history row's stat cells can show.</summary>
public enum HistoryStats
{
    /// <summary>The demo's facet is not written yet.</summary>
    Pending,

    /// <summary>The facet is written and being read.</summary>
    Loading,

    /// <summary>The facet is written but nothing reads it here, or the read failed.</summary>
    Unread,

    /// <summary>The facet holds no row the account was active in.</summary>
    NoRow,

    /// <summary>The account's line is read.</summary>
    Ready
}

/// <summary>One demo in the subject's match history, every cell formatted.</summary>
public sealed partial class HistoryRowItem : ObservableObject
{
    private const string None = "–";

    /// <summary>True when <see cref="HistorySectionViewModel.OpenDemoCommand" /> found no path on disk.</summary>
    [ObservableProperty]
    private bool _isFileMissing;

    /// <param name="match">The demo as the directory lists it.</param>
    /// <param name="steamId64">The subject.</param>
    /// <param name="stats">What the stat cells can show.</param>
    /// <param name="values">The subject's merged facet row when <paramref name="stats" /> is <see cref="HistoryStats.Ready" />.</param>
    public HistoryRowItem(PlayerMatch match, ulong steamId64, HistoryStats stats, IReadOnlyDictionary<string, double>? values)
    {
        Match = match ?? throw new ArgumentNullException(nameof(match));
        SteamId64 = steamId64;
        Stats = stats;
        DemoCacheIndexEntry row = match.Row;

        DateText = DateOf(match);
        IsApproximateTime = row.MatchTimeSource is not (MatchTimeSource.UserOverride or MatchTimeSource.ValveMatchInfo or MatchTimeSource.ExternalMetadata);
        TimeChip = IsApproximateTime ? "approx" : "exact";
        TimeTip = row.MatchTimeSource switch
        {
            MatchTimeSource.ValveMatchInfo => "Match time from Valve's match info file",
            MatchTimeSource.UserOverride => "Match time set by you",
            MatchTimeSource.ExternalMetadata => "Match time from external metadata",
            _ => "The file's write time; the match may have been earlier"
        };
        MapText = DemoEntry.PrettifyMap(row.Map);
        ModeText = ModeOf(row);
        SideText = match.Side switch { 3 => "CT", 2 => "T", _ => None };
        RankChangeText = None;

        if (stats != HistoryStats.Ready || values is null)
        {
            StatsNote = stats switch
            {
                HistoryStats.Pending => "stats pending",
                HistoryStats.Loading => "reading stats",
                HistoryStats.NoRow => "no stats for this player",
                _ => "stats not read"
            };
            ScoreText = ResultText = KdaText = AdrText = HsText = KastText = HltvText = None;
            return;
        }

        HasStats = true;
        StatsNote = "";
        int won = Whole(values.GetValueOrDefault("CTW") + values.GetValueOrDefault("TW"));
        int lost = Whole(values.GetValueOrDefault("CTL") + values.GetValueOrDefault("TL"));
        ScoreText = won + lost == 0 ? None : string.Create(CultureInfo.InvariantCulture, $"{won}-{lost}");
        Outcome = PlayerAggregate.OutcomeOf(values);
        ResultText = Outcome switch { MatchOutcome.Win => "W", MatchOutcome.Loss => "L", MatchOutcome.Tie => "T", _ => None };
        KdaText = string.Create(CultureInfo.InvariantCulture,
            $"{Whole(values.GetValueOrDefault("TotalK"))}-{Whole(values.GetValueOrDefault("TotalD"))}-{Whole(values.GetValueOrDefault("TotalA"))}");
        IReadOnlyDictionary<string, double>[] one = [values];
        AdrText = Format(ColumnRules.For("ADR")?.Pool(one), "0.0");
        HsText = Format(ColumnRules.For("HS%")?.Pool(one), "0'%'");
        KastText = Format(ColumnRules.For("KAST%")?.Pool(one), "0.0'%'");
        HltvText = Format(ColumnRules.For("HLTV")?.Pool(one), "0.00");
    }

    /// <summary>The demo.</summary>
    public PlayerMatch Match { get; }

    /// <summary>The subject.</summary>
    public ulong SteamId64 { get; }

    /// <summary>What the stat cells show.</summary>
    public HistoryStats Stats { get; }

    /// <summary>True when the stat cells hold the subject's line.</summary>
    public bool HasStats { get; }

    /// <summary>Why the stat cells are empty ("stats pending" for a facet not yet written), or empty.</summary>
    public string StatsNote { get; }

    /// <summary>The match time, local, "yyyy-MM-dd HH:mm".</summary>
    public string DateText { get; }

    /// <summary>True when the time is the file's write time rather than the match's own.</summary>
    public bool IsApproximateTime { get; }

    /// <summary>"exact" or "approx".</summary>
    public string TimeChip { get; }

    /// <summary>Where the time came from.</summary>
    public string TimeTip { get; }

    /// <summary>True when only the facet has the subject: it ended the demo as a spectator or on an abandoned slot.</summary>
    public bool LeftEarly => Match.LeftEarly;

    /// <summary>True when the side cell shows a side rather than the left-early chip.</summary>
    public bool ShowSide => !Match.LeftEarly;

    /// <summary>The map, prettified.</summary>
    public string MapText { get; }

    /// <summary>The game mode, else where the demo came from.</summary>
    public string ModeText { get; }

    /// <summary>The subject's side's rounds won and lost, "13-9".</summary>
    public string ScoreText { get; }

    /// <summary>Win, loss or tie for the subject; null without stats.</summary>
    public MatchOutcome? Outcome { get; }

    /// <summary>"W", "L" or "T".</summary>
    public string ResultText { get; }

    /// <summary>True for a win.</summary>
    public bool IsWin => Outcome == MatchOutcome.Win;

    /// <summary>True for a loss.</summary>
    public bool IsLoss => Outcome == MatchOutcome.Loss;

    /// <summary>The side the subject ended on, "CT" or "T".</summary>
    public string SideText { get; }

    /// <summary>Kills, deaths and assists, "21-15-4".</summary>
    public string KdaText { get; }

    /// <summary>Damage per round.</summary>
    public string AdrText { get; }

    /// <summary>Headshot kill percentage.</summary>
    public string HsText { get; }

    /// <summary>KAST percentage.</summary>
    public string KastText { get; }

    /// <summary>HLTV 2.0-style rating.</summary>
    public string HltvText { get; }

    /// <summary>The rank change in the match; an en dash until rank capture lands.</summary>
    public string RankChangeText { get; }

    // The match time is UTC; a row the match-time pass has not reached carries the file's local write time.
    private static string DateOf(PlayerMatch match)
    {
        long ticks = match.TimeTicks;
        if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
        {
            return None;
        }

        DateTime time = match.Row.MatchTimeUtcTicks is null
            ? new DateTime(ticks, DateTimeKind.Local)
            : new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
        return time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    internal static string ModeOf(DemoCacheIndexEntry row)
    {
        if (!string.IsNullOrEmpty(row.GameMode))
        {
            return char.ToUpperInvariant(row.GameMode[0]) + row.GameMode[1..];
        }

        return (row.MatchSource ?? row.SourceKind) switch
        {
            null or "" or "Unknown" => None,
            "GotvMatchmaking" => "Matchmaking",
            "Faceit" => "FACEIT",
            { } source => source
        };
    }

    private static int Whole(double value) => (int) Math.Round(value);

    private static string Format(double? value, string format) =>
        value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : None;
}
