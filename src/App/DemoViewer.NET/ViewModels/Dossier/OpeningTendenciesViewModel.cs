#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Review;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.ViewModels.Dossier;

/// <summary>
///     The Dossier's Opening Tendencies section (plan.md §3, Phase 5): per map and side, the first
///     utility's clock histogram and landing places, the first-contact histogram, and on T the site
///     split, the entry player by site and the lurk timing. Every number is a
///     <see cref="TendencyLinkViewModel" /> whose click sends its rounds to the Review Queue under one
///     title card and shows the Review tab.
///     <para>
///         <b>Builds on a worker.</b> The build reads every positions file of the team, so it runs off the
///         UI thread and posts its blocks when done; a new selection bumps the generation and the old build
///         posts nothing.
///     </para>
/// </summary>
public sealed partial class OpeningTendenciesSectionViewModel : ObservableObject, IDisposable
{
    private readonly Action<Action> _post;
    private readonly ReviewQueue? _review;
    private readonly Func<string, bool>? _selectTab;
    private readonly OpeningTendenciesService? _service;

    // Bumped per build; a worker whose generation is behind posts nothing.
    private int _generation;

    [ObservableProperty]
    private bool _isBuilding;

    /// <summary>"40 rounds over 2 maps", or why there are none; empty with no team selected.</summary>
    [ObservableProperty]
    private string _line = "";

    /// <summary>"6 rounds sent to Review", or why none were; empty until a number is opened.</summary>
    [ObservableProperty]
    private string _reviewLine = "";

    private string _teamName = "";

    /// <param name="service">Builds the tendencies; null hides the section.</param>
    /// <param name="review">The Review Queue a number's rounds are sent to; null says so on open.</param>
    /// <param name="selectTab">Shows a tab by id, for the Review tab after a send; null stays on the Dossier.</param>
    /// <param name="post">UI-thread marshal for the worker's result.</param>
    public OpeningTendenciesSectionViewModel(OpeningTendenciesService? service, ReviewQueue? review,
        Func<string, bool>? selectTab, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        _service = service;
        _review = review;
        _selectTab = selectTab;
        _post = post;
    }

    /// <summary>This host builds the section at all.</summary>
    public bool HasSection => _service is not null;

    /// <summary>One block per map and side, in the service's order.</summary>
    public ObservableCollection<OpeningBlockViewModel> Blocks { get; } = [];

    public bool HasBlocks => Blocks.Count > 0;

    /// <summary>The running build; tests await it.</summary>
    internal Task BuildTask { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => Interlocked.Increment(ref _generation);

    /// <summary>Rebuilds for a team, or clears the section when null.</summary>
    /// <param name="teamId">The team, or null.</param>
    /// <param name="teamName">Its name, for the Review title card.</param>
    public void Load(Guid? teamId, string teamName)
    {
        int generation = Interlocked.Increment(ref _generation);
        Blocks.Clear();
        ReviewLine = "";
        OnPropertyChanged(nameof(HasBlocks));
        _teamName = teamName;
        if (_service is null || teamId is not { } id)
        {
            Line = "";
            IsBuilding = false;
            BuildTask = Task.CompletedTask;
            return;
        }

        IsBuilding = true;
        Line = "reading rounds and grenades";
        BuildTask = Task.Run(() => Run(generation, id));
    }

    /// <summary>The section line for a finished build.</summary>
    /// <param name="set">The build.</param>
    public static string LineFor(OpeningTendenciesSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Blocks.Count == 0)
        {
            return set.DemosRead == 0
                ? "no rounds yet: no demo of this team has round facts"
                : "no rounds: Team Identity put this team on a side in no round with facts";
        }

        int rounds = set.Blocks.Sum(b => b.Rounds.Count);
        int maps = set.Blocks.Select(b => b.Map).Distinct(StringComparer.Ordinal).Count();
        List<string> parts = [$"{Plural(rounds, "round")} over {Plural(maps, "map")}"];
        if (set.DemosWithoutGrenades > 0)
        {
            parts.Add($"{Plural(set.DemosWithoutGrenades, "demo")} without grenades; index grenades");
        }

        if (set.DemosWithoutPositions > 0)
        {
            parts.Add($"{Plural(set.DemosWithoutPositions, "demo")} without positions; rebuild the index");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The Review Queue clip for one round behind a number.</summary>
    /// <param name="link">The number.</param>
    /// <param name="round">One of its rounds.</param>
    public static ReviewEntry ReviewClipFor(TendencyLinkViewModel link, TendencyRound round)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(round);
        return ReviewEntry.Clip(round.DemoPath, round.FromTick, round.ToTick,
            $"{link.Title} · round {round.RoundNumber}", ReviewSources.Dossier, round.TickRate, round.Sha256);
    }

    /// <summary>
    ///     Sends a number's rounds to the Review Queue under one title card naming the team and what the
    ///     number counts, then shows the Review tab. Rounds already queued are skipped.
    /// </summary>
    /// <param name="link">The number clicked.</param>
    [RelayCommand]
    private void Open(TendencyLinkViewModel? link)
    {
        if (link is null || link.Count == 0)
        {
            return;
        }

        if (_review is null)
        {
            ReviewLine = "no Review Queue on this host";
            return;
        }

        int added = _review.Add(link.Bucket.Rounds.Select(r => ReviewClipFor(link, r)),
            $"Dossier · {_teamName} · {link.Title}", Plural(link.Count, "round"));
        ReviewLine = added == 0 ? "already in Review" : $"{Plural(added, "round")} sent to Review";
        _selectTab?.Invoke(ReviewQueueModule.TabId);
    }

    private void Run(int generation, Guid teamId)
    {
        OpeningTendenciesSet set;
        try
        {
            set = _service!.Build(teamId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _post(() =>
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    IsBuilding = false;
                    Line = $"opening tendencies unavailable: {ex.Message}";
                }
            });
            return;
        }

        List<OpeningBlockViewModel> blocks = [.. set.Blocks.Select(b => new OpeningBlockViewModel(b))];
        _post(() =>
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            foreach (OpeningBlockViewModel block in blocks)
            {
                Blocks.Add(block);
            }

            OnPropertyChanged(nameof(HasBlocks));
            Line = LineFor(set);
            IsBuilding = false;
        });
    }

    internal static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}s";
}

