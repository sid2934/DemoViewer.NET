#region

using Avalonia.Controls;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     An extension's tabs that throw at every step the shell drives: building the view model, building the
///     view, activating, deactivating and the session snapshot, plus a hub whose view model throws and a
///     module whose CreateTabs throws. The shell builds, the user can select every tab, the session saves,
///     and every throw is counted against the extension.
/// </summary>
[NotInParallel]
public class ExtensionTabFaultTests
{
    private static (MainViewModel Shell, ExtensionFaults Faults, ThrowingModule Module) NewShell()
    {
        ExtensionFaults faults = ExtensionFaults.For([new ThrowingPack()], static a => a());
        ServiceCollection services = new();
        services.AddSingleton(faults);
        PackContributionSet set = new([new ThrowingPack()], services.BuildServiceProvider());
        ModuleRegistry registry = new();
        foreach (IWorkspaceModule module in set.Packs.Single().Modules)
        {
            registry.Register(module);
        }

        MainViewModel vm = new(null, registry, TestLibraries.Empty(), hubTabs: set.HubTabs);
        vm.RestoreSession();
        return (vm, faults, (ThrowingModule)set.Packs.Single().Modules.First(m => m is ThrowingModule));
    }

    [Test]
    public async Task EveryThrowingTab_StillSelects_ShowingThePlaceholderWhereNothingCouldBeBuilt() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (MainViewModel vm, ExtensionFaults faults, _) = NewShell();
            try
            {
                await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain("bad.hub")
                    .Because("a hub with no sections has no tab, and its throwing session state costs nothing else");

                await Assert.That(vm.TrySelectTab("bad.view")).IsTrue();
                await Assert.That(PlaceholderText(vm)).Contains("could not show this tab");

                await Assert.That(vm.TrySelectTab("bad.vm")).IsTrue();
                await Assert.That(PlaceholderText(vm)).Contains("could not show this tab");

                await Assert.That(vm.TrySelectTab("bad.lifecycle")).IsTrue();
                await Assert.That(vm.TrySelectTab("bad.view")).IsTrue().Because("leaving a tab whose deactivate throws");

                vm.SaveSession();

                ExtensionFaultState state = faults.StateOf(PackFeature);
                using (Assert.Multiple())
                {
                    await Assert.That(state.Count).IsEqualTo(ExtensionFaults.WindowLimit)
                        .Because("faults after the switch-off are logged, not counted");
                    await Assert.That(state.Suspended).IsTrue();
                }
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task TheShellsCopyOfATab_FollowsTheBadgeTheModuleSets() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (MainViewModel vm, _, ThrowingModule module) = NewShell();
            try
            {
                module.Lifecycle!.Badge = "4";
                WorkspaceTabDescriptor shown = vm.Tabs.Single(t => t.TabId == "bad.lifecycle");
                await Assert.That(shown).IsNotSameReferenceAs(module.Lifecycle);
                await Assert.That(shown.Badge).IsEqualTo("4");
            }
            finally
            {
                vm.Dispose();
            }
        });

    private const string PackFeature = "pack.throwing";

    private static string? PlaceholderText(MainViewModel vm) => (vm.SelectedTab?.ActiveContent as TextBlock)?.Text;

    private sealed class ThrowingPack : IExtension
    {
        public string Id => "net.demoviewer.test.throwing";
        public string FeatureId => PackFeature;
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
            contributions.HubTab(new HubTabContribution("bad.hub", "Bad hub", 9, "BAD") { Session = new ThrowingSession() });
            contributions.Tabs(new ThrowingModule());
            contributions.Tabs(new CreateTabsThrows());
        }
    }

    private sealed class ThrowingSession : IExtensionSessionState
    {
        public System.Text.Json.JsonElement? Snapshot() => throw new InvalidOperationException("hub session");

        public void Restore(System.Text.Json.JsonElement state) => throw new InvalidOperationException("hub session");
    }

    private sealed class ThrowingModule : IWorkspaceModule
    {
        public string Id => "net.demoviewer.test.throwingtabs";
        public string DisplayName => "Throwing";
        public Version ContractVersion => new(1, 0, 0);
        public WorkspaceTabDescriptor? Lifecycle { get; private set; }

        public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
        {
            Lifecycle = new WorkspaceTabDescriptor
            {
                TabId = "bad.lifecycle",
                Header = "Lifecycle",
                Order = 92,
                ViewModelFactory = () => new ThrowingLifecycle(),
                ViewFactory = () => new TextBlock { Text = "lifecycle view" }
            };

            return
            [
                new WorkspaceTabDescriptor
                {
                    TabId = "bad.view",
                    Header = "View",
                    Order = 90,
                    ViewModelFactory = () => new PlaceholderTabViewModel(),
                    ViewFactory = () => throw new InvalidOperationException("view")
                },
                new WorkspaceTabDescriptor
                {
                    TabId = "bad.vm",
                    Header = "View model",
                    Order = 91,
                    ViewModelFactory = () => throw new InvalidOperationException("vm"),
                    ViewFactory = () => new TextBlock { Text = "never shown over a missing view model" }
                },
                Lifecycle
            ];
        }
    }

    private sealed class CreateTabsThrows : IWorkspaceModule
    {
        public string Id => "net.demoviewer.test.createtabsthrows";
        public string DisplayName => "Create throws";
        public Version ContractVersion => new(1, 0, 0);

        public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host) => throw new InvalidOperationException("create");
    }

    private sealed class ThrowingLifecycle : IWorkspaceTabViewModel
    {
        public void OnActivated(IModuleContext context) => throw new InvalidOperationException("activate");

        public void OnDeactivated() => throw new InvalidOperationException("deactivate");

        public object? SnapshotState() => throw new InvalidOperationException("snapshot");
    }
}
