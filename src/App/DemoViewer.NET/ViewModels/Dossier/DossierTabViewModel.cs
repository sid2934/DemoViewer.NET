#region

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Dossier;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Review;
using DemoViewer.NET.Playback2D.Core.Overlay;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.ViewModels.Dossier;

/// <summary>
///     The Opponent Dossier tab (plan.md §3, Phase 5): a team picker over Team Identity and its built
///     sections. The Map Pool Record: maps played, win rate, side wins, the decider record where a
///     best-of series is inferable, and the optional user-entered veto history filed beside it. The
///     Setup Heatmaps By Buy: one heatmap per map and CT buy with its fixed and rotating places, each of
///     which sends its rounds to the Review Queue. The Opening Tendencies, a section VM of its own
///     (<see cref="OpeningTendenciesSectionViewModel" />) whose every number opens its rounds the same
///     way. The Post-Plant And Retake (<see cref="PostPlantSectionViewModel" />): plant clusters,
///     post-plant holds and retake grouping, every number opening its rounds too. The Situational
///     Behaviour (<see cref="SituationalBehaviourSectionViewModel" />): pistol patterns and their
///     follow-up, anti-eco setups, man-advantage handling and save discipline, over Round Facts alone.
///     The later sections (the period diff, editing and export) are not built here.
///     <para>
///         <b>Heatmaps build on a worker.</b> They read every one of the team's positions files and
///         render one picture per heatmap, so the build runs off the UI thread and posts the rows first
///         and each picture as it lands; a selection change while it runs bumps the generation and the
///         old build posts nothing.
///     </para>
///     <para>
///         Delegate-injected (the Teams tab's own precedent): the VM owns no clustering and no
///         aggregation of its own; it reads <see cref="TeamIdentityService" /> through
///         <see cref="MapPoolRecordService.Build" /> and re-projects on every <c>Changed</c>.
///     </para>
/// </summary>
public sealed partial class DossierTabViewModel : ViewModelBase, IWorkspaceTabViewModel, IDisposable
{
    private readonly DemoCacheStore _demoCache;
    private readonly Func<byte[], Bitmap?> _decode;
    private readonly SetupHeatmapService? _heatmaps;
    private readonly Action<Action> _post;
    private readonly Func<SetupHeatmapRenderer> _renderer;
    private readonly ReviewQueue? _review;
    private readonly Func<string, bool>? _selectTab;
    private readonly TeamIdentityService _teams;
    private readonly VetoHistoryStore _vetoes;
    private bool _disposed;

    // Bumped per heatmap build; a worker whose generation is behind posts nothing.
    private int _generation;

    /// <summary>"12 heatmaps over 80 CT rounds", or why there are none; empty with no team selected.</summary>
    [ObservableProperty]
    private string _heatmapLine = "";

    /// <summary>True while the heatmap worker is reading or rendering.</summary>
    [ObservableProperty]
    private bool _isHeatmapBuilding;

    /// <summary>"12 rounds sent to Review", or why none were; empty until a heatmap is opened.</summary>
    [ObservableProperty]
    private string _reviewLine = "";

    [ObservableProperty]
    private VetoAction _newVetoAction = VetoAction.Ban;

    [ObservableProperty]
    private bool _newVetoByOpponent;

    [ObservableProperty]
    private string _newVetoMap = "";

    [ObservableProperty]
    private DossierTeamRow? _selectedTeam;

