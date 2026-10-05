#region

using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;

/// <summary>Which Strat Book panes were collapsed. The session blob's shape: field names are the JSON member names.</summary>
/// <param name="RailCollapsed">The hub's section rail.</param>
/// <param name="ListCollapsed">The Strats section's list.</param>
public sealed record StratBookLayoutState(bool RailCollapsed, bool ListCollapsed);

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

    /// <summary>The pack session blob: <see cref="Snapshot" /> as a <c>JsonElement</c>, default STJ naming.</summary>
    public JsonElement SnapshotSessionState() => JsonSerializer.SerializeToElement(Snapshot());

    /// <summary>
    ///     Applies a persisted pack session blob. Reads each member independently, so a missing member, a
    ///     wrong-typed one, or a non-object blob leaves the corresponding pane as it is instead of discarding
    ///     the rest.
    /// </summary>
    /// <param name="state">The persisted blob.</param>
    public void RestoreSessionState(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (state.TryGetProperty(nameof(StratBookLayoutState.RailCollapsed), out JsonElement rail)
            && rail.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            IsRailCollapsed = rail.GetBoolean();
        }

        if (state.TryGetProperty(nameof(StratBookLayoutState.ListCollapsed), out JsonElement list)
            && list.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            IsListCollapsed = list.GetBoolean();
        }
    }
}
