#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Review;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.ViewModels.Dossier;

/// <summary>
///     The Dossier's Situational Behaviour section (plan.md §3, Phase 5): per map and side, the pistol
///     rounds and their follow-up, the rounds played against a low-buy opponent, the rounds where the
///     team held an alive-count edge and whether it closed them out, and what it bought after a loss.
///     Every number is a <see cref="TendencyLinkViewModel" /> whose click sends its rounds to the Review
///     Queue under one title card and shows the Review tab, the same way the other Dossier sections do.
///     <para>
///         <b>Builds on a worker.</b> The build reads only the demo cache's Round Facts rows, so it is
///         quick, but it still runs off the UI thread for the same reason the other sections do: a new
///         selection bumps the generation and the old build posts nothing.
///     </para>
/// </summary>
public sealed partial class SituationalBehaviourSectionViewModel : ObservableObject, IDisposable
{
    private readonly Action<Action> _post;
    private readonly ReviewQueue? _review;
    private readonly Func<string, bool>? _selectTab;
    private readonly SituationalBehaviourService? _service;

    // Bumped per build; a worker whose generation is behind posts nothing.
    private int _generation;

    [ObservableProperty]
    private bool _isBuilding;

    /// <summary>"9 pistols over 2 maps", or why there are none; empty with no team selected.</summary>
    [ObservableProperty]
    private string _line = "";

    /// <summary>"6 rounds sent to Review", or why none were; empty until a number is opened.</summary>
    [ObservableProperty]
    private string _reviewLine = "";

    private string _teamName = "";

    /// <param name="service">Builds the section; null hides it.</param>
    /// <param name="review">The Review Queue a number's rounds are sent to; null says so on open.</param>
    /// <param name="selectTab">Shows a tab by id, for the Review tab after a send; null stays on the Dossier.</param>
    /// <param name="post">UI-thread marshal for the worker's result.</param>
    public SituationalBehaviourSectionViewModel(SituationalBehaviourService? service, ReviewQueue? review,
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
    public ObservableCollection<SituationalBlockViewModel> Blocks { get; } = [];

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
        Line = "reading rounds";
        BuildTask = Task.Run(() => Run(generation, id));
    }

    /// <summary>The section line for a finished build.</summary>
    /// <param name="set">The build.</param>
    public static string LineFor(SituationalBehaviourSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Blocks.Count == 0)
        {
            return set.DemosRead == 0
                ? "no rounds yet: no demo of this team has round facts"
                : "no rounds: Team Identity put this team on a side in no round with facts";
        }

        int pistols = set.Blocks.Sum(b => b.PistolRounds.Count);
        int maps = set.Blocks.Select(b => b.Map).Distinct(StringComparer.Ordinal).Count();
        return $"{OpeningTendenciesSectionViewModel.Plural(pistols, "pistol")} over {OpeningTendenciesSectionViewModel.Plural(maps, "map")}";
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

        int added = _review.Add(link.Bucket.Rounds.Select(r => OpeningTendenciesSectionViewModel.ReviewClipFor(link, r)),
            $"Dossier · {_teamName} · {link.Title}", OpeningTendenciesSectionViewModel.Plural(link.Count, "round"));
        ReviewLine = added == 0 ? "already in Review" : $"{OpeningTendenciesSectionViewModel.Plural(added, "round")} sent to Review";
        _selectTab?.Invoke(ReviewQueueModule.TabId);
    }

    private void Run(int generation, Guid teamId)
    {
        SituationalBehaviourSet set;
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
                    Line = $"situational behaviour unavailable: {ex.Message}";
                }
            });
            return;
        }

        List<SituationalBlockViewModel> blocks = [.. set.Blocks.Select(b => new SituationalBlockViewModel(b))];
        _post(() =>
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            foreach (SituationalBlockViewModel block in blocks)
            {
                Blocks.Add(block);
            }

            OnPropertyChanged(nameof(HasBlocks));
            Line = LineFor(set);
            IsBuilding = false;
        });
    }
}

