#region

using Avalonia.Controls;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Models;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.Services.Provenance;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Library;
using DemoViewer.NET.ViewModels.Playback2D;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.ViewModels.StratBook;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Strat Book shell: sections a module hosts on <see cref="StratBookHubViewModel.HostId" /> leave
///     the strip for one Strat Book tab's rail, a <see cref="LibraryTabViewModel.HostId" /> section lives
///     behind the Library's Demos / Teams toggle, and every id a section had as a strip tab still navigates,
///     gates and persists. Pinned with fake modules and the real hub contribution, so the mechanism is tested
///     apart from any one feature.
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

        MainViewModel vm = new(null, registry, TestLibraries.Empty(), null, gate, null, settings,
            hostTabs: [StratBookHubAccess.HubHost()]);
        vm.RestoreSession();
        return vm;
    }

    // Item 4: the Library's team filter and provenance chip, which NewShell above never wires.
    private static MainViewModel NewShellWithTeams(
        IFeatureGate? gate, TeamIdentityService teams, IDemoProvenanceSource provenance,
        params IWorkspaceModule[] modules)
    {
        ModuleRegistry registry = new();
        foreach (IWorkspaceModule module in modules)
        {
            registry.Register(module);
        }

        MainViewModel vm = new(null, registry, TestLibraries.Empty(), null, gate, null, null,
            teams: teams, provenance: provenance, hostTabs: [StratBookHubAccess.HubHost()]);
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
                string[] rail = vm.StratBookHub().Sections.Sections.Select(s => s.TabId).ToArray();

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

    // Item 5's window test: a LIVE FeatureGate over a real SettingsService (not FakeGate, which has no
    // cascade), the same harness TabFeatureGatingTests.WithGatedShell uses. Flipping the master switch
    // override cascades off every section's tab id (they are all in MainViewModel._tabFeatureIds,
    // parented to the pack in the catalog), so the hub tab, synthesized only while some section passes
    // the gate, vanishes, and clearing the override brings it right back without a rebuild.
    [Test]
    public async Task LiveToggle_OfTheMasterSwitch_HidesAndRestoresTheStratBookHubTab()
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                SettingsService svc = new(dir);
                ServiceCollection services = new();
                services.Configure<AppSettings>(svc.Configuration);
                using ServiceProvider sp = services.BuildServiceProvider();
                IOptionsMonitor<AppSettings> monitor = sp.GetRequiredService<IOptionsMonitor<AppSettings>>();
                using FeatureGate gate = new(monitor, false);

                MainViewModel vm = NewShell(gate, svc, new SectionsModule());
                try
                {
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains(StratBookHubViewModel.TabId)
                        .Because("the pack defaults on, so its sections synthesize the hub tab");

                    svc.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);

                    await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain(StratBookHubViewModel.TabId)
                        .Because("every section cascades off with the master switch, leaving nothing to host the hub");

                    svc.Write(s => s.Features.Overrides.Remove(StratBookPack.PackFeatureId));

                    await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains(StratBookHubViewModel.TabId)
                        .Because("clearing the override brings the hub right back, live, with no rebuild");
                }
                finally
                {
                    vm.Dispose();
                }
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
    public async Task SelectingTheHub_ActivatesOnlyTheSelectedSection_AndSwitchingMovesIt() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            SectionsModule module = new();
            MainViewModel vm = NewShell(null, null, module);
            try
            {
                WorkspaceTabDescriptor hub = vm.Tabs.First(t => t.TabId == StratBookHubViewModel.TabId);
                WorkspaceTabDescriptor strats = vm.StratBookHub().Sections.Sections[0];
                WorkspaceTabDescriptor situations = vm.StratBookHub().Sections.Sections[1];

                await Assert.That(strats.IsActive).IsFalse().Because("nothing is live while the hub is not the selected tab");

                vm.SelectedTab = hub;
                using (Assert.Multiple())
                {
                    await Assert.That(vm.StratBookHub().Sections.SelectedSection).IsSameReferenceAs(strats);
                    await Assert.That(strats.IsActive).IsTrue();
                    await Assert.That(strats.ActiveContent).IsNotNull();
                    await Assert.That(situations.IsActive).IsFalse();
                    await Assert.That(module.Activations["situations.search"]).IsEqualTo(0);
                }

                vm.StratBookHub().Sections.SelectedSection = situations;
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
                    await Assert.That(vm.StratBookHub().Sections.SelectedSection!.TabId).IsEqualTo("review.queue");
                    await Assert.That(vm.StratBookHub().Sections.SelectedSection!.IsActive).IsTrue();
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
                    await Assert.That(vm.StratBookHub().Sections.Sections.Select(s => s.TabId))
                        .IsEquivalentTo(_withoutSituations);
                    await Assert.That(vm.StratBookHub().Sections.SelectedSection!.TabId).IsEqualTo("stratbook.browser")
                        .Because("the removed selection falls to its lower neighbour");
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains(StratBookHubViewModel.TabId);
                }

                gate.Answers["tab.stratbook"] = false;
                gate.Answers["tab.review"] = false;
                gate.RaiseChanged();
                using (Assert.Multiple())
                {
                    await Assert.That(vm.StratBookHub().Sections.Sections).IsEmpty();
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain(StratBookHubViewModel.TabId)
                        .Because("a rail with nothing on it has no tab");
                    await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("builtin.library")
                        .Because("the hub's own order-neighbour is not always Library, but a disabled pack falls back to it specifically");
                }

                gate.Answers["tab.review"] = true;
                gate.RaiseChanged();
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains(StratBookHubViewModel.TabId);
                    await Assert.That(vm.StratBookHub().Sections.Sections.Select(s => s.TabId)).IsEquivalentTo(_reviewOnly);
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

    // A descriptor's own FeatureId gates it with no entry in MainViewModel's fallback map at all; a
    // descriptor that declares neither still fails open.
    [Test]
    public async Task DescriptorFeatureId_GatesTheTab_WithNoFallbackMapEntry() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeGate gate = new();
            MainViewModel vm = NewShell(gate, null, new FeatureIdModule());
            try
            {
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains("test.featured");
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains("test.unmapped");
                }

                gate.Answers[FeatureIdModule.FeatureId] = false;
                gate.RaiseChanged();
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain("test.featured")
                        .Because("the descriptor's own FeatureId gates it, with no _tabFeatureIds entry for its TabId");
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains("test.unmapped")
                        .Because("no FeatureId and no fallback entry fails open");
                }

                gate.Answers[FeatureIdModule.FeatureId] = true;
                gate.RaiseChanged();
                await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains("test.featured");
            }
            finally
            {
                vm.Dispose();
            }
        });

    // A built-in tab contributes no FeatureId of its own: MainViewModel's fallback map still gates it by TabId.
    [Test]
    public async Task ABuiltInTab_WithNoDeclaredFeatureId_StillGatesThroughTheFallbackMap() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeGate gate = new();
            MainViewModel vm = NewShell(gate, null);
            try
            {
                await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains("builtin.parser");

                gate.Answers["tab.parser"] = false;
                gate.RaiseChanged();
                await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain("builtin.parser")
                    .Because("builtin.parser declares no FeatureId; MainViewModel._tabFeatureIds still gates it");

                gate.Answers["tab.parser"] = true;
                gate.RaiseChanged();
                await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains("builtin.parser");
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
                    await Assert.That(vm2.StratBookHub().Sections.SelectedSection!.TabId).IsEqualTo("review.queue");
                    await Assert.That(vm2.StratBookHub().Sections.SelectedSection!.IsActive).IsTrue();
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
                    await Assert.That(vm3.StratBookHub().Sections.SelectedSection!.TabId).IsEqualTo("situations.search");
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

    // FakeGate has no cascade, so every id the real pack parents to pack.stratbook is set by hand here,
    // the same way TheGate_Hides... above does.
    private static void SetPackOff(FakeGate gate)
    {
        gate.Answers[StratBookPack.PackFeatureId] = false;
        gate.Answers["tab.stratbook"] = false;
        gate.Answers["tab.teams"] = false;
        gate.Answers["tab.situations"] = false;
        gate.Answers["tab.review"] = false;
    }

    private static void SetPackOn(FakeGate gate)
    {
        gate.Answers.Remove(StratBookPack.PackFeatureId);
        gate.Answers.Remove("tab.stratbook");
        gate.Answers.Remove("tab.teams");
        gate.Answers.Remove("tab.situations");
        gate.Answers.Remove("tab.review");
    }

    private static (DemoCacheStore Cache, TeamIdentityService Teams) NewTeams()
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache, run: a =>
        {
            a();
            return Task.CompletedTask;
        });
        return (cache, teams);
    }

    [Test]
    public async Task PackOff_AtStartup_HidesTheSharedSurfaces_AndNeverQueriesProvenance() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeGate gate = new();
            SetPackOff(gate);
            (_, TeamIdentityService teams) = NewTeams();
            await teams.StartAsync();
            CountingProvenanceSource provenance = new();

            MainViewModel vm = NewShellWithTeams(gate, teams, provenance, new SectionsModule());
            FakeExportJob job = new();
            Playback2DExportStatusViewModel status = new(job);
            try
            {
                vm.AttachStratExportStatus(status);
                job.Push(new ExportJobStatus(ExportPhase.Rendering, 1, 10, 0, TimeSpan.Zero, "strat.webm", null));

                using (Assert.Multiple())
                {
                    await Assert.That(vm.LibraryTab.HasTeamFilter).IsFalse();
                    await Assert.That(vm.LibraryTab.HasProvenance).IsFalse();
                    await Assert.That(vm.LibraryTab.AvailableTeams).IsEquivalentTo([TeamFilterItem.All])
                        .Because("no team filter items beyond the sentinel");
                    await Assert.That(vm.LibraryTab.HasTeamsView).IsFalse();
                    await Assert.That(vm.TrySelectTab("stratbook.browser")).IsFalse();
                    await Assert.That(vm.Chips.Contains(status.Chip)).IsFalse()
                        .Because("a strat export attached while the pack is off never joins the strip");
                    await Assert.That(provenance.ResolveAllCalls).IsEqualTo(0)
                        .Because("the Library must not query provenance while the pack is off");
                }
            }
            finally
            {
                vm.Dispose();
                teams.Dispose();
            }
        });

    [Test]
    public async Task PackOff_AtStartup_WithAPersistedPackSection_RestoresToLibrary_AndKeepsTheStratBookBlob()
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                SettingsService svc = new(dir);
                svc.SaveSession(new SessionPayload(null, null, null, false, false, "stratbook.browser",
                    null, null, new StratBookLayoutState(true, true)));

                FakeGate gate = new();
                SetPackOff(gate);
                MainViewModel vm = NewShell(gate, svc, new SectionsModule());
                try
                {
                    using (Assert.Multiple())
                    {
                        await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("builtin.library")
                            .Because("a persisted pack section with the pack off lands on Library, not nothing");
                        await Assert.That(vm.StratBookHub().Layout.IsRailCollapsed).IsTrue()
                            .Because("the pack's own session blob restores untouched even while the pack is off");
                        await Assert.That(vm.StratBookHub().Layout.IsListCollapsed).IsTrue();
                    }

                    vm.SaveSession();
                }
                finally
                {
                    vm.Dispose();
                }

                SessionPayload? reloaded = svc.LoadSession();
                using (Assert.Multiple())
                {
                    await Assert.That(reloaded!.StratBook!.RailCollapsed).IsTrue()
                        .Because("re-saving with the pack off must not clobber the blob with a fresh default");
                    await Assert.That(reloaded.StratBook!.ListCollapsed).IsTrue();
                }
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
    public async Task PackToggledOffInSession_WhileAPackSectionIsActive_FallsBackToLibrary_AndOnReappears() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeGate gate = new();
            (_, TeamIdentityService teams) = NewTeams();
            await teams.StartAsync();
            CountingProvenanceSource provenance = new();
            SectionsModule module = new();
            MainViewModel vm = NewShellWithTeams(gate, teams, provenance, module);
            FakeExportJob job = new();
            Playback2DExportStatusViewModel status = new(job);
            try
            {
                vm.AttachStratExportStatus(status);
                job.Push(new ExportJobStatus(ExportPhase.Rendering, 1, 10, 0, TimeSpan.Zero, "strat.webm", null));

                await Assert.That(vm.TrySelectTab("stratbook.browser")).IsTrue();
                using (Assert.Multiple())
                {
                    await Assert.That(vm.SelectedTab!.TabId).IsEqualTo(StratBookHubViewModel.TabId);
                    await Assert.That(vm.LibraryTab.HasTeamFilter).IsTrue();
                    await Assert.That(vm.LibraryTab.HasProvenance).IsTrue();
                    await Assert.That(vm.Chips.Contains(status.Chip)).IsTrue();
                }

                SetPackOff(gate);
                gate.RaiseChanged();
                using (Assert.Multiple())
                {
                    await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("builtin.library")
                        .Because("the Strat Book tab the user was on just went away");
                    await Assert.That(vm.LibraryTab.HasTeamFilter).IsFalse();
                    await Assert.That(vm.LibraryTab.HasProvenance).IsFalse();
                    await Assert.That(vm.LibraryTab.AvailableTeams).IsEquivalentTo([TeamFilterItem.All]);
                    await Assert.That(vm.Chips.Contains(status.Chip)).IsFalse()
                        .Because("a running strat export is hidden, not stopped, while the pack is off");
                }

                // A status change while still off (the mapper keeps running) must not resurrect the chip
                // through OnExportStatusPropertyChanged's own reconcile call.
                job.Push(new ExportJobStatus(ExportPhase.Completed, 10, 10, 60, TimeSpan.FromSeconds(10),
                    "strat.webm", null));
                await Assert.That(vm.Chips.Contains(status.Chip)).IsFalse();

                SetPackOn(gate);
                gate.RaiseChanged();
                using (Assert.Multiple())
                {
                    await Assert.That(vm.LibraryTab.HasTeamFilter).IsTrue();
                    await Assert.That(vm.LibraryTab.HasProvenance).IsTrue();
                    await Assert.That(vm.Chips.Contains(status.Chip)).IsTrue()
                        .Because("the same mapper reappears: turning the pack off never unsubscribed it");
                    await Assert.That(vm.TrySelectTab("stratbook.browser")).IsTrue();
                }
            }
            finally
            {
                vm.Dispose();
                teams.Dispose();
            }
        });

    [Test]
    public async Task PackOn_TheLibraryTeamFilterAndProvenanceChip_WorkAsBefore() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (_, TeamIdentityService teams) = NewTeams();
            await teams.StartAsync();
            CountingProvenanceSource provenance = new();

            MainViewModel vm = NewShellWithTeams(null, teams, provenance, new SectionsModule());
            try
            {
                using (Assert.Multiple())
                {
                    await Assert.That(vm.LibraryTab.HasTeamFilter).IsTrue();
                    await Assert.That(vm.LibraryTab.HasProvenance).IsTrue();
                    await Assert.That(provenance.ResolveAllCalls).IsGreaterThan(0)
                        .Because("the pack on is the pre-gating behaviour: nothing new is suppressed");
                }
            }
            finally
            {
                vm.Dispose();
                teams.Dispose();
            }
        });

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
                vm1.StratBookHub().Layout.IsRailCollapsed = true;
                vm1.StratBookHub().Layout.IsListCollapsed = true;
                vm1.SaveSession();
                vm1.Dispose();

                // The next session never opens the Strat Book and loads no demo.
                svc.SaveSession(svc.LoadSession()! with { ActiveTabId = "builtin.library" });
                MainViewModel vm2 = NewShell(null, svc, new SectionsModule());
                using (Assert.Multiple())
                {
                    await Assert.That(vm2.SelectedTab!.TabId).IsNotEqualTo(StratBookHubViewModel.TabId);
                    await Assert.That(vm2.StratBookHub().Layout.IsRailCollapsed).IsTrue();
                    await Assert.That(vm2.StratBookHub().Layout.IsListCollapsed).IsTrue();
                }

                vm2.SaveSession();
                vm2.Dispose();

                MainViewModel vm3 = NewShell(null, svc, new SectionsModule());
                using (Assert.Multiple())
                {
                    await Assert.That(vm3.StratBookHub().Layout.IsRailCollapsed).IsTrue()
                        .Because("a session that never opened the Strat Book still writes its panes back");
                    await Assert.That(vm3.StratBookHub().Layout.IsListCollapsed).IsTrue();
                }

                vm3.Dispose();

                // A file from before the panes collapsed opens both.
                svc.SaveSession(new SessionPayload(null, null, null, false, false, "stratbook.browser"));
                MainViewModel vm4 = NewShell(null, svc, new SectionsModule());
                using (Assert.Multiple())
                {
                    await Assert.That(vm4.StratBookHub().Layout.IsRailCollapsed).IsFalse();
                    await Assert.That(vm4.StratBookHub().Layout.IsListCollapsed).IsFalse();
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
    ///     A strip tab whose descriptor declares its own <see cref="WorkspaceTabDescriptor.FeatureId" />
    ///     (an id with no entry anywhere in <c>MainViewModel._tabFeatureIds</c>), alongside one that
    ///     declares neither.
    /// </summary>
    private sealed class FeatureIdModule : IWorkspaceModule
    {
        public const string FeatureId = "test.featureid";

        public string Id => "net.demoviewer.test.featureid";
        public string DisplayName => "FeatureId";
        public Version ContractVersion => new(1, 0, 0);

        public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
        {
            yield return new WorkspaceTabDescriptor
            {
                TabId = "test.featured",
                Header = "Featured",
                Order = 10,
                FeatureId = FeatureId,
                ViewFactory = () => new ContentControl()
            };
            yield return new WorkspaceTabDescriptor
            {
                TabId = "test.unmapped",
                Header = "Unmapped",
                Order = 11,
                ViewFactory = () => new ContentControl()
            };
        }
    }

    /// <summary>
    ///     Three rail sections declaring the shipped feature ids on their own descriptors (out of rail
    ///     order, to pin the sort) and the Library-hosted Teams view.
    /// </summary>
    private sealed class SectionsModule : IWorkspaceModule
    {
        public Dictionary<string, int> Activations { get; } = new(StringComparer.Ordinal);

        public string Id => "net.demoviewer.test.sections";
        public string DisplayName => "Sections";
        public Version ContractVersion => new(1, 0, 0);

        public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
        {
            yield return Section("review.queue", "Review", 4, StratBookHubViewModel.HostId, "tab.review");
            yield return Section("stratbook.browser", "Strats", 0, StratBookHubViewModel.HostId, "tab.stratbook");
            yield return Section("situations.search", "Situations", 1, StratBookHubViewModel.HostId, "tab.situations");
            yield return Section("teams.browser", "Teams", 0, LibraryTabViewModel.HostId, "tab.teams");
        }

        private WorkspaceTabDescriptor Section(string id, string header, int order, string hostId, string featureId)
        {
            Activations[id] = 0;
            return new WorkspaceTabDescriptor
            {
                TabId = id,
                Header = header,
                Order = order,
                HostId = hostId,
                FeatureId = featureId,
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

    // Counts ResolveAll calls so a test can assert the Library never queries provenance while the pack
    // is off; everything it returns is unlabeled, which is enough to drive RefreshProvenance.
    private sealed class CountingProvenanceSource : IDemoProvenanceSource
    {
        public int ResolveAllCalls { get; private set; }

        public string? LabelFor(string sha256) => null;

        public IReadOnlyDictionary<string, string?> LabelsFor(IEnumerable<string> sha256s) =>
            sha256s.ToDictionary(s => s, _ => (string?)null, StringComparer.Ordinal);

        public DemoProvenance? Resolve(string demoPath) => null;

        public IReadOnlyDictionary<string, DemoProvenance> ResolveAll(IEnumerable<string> demoPaths)
        {
            ResolveAllCalls++;
            return demoPaths.ToDictionary(p => p,
                p => new DemoProvenance(p, null, null, null, ProvenanceOrigin.None), StringComparer.Ordinal);
        }

        // Never raised: no test here depends on it, and the interface requires the member regardless.
#pragma warning disable CS0067
        public event Action? Changed;
#pragma warning restore CS0067
    }

    // A job that only publishes what a test tells it to, the same seam Playback2DExportSurfaceTests builds
    // its FakeExportJob on.
    private sealed class FakeExportJob : IExportJobService
    {
        public ExportJobStatus Status { get; private set; } = ExportJobStatus.Idle;

        public event EventHandler<ExportJobStatus>? StatusChanged;

        public void Start(Scene2DExportRequest request)
        {
        }

        public Task CancelAsync() => Task.CompletedTask;

        public void Push(ExportJobStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, status);
        }
    }
}
