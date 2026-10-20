#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Controls.Stats;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>
///     The subject's rate tiles as percentiles among the other players in this library: every account with at
///     least the chosen count of demos with stats under the same filter, optionally only those whose latest
///     CS Rating is in the subject's Premier tier. The line above the rows always names who the subject is
///     compared with. Every account's stats come from <see cref="PlayerRowCache.LoadLibraryAsync" /> and the
///     ratings from <see cref="PlayerRankCache.LoadLibraryAsync" />, both read off the UI thread; the pooling of
///     every account runs off it too.
/// </summary>
public sealed partial class BenchmarksSectionViewModel : PlayerOverviewSection
{
    /// <summary>Fewer players than this give no percentiles: a rank among three says nothing.</summary>
    public const int MinimumPopulation = 5;

    private static readonly (string Key, string Label, string Format)[] Keys =
    [
        (PlayerBenchmark.WinRate, "Win rate", "0'%'"),
        ("HLTV", "Rating", "0.00"),
        ("KD", "K/D", "0.00"),
        ("ADR", "ADR", "0.0"),
        ("KAST%", "KAST", "0.0'%'"),
        ("KPR", "KPR", "0.00"),
        ("HS%", "HS%", "0'%'"),
        ("Duel%", "Opening duels", "0'%'")
    ];

    private readonly PlayerOverviewServices _services;
    private PlayerOverviewContext? _context;
    private int _load;

    // The last population pooled, kept while the library, subject, filter and minimum hold: the band toggle
    // and a refresh that changes none of them pool nothing again.
    private Pooled? _pooled;
    private bool _updating;

    [ObservableProperty]
    private bool _canUseRankBand;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    private string _loadText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string _noteText = "";

    [ObservableProperty]
    private string _populationText = "";

    [ObservableProperty]
    private string _rankBandText = "";

    [ObservableProperty]
    private IReadOnlyList<BenchmarkRowItem> _rows = [];

    [ObservableProperty]
    private int _selectedMinimum = PlayerBenchmark.DefaultMinimumMatches;

    [ObservableProperty]
    private bool _useRankBand;

    /// <param name="services">The library's stats and ranks.</param>
    public BenchmarksSectionViewModel(PlayerOverviewServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc />
    public override string Id => "benchmarks";

    /// <inheritdoc />
    public override PlayerOverviewPageKind Page => PlayerOverviewPageKind.Stats;

    /// <inheritdoc />
    public override SectionSlot Slot => SectionSlot.Rail;

    /// <summary>The minimum match counts the user can pick.</summary>
    public IReadOnlyList<int> Minimums { get; } = [1, 3, 5, 10, 20];

    /// <summary>True while the library's stats or ranks are being read.</summary>
    public bool IsLoading => LoadText.Length > 0;

    /// <summary>True when there is a line under the rows.</summary>
    public bool HasNote => NoteText.Length > 0;

    /// <summary>The latest read and pooling; completed when nothing is running.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public override void Update(PlayerOverviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        if (context.Player is null || _services.Rows is null)
        {
            _load++;
            IsVisible = false;
            Rows = [];
            LoadText = NoteText = PopulationText = "";
            return;
        }

        RankBand? band = SubjectBand(context.Ranks);
        _updating = true;
        try
        {
            CanUseRankBand = band is not null && _services.Ranks is not null;
            RankBandText = band is { } b ? "Only " + b.Label : "Only my Premier tier";
        }
        finally
        {
            _updating = false;
        }

        IsVisible = true;
        Refresh();
    }

    partial void OnSelectedMinimumChanged(int value) => RefreshIfShown();

    partial void OnUseRankBandChanged(bool value) => RefreshIfShown();

    private void RefreshIfShown()
    {
        if (!_updating && _context is { Player: not null })
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        int load = ++_load;
        if (_context is not { Player: { } player } context || _services.Rows is not { } rows)
        {
            return;
        }

        PlayerAggregate subject = PlayerAggregate.Of(context.Window);
        RankBand? band = UseRankBand && CanUseRankBand ? SubjectBand(context.Ranks) : null;
        Request request = new(player.SteamId64, subject, context.Filter, Math.Max(SelectedMinimum, 1), band, context.IsFiltered);
        Loading = RunAsync(rows, request, load);
    }