/// <summary>One map and side of the Situational Behaviour section, worded for display.</summary>
public sealed class SituationalBlockViewModel
{
    public SituationalBlockViewModel(SituationalBehaviourBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        Block = block;
        SideLabel = block.Side == 3 ? "CT" : "T";
        string prefix = $"{block.Map} {SideLabel}";
        RoundsLink = new TendencyLinkViewModel(block.SideRounds, $"{prefix} · rounds");
        PistolLink = new TendencyLinkViewModel(block.PistolRounds, $"{prefix} · pistols");
        PistolOutcomes = Links(block.PistolOutcomes, $"{prefix} · pistol");
        PistolFollowUp = Links(block.PistolFollowUp, prefix);
        AntiEcoLink = new TendencyLinkViewModel(block.AntiEcoRounds, $"{prefix} · vs low buy");
        AntiEcoBuyTypes = Links(block.AntiEcoBuyTypes, $"{prefix} · vs");
        AntiEcoOutcomes = Links(block.AntiEcoOutcomes, $"{prefix} · vs low buy");
        AntiEcoContactClock = Bars(block.AntiEcoContactClock, $"{prefix} · vs low buy, first contact");
        ManAdvantageLink = new TendencyLinkViewModel(block.ManAdvantageRounds, $"{prefix} · man advantage");
        ManAdvantage = Links(block.ManAdvantage, $"{prefix} · led");
        ManAdvantageOutcomes = Links(block.ManAdvantageOutcomes, $"{prefix} · man advantage");
        RoundsLostLink = new TendencyLinkViewModel(block.RoundsLost, $"{prefix} · losses");
        SaveDiscipline = Links(block.SaveDiscipline, $"{prefix} · after a loss,");
    }

    public SituationalBehaviourBlock Block { get; }

    public string Map => Block.Map;

    /// <summary>"T" or "CT".</summary>
    public string SideLabel { get; }

    /// <summary>Every round of the block on this side: the denominator, itself a link.</summary>
    public TendencyLinkViewModel RoundsLink { get; }

    /// <summary>The pistol rounds: itself a link.</summary>
    public TendencyLinkViewModel PistolLink { get; }

    public IReadOnlyList<TendencyLinkViewModel> PistolOutcomes { get; }

    public IReadOnlyList<TendencyLinkViewModel> PistolFollowUp { get; }

    public bool HasPistols => Block.PistolRounds.Count > 0;

    /// <summary>The rounds against a low-buy opponent: itself a link.</summary>
    public TendencyLinkViewModel AntiEcoLink { get; }

    public IReadOnlyList<TendencyLinkViewModel> AntiEcoBuyTypes { get; }

    public IReadOnlyList<TendencyLinkViewModel> AntiEcoOutcomes { get; }

    public IReadOnlyList<TendencyLinkViewModel> AntiEcoContactClock { get; }

    public bool HasAntiEco => Block.AntiEcoRounds.Count > 0;

    /// <summary>The man-advantage rounds: itself a link.</summary>
    public TendencyLinkViewModel ManAdvantageLink { get; }

    public IReadOnlyList<TendencyLinkViewModel> ManAdvantage { get; }

    public IReadOnlyList<TendencyLinkViewModel> ManAdvantageOutcomes { get; }

    public bool HasManAdvantage => Block.ManAdvantageRounds.Count > 0;

    /// <summary>The rounds lost: itself a link.</summary>
    public TendencyLinkViewModel RoundsLostLink { get; }

    public IReadOnlyList<TendencyLinkViewModel> SaveDiscipline { get; }

    public bool HasLosses => Block.RoundsLost.Count > 0;

    private static IReadOnlyList<TendencyLinkViewModel> Links(IReadOnlyList<TendencyBucket> buckets, string title) =>
        [.. buckets.Select(b => new TendencyLinkViewModel(b, $"{title} {b.Label}"))];

    // Bars scaled to the tallest in the histogram.
    private static IReadOnlyList<TendencyLinkViewModel> Bars(IReadOnlyList<TendencyBucket> buckets, string title)
    {
        int max = buckets.Select(b => b.Count).DefaultIfEmpty(0).Max();
        return [.. buckets.Select(b => new TendencyLinkViewModel(b, $"{title} {b.Label}", max))];
    }
}
