#region

using System.Text.Json;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.ViewModels.Shell;

/// <summary>
///     The view model of a tab that hosts sections instead of a body of its own: a strip tab a pack
///     contributes whose view lists <see cref="Sections" /> on a rail and shows the selected one. The shell
///     builds it when the strip is built, not on first activation, because it reconciles the sections and
///     restores the session through it before the tab is ever selected.
/// </summary>
public interface IHostTabViewModel : IWorkspaceTabViewModel
{
    /// <summary>The hosted sections and the selection. The shell fills and gates the list; the view binds it.</summary>
    TabSectionHost Sections { get; }

    /// <summary>The band over the rail. The shell sets it from the contribution before any view binds it.</summary>
    string RailLabel { get; set; }

    // Distinct from IWorkspaceTabViewModel.SnapshotState/RestoreState (the per-TAB blob keyed by TabId,
    // applied lazily on first activation): this is per-PACK state, keyed by pack id in SessionPayload.Packs
    // and restored before any tab activates, through every host regardless of which one is selected.
    /// <summary>The pack id this host's session state is keyed under, or null when it keeps none.</summary>
    string? SessionPackId => null;

    /// <summary>The pack's session state to persist, or null to persist nothing.</summary>
    JsonElement? SnapshotPackState() => null;

    /// <summary>Applies a persisted pack session blob. Must tolerate an old or partial shape.</summary>
    /// <param name="state">The persisted blob.</param>
    void RestorePackState(JsonElement state)
    {
    }
}