    private async Task RunAsync(PlayerRowCache rows, Request request, int load)
    {
        LibraryRows? library = rows.TryGetLibrary();
        PlayerRankCache? rankCache = request.Band is null ? null : _services.Ranks;
        LibraryRanks? ranks = rankCache?.TryGetLibrary();
        // Rows already on show stay while a demo the queue finished is pooled in; only a first read says so.
        bool quiet = Rows.Count > 0;
        object gate = new();
        bool reading = !quiet;
        Progress<PlayerRowsProgress> progress = new(p =>
        {
            lock (gate)
            {
                if (reading && _load == load && p.Total > 0)
                {
                    LoadText = string.Create(CultureInfo.InvariantCulture, $"Reading library stats {p.Done} of {p.Total}");
                }
            }
        });
        if (!quiet && (library is null || (rankCache is not null && ranks is null)))
        {
            LoadText = library is null ? "Reading library stats" : "Reading ranks";
        }

        Result? result = null;
        try
        {
            library ??= await rows.LoadLibraryAsync(progress);
            if (rankCache is not null)
            {
                ranks ??= await rankCache.LoadLibraryAsync();
            }

            LibraryRows held = library;
            result = await Task.Run(() => Compute(request, held, ranks));
        }
        catch (Exception)
        {
            // A library that will not read shows nothing; the next refresh tries again.
        }

        lock (gate)
        {
            reading = false;
        }

        if (_load != load)
        {
            return;
        }

        LoadText = "";
        Show(request, result);
    }

    private Result Compute(Request request, LibraryRows library, LibraryRanks? ranks)
    {
        Pooled? pooled = _pooled;
        if (pooled is null
            || !ReferenceEquals(pooled.Library, library)
            || pooled.Subject != request.Subject
            || pooled.Filter != request.Filter
            || pooled.Minimum != request.Minimum)
        {
            pooled = new Pooled(library, request.Subject, request.Filter, request.Minimum,
                PlayerBenchmark.PopulationOf(library, request.Subject, request.Filter, request.Minimum));
            _pooled = pooled;
        }

        List<PlayerAggregate> population = new(pooled.Population.Count);
        foreach ((ulong steamId64, PlayerAggregate pool) in pooled.Population)
        {
            if (request.Band is not { } band || (ranks?.Premier.TryGetValue(steamId64, out int rating) == true && band.Contains(rating)))
            {
                population.Add(pool);
            }
        }

        return new Result(PlayerBenchmark.Of(request.SubjectPool, population, [.. Keys.Select(k => k.Key)]));
    }

    private void Show(Request request, Result? result)
    {
        if (result is null)
        {
            Rows = [];
            PopulationText = "";
            NoteText = "The library's stats did not read.";
            return;
        }

        PlayerBenchmark benchmark = result.Benchmark;
        PopulationText = Describe(benchmark.Population, request);
        if (request.SubjectPool.WithStats == 0)
        {
            Rows = [];
            NoteText = request.IsFiltered ? "No match in the window has stats to compare." : "No match has stats to compare yet.";
        }
        else if (benchmark.Population < MinimumPopulation)
        {
            Rows = [];
            NoteText = string.Create(CultureInfo.InvariantCulture,
                $"Too few players to compare with: percentiles need {MinimumPopulation}. Lower the minimum matches or widen the filter.");
        }
        else
        {
            Dictionary<string, BenchmarkStat> byKey = benchmark.Stats.ToDictionary(s => s.Key, StringComparer.Ordinal);
            Rows = [.. Keys.Where(k => byKey.ContainsKey(k.Key)).Select(k => new BenchmarkRowItem(byKey[k.Key], k.Label, k.Format, benchmark.Population))];
            NoteText = "";
        }
    }

