#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

#endregion

namespace DemoViewer.NET.ViewModels.Dossier;

/// <summary>
///     A collapsible part of the Dossier: a title with its count, and whether its body shows. The open state
///     outlives a rebuild of the team's numbers, for the session only.
/// </summary>
public abstract partial class DossierSectionViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    private bool _isExpanded;

    /// <param name="key">The section's session key.</param>
    /// <param name="expanded">Whether it starts open.</param>
    protected DossierSectionViewModel(string key, bool expanded)
    {
        Key = key;
        _isExpanded = expanded;
    }

    public string Key { get; }

    /// <summary>▾ open, ▸ closed.</summary>
    public string Glyph => IsExpanded ? "▾" : "▸";

    /// <summary>The title with its count.</summary>
    public abstract string Header { get; }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>A section about the team as a whole: the overall record, the roster and form, the vetoes, the notes.</summary>
public sealed class DossierGeneralSectionViewModel : DossierSectionViewModel
{
    private readonly string _title;
    private string _count = "";

    /// <param name="key">The section's session key.</param>
    /// <param name="title">Its title.</param>
    /// <param name="expanded">Whether it starts open.</param>
    public DossierGeneralSectionViewModel(string key, string title, bool expanded) : base(key, expanded) => _title = title;

    public override string Header => _count.Length == 0 ? _title : $"{_title} · {_count}";

    /// <summary>Sets the count shown after the title; empty shows the title alone.</summary>
    /// <param name="count">The count text.</param>
    public void SetCount(string count)
    {
        if (!string.Equals(_count, count, StringComparison.Ordinal))
        {
            _count = count;
            OnPropertyChanged(nameof(Header));
        }
    }
}

/// <summary>
///     One map's section: its line of the map pool record, its heatmaps, its opening, post-plant and situational
///     blocks, and its findings. A closed section hands its lists out empty, so nothing inside it is built.
/// </summary>
public sealed class DossierMapSectionViewModel : DossierSectionViewModel
{
    private static readonly IReadOnlyList<object> _none = [];

    /// <param name="map">The map.</param>
    /// <param name="expanded">Whether it starts open.</param>
    public DossierMapSectionViewModel(string map, bool expanded) : base("map|" + map, expanded) => Map = map;

    public string Map { get; }

    public MapPoolRowViewModel? Record { get; private set; }

    public IReadOnlyList<SetupHeatmapViewModel> Heatmaps { get; private set; } = [];

    public IReadOnlyList<OpeningBlockViewModel> Openings { get; private set; } = [];

    public IReadOnlyList<PostPlantBlockViewModel> PostPlant { get; private set; } = [];

    public IReadOnlyList<SituationalBlockViewModel> Situational { get; private set; } = [];

    public IReadOnlyList<DossierFindingViewModel> Findings { get; private set; } = [];

    public IReadOnlyList<object> ShownRecord => IsExpanded && Record is not null ? [Record] : _none;

    public IReadOnlyList<SetupHeatmapViewModel> ShownHeatmaps => IsExpanded ? Heatmaps : [];

    public IReadOnlyList<OpeningBlockViewModel> ShownOpenings => IsExpanded ? Openings : [];

    public IReadOnlyList<PostPlantBlockViewModel> ShownPostPlant => IsExpanded ? PostPlant : [];

    public IReadOnlyList<SituationalBlockViewModel> ShownSituational => IsExpanded ? Situational : [];

    public IReadOnlyList<DossierFindingViewModel> ShownFindings => IsExpanded ? Findings : [];

    public bool HasHeatmaps => Heatmaps.Count > 0;

    public bool HasOpenings => Openings.Count > 0;

    public bool HasPostPlant => PostPlant.Count > 0;

    public bool HasSituational => Situational.Count > 0;

    public bool HasFindings => Findings.Count > 0;

    public override string Header
    {
        get
        {
            List<string> parts = [Map];
            if (Record is not null)
            {
                parts.Add(Record.PlayedLabel);
            }

            parts.Add(Count(Findings.Count, "finding"));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Points the section at the tab's current lists for its map.</summary>
    public void Set(MapPoolRowViewModel? record, IReadOnlyList<SetupHeatmapViewModel> heatmaps,
        IReadOnlyList<OpeningBlockViewModel> openings, IReadOnlyList<PostPlantBlockViewModel> postPlant,
        IReadOnlyList<SituationalBlockViewModel> situational, IReadOnlyList<DossierFindingViewModel> findings)
    {
        Record = record;
        Heatmaps = heatmaps;
        Openings = openings;
        PostPlant = postPlant;
        Situational = situational;
        Findings = findings;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Points the section at a new findings list only: a star, a dismiss, a filter.</summary>
    public void SetFindings(IReadOnlyList<DossierFindingViewModel> findings)
    {
        Findings = findings;
        OnPropertyChanged(nameof(Findings));
        OnPropertyChanged(nameof(ShownFindings));
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(Header));
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsExpanded))
        {
            OnPropertyChanged(nameof(ShownRecord));
            OnPropertyChanged(nameof(ShownHeatmaps));
            OnPropertyChanged(nameof(ShownOpenings));
            OnPropertyChanged(nameof(ShownPostPlant));
            OnPropertyChanged(nameof(ShownSituational));
            OnPropertyChanged(nameof(ShownFindings));
        }
    }

    private static string Count(int n, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? "" : "s")}");
}
