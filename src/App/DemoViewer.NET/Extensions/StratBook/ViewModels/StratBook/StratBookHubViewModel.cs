#region

using DemoViewer.NET.Models;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>
///     The Strat Book tab: one Main-strip tab whose left rail lists the Strat Room's surfaces as sections
///     (Strats, Situations, Tags, Utility, Review, Dossier, Suggested) instead of each one taking a strip
///     tab. The pack contributes it as a host tab (<see cref="HostId" />); a module puts a section on the
///     rail by naming that id in <see cref="WorkspaceTabDescriptor.HostId" />, and the shell shows the tab
///     only while some section resolves on.
///     <para>
///         The selected section is the one piece of state the hub owns, and the shell persists it as the
///         session's active tab id (a section id resolves back through the hub), so a session file written
///         when these were strip tabs restores to the same place.
///     </para>
/// </summary>
public sealed class StratBookHubViewModel : IHostTabViewModel
{
    /// <summary>The id sections name as their host. A persisted key; never renamed.</summary>
    public const string HostId = "stratbook.hub";

    /// <summary>The hub's tab id, the same key. Persisted; never renamed.</summary>
    public const string TabId = HostId;

    /// <param name="layout">The collapsed panes, shared with the Strats section; a fresh one when omitted.</param>
    public StratBookHubViewModel(StratBookLayout? layout = null) => Layout = layout ?? new StratBookLayout();

    /// <summary>The sections and the selection. Bound by the hub view's rail.</summary>
    public TabSectionHost Sections { get; } = new(autoSelectFirst: true);

    /// <summary>Whether the rail and the strat list are collapsed. The shell persists it.</summary>
    public StratBookLayout Layout { get; }

    /// <summary>The band over the rail, from the contribution. Bound by the hub view.</summary>
    public string RailLabel { get; set; } = "";

    public void OnActivated(IModuleContext context) => Sections.OnHostActivated(context);

    public void OnDeactivated() => Sections.OnHostDeactivated();

    public StratBookLayoutState? SnapshotLayout() => Layout.Snapshot();

    public void RestoreLayout(StratBookLayoutState? state) => Layout.Restore(state);
}
