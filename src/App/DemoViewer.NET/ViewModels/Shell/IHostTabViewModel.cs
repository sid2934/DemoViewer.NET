#region

using DemoViewer.NET.Models;
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

    // The session file keeps one pane-layout slot beside the active tab, restored before any demo loads. Item 23
    // moves it into pack session state; until then the shell hands the slot to every host and the one that owns
    // it answers.
    /// <summary>The pane layout to persist, or null when the host keeps none.</summary>
    StratBookLayoutState? SnapshotLayout() => null;

    /// <summary>Applies a persisted pane layout; null (an older session file) leaves the panes as they are.</summary>
    /// <param name="state">The persisted layout, or null.</param>
    void RestoreLayout(StratBookLayoutState? state)
    {
    }
}
