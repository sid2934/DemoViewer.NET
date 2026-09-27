#region

using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.ViewModels.Shell;

/// <summary>
///     The Strat Book tab: one Main-strip tab whose left rail lists the Strat Room's surfaces as sections
///     (Strats, Situations, Tags, Utility, Review, Dossier) instead of each one taking a strip tab. The
///     shell builds it when any module contributes a <see cref="TabPlacement.StratBook" /> descriptor and
///     hides it when the gate turns every section off.
///     <para>
///         The selected section is the one piece of state the hub owns, and the shell persists it as the
///         session's active tab id (a section id resolves back through the hub), so a session file written
///         when these were strip tabs restores to the same place.
///     </para>
/// </summary>
public sealed class StratBookHubViewModel : IWorkspaceTabViewModel
{
    /// <summary>The hub's tab id. A persisted key; never renamed.</summary>
    public const string TabId = "stratbook.hub";

    /// <summary>The sections and the selection. Bound by the hub view's rail.</summary>
    public TabSectionHost Sections { get; } = new(autoSelectFirst: true);

    public void OnActivated(IModuleContext context) => Sections.OnHostActivated(context);

    public void OnDeactivated() => Sections.OnHostDeactivated();
}