    /// <summary>"vs 214 players in your library · 5+ matches each · Premier 15,000-19,999 · same filter".</summary>
    internal static string Describe(int population, int minimum, RankBand? band, bool filtered)
    {
        string text = population == 1
            ? "vs 1 player in your library"
            : string.Create(CultureInfo.InvariantCulture, $"vs {population:N0} players in your library");
        text += minimum == 1
            ? " · 1+ match each"
            : string.Create(CultureInfo.InvariantCulture, $" · {minimum}+ matches each");
        if (band is { } b)
        {
            text += " · " + b.Label;
        }

        return filtered ? text + " · same filter" : text;
    }

    private static string Describe(int population, Request request) => Describe(population, request.Minimum, request.Band, request.IsFiltered);

    /// <summary>The subject's latest CS Rating's tier, from their newest rated Premier demo; null when there is none.</summary>
    internal static RankBand? SubjectBand(PlayerRanks? ranks)
    {
        foreach (PlayerRank demo in ranks?.Demos ?? [])
        {
            if (demo.Rank is { RankType: PlayerRankCache.PremierRankType } rank && RankDisplay.After(rank) is { } rating)
            {
                return RankBand.For(rating);
            }
        }

        return null;
    }

    private sealed record Request(ulong Subject, PlayerAggregate SubjectPool, PlayerFilter Filter, int Minimum, RankBand? Band, bool IsFiltered);

    private sealed record Pooled(
        LibraryRows Library,
        ulong Subject,
        PlayerFilter Filter,
        int Minimum,
        IReadOnlyList<(ulong SteamId64, PlayerAggregate Pool)> Population);

    private sealed record Result(PlayerBenchmark Benchmark);
}

/// <summary>One benchmarked number, formatted.</summary>
public sealed class BenchmarkRowItem
{
    /// <param name="stat">Where the subject's value sits.</param>
    /// <param name="label">The caption.</param>
    /// <param name="format">How the value and the median are written.</param>
    /// <param name="population">How many players the section compares with, for the tooltip when this key has fewer.</param>
    public BenchmarkRowItem(BenchmarkStat stat, string label, string format, int population)
    {
        Stat = stat ?? throw new ArgumentNullException(nameof(stat));
        Label = label;
        ValueText = stat.Value.ToString(format, CultureInfo.InvariantCulture);
        MedianText = "median " + stat.Median.ToString(format, CultureInfo.InvariantCulture);
        int rank = (int) Math.Round(stat.Percentile, MidpointRounding.AwayFromZero);
        PercentileText = Ordinal(rank);
        StatPolarity polarity = stat.Key == PlayerBenchmark.WinRate
            ? StatPolarity.HigherIsBetter
            : ColumnCatalogue.Resolve(stat.Key).Scale?.Polarity ?? StatPolarity.Neutral;
        Scale = StatScale.Absolute(0, 100, 40, 60, polarity);
        string of = stat.Population == population
            ? string.Create(CultureInfo.InvariantCulture, $"the {population:N0} players compared")
            : string.Create(CultureInfo.InvariantCulture, $"the {stat.Population:N0} of {population:N0} players with enough attempts");
        Tooltip = string.Create(CultureInfo.InvariantCulture,
            $"Higher than {stat.Percentile:0}% of {of}, a tie counting half. Their median is {stat.Median.ToString(format, CultureInfo.InvariantCulture)}.");
    }

    /// <summary>Where the subject's value sits.</summary>
    public BenchmarkStat Stat { get; }

    /// <summary>"Rating".</summary>
    public string Label { get; }

    /// <summary>The subject's value, "1.12".</summary>
    public string ValueText { get; }

    /// <summary>The percentile, 0 to 100, for the bar.</summary>
    public double Percentile => Stat.Percentile;

    /// <summary>The bar's fixed 0-100 domain, coloured by the stat's polarity.</summary>
    public StatScale Scale { get; }

    /// <summary>"73rd".</summary>
    public string PercentileText { get; }

    /// <summary>"median 0.98".</summary>
    public string MedianText { get; }

    /// <summary>What the percentile means and who it counts.</summary>
    public string Tooltip { get; }

    internal static string Ordinal(int n)
    {
        string suffix = (n % 100) is 11 or 12 or 13
            ? "th"
            : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return string.Create(CultureInfo.InvariantCulture, $"{n}{suffix}");
    }
}