/// <summary>One map and side of the section, worded for display.</summary>
public sealed class OpeningBlockViewModel
{
    public OpeningBlockViewModel(OpeningTendencies block)
    {
        ArgumentNullException.ThrowIfNull(block);
        Block = block;
        SideLabel = block.Side == 3 ? "CT" : "T";
        string prefix = $"{block.Map} {SideLabel}";
        RoundsLink = new TendencyLinkViewModel(block.Rounds, $"{prefix} · rounds");
        UtilityClock = Bars(block.FirstUtilityClock, $"{prefix} · first utility");
        UtilityPlaces = Links(block.FirstUtilityPlaces, $"{prefix} · first utility");
        ContactClock = Bars(block.FirstContactClock, $"{prefix} · first contact");
        SiteSplit = Links(block.SiteSplit, $"{prefix} · site");
        Entries = Links(block.EntryBySite, $"{prefix} · entry");
        LurkClock = Bars(block.LurkClock, $"{prefix} · lurk");
        Lurkers = Links(block.Lurkers, $"{prefix} · lurker");
        UtilityNote = block.UtilityRounds == 0
            ? "first utility: no grenades indexed for these rounds"
            : $"first utility over {OpeningTendenciesSectionViewModel.Plural(block.UtilityRounds, "round")}";
        LurkNote = block.LurkRounds == 0
            ? "lurk: no positions for these rounds"
            : $"lurk over {OpeningTendenciesSectionViewModel.Plural(block.LurkRounds, "round")}";
    }

    public OpeningTendencies Block { get; }

    public string Map => Block.Map;

    /// <summary>"T" or "CT".</summary>
    public string SideLabel { get; }

    /// <summary>Every round of the block: the sample size, itself a link.</summary>
    public TendencyLinkViewModel RoundsLink { get; }

    public IReadOnlyList<TendencyLinkViewModel> UtilityClock { get; }

    public IReadOnlyList<TendencyLinkViewModel> UtilityPlaces { get; }

    /// <summary>"first utility over 12 rounds", or why there is none.</summary>
    public string UtilityNote { get; }

    public IReadOnlyList<TendencyLinkViewModel> ContactClock { get; }

    public IReadOnlyList<TendencyLinkViewModel> SiteSplit { get; }

    public IReadOnlyList<TendencyLinkViewModel> Entries { get; }

    public IReadOnlyList<TendencyLinkViewModel> LurkClock { get; }

    public IReadOnlyList<TendencyLinkViewModel> Lurkers { get; }

    /// <summary>"lurk over 12 rounds", or why there is none.</summary>
    public string LurkNote { get; }

    public bool HasUtility => UtilityClock.Count > 0;

    public bool IsT => Block.Side == 2;

    private static IReadOnlyList<TendencyLinkViewModel> Links(IReadOnlyList<TendencyBucket> buckets, string title) =>
        [.. buckets.Select(b => new TendencyLinkViewModel(b, $"{title} {b.Label}"))];

    // Bars scaled to the tallest in the histogram.
    private static IReadOnlyList<TendencyLinkViewModel> Bars(IReadOnlyList<TendencyBucket> buckets, string title)
    {
        int max = buckets.Select(b => b.Count).DefaultIfEmpty(0).Max();
        return [.. buckets.Select(b => new TendencyLinkViewModel(b, $"{title} {b.Label}", max))];
    }
}

/// <summary>
///     One number in the section and the rounds behind it. As a histogram bar it also carries a height
///     scaled to the tallest bar of its histogram.
/// </summary>
public sealed class TendencyLinkViewModel
{
    /// <summary>The tallest bar, in pixels.</summary>
    public const double MaxBarHeight = 60;

    /// <param name="bucket">The rounds.</param>
    /// <param name="title">What the number counts, for the Review title card and each clip's note.</param>
    /// <param name="max">The tallest bar's count when this is a bar; 0 when it is not.</param>
    public TendencyLinkViewModel(TendencyBucket bucket, string title, int max = 0)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        Bucket = bucket;
        Title = title;
        BarHeight = max <= 0 || bucket.Count == 0 ? 2 : Math.Max(4, MaxBarHeight * bucket.Count / max);
    }

    public TendencyBucket Bucket { get; }

    public string Label => Bucket.Label;

    public int Count => Bucket.Count;

    public string CountText => Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>"de_nuke T · first utility 10-15 s".</summary>
    public string Title { get; }

    public double BarHeight { get; }

    /// <summary>A zero opens nothing.</summary>
    public bool CanOpen => Count > 0;

    /// <summary>"Open 6 rounds".</summary>
    public string ToolTip => Count == 1 ? "Open 1 round" : $"Open {Count} rounds";
}
