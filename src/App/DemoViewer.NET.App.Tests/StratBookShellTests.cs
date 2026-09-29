#region

using Avalonia.Controls;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Models;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Shell;
using TabPlacement = DemoViewer.NET.Modules.Abstractions.TabPlacement;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Strat Book shell: sections a module places on <see cref="TabPlacement.StratBook" /> leave the
///     strip for one Strat Book tab's rail, a <see cref="TabPlacement.Library" /> section lives behind the
///     Library's Demos / Teams toggle, and every id a section had as a strip tab still navigates, gates and
///     persists. Pinned with fake modules so the mechanism is tested apart from any one feature.
/// </summary>
public class StratBookShellTests
{
    private static readonly string[] _railOrder = ["stratbook.browser", "situations.search", "review.queue"];
    private static readonly string[] _teamsOnly = ["teams.browser"];
    private static readonly string[] _withoutSituations = ["stratbook.browser", "review.queue"];
    private static readonly string[] _reviewOnly = ["review.queue"];

    private static MainViewModel NewShell(IFeatureGate? gate, SettingsService? settings = null, params IWorkspaceModule[] modules)
    {
        ModuleRegistry registry = new();
        foreach (IWorkspaceModule module in modules)
        {
            registry.Register(module);
        }

        MainViewModel vm = new(null, registry, TestLibraries.Empty(), null, gate, null, settings);
        vm.RestoreSession();
        return vm;
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvstratshell_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task Sections_LeaveTheStrip_ForOneStratBookTab_InRailOrder() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            SectionsModule module = new();
            MainViewModel vm = NewShell(null, null, module);
            try
            {
                string[] strip = vm.Tabs.Select(t => t.TabId).ToArray();
                string[] rail = vm.StratBookHub.Sections.Sections.Select(s => s.TabId).ToArray();

                using (Assert.Multiple())
                {
                    await Assert.That(strip).Contains(StratBookHubViewModel.TabId);
                    await Assert.That(strip).DoesNotContain("situations.search");
                    await Assert.That(strip).DoesNotContain("review.queue");
                    await Assert.That(strip).DoesNotContain("teams.browser");
                    await Assert.That(rail).IsEquivalentTo(_railOrder)
                        .Because("rail order is the section Order, not registration order");
                    await Assert.That(vm.LibraryTab.Sections.Sections.Select(s => s.TabId)).IsEquivalentTo(_teamsOnly);
                    await Assert.That(vm.LibraryTab.HasTeamsView).IsTrue();
                    await Assert.That(vm.LibraryTab.IsDemosView).IsTrue().Because("the Library lands on the demo browser");
                }
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task WithoutAnySection_ThereIsNoStratBookTab() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = NewShell(null);
            try
            {
                await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain(StratBookHubViewModel.TabId);
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task SelectingTheHub_ActivatesOnlyTheSelectedSection_AndSwitchingMovesIt() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            SectionsModule module = new();
            MainViewModel vm = NewShell(null, null, module);
            try
            {
                WorkspaceTabDescriptor hub = vm.Tabs.First(t => t.TabId == StratBookHubViewModel.TabId);
                WorkspaceTabDescriptor strats = vm.StratBookHub.Sections.Sections[0];
                WorkspaceTabDescriptor situations = vm.StratBookHub.Sections.Sections[1];

                await Assert.That(strats.IsActive).IsFalse().Because("nothing is live while the hub is not the selected tab");

                vm.SelectedTab = hub;
                using (Assert.Multiple())
                {
                    await Assert.That(vm.StratBookHub.Sections.SelectedSection).IsSameReferenceAs(strats);
                    await Assert.That(strats.IsActive).IsTrue();
                    await Assert.That(strats.ActiveContent).IsNotNull();
                    await Assert.That(situations.IsActive).IsFalse();
                    await Assert.That(module.Activations["situations.search"]).IsEqualTo(0);
                }

                vm.StratBookHub.Sections.SelectedSection = situations;
                using (Assert.Multiple())
                {
                    await Assert.That(strats.IsActive).IsFalse();
                    await Assert.That(strats.ActiveContent).IsNull().Because("the inactive section drops its view");
                    await Assert.That(situations.IsActive).IsTrue();
                    await Assert.That(module.Activations["situations.search"]).IsEqualTo(1);
                }

                // Leaving the hub deactivates the live section; coming back re-activates the same one.
                vm.SelectedTab = vm.Tabs[0];
                await Assert.That(situations.IsActive).IsFalse();
                vm.SelectedTab = hub;
                using (Assert.Multiple())
                {
                    await Assert.That(situations.IsActive).IsTrue();
                    await Assert.That(module.Activations["situations.search"]).IsEqualTo(2);
                }
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task TrySelectTab_WithASectionId_SelectsTheHubAndTheSection() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            SectionsModule module = new();
            MainViewModel vm = NewShell(null, null, module);
            try
            {
                bool found = vm.TrySelectTab("review.queue");

                using (Assert.Multiple())
                {
                    await Assert.That(found).IsTrue();
                    await Assert.That(vm.SelectedTab!.TabId).IsEqualTo(StratBookHubViewModel.TabId);
                    await Assert.That(vm.StratBookHub.Sections.SelectedSection!.TabId).IsEqualTo("review.queue");
                    await Assert.That(vm.StratBookHub.Sections.SelectedSection.IsActive).IsTrue();
                }

                bool teams = vm.TrySelectTab("teams.browser");
                using (Assert.Multiple())
                {
                    await Assert.That(teams).IsTrue();
                    await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("builtin.library");
                    await Assert.That(vm.LibraryTab.IsTeamsView).IsTrue();
                    await Assert.That(vm.LibraryTab.Sections.SelectedSection!.IsActive).IsTrue();
                }

                vm.LibraryTab.IsDemosView = true;
                using (Assert.Multiple())
                {
                    await Assert.That(vm.LibraryTab.IsTeamsView).IsFalse();
                    await Assert.That(vm.LibraryTab.Sections.SelectedSection).IsNull();
                    await Assert.That(vm.LibraryTab.Sections.Sections[0].IsActive).IsFalse();
                }

                await Assert.That(vm.TrySelectTab("does.not.exist")).IsFalse();
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task TheGate_HidesASection_AndTheHubOnlyWhenEverySectionIsOff() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeGate gate = new();
            SectionsModule module = new();
            MainViewModel vm = NewShell(gate, null, module);
            try
            {
                vm.TrySelectTab("situations.search");

                gate.Answers["tab.situations"] = false;
                gate.RaiseChanged();
                using (Assert.Multiple())
                {
                    await Assert.That(vm.StratBookHub.Sections.Sections.Select(s => s.TabId))
                        .IsEquivalentTo(_withoutSituations);
                    await Assert.That(vm.StratBookHub.Sections.SelectedSection!.TabId).IsEqualTo("stratbook.browser")
                        .Because("the removed selection falls to its lower neighbour");
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains(StratBookHubViewModel.TabId);
                }

                gate.Answers["tab.stratbook"] = false;
                gate.Answers["tab.review"] = false;
                gate.RaiseChanged();
                using (Assert.Multiple())
                {
                    await Assert.That(vm.StratBookHub.Sections.Sections).IsEmpty();
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain(StratBookHubViewModel.TabId)
                        .Because("a rail with nothing on it has no tab");
                    await Assert.That(vm.SelectedTab!.TabId).IsNotEqualTo(StratBookHubViewModel.TabId);
                }

                gate.Answers["tab.review"] = true;
                gate.RaiseChanged();
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains(StratBookHubViewModel.TabId);
                    await Assert.That(vm.StratBookHub.Sections.Sections.Select(s => s.TabId)).IsEquivalentTo(_reviewOnly);
                }

                gate.Answers["tab.teams"] = false;
                gate.RaiseChanged();
                using (Assert.Multiple())
                {
                    await Assert.That(vm.LibraryTab.HasTeamsView).IsFalse();
                    await Assert.That(vm.LibraryTab.IsDemosView).IsTrue();
                }
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task Session_PersistsTheSelectedSectionAsTheActiveTab_AndAnOldStripSectionId_LandsOnTheRail()
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                SettingsService svc = new(dir);

                MainViewModel vm1 = NewShell(null, svc, new SectionsModule());
                vm1.TrySelectTab("review.queue");
                vm1.SaveSession();
                vm1.Dispose();

                SessionPayload? saved = svc.LoadSession();
                using (Assert.Multiple())
                {
                    await Assert.That(saved!.ActiveTabId).IsEqualTo("review.queue")
                        .Because("the section id is the persisted position; it resolves back through the hub");
                    await Assert.That(saved.ModuleTabs!.ContainsKey("review.queue")).IsTrue()
                        .Because("a section's own state is keyed by its own id, as a strip tab's was");
                }

                MainViewModel vm2 = NewShell(null, svc, new SectionsModule());
                using (Assert.Multiple())
                {
                    await Assert.That(vm2.SelectedTab!.TabId).IsEqualTo(StratBookHubViewModel.TabId);
                    await Assert.That(vm2.StratBookHub.Sections.SelectedSection!.TabId).IsEqualTo("review.queue");
                    await Assert.That(vm2.StratBookHub.Sections.SelectedSection.IsActive).IsTrue();
                }

                vm2.Dispose();

                // The Library's Teams view persists the same way.
                MainViewModel vmTeams = NewShell(null, svc, new SectionsModule());
                vmTeams.TrySelectTab("teams.browser");
                vmTeams.SaveSession();
                vmTeams.Dispose();
                await Assert.That(svc.LoadSession()!.ActiveTabId).IsEqualTo("teams.browser");
                MainViewModel vmTeams2 = NewShell(null, svc, new SectionsModule());
                using (Assert.Multiple())
                {
                    await Assert.That(vmTeams2.SelectedTab!.TabId).IsEqualTo("builtin.library");
                    await Assert.That(vmTeams2.LibraryTab.IsTeamsView).IsTrue();
                }

                vmTeams2.Dispose();

                // A session file from before the rail names the section as the active strip tab.
                svc.SaveSession(new SessionPayload(null, null, null, false, false, "situations.search"));
                MainViewModel vm3 = NewShell(null, svc, new SectionsModule());
                using (Assert.Multiple())
                {
                    await Assert.That(vm3.SelectedTab!.TabId).IsEqualTo(StratBookHubViewModel.TabId);
                    await Assert.That(vm3.StratBookHub.Sections.SelectedSection!.TabId).IsEqualTo("situations.search");
                }

                vm3.Dispose();
            });
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Test]
    public async Task CollapsedPanes_SurviveRestarts_WithoutADemo_AndWithoutOpeningTheStratBook()
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                SettingsService svc = new(dir);

                MainViewModel vm1 = NewShell(null, svc, new SectionsModule());
                vm1.TrySelectTab("stratbook.browser");
                vm1.StratBookHub.Layout.IsRailCollapsed = true;
                vm1.StratBookHub.Layout.IsListCollapsed = true;
                vm1.SaveSession();
                vm1.Dispose();

                // The next session never opens the Strat Book and loads no demo.
                svc.SaveSession(svc.LoadSession()! with { ActiveTabId = "builtin.library" });
                MainViewModel vm2 = NewShell(null, svc, new SectionsModule());
                using (Assert.Multiple())
                {
                    await Assert.That(vm2.SelectedTab!.TabId).IsNotEqualTo(StratBookHubViewModel.TabId);
                    await Assert.That(vm2.StratBookHub.Layout.IsRailCollapsed).IsTrue();
                    await Assert.That(vm2.StratBookHub.Layout.IsListCollapsed).IsTrue();
                }

                vm2.SaveSession();
                vm2.Dispose();

                MainViewModel vm3 = NewShell(null, svc, new SectionsModule());
                using (Assert.Multiple())
                {
                    await Assert.That(vm3.StratBookHub.Layout.IsRailCollapsed).IsTrue()
                        .Because("a session that never opened the Strat Book still writes its panes back");
                    await Assert.That(vm3.StratBookHub.Layout.IsListCollapsed).IsTrue();
                }

                vm3.Dispose();

                // A file from before the panes collapsed opens both.
                svc.SaveSession(new SessionPayload(null, null, null, false, false, "stratbook.browser"));
                MainViewModel vm4 = NewShell(null, svc, new SectionsModule());
                using (Assert.Multiple())
                {
                    await Assert.That(vm4.StratBookHub.Layout.IsRailCollapsed).IsFalse();
                    await Assert.That(vm4.StratBookHub.Layout.IsListCollapsed).IsFalse();
                }

                vm4.Dispose();
            });
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    /// <summary>
    ///     Three rail sections under the shipped feature ids (out of rail order, to pin the sort) and the
    ///     Library-hosted Teams view.
    /// </summary>
    private sealed class SectionsModule : IWorkspaceModule
    {
        public Dictionary<string, int> Activations { get; } = new(StringComparer.Ordinal);

