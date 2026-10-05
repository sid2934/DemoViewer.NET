#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.ViewModels.StratBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Strat Book hub from a shell that hosts it. The shell knows hubs by id only; tests that pin the
///     hub's own state (its rail, its sections) reach the host's hub VM through here.
/// </summary>
internal static class StratBookHubAccess
{
    /// <summary>The hub's VM, which the shell builds with the strip whether or not the pack is on.</summary>
    /// <param name="vm">A shell built with <see cref="HubHost" /> among its hub tabs.</param>
    public static HubTabViewModel StratBookHub(this MainViewModel vm) =>
        vm.HostViewModel(HostIds.StratBookHub)
        ?? throw new InvalidOperationException("the shell was built without the Strat Book hub tab");

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MainViewModel, StratBookLayout> _layouts = new();

    /// <summary>Records the Strats list state a test built the shell's hub with.</summary>
    public static void HoldsStratBookLayout(this MainViewModel vm, StratBookLayout layout) => _layouts.AddOrUpdate(vm, layout);

    /// <summary>The Strats list state the shell's hub keeps in the session, as recorded by <see cref="HoldsStratBookLayout" />.</summary>
    public static StratBookLayout StratsLayout(this MainViewModel vm) =>
        _layouts.TryGetValue(vm, out StratBookLayout? layout)
            ? layout
            : throw new InvalidOperationException("the shell was built without a recorded Strat Book layout");

    /// <summary>The real hub as the shell builds it from the pack's declaration, for shells built without the pack.</summary>
    /// <param name="layout">The Strats list state the hub keeps in the session; null keeps none.</param>
    public static ContributedHub HubHost(StratBookLayout? layout = null) =>
        ContributedHub.For(StratBookPack.HubTab(layout), StratBookPack.PackId, StratBookPack.PackFeatureId);
}
