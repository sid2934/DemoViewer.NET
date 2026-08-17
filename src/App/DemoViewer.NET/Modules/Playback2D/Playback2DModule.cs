#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The 2D Playback pilot module (docs/2d-playback/2d-playback-module-requirements.md). Contributes
///     one Main-strip tab ("2D Playback") whose VM animates every player's reconstructed world position as
///     the framework playback clock advances, with event-driven ring colours, an attributes panel, and a
///     game-info panel.
///     <para>
///         <b>Wiring contract.</b> The descriptor uses <see cref="WorkspaceTabDescriptor.ViewModelFactory" />
///         (lazy + retained) — NOT <c>DataContext</c>. That is what makes <c>Activate()</c> build the VM and
///         drive its <c>OnActivated</c>/<c>OnDeactivated</c> lifecycle (the subscribe/unsubscribe that
///         guarantees zero per-tick work while inactive). Copying <c>PlaceholderModule</c>'s
///         <c>DataContext = vm</c> branch would leave <c>TabViewModel</c> null and skip activation.
///     </para>
/// </summary>
public sealed class Playback2DModule : IWorkspaceModule
{
    public string Id => "net.demoviewer.playback2d";

    public string DisplayName => "2D Playback";

    // 1.1: consumes IModuleContext.MapName (additive) to select baked map assets.
    public Version ContractVersion => new(1, 1, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        // host.HasCapability("Playback.Control") is true for first-party modules (FirstPartyCapabilities),
        // so the optional clock-control Request* calls work with no extra wiring.
        yield return new WorkspaceTabDescriptor
        {
            TabId = "playback2d.viewport",
            Header = "2D Playback",
            Order = 4, // after the four built-ins (0..3)
            Placement = TabPlacement.Main,
            // ViewModelFactory (LAZY + RETAINED) — NOT DataContext. This is what makes Activate() build
            // and drive the VM's OnActivated/OnDeactivated lifecycle.
            ViewModelFactory = () => new Playback2DTabViewModel(),
            ViewFactory = () => new Playback2DView()
        };
    }
}
