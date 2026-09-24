#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Provenance;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.ViewModels.Situations;

/// <summary>One choice of a filter field. The base carries the text the ComboBox renders; the typed record carries the value.</summary>
/// <param name="Display">The label.</param>
public abstract record SearchFilterOption(string Display);

/// <summary>One typed choice; the first option of every field is "any", whose value is the type's null.</summary>
/// <param name="Display">The label.</param>
/// <param name="Value">The value the filter applies, or null for any.</param>
public sealed record SearchFilterOption<T>(string Display, T Value) : SearchFilterOption(Display);

/// <summary>
///     One single-select field of the rail: its options, the selected one, and the value that selection
///     means. The first option is always "any"; <see cref="Reset" /> returns to it and
///     <see cref="Replace" /> keeps a selection across a rebuilt option list by value, the way the
///     Library's team filter keeps its team across a rename.
/// </summary>
public sealed class SearchFilterField<T> : ViewModelBase
{
    private SearchFilterOption<T>? _selected;

    /// <param name="label">The field's label on the rail.</param>
    /// <param name="options">The choices, "any" first.</param>
    public SearchFilterField(string label, IEnumerable<SearchFilterOption<T>> options)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(options);
        Label = label;
        foreach (SearchFilterOption<T> option in options)
        {
            Options.Add(option);
        }

        if (Options.Count == 0)
        {
            throw new ArgumentException("a field needs its any option", nameof(options));
        }

        _selected = Options[0];
    }

    /// <summary>The field's label on the rail.</summary>
    public string Label { get; }

    /// <summary>The choices, "any" first.</summary>
    public ObservableCollection<SearchFilterOption<T>> Options { get; } = [];

    /// <summary>The chosen option; the ComboBox writes null while its list is rebuilt, which reads as any.</summary>
    public SearchFilterOption<T>? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(Value));
                OnPropertyChanged(nameof(IsAny));
                Changed?.Invoke();
            }
        }
    }

    /// <summary>The value the selection means; the any option's null when nothing is chosen.</summary>
    public T Value => _selected is { } selected ? selected.Value : Options[0].Value;

    /// <summary>True when the field narrows nothing.</summary>
    public bool IsAny => _selected is null || ReferenceEquals(_selected, Options[0]);

    /// <summary>The selection moved.</summary>
    public event Action? Changed;

    /// <summary>Back to any.</summary>
    public void Reset() => Selected = Options[0];

    /// <summary>
    ///     Rebuilds the choices, keeping the selection when its value is still offered and falling back
    ///     to any when it is not (a team that was hidden, an us team that was unset).
    /// </summary>
    /// <param name="options">The new choices, "any" first.</param>
    public void Replace(IEnumerable<SearchFilterOption<T>> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        T keep = Value;
        List<SearchFilterOption<T>> next = [.. options];
        if (next.Count == 0)
        {
            throw new ArgumentException("a field needs its any option", nameof(options));
        }

        Options.Clear();
        foreach (SearchFilterOption<T> option in next)
        {
            Options.Add(option);
        }

        Selected = next.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, keep)) ?? next[0];
    }
}

/// <summary>
///     The filter rail of the Situations tab (plan §3 Search Filters And Live Count): our side, each
///     side's buy type on its own, the phase, the clock band, the man-count state and the score before
///     the round from Round Facts; the opponent from Team Identity; the date range from the cache
///     row; the source from Demo Provenance Labels. The map is the canvas's own picker, since the query
///     runs over one map by construction, and is not repeated here.
///     <para>
///         Two outputs, one per join. <see cref="ToFacts" /> is the <see cref="RoundFactsFilter" /> the
///         index applies per (demo, round), null when no fact field is set so demos without rows still
///         match. <see cref="ToDemos" /> is the demo set the opponent, date and source fields narrow to,
///         as stable keys for <c>SituationQuery.Demos</c>, null when none of the three is set. Every
///         value is absolute per side, the Round Facts rule; "our side" is the one relative field, and
///         it joins through <see cref="TeamIdentityService.SideAtRound" /> on the us team, so it is
///         offered only while one is set.
///     </para>
///     <para>
///         Both joins run on <see cref="Values" />, the rail's fields as one plain
///         <see cref="SearchFilterValues" />: the same object a watched situation stores and resolves
///         through the same two functions, so a saved filter matches what the rail matched, and
///         <see cref="Apply" /> puts a stored one back on the rail for a re-run.
///     </para>
/// </summary>
public sealed partial class SearchFiltersViewModel : ViewModelBase, IDisposable
{
    /// <summary>The source option for demos that carry no label: <see cref="SearchFilterOptions.Unlabeled" />, spelt once for the rail and a stored filter.</summary>
    public const string Unlabeled = SearchFilterOptions.Unlabeled;

