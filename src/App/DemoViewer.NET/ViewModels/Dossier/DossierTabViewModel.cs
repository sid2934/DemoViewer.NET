#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.ViewModels.Dossier;

/// <summary>
///     The Opponent Dossier tab (plan.md §3, Phase 5): a team picker over Team Identity and, today, its
///     one built section, the Map Pool Record — maps played, win rate, side wins, the decider record
///     where a best-of series is inferable, and the optional user-entered veto history filed beside it.
///     Every later Dossier section (setup heatmaps, opening tendencies, post-plant, situational
///     behaviour, the period diff, editing and export) is unblocked by this shell, not built by it.
///     <para>
///         Delegate-injected (the Teams tab's own precedent): the VM owns no clustering and no
///         aggregation of its own; it reads <see cref="TeamIdentityService" /> through
///         <see cref="MapPoolRecordService.Build" /> and re-projects on every <c>Changed</c>.
///     </para>
/// </summary>
public sealed partial class DossierTabViewModel : ViewModelBase, IWorkspaceTabViewModel, IDisposable
{
    private readonly DemoCacheStore _demoCache;
    private readonly TeamIdentityService _teams;
    private readonly VetoHistoryStore _vetoes;
    private bool _disposed;

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
    public DossierTabViewModel(TeamIdentityService teams, DemoCacheStore demoCache, VetoHistoryStore vetoes, bool? isBrowser = null)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(demoCache);
        ArgumentNullException.ThrowIfNull(vetoes);
        _teams = teams;
        _demoCache = demoCache;
        _vetoes = vetoes;
        IsBrowser = isBrowser ?? OperatingSystem.IsBrowser();
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
