#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Views.RuleWorkbench;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.Modules.RuleWorkbench;

/// <summary>
///     The Rulesets v2 authoring Workbench module
///. Contributes one Main-strip <c>Authoring</c>
///     tab: an in-app editor + live diagnostics + trace for v2 rulesets, sitting on the app's v2
///     checker/evaluator seam.
///     <para>
///         Milestones: M0 the empty tab scaffold (this) → M1 in-process v2 checker diagnostics →
///         M2 the AvaloniaEdit editor + FileSystemWatcher → M3 catalog-driven completion → M4 the
///         data browser → M5 evaluate + <c>2MUCH</c> results → M6 the clause-level trace panel.
///     </para>
///     <para>
///         Uses <see cref="WorkspaceTabDescriptor.ViewModelFactory" /> (lazy + retained) so
///         <c>Activate()</c> drives the VM's <c>OnActivated</c>/<c>OnDeactivated</c> lifecycle —
///         the same wiring contract as the 2D Playback pilot.
///     </para>
/// </summary>
/// <param name="settings">
///     Live user-settings monitor from the composition root. Threaded into the tab VM so
///     its DeveloperMode gate is a live read of <c>AppSettings.Features.DeveloperMode</c>. Null → the
///     env-only fallback (designer / tests construct the module parameterless).
/// </param>
public sealed class RuleWorkbenchModule(IOptionsMonitor<AppSettings>? settings = null) : IWorkspaceModule
{
    public string Id => "net.demoviewer.ruleworkbench";
    public string DisplayName => "Rule Authoring Workbench";
    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        yield return new WorkspaceTabDescriptor
        {
            TabId = "ruleworkbench.editor",
            Header = "Authoring",
            Order = 5, // after the four built-ins (0..3) and 2D Playback (4)
            Placement = TabPlacement.Main,
            ViewModelFactory = () => new RuleWorkbenchTabViewModel(settings),
            ViewFactory = () => new RuleWorkbenchView()
        };
    }
}