    /// <summary>The line the side field shows while no team is us.</summary>
    public const string NoUsNote = "set a team as us in Teams to filter by our side";

    private static readonly SearchFilterOption<int?> _anySide = new("Any side", null);
    private static readonly SearchFilterOption<Guid?> _anyOpponent = new("Any opponent", null);

    private readonly DemoCacheStore _demoCache;
    private readonly IDemoProvenanceSource? _provenance;
    private readonly TeamIdentityService? _teams;

    private bool _applying;
    private bool _disposed;

    /// <summary>The earliest demo date kept, inclusive; null for no lower bound.</summary>
    [ObservableProperty]
    private DateTime? _from;

    /// <summary>The latest demo date kept, inclusive; null for no upper bound.</summary>
    [ObservableProperty]
    private DateTime? _to;

    /// <param name="demoCache">The index rows the date and source fields read.</param>
    /// <param name="teams">Team Identity, for the opponent list and our side; null offers neither.</param>
    /// <param name="provenance">Demo Provenance Labels, for the source field; null offers no source filter.</param>
    public SearchFiltersViewModel(DemoCacheStore demoCache, TeamIdentityService? teams = null, IDemoProvenanceSource? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        _demoCache = demoCache;
        _teams = teams;
        _provenance = provenance;

        Side = new SearchFilterField<int?>("Our side", [_anySide, new SearchFilterOption<int?>("Us on CT", 3), new SearchFilterOption<int?>("Us on T", 2)]);
        BuyCt = new SearchFilterField<BuyType?>("CT buy", BuyOptions());
        BuyT = new SearchFilterField<BuyType?>("T buy", BuyOptions());
        Phase = new SearchFilterField<RoundPhase?>("Phase",
        [
            new SearchFilterOption<RoundPhase?>("Any phase", null),
            new SearchFilterOption<RoundPhase?>("Opening", RoundPhase.Opening),
            new SearchFilterOption<RoundPhase?>("Mid-round", RoundPhase.MidRound),
            new SearchFilterOption<RoundPhase?>("Post-plant / retake", RoundPhase.PostPlant)
        ]);
        Clock = new SearchFilterField<ClockBand?>("Clock",
        [
            new SearchFilterOption<ClockBand?>("Any time", null),
            new SearchFilterOption<ClockBand?>($"First {RoundFactsSource.EarlyBandSeconds} s", ClockBand.Early),
            new SearchFilterOption<ClockBand?>($"{RoundFactsSource.EarlyBandSeconds} s to {MinutesAndSeconds(RoundFactsSource.LateBandSeconds)}", ClockBand.Middle),
            new SearchFilterOption<ClockBand?>($"After {MinutesAndSeconds(RoundFactsSource.LateBandSeconds)}", ClockBand.Late)
        ]);
        ManCount = new SearchFilterField<ManCountState?>("Man count",
        [
            new SearchFilterOption<ManCountState?>("Any man count", null),
            new SearchFilterOption<ManCountState?>("Even", ManCountState.Even),
            new SearchFilterOption<ManCountState?>("CT up", ManCountState.CtUp),
            new SearchFilterOption<ManCountState?>("T up", ManCountState.TUp)
        ]);
        Score = new SearchFilterField<ScoreSituation?>("Score",
        [
            new SearchFilterOption<ScoreSituation?>("Any score", null),
            new SearchFilterOption<ScoreSituation?>("Tied", ScoreSituation.Tied),
            new SearchFilterOption<ScoreSituation?>("CT leading", ScoreSituation.CtLeading),
            new SearchFilterOption<ScoreSituation?>("T leading", ScoreSituation.TLeading),
            new SearchFilterOption<ScoreSituation?>("Match point", ScoreSituation.MatchPoint)
        ]);
        Opponent = new SearchFilterField<Guid?>("Opponent", OpponentOptions());
        Source = new SearchFilterField<string?>("Source",
        [
            new SearchFilterOption<string?>("Any source", null),
            .. DemoProvenanceLabel.All.Select(label => new SearchFilterOption<string?>(label, label)),
            new SearchFilterOption<string?>(Unlabeled, Unlabeled)
        ]);

        foreach (Action<Action> subscribe in Subscriptions())
        {
            subscribe(OnFieldChanged);
        }

        if (_teams is not null)
        {
            _teams.Changed += OnTeamsChanged;
        }

        if (_provenance is not null)
        {
            _provenance.Changed += OnProvenanceChanged;
        }
    }

