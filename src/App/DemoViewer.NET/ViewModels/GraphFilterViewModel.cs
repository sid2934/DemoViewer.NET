#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>
///     Focused sub-VM owning the Analysis-graph filter state: which chains are selected
///     (multi-select chips) and which player is selected (single-select). Raises
///     <see cref="FiltersChanged" /> so the owning <c>AnalysisViewModel</c> re-applies the
///     cheap dim / inert passes (no MSAGL relayout).
///     <para>
///         Chain keys are the literal <c>_chain_{id}</c> form throughout: the same key the
///         chain-summary chips and <c>PerPlayerColumnAssignment.ChainId</c> use, so the joins line up.
///     </para>
/// </summary>
public sealed partial class GraphFilterViewModel : ObservableObject
{
    /// <summary>Sentinel slot for the "All players" option (no player filter).</summary>
    public const int AllPlayersSlot = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilter))]
    private PlayerFilterOption? _selectedPlayer;

    // Suppresses FiltersChanged while a batch mutation (Populate / ResetAll) is in progress, so a
    // single coalesced notification fires instead of one per property.
    private bool _suppressNotify;

    /// <summary>Chain chips, one per chain, in display order.</summary>
    public ObservableCollection<ChainFilterChipViewModel> Chains { get; } = [];

    /// <summary>Player options; index 0 is always the "All players" sentinel.</summary>
    public ObservableCollection<PlayerFilterOption> Players { get; } = [];

    /// <summary>The set of selected chain join-keys (<c>_chain_{id}</c>). Empty = no chain filter.</summary>
    public IReadOnlySet<string> SelectedChainKeys =>
        Chains.Where(c => c.IsSelected).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>True when any chain is selected or a concrete (non-"All") player is chosen.</summary>
    public bool HasActiveFilter =>
        Chains.Any(c => c.IsSelected) || SelectedPlayer is { Slot: >= 0 };

    /// <summary>Raised whenever the filter state changes; the owner re-applies the dim passes.</summary>
    public event Action? FiltersChanged;

    /// <summary>
    ///     Rebuilds the chip + player lists from a fresh analysis result. Clears any prior
    ///     selection (filters don't persist across loads, by design).
    /// </summary>
    /// <param name="chains">(Key=<c>_chain_{id}</c>, Label, Count) for each chain.</param>
    /// <param name="players">(Slot, Name) for each materialized player.</param>
    public void Populate(
        IReadOnlyList<(string Key, string Label, int Count)> chains,
        IReadOnlyList<(int Slot, string Name)> players)
    {
        _suppressNotify = true;
        try
        {
            Clear();

            foreach ((string key, string label, int count) in chains)
            {
                Chains.Add(new ChainFilterChipViewModel(key, label, count));
            }

            Players.Add(new PlayerFilterOption(AllPlayersSlot, "All players"));
            foreach ((int slot, string name) in players.OrderBy(p => p.Slot))
            {
                Players.Add(new PlayerFilterOption(slot, name));
            }

            SelectedPlayer = Players[0];
        }
        finally
        {
            _suppressNotify = false;
        }

        OnPropertyChanged(nameof(HasActiveFilter));
        // Populate establishes the empty-filter baseline; no FiltersChanged needed (the owner
        // applies the cleared state itself right after populating).
    }

    /// <summary>
    ///     Clears all filter state (chips, players, scope map). Used on reset / new demo.
    ///     Does not raise <see cref="FiltersChanged" />: teardown shouldn't trigger an apply
    ///     against stale state; the owner re-applies the cleared baseline after repopulating.
    /// </summary>
    public void Clear()
    {
        _suppressNotify = true;
        try
        {
            Chains.Clear();
            Players.Clear();
            SelectedPlayer = null;
        }
        finally
        {
            _suppressNotify = false;
        }

        OnPropertyChanged(nameof(HasActiveFilter));
    }

    partial void OnSelectedPlayerChanged(PlayerFilterOption? value)
    {
        if (!_suppressNotify)
        {
            FiltersChanged?.Invoke();
        }
    }

    [RelayCommand]
    private void ToggleChain(ChainFilterChipViewModel? chip)
    {
        if (chip is null)
        {
            return;
        }

        chip.IsSelected = !chip.IsSelected;
        OnPropertyChanged(nameof(HasActiveFilter));
        FiltersChanged?.Invoke();
    }

    [RelayCommand]
    private void ClearChains()
    {
        bool any = false;
        foreach (ChainFilterChipViewModel chip in Chains)
        {
            if (chip.IsSelected)
            {
                chip.IsSelected = false;
                any = true;
            }
        }

        if (!any)
        {
            return;
        }

        OnPropertyChanged(nameof(HasActiveFilter));
        FiltersChanged?.Invoke();
    }

    [RelayCommand]
    private void ResetAll()
    {
        _suppressNotify = true;
        try
        {
            foreach (ChainFilterChipViewModel chip in Chains)
            {
                chip.IsSelected = false;
            }

            if (Players.Count > 0)
            {
                SelectedPlayer = Players[0];
            }
        }
        finally
        {
            _suppressNotify = false;
        }

        OnPropertyChanged(nameof(HasActiveFilter));
        FiltersChanged?.Invoke();
    }
}

/// <summary>One chain chip in the filter bar: a selectable, count-badged chain.</summary>
public sealed partial class ChainFilterChipViewModel(string key, string label, int count)
    : ObservableObject
{
    /// <summary>Whether this chip is currently selected (in the active filter).</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The <c>_chain_{id}</c> join-key carried internally (display uses <see cref="Label" />).</summary>
    public string Key { get; } = key;

    /// <summary>Human-readable label shown on the chip.</summary>
    public string Label { get; } = label;

    /// <summary>Event count badge.</summary>
    public int Count { get; } = count;
}

/// <summary>One option in the player single-select picker.</summary>
/// <param name="Slot">Player slot, or <see cref="GraphFilterViewModel.AllPlayersSlot" /> for "All".</param>
/// <param name="Name">Display name.</param>
public sealed record PlayerFilterOption(int Slot, string Name);
