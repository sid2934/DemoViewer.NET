#region

using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>Which Strat Book panes were collapsed. The session blob's shape: field names are the JSON member names.</summary>
/// <param name="ListCollapsed">The Strats section's list.</param>
public sealed record StratBookLayoutState(bool ListCollapsed);

/// <summary>
///     The Strats section's collapsible list. The host keeps it in the session file through the Strat Book
///     hub, which restores it before any section is shown.
///     <para>
///         Not module-tab state: module-tab state waits for a demo load and the Strat Book needs no demo.
///     </para>
/// </summary>
public sealed partial class StratBookLayout : ObservableObject, IExtensionSessionState
{
    [ObservableProperty]
    private bool _isListCollapsed;

    [RelayCommand]
    private void ToggleList() => IsListCollapsed = !IsListCollapsed;

    /// <summary>The state the session file keeps.</summary>
    public StratBookLayoutState Snapshot() => new(IsListCollapsed);

    /// <summary>The pack session blob: <see cref="Snapshot" /> as a <c>JsonElement</c>, default STJ naming.</summary>
    public JsonElement SnapshotSessionState() => JsonSerializer.SerializeToElement(Snapshot());

    /// <summary>
    ///     Applies a persisted pack session blob. A missing or wrong-typed member, or a non-object blob, leaves
    ///     the pane as it is. A blob from before the host owned the hub rail also carries its state; it is
    ///     ignored here.
    /// </summary>
    /// <param name="state">The persisted blob.</param>
    public void RestoreSessionState(JsonElement state)
    {
        if (state.ValueKind == JsonValueKind.Object
            && state.TryGetProperty(nameof(StratBookLayoutState.ListCollapsed), out JsonElement list)
            && list.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            IsListCollapsed = list.GetBoolean();
        }
    }

    JsonElement? IExtensionSessionState.Snapshot() => SnapshotSessionState();

    void IExtensionSessionState.Restore(JsonElement state) => RestoreSessionState(state);
}