    /// <param name="teams">Team Identity, for the team picker and the sides the record is built from.</param>
    /// <param name="demoCache">The cache a demo's map, score and side-round totals come from.</param>
    /// <param name="vetoes">The user's manually entered veto steps, filed per opponent.</param>
    /// <param name="isBrowser">Whether the host is the WASM head; null reads the runtime.</param>
    /// <param name="heatmaps">Builds the Setup Heatmaps By Buy; null hides the section.</param>
    /// <param name="review">The Review Queue a heatmap's rounds are sent to; null says so on open.</param>
    /// <param name="selectTab">Shows a tab by id, for the Review tab after a send; null stays on the Dossier.</param>
    /// <param name="renderer">Builds the heatmap renderer per build; the pipeline's bundle loader when null.</param>
    /// <param name="post">UI-thread marshal for the worker's results; the dispatcher when null.</param>
    /// <param name="decode">PNG bytes to a bitmap; Avalonia's decoder when null, a stub in a test without a platform.</param>
    /// <param name="openings">Builds the Opening Tendencies; null hides the section.</param>
    /// <param name="postPlant">Builds the Post-Plant And Retake; null hides the section.</param>
    /// <param name="situational">Builds the Situational Behaviour; null hides the section.</param>
    public DossierTabViewModel(TeamIdentityService teams, DemoCacheStore demoCache, VetoHistoryStore vetoes, bool? isBrowser = null,
        SetupHeatmapService? heatmaps = null,
        ReviewQueue? review = null,
        Func<string, bool>? selectTab = null,
        Func<SetupHeatmapRenderer>? renderer = null,
        Action<Action>? post = null,
        Func<byte[], Bitmap?>? decode = null,
        OpeningTendenciesService? openings = null,
        PostPlantService? postPlant = null,
        SituationalBehaviourService? situational = null)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(demoCache);
        ArgumentNullException.ThrowIfNull(vetoes);
        _teams = teams;
        _demoCache = demoCache;
        _vetoes = vetoes;
        _heatmaps = heatmaps;
        _review = review;
        _selectTab = selectTab;
        _renderer = renderer ?? (() => new SetupHeatmapRenderer());
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _decode = decode ?? DecodePng;
        IsBrowser = isBrowser ?? OperatingSystem.IsBrowser();
        Openings = new OpeningTendenciesSectionViewModel(openings, review, selectTab, _post);
        PostPlant = new PostPlantSectionViewModel(postPlant, review, selectTab, _post);
        Situational = new SituationalBehaviourSectionViewModel(situational, review, selectTab, _post);
        _teams.Changed += Refresh;
        _vetoes.Changed += ProjectVetoes;
        Refresh();
    }

    /// <summary>The line the panel shows on the browser host: nothing here outlives the tab.</summary>
    public static string BrowserNote => TeamIdentityService.BrowserNote;

    public bool IsBrowser { get; }

    public ObservableCollection<DossierTeamRow> Teams { get; } = [];

    public ObservableCollection<MapPoolRowViewModel> Maps { get; } = [];

    public ObservableCollection<VetoRowViewModel> Vetoes { get; } = [];

    public bool HasTeams => Teams.Count > 0;

    public bool HasSelection => SelectedTeam is not null;

    public bool HasMaps => Maps.Count > 0;

    public bool HasVetoes => Vetoes.Count > 0;

    /// <summary>The selected team's Setup Heatmaps By Buy, in the service's order.</summary>
    public ObservableCollection<SetupHeatmapViewModel> Heatmaps { get; } = [];

    /// <summary>This host builds heatmaps at all.</summary>
    public bool HasHeatmapSection => _heatmaps is not null;

    public bool HasHeatmaps => Heatmaps.Count > 0;

    /// <summary>The selected team's Opening Tendencies.</summary>
    public OpeningTendenciesSectionViewModel Openings { get; }

    /// <summary>The selected team's Post-Plant And Retake.</summary>
    public PostPlantSectionViewModel PostPlant { get; }

    /// <summary>The selected team's Situational Behaviour.</summary>
    public SituationalBehaviourSectionViewModel Situational { get; }

    /// <summary>The running heatmap build; tests await it.</summary>
    internal Task HeatmapTask { get; private set; } = Task.CompletedTask;

    /// <summary>The two veto step kinds, for the add-row picker.</summary>
    public static IReadOnlyList<VetoAction> VetoActionOptions { get; } = Enum.GetValues<VetoAction>();

    /// <summary>"18 demos" — the section's own overall sample size, or the empty-state line.</summary>
    public string SampleSizeLine { get; private set; } = "";

    /// <summary>"Deciders: 3-1 (4)" or "" when nothing is inferable.</summary>
    public string DeciderLine { get; private set; } = "";

    public bool HasDeciderData => DeciderLine.Length > 0;

    public bool VetoesAreSessionOnly => _vetoes.IsSessionOnly;

    /// <inheritdoc />
    public void OnActivated(IModuleContext context) => Refresh();

    /// <inheritdoc />
    public void OnDeactivated()
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _generation);
        Openings.Dispose();
        PostPlant.Dispose();
        Situational.Dispose();
        _teams.Changed -= Refresh;
        _vetoes.Changed -= ProjectVetoes;
    }

    partial void OnSelectedTeamChanged(DossierTeamRow? value)
    {
        NewVetoMap = "";
        Project();
        OnPropertyChanged(nameof(HasSelection));
    }

    [RelayCommand]
    private void AddVeto()
    {
        if (SelectedTeam is not { } team || string.IsNullOrWhiteSpace(NewVetoMap))
        {
            return;
        }

        _vetoes.Add(new VetoEntry
        {
            OpponentTeamId = team.Id,
            Order = Vetoes.Count + 1,
            Map = NewVetoMap.Trim(),
            Action = NewVetoAction,
            ByOpponent = NewVetoByOpponent
        });
        NewVetoMap = "";
    }

    [RelayCommand]
    private void RemoveVeto(VetoRowViewModel? row)
    {
        if (row is not null)
        {
            _vetoes.Remove(row.Id);
        }
    }

    private void Refresh()
    {
        Guid? keep = SelectedTeam?.Id;
        Teams.Clear();
        foreach (Team team in _teams.Teams)
        {
            Teams.Add(new DossierTeamRow(team));
        }

        OnPropertyChanged(nameof(HasTeams));
        SelectedTeam = keep is { } id ? Teams.FirstOrDefault(t => t.Id == id) : null;
        if (keep is not null && SelectedTeam is null)
        {
            Project();
        }
    }

    private void Project()
    {
        Maps.Clear();
        if (SelectedTeam is not { } row)
        {
            SampleSizeLine = "";
            DeciderLine = "";
            OnPropertyChanged(nameof(HasMaps));
            OnPropertyChanged(nameof(SampleSizeLine));
            OnPropertyChanged(nameof(DeciderLine));
            OnPropertyChanged(nameof(HasDeciderData));
            ProjectVetoes();
            BuildHeatmaps();
            Openings.Load(null, "");
            PostPlant.Load(null, "");
            Situational.Load(null, "");
            return;
        }

        MapPoolRecord record = MapPoolRecordService.Build(_teams, _demoCache, row.Id);
        foreach (MapPoolMapRow map in record.Maps)
        {
            Maps.Add(new MapPoolRowViewModel(map));
        }

        SampleSizeLine = record.TotalDemos == 0
            ? "no demos yet: this team has no side assigned in any indexed demo"
            : $"{record.TotalDemos} demo{(record.TotalDemos == 1 ? "" : "s")} across {record.Maps.Count} map{(record.Maps.Count == 1 ? "" : "s")}";
        DeciderLine = record.Deciders.Played == 0
            ? ""
            : $"Deciders: {record.Deciders.Wins}-{record.Deciders.Losses} ({record.Deciders.Played} inferable)";

        OnPropertyChanged(nameof(HasMaps));
        OnPropertyChanged(nameof(SampleSizeLine));
        OnPropertyChanged(nameof(DeciderLine));
        OnPropertyChanged(nameof(HasDeciderData));
        ProjectVetoes();
        BuildHeatmaps();
        Openings.Load(row.Id, row.Name);
        PostPlant.Load(row.Id, row.Name);
        Situational.Load(row.Id, row.Name);
    }

    /// <summary>The Review Queue clip for one of a heatmap's rounds: freeze end to the setup window's end plus the tail.</summary>
    /// <param name="heatmap">The heatmap.</param>
    /// <param name="round">One of its rounds.</param>
    public static ReviewEntry ReviewClipFor(SetupHeatmap heatmap, SetupHeatmapRound round)
    {
        ArgumentNullException.ThrowIfNull(heatmap);
        ArgumentNullException.ThrowIfNull(round);
        return ReviewEntry.Clip(round.DemoPath, round.FreezeEndTick, round.ClipEndTick,
            $"{heatmap.Map} · {SetupHeatmapViewModel.BuyLabelFor(heatmap.Buy)} · round {round.RoundNumber}",
            ReviewSources.Dossier, round.TickRate, round.Sha256);
    }

    /// <summary>The section line for a finished build.</summary>
    /// <param name="set">The build.</param>
    public static string HeatmapLineFor(SetupHeatmapSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Heatmaps.Count == 0)
        {
            return set.DemosRead + set.DemosWithoutPositions == 0
                ? "no CT rounds yet: no demo of this team has round facts"
                : "no CT rounds: Team Identity put this team on CT in no round with facts";
        }

        int rounds = set.Heatmaps.Sum(h => h.Rounds.Count);
        string line = $"{Plural(set.Heatmaps.Count, "heatmap")} over {Plural(rounds, "CT round")}";
        return set.DemosWithoutPositions == 0
            ? line
            : $"{line} · {Plural(set.DemosWithoutPositions, "demo")} without positions; rebuild the index";
    }

    /// <summary>
    ///     Sends a heatmap's rounds to the Review Queue under one title card naming the team, the map and
    ///     the buy, then shows the Review tab. Rounds already queued are skipped.
    /// </summary>
    /// <param name="heatmap">The heatmap clicked.</param>
    [RelayCommand]
    private void OpenRounds(SetupHeatmapViewModel? heatmap)
    {
        if (heatmap is null || heatmap.Heatmap.Rounds.Count == 0)
        {
            return;
        }

        if (_review is null)
        {
            ReviewLine = "no Review Queue on this host";
            return;
        }

        SetupHeatmap model = heatmap.Heatmap;
        string team = SelectedTeam?.Name ?? "";
        int added = _review.Add(model.Rounds.Select(r => ReviewClipFor(model, r)),
            $"Dossier · {team} · {model.Map} {heatmap.BuyLabel}", Plural(model.Rounds.Count, "CT round"));
        ReviewLine = added == 0 ? "already in Review" : $"{Plural(added, "round")} sent to Review";
        _selectTab?.Invoke(ReviewQueueModule.TabId);
    }

    private void BuildHeatmaps()
    {
        int generation = Interlocked.Increment(ref _generation);
        Heatmaps.Clear();
        ReviewLine = "";
        OnPropertyChanged(nameof(HasHeatmaps));
        if (_heatmaps is null || SelectedTeam is not { } row)
        {
            HeatmapLine = "";
            IsHeatmapBuilding = false;
            HeatmapTask = Task.CompletedTask;
            return;
        }

        IsHeatmapBuilding = true;
        HeatmapLine = "reading positions";
        Guid teamId = row.Id;
        HeatmapTask = Task.Run(() => RunHeatmaps(generation, teamId));
    }

    // The worker: one build, the rows posted at once, then one render per heatmap from its own
    // OverlayDocument, each picture posted as it lands.
    private void RunHeatmaps(int generation, Guid teamId)
    {
        SetupHeatmapSet set;
        try
        {
            set = _heatmaps!.Build(teamId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _post(() =>
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    IsHeatmapBuilding = false;
                    HeatmapLine = $"heatmaps unavailable: {ex.Message}";
                }
            });
            return;
        }

        List<SetupHeatmapViewModel> rows = [.. set.Heatmaps.Select(h => new SetupHeatmapViewModel(h))];
        _post(() =>
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            foreach (SetupHeatmapViewModel heatmap in rows)
            {
                Heatmaps.Add(heatmap);
            }

            OnPropertyChanged(nameof(HasHeatmaps));
            HeatmapLine = HeatmapLineFor(set);
        });

        using (SetupHeatmapRenderer renderer = _renderer())
        {
            foreach (SetupHeatmapViewModel heatmap in rows)
            {
                if (generation != Volatile.Read(ref _generation))
                {
                    return;
                }

                byte[]? png = heatmap.Overlay.IsEmpty ? null : renderer.Render(heatmap.Overlay);
                Bitmap? bitmap = png is null ? null : _decode(png);
                _post(() =>
                {
                    if (generation == Volatile.Read(ref _generation))
                    {
                        heatmap.ApplyImage(png, bitmap);
                    }
                });
            }
        }

        _post(() =>
        {
            if (generation == Volatile.Read(ref _generation))
            {
                IsHeatmapBuilding = false;
            }
        });
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}s";

    private static Bitmap? DecodePng(byte[] png)
    {
        try
        {
            using MemoryStream stream = new(png);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ProjectVetoes()
    {
        Vetoes.Clear();
        if (SelectedTeam is { } row)
        {
            foreach (VetoEntry entry in _vetoes.For(row.Id))
            {
                Vetoes.Add(new VetoRowViewModel(entry));
            }
        }

        OnPropertyChanged(nameof(VetoesAreSessionOnly));
        OnPropertyChanged(nameof(HasVetoes));
    }
}

/// <summary>One team in the Dossier's picker.</summary>
public sealed class DossierTeamRow(Team team)
{
    public Guid Id { get; } = team.Id;

    public string Name { get; } = DisplayText.Sanitize(team.Name);

    public bool IsUs { get; } = team.IsUs;
}

/// <summary>One map row of the selected team's Map Pool Record, worded for display.</summary>
public sealed class MapPoolRowViewModel
{
    public MapPoolRowViewModel(MapPoolMapRow row)
    {
        Map = row.Map;
        PlayedLabel = $"{row.Played} played";
        RecordLabel = row.Wins + row.Losses == 0
            ? "no resolved score"
            : $"{row.Wins}-{row.Losses}" + (row.Undetermined > 0 ? $" ({row.Undetermined} undetermined)" : "");
        WinRateLabel = row.WinRate is { } wr
            ? wr.ToString("P0", CultureInfo.InvariantCulture)
            : "—";
        SideLabel = row.HasRoundData
            ? $"CT {row.CtRoundsWon}-{row.TRoundsWon} T ({row.CtRoundShare!.Value:P0} CT)"
            : "no round data";
    }

    public string Map { get; }

    public string PlayedLabel { get; }

    public string RecordLabel { get; }

    public string WinRateLabel { get; }

    public string SideLabel { get; }
}

/// <summary>One user-entered veto step, worded for display.</summary>
public sealed class VetoRowViewModel(VetoEntry entry)
{
    public Guid Id { get; } = entry.Id;

    public int Order { get; } = entry.Order;

    public string Map { get; } = entry.Map;

    public string ActionLabel { get; } = entry.Action == VetoAction.Ban ? "Ban" : "Pick";

    public string ByLabel { get; } = entry.ByOpponent ? "them" : "us";

    public string Note { get; } = entry.Note;
}

/// <summary>
///     One Setup Heatmap, worded for display: the map and buy, the sample, the fixed and rotating
///     places, and the picture. The heatmap's points sit in its own <see cref="OverlayDocument" />, the
///     Overlay View's input, which the renderer draws through the Overlay View's own layer.
/// </summary>
public sealed partial class SetupHeatmapViewModel : ObservableObject
{
    /// <summary>The note in place of a picture when no round of the heatmap had a sampled setup.</summary>
    public const string NoPositionsNote = "no positions in the setup window; rebuild the index";

    [ObservableProperty]
    private Bitmap? _image;

    [ObservableProperty]
    private string _imageNote = "";

    public SetupHeatmapViewModel(SetupHeatmap heatmap)
    {
        ArgumentNullException.ThrowIfNull(heatmap);
        Heatmap = heatmap;
        Overlay = new OverlayDocument();
        if (heatmap.Points.Count > 0)
        {
            Overlay.Replace(heatmap.Map, heatmap.Points, heatmap.StateCount);
        }
        else
        {
            _imageNote = NoPositionsNote;
        }

        BuyLabel = BuyLabelFor(heatmap.Buy);
        int rounds = heatmap.Rounds.Count;
        RoundsLabel = $"{rounds} round{(rounds == 1 ? "" : "s")} · {heatmap.SampledRounds} with a setup sampled";
        OpenLabel = $"Open {rounds} round{(rounds == 1 ? "" : "s")}";
        FixedLabel = PlacesLabel("fixed", heatmap.Positions.Where(p => p.IsFixed));
        RotatingLabel = PlacesLabel("rotating", heatmap.Positions.Where(p => !p.IsFixed));
    }

    public SetupHeatmap Heatmap { get; }

    /// <summary>The heatmap's points, the Overlay View's document; empty when no round was sampled.</summary>
    public OverlayDocument Overlay { get; }

    public string Map => Heatmap.Map;

    /// <summary>"full buy".</summary>
    public string BuyLabel { get; }

    /// <summary>"12 rounds · 10 with a setup sampled".</summary>
    public string RoundsLabel { get; }

    /// <summary>"fixed: BombsiteA 9/10, Ramp 7/10", or "fixed: none".</summary>
    public string FixedLabel { get; }

    /// <summary>"rotating: Heaven 3/10", or "rotating: none".</summary>
    public string RotatingLabel { get; }

    /// <summary>"Open 12 rounds".</summary>
    public string OpenLabel { get; }

    /// <summary>The rendered PNG, kept for a test; null until rendered or with nothing to draw.</summary>
    public byte[]? ImagePng { get; private set; }

    public bool HasImage => Image is not null;

    public bool HasImageNote => ImageNote.Length > 0;

    /// <summary>The label for a buy class, lower case the way the fact vocabulary spells it.</summary>
    /// <param name="buy">The buy.</param>
    public static string BuyLabelFor(BuyType buy) => buy switch
    {
        BuyType.Pistol => "pistol",
        BuyType.Eco => "eco",
        BuyType.Semi => "semi buy",
        BuyType.Force => "force buy",
        BuyType.Full => "full buy",
        _ => "unknown buy"
    };

    /// <summary>Lands a rendered picture; a null PNG leaves the note.</summary>
    /// <param name="png">The PNG, or null.</param>
    /// <param name="bitmap">The decoded bitmap, or null.</param>
    public void ApplyImage(byte[]? png, Bitmap? bitmap)
    {
        ImagePng = png;
        Image = bitmap;
        ImageNote = png is null ? NoPositionsNote : "";
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(HasImageNote));
    }

    private static string PlacesLabel(string kind, IEnumerable<SetupPosition> places)
    {
        string[] parts = [.. places.Select(p => $"{p.Place} {p.RoundsHeld}/{p.SampledRounds}")];
        return $"{kind}: {(parts.Length == 0 ? "none" : string.Join(", ", parts))}";
    }
}