        public string Id => "net.demoviewer.test.sections";
        public string DisplayName => "Sections";
        public Version ContractVersion => new(1, 0, 0);

        public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
        {
            yield return Section("review.queue", "Review", 4, TabPlacement.StratBook);
            yield return Section("stratbook.browser", "Strats", 0, TabPlacement.StratBook);
            yield return Section("situations.search", "Situations", 1, TabPlacement.StratBook);
            yield return Section("teams.browser", "Teams", 0, TabPlacement.Library);
        }

        private WorkspaceTabDescriptor Section(string id, string header, int order, TabPlacement placement)
        {
            Activations[id] = 0;
            return new WorkspaceTabDescriptor
            {
                TabId = id,
                Header = header,
                Order = order,
                Placement = placement,
                ViewModelFactory = () => new SectionViewModel(id, () => Activations[id]++),
                ViewFactory = () => new ContentControl()
            };
        }
    }

    private sealed class SectionViewModel(string id, Action activated) : IWorkspaceTabViewModel
    {
        public void OnActivated(IModuleContext context) => activated();

        public void OnDeactivated()
        {
        }

        public object? SnapshotState() => new SectionState(id);
    }

    private sealed record SectionState(string Id);

    private sealed class FakeGate : IFeatureGate
    {
        public Dictionary<string, bool> Answers { get; } = new(StringComparer.Ordinal);

        public UserCategory Category => UserCategory.Developer;

        public int HiddenCount => 0;

        public bool IsEnabled(string featureId) =>
            !Answers.TryGetValue(featureId, out bool value) || value;

        public event EventHandler? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
