#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Models;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>
///     The Strat Book's two collapsible panes: the hub rail and the Strats section's list. One instance is
///     shared by the hub and the Strats section, and the shell persists it in the session file.
///     <para>
///         Restored with the active tab, not with module-tab state: module-tab state waits for a demo load and
///         the Strat Book needs no demo.
///     </para>
/// </summary>
public sealed partial class StratBookLayout : ObservableObject
{
    [ObservableProperty]
    private bool _isListCollapsed;

    [ObservableProperty]
    private bool _isRailCollapsed;

    [RelayCommand]
    private void ToggleRail() => IsRailCollapsed = !IsRailCollapsed;

    [RelayCommand]
    private void ToggleList() => IsListCollapsed = !IsListCollapsed;

    /// <summary>The state the session file keeps.</summary>
    public StratBookLayoutState Snapshot() => new(IsRailCollapsed, IsListCollapsed);

    /// <summary>Applies a persisted state; null (an older session file) leaves both panes open.</summary>
    /// <param name="state">The persisted state, or null.</param>
    public void Restore(StratBookLayoutState? state)
    {
        if (state is null)
        {
            return;
        }

        IsRailCollapsed = state.RailCollapsed;
        IsListCollapsed = state.ListCollapsed;
    }
}