    /// <summary>Our side in the round: CT or T, through the us team and <see cref="TeamIdentityService.SideAtRound" />.</summary>
    public SearchFilterField<int?> Side { get; }

    public SearchFilterField<BuyType?> BuyCt { get; }

    public SearchFilterField<BuyType?> BuyT { get; }

    /// <summary>The phase at the matched tick. Post-plant and retake are one interval seen from two sides.</summary>
    public SearchFilterField<RoundPhase?> Phase { get; }

    /// <summary>Seconds since the freeze end at the matched tick, in the three bands.</summary>
    public SearchFilterField<ClockBand?> Clock { get; }

    /// <summary>The relative alive count at the matched tick.</summary>
    public SearchFilterField<ManCountState?> ManCount { get; }

    /// <summary>The score before the round.</summary>
    public SearchFilterField<ScoreSituation?> Score { get; }

    /// <summary>A team the library knows, other than us: demos where our side resolved and they were the other side.</summary>
    public SearchFilterField<Guid?> Opponent { get; }

    /// <summary>The provenance label in force, or <see cref="Unlabeled" />.</summary>
    public SearchFilterField<string?> Source { get; }

    /// <summary>Team Identity is wired, so the opponent field has somewhere to read from.</summary>
    public bool HasTeams => _teams is not null;

    /// <summary>A team is us, so our side can be resolved per round.</summary>
    public bool HasUs => _teams?.Us is not null;

    /// <summary>Teams are wired but none is us: the side field is offered, disabled, with <see cref="NoUsNote" /> under it.</summary>
    public bool ShowNoUsNote => HasTeams && !HasUs;

    /// <summary>Demo Provenance Labels is wired, so the source field has somewhere to read from.</summary>
    public bool HasProvenance => _provenance is not null;

    /// <summary>Any field narrows something.</summary>
    public bool IsActive => HasFactFilter || HasDemoFilter;

    /// <summary>A field that reads the Round Facts rows is set.</summary>
    public bool HasFactFilter =>
        !Side.IsAny || !BuyCt.IsAny || !BuyT.IsAny || !Phase.IsAny || !Clock.IsAny || !ManCount.IsAny || !Score.IsAny;

    /// <summary>A field that narrows the demo set is set.</summary>
    public bool HasDemoFilter => !Opponent.IsAny || !Source.IsAny || From is not null || To is not null;

    /// <summary>"CT buy full · post-plant · vs Falcons", or empty when nothing is set.</summary>
    public string ActiveLine => string.Join(" · ", ActiveParts());

