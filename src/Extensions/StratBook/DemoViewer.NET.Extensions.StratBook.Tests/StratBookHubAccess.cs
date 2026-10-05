#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Strat Book hub from a shell that hosts it. The shell knows hosts by id only; tests that pin the
///     hub's own state (its rail, its panes) reach the pack's VM through here.
/// </summary>
internal static class StratBookHubAccess
{
    /// <summary>The hub's VM, which the shell builds with the strip whether or not the pack is on.</summary>
    /// <param name="vm">A shell built with <see cref="HubHost" /> among its host tabs.</param>
    public static StratBookHubViewModel StratBookHub(this MainViewModel vm) =>
        (StratBookHubViewModel?)vm.HostViewModel(StratBookHubViewModel.HostId)
        ?? throw new InvalidOperationException("the shell was built without the Strat Book hub host tab");

    /// <summary>The real hub as a host-tab contribution, over a fresh layout, for shells built without the pack.</summary>
    public static HostTabContribution HubHost() => StratBookPack.HubHostTab(() => null);
}