    /// <summary>Any field moved, or the lists behind the team and source fields were rebuilt.</summary>
    public event Action? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_teams is not null)
        {
            _teams.Changed -= OnTeamsChanged;
        }

        if (_provenance is not null)
        {
            _provenance.Changed -= OnProvenanceChanged;
        }
    }

    /// <summary>The rail's fields as one plain object, read fresh on every call.</summary>
    public SearchFilterValues Values => new()
    {
        Side = Side.Value,
        BuyCt = BuyCt.Value,
        BuyT = BuyT.Value,
        Phase = Phase.Value,
        Clock = Clock.Value,
        ManCount = ManCount.Value,
        Score = Score.Value,
        Opponent = Opponent.Value,
        Source = Source.Value,
        From = From,
        To = To
    };

    /// <summary>
    ///     The Round Facts filter the fact fields amount to, or null when none is set. Our side becomes
    ///     the filter's <see cref="RoundFactsFilter.Where" />, joined per round through the us team; the
    ///     rest map one to one.
    /// </summary>
    public RoundFactsFilter? ToFacts() => Values.ToFacts(_teams);

    /// <summary>
    ///     The demos the opponent, date and source fields keep, as stable keys, or null when none of the
    ///     three is set. The three intersect: a demo must pass every set field.
    /// </summary>
    public IReadOnlySet<string>? ToDemos() => Values.ToDemos(_demoCache, _teams, _provenance);

    /// <summary>
    ///     Puts stored values back on the rail, one <see cref="Changed" /> for the lot. A value the rail
    ///     no longer offers (an opponent that was merged away, a side with no us team) falls back to
    ///     any, the way <see cref="SearchFilterField{T}.Replace" /> keeps a selection.
    /// </summary>
    /// <param name="values">The values to show.</param>
    public void Apply(SearchFilterValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _applying = true;
        try
        {
            Select(Side, values.Side);
            Select(BuyCt, values.BuyCt);
            Select(BuyT, values.BuyT);
            Select(Phase, values.Phase);
            Select(Clock, values.Clock);
            Select(ManCount, values.ManCount);
            Select(Score, values.Score);
            Select(Opponent, values.Opponent);
            Select(Source, values.Source);
            From = values.From;
            To = values.To;
        }
        finally
        {
            _applying = false;
        }

        OnFieldChanged();
    }

    private static void Select<T>(SearchFilterField<T> field, T value) =>
        field.Selected = field.Options.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, value)) ?? field.Options[0];

    /// <summary>Every field back to any.</summary>
    [RelayCommand]
    public void Clear()
    {
        Side.Reset();
        BuyCt.Reset();
        BuyT.Reset();
        Phase.Reset();
        Clock.Reset();
        ManCount.Reset();
        Score.Reset();
        Opponent.Reset();
        Source.Reset();
        From = null;
        To = null;
    }

    partial void OnFromChanged(DateTime? value) => OnFieldChanged();

    partial void OnToChanged(DateTime? value) => OnFieldChanged();

    private static string MinutesAndSeconds(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

    private static IEnumerable<SearchFilterOption<BuyType?>> BuyOptions()
    {
        yield return new SearchFilterOption<BuyType?>("Any buy", null);
        foreach (BuyType buy in new[] { BuyType.Pistol, BuyType.Eco, BuyType.Semi, BuyType.Force, BuyType.Full })
        {
            yield return new SearchFilterOption<BuyType?>(RoundFactsValues.LowerCamel(buy), buy);
        }
    }

    // Names are sanitized here, at the render boundary, the Library's rule for a team's name.
    private IEnumerable<SearchFilterOption<Guid?>> OpponentOptions()
    {
        yield return _anyOpponent;
        if (_teams is null)
        {
            yield break;
        }

        foreach (Team team in _teams.Teams.Where(t => !t.IsUs))
        {
            yield return new SearchFilterOption<Guid?>(DisplayText.Sanitize(team.Name), team.Id);
        }
    }

    private IEnumerable<Action<Action>> Subscriptions()
    {
        yield return h => Side.Changed += h;
        yield return h => BuyCt.Changed += h;
        yield return h => BuyT.Changed += h;
        yield return h => Phase.Changed += h;
        yield return h => Clock.Changed += h;
        yield return h => ManCount.Changed += h;
        yield return h => Score.Changed += h;
        yield return h => Opponent.Changed += h;
        yield return h => Source.Changed += h;
    }

    private IEnumerable<string> ActiveParts()
    {
        if (!Side.IsAny)
        {
            yield return Side.Selected!.Display;
        }

        if (!BuyCt.IsAny)
        {
            yield return $"CT buy {BuyCt.Selected!.Display}";
        }

        if (!BuyT.IsAny)
        {
            yield return $"T buy {BuyT.Selected!.Display}";
        }

        if (!Phase.IsAny)
        {
            yield return Phase.Selected!.Display;
        }

        if (!Clock.IsAny)
        {
            yield return Clock.Selected!.Display;
        }

        if (!ManCount.IsAny)
        {
            yield return ManCount.Selected!.Display;
        }

        if (!Score.IsAny)
        {
            yield return Score.Selected!.Display;
        }

        if (!Opponent.IsAny)
        {
            yield return $"vs {Opponent.Selected!.Display}";
        }

        if (From is { } from)
        {
            yield return $"from {from:yyyy-MM-dd}";
        }

        if (To is { } to)
        {
            yield return $"to {to:yyyy-MM-dd}";
        }

        if (!Source.IsAny)
        {
            yield return Source.Selected!.Display;
        }
    }

    private void OnFieldChanged()
    {
        if (_applying)
        {
            return; // Apply raises once for the lot
        }

        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(HasFactFilter));
        OnPropertyChanged(nameof(HasDemoFilter));
        OnPropertyChanged(nameof(ActiveLine));
        Changed?.Invoke();
    }

    // A rename, merge or hide rebuilds the opponent list; an unset us team takes the side field with it.
    private void OnTeamsChanged()
    {
        Opponent.Replace(OpponentOptions());
        if (!HasUs && !Side.IsAny)
        {
            Side.Reset();
        }

        OnPropertyChanged(nameof(HasUs));
        OnPropertyChanged(nameof(ShowNoUsNote));
        OnFieldChanged();
    }

    // A pin or a re-derived default moves demos between labels; the set is recomputed on the next read.
    private void OnProvenanceChanged()
    {
        if (!Source.IsAny)
        {
            OnFieldChanged();
        }
    }
}
