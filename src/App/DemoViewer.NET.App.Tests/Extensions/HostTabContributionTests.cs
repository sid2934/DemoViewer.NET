#region

using System.Runtime.Loader;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Library;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views;
using DemoViewer.NET.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     Hub tabs as declarations the host draws, apart from the Strat Book: fake packs declare hubs through the
///     public <see cref="IExtensionContributions.HubTab" /> and put sections on them through module
///     descriptors, and the shell builds the strip from <see cref="PackContributionSet" /> alone. Nothing here
///     knows a pack beyond its id. The last tests run the sample extension through the external loading path.
/// </summary>
[NotInParallel]
[Category("Render")]
public class HostTabContributionTests
{
    private const string PackId = "pack.fake";
    private const string HostId = "fake.hub";
    private const string OtherPackId = "pack.other";
    private const string OtherHostId = "other.hub";
    private const string HelloId = "dev.example.hello";

    private static readonly string[] _bothSections = ["fake.alpha", "fake.beta"];
    private static readonly string[] _otherSections = ["other.one", "other.two"];
    private static readonly string[] _helloSections = ["First", "Second"];

    private static FakeHubPack FakePack() =>
        new("net.demoviewer.test.fakepack", PackId, new HubTabContribution(HostId, "Fake", 7, "FAKE RAIL"),
            Section("fake.beta", "Beta", 1, HostId), Section("fake.alpha", "Alpha", 0, HostId));

    private static FakeHubPack OtherPack() =>
        new("net.demoviewer.test.otherpack", OtherPackId, new HubTabContribution(OtherHostId, "Other", 8, "OTHER RAIL"),
            Section("other.two", "Two", 1, OtherHostId), Section("other.one", "One", 0, OtherHostId));

    private static (MainViewModel Shell, FakeGate Gate) NewShell(IReadOnlyList<IExtension> packs, FakeGate? gate = null,
        SettingsService? settings = null)
    {
        PackContributionSet set = new(packs, new ServiceCollection().BuildServiceProvider());
        ModuleRegistry registry = new();
        foreach (IWorkspaceModule module in set.Packs.SelectMany(p => p.Modules))
        {
            registry.Register(module);
        }

        gate ??= new FakeGate();
        MainViewModel vm = new(null, registry, TestLibraries.Empty(), null, gate, null, settings, hubTabs: set.HubTabs);
        vm.RestoreSession();
        return (vm, gate);
    }

    private static (MainViewModel Shell, FakeGate Gate) NewShell(bool packOn = true)
    {
        FakeGate gate = new();
        if (!packOn)
        {
            gate.Answers[PackId] = false;
        }

        return NewShell([FakePack()], gate);
    }

    private static List<string> RenderedTexts(MainViewModel vm, string tabId)
    {
        Window window = new() { Width = 1280, Height = 800, Content = new MainView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        vm.TrySelectTab(tabId);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        List<string> texts =
        [
            .. window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0)
        ];
        window.Close();
        return texts;
    }

    [Test]
    public async Task TheCollector_StampsTheHub_WithThePackFeatureIdAndThePackId()
    {
        PackContributionSet set = new([FakePack()], new ServiceCollection().BuildServiceProvider());
        ContributedHub hub = set.HubTabs.Single();
        using (Assert.Multiple())
        {
            await Assert.That(hub.Id).IsEqualTo(HostId);
            await Assert.That(hub.FeatureId).IsEqualTo(PackId).Because("a null feature id takes the owning pack's");
            await Assert.That(hub.PackId).IsEqualTo("net.demoviewer.test.fakepack");
            await Assert.That(hub.RailLabel).IsEqualTo("FAKE RAIL");
        }
    }

    [Test]
    public async Task AFakePack_HostsItsTwoSections_AndTheHostDrawsTheRail() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (MainViewModel vm, _) = NewShell();
            try
            {
                List<string> texts = RenderedTexts(vm, HostId);
                HubTabViewModel host = vm.HostViewModel(HostId)!;
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains(HostId)
                        .Because("the hub tab comes from the declaration, not from the shell");
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain("fake.alpha");
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain("fake.beta");
                    await Assert.That(host.Sections.Sections.Select(s => s.TabId)).IsEquivalentTo(_bothSections);
                    await Assert.That(vm.Tabs.Single(t => t.TabId == HostId).ActiveContent).IsTypeOf<HubTabView>()
                        .Because("the host draws the hub; the extension hands over no view of its own");
                    await Assert.That(texts).Contains("FAKE RAIL").Because("the band binds the declaration's label");
                    await Assert.That(texts).Contains("Alpha");
                    await Assert.That(texts).Contains("Beta");
                    await Assert.That(texts).Contains("Alpha section view")
                        .Because("the first section is selected and realized when the host is");
                }
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task HidingThePack_HidesTheHub_AndShowingItBringsItBack() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (MainViewModel vm, FakeGate gate) = NewShell();
            try
            {
                await Assert.That(vm.TrySelectTab("fake.beta")).IsTrue();
                await Assert.That(vm.SelectedTab!.TabId).IsEqualTo(HostId);

                gate.Answers[PackId] = false;
                gate.RaiseChanged();
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain(HostId)
                        .Because("the hub's own feature id is the pack's, and off means no tab");
                    await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("builtin.library")
                        .Because("a hub that goes away lands on Library, not on whatever sorts before it");
                    await Assert.That(vm.TrySelectTab("fake.beta")).IsFalse()
                        .Because("a section of a hidden hub is not here");
                }

                gate.Answers.Remove(PackId);
                gate.RaiseChanged();
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains(HostId);
                    await Assert.That(vm.HostViewModel(HostId)!.Sections.Sections.Select(s => s.TabId)).IsEquivalentTo(_bothSections)
                        .Because("the same descriptors come back; nothing re-ran CreateTabs");
                }
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task ASectionId_SelectsThroughTheHost_AndActivatesOnlyThatSection() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (MainViewModel vm, _) = NewShell();
            try
            {
                await Assert.That(vm.TrySelectTab("fake.beta")).IsTrue();
                HubTabViewModel host = vm.HostViewModel(HostId)!;
                using (Assert.Multiple())
                {
                    await Assert.That(vm.SelectedTab!.TabId).IsEqualTo(HostId);
                    await Assert.That(host.Sections.SelectedSection!.TabId).IsEqualTo("fake.beta");
                    await Assert.That(host.Sections.SelectedSection.IsActive).IsTrue();
                    await Assert.That(host.Sections.Sections[0].IsActive).IsFalse()
                        .Because("one realized view per host");
                }
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task PackOffAtStartup_HasNoHub_AndASectionIdAnswersFalse() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (MainViewModel vm, _) = NewShell(packOn: false);
            try
            {
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain(HostId);
                    await Assert.That(vm.TrySelectTab("fake.alpha")).IsFalse();
                    await Assert.That(vm.HostViewModel(HostId)).IsNotNull()
                        .Because("the hub VM exists from the strip build so a gate flip has somewhere to reconcile into");
                }
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task ASection_NamingAHostNothingContributes_IsDroppedFromTheStrip() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            ModuleRegistry registry = new();
            registry.Register(new SectionsModule("net.demoviewer.test.orphan", Section("orphan.section", "Orphan", 0, "nobody.hosts.this")));
            MainViewModel vm = new(null, registry, TestLibraries.Empty());
            try
            {
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain("orphan.section")
                        .Because("a section never falls back to the strip");
                    await Assert.That(vm.Output.DecodeErrors.Rows.Any(r => r.Message.Contains("orphan.section", StringComparison.Ordinal))).IsTrue()
                        .Because("the drop is logged where module failures go");
                }
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task TwoIndependentHubs_EachHostOnlyItsOwnSections_AndGatingOneLeavesTheOther() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (MainViewModel vm, FakeGate gate) = NewShell([FakePack(), OtherPack()]);
            try
            {
                List<string> texts = RenderedTexts(vm, OtherHostId);
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains(HostId);
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains(OtherHostId);
                    await Assert.That(vm.HostViewModel(HostId)!.Sections.Sections.Select(s => s.TabId)).IsEquivalentTo(_bothSections);
                    await Assert.That(vm.HostViewModel(OtherHostId)!.Sections.Sections.Select(s => s.TabId)).IsEquivalentTo(_otherSections);
                    await Assert.That(texts).Contains("OTHER RAIL");
                    await Assert.That(texts).Contains("One section view");
                    await Assert.That(texts).DoesNotContain("FAKE RAIL").Because("only the selected hub is realized");
                }

                await Assert.That(vm.TrySelectTab("other.two")).IsTrue();
                gate.Answers[PackId] = false;
                gate.RaiseChanged();
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain(HostId);
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains(OtherHostId)
                        .Because("each hub follows its own pack's switch");
                    await Assert.That(vm.SelectedTab!.TabId).IsEqualTo(OtherHostId);
                    await Assert.That(vm.HostViewModel(OtherHostId)!.Sections.SelectedSection!.TabId).IsEqualTo("other.two");
                }
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task AHubId_AlreadyTaken_IsLeftOut_AndLogged() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeHubPack libraryThief = new("net.demoviewer.test.thief", "pack.thief",
                new HubTabContribution(HostIds.LibraryTab, "Thief", 9, "THIEF"));
            FakeHubPack twin = new("net.demoviewer.test.twin", "pack.twin", new HubTabContribution(HostId, "Twin", 9, "TWIN"));
            (MainViewModel vm, _) = NewShell([FakePack(), libraryThief, twin]);
            try
            {
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Tabs.Count(t => t.TabId == HostIds.LibraryTab)).IsEqualTo(1);
                    await Assert.That(vm.Tabs.Count(t => t.TabId == HostId)).IsEqualTo(1);
                    await Assert.That(vm.HostViewModel(HostId)!.RailLabel).IsEqualTo("FAKE RAIL")
                        .Because("the first declaration keeps the id; the second is left out");
                    await Assert.That(vm.Output.DecodeErrors.Rows.Count(r => r.Message.Contains("already taken", StringComparison.Ordinal)))
                        .IsEqualTo(2);
                }
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task ASection_JoinsAFirstPartyHub_ThroughItsPublishedId() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeHubPack joiner = new("net.demoviewer.test.joiner", "pack.joiner", null,
                Section("joiner.section", "Joined", 5, HostIds.LibraryTab));
            (MainViewModel vm, FakeGate gate) = NewShell([joiner]);
            try
            {
                await Assert.That(vm.LibraryTab.Sections.Sections.Select(s => s.TabId)).Contains("joiner.section");
                await Assert.That(vm.TrySelectTab("joiner.section")).IsTrue();
                await Assert.That(vm.SelectedTab!.TabId).IsEqualTo(LibraryTabViewModel.HostId);

                gate.Answers["pack.joiner"] = false;
                gate.RaiseChanged();
                await Assert.That(vm.LibraryTab.Sections.Sections).IsEmpty()
                    .Because("a section in a first-party hub still follows its own extension's switch");
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task TheRailState_IsTheHostsOwn_AndSurvivesARestart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvhub_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                SettingsService svc = new(dir);
                (MainViewModel vm1, _) = NewShell([FakePack()], settings: svc);
                vm1.HostViewModel(HostId)!.ToggleRailCommand.Execute(null);
                vm1.SaveSession();
                vm1.Dispose();

                (MainViewModel vm2, _) = NewShell([FakePack()], settings: svc);
                try
                {
                    await Assert.That(vm2.HostViewModel(HostId)!.IsRailCollapsed).IsTrue();
                }
                finally
                {
                    vm2.Dispose();
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

    // The sample extension installed and resolved as a third party, then registered on a container of its
    // own, the way the composition root registers an external extension.
    private sealed record ExternalHello(MainViewModel Shell, PackContributionSet Set, IServiceProvider Services, string Root)
        : IDisposable
    {
        public void Dispose()
        {
            Shell.Dispose();
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
                // best effort
            }
            catch (UnauthorizedAccessException)
            {
                // best effort
            }
        }
    }

    private static ExternalHello ExternalHelloShell()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-hub-" + Guid.NewGuid().ToString("N"));
        string configuration = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Name;
        string sample = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "HelloExtension", configuration));
        string target = Path.Combine(root, ExtensionLoader.ExtensionsDirectoryName, HelloId, "1.0.0");
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(sample))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        ExternalResolution resolved = ExternalExtensions.Resolve(root, [], ExtensionHost.Current,
            new ExternalTrust(PublisherKeys.Current, true));
        IExtension hello = resolved.Loaded.Single().Pack;

        ServiceCollection services = new();
        services.AddSingleton<ExtensionShellHub>();
        services.AddKeyedSingleton<IExtensionContext>(ExtensionContextAccess.KeyFor(hello.Id), (sp, _) => new ExtensionContext(hello, sp));
        hello.Register(services);
        ServiceProvider provider = services.BuildServiceProvider();
        PackContributionSet set = new([hello], provider);
        ModuleRegistry registry = new();
        foreach (IWorkspaceModule module in set.Packs.Single().Modules)
        {
            registry.Register(module);
        }

        MainViewModel vm = new(null, registry, TestLibraries.Empty(), hubTabs: set.HubTabs);
        vm.AttachStatusChips(set.StatusChips);
        vm.RestoreSession();
        return new ExternalHello(vm, set, provider, root);
    }

    [Test]
    public async Task AnExternalExtension_OwnsAHubWithTwoSections_ThatTheHostDraws() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using ExternalHello hello = ExternalHelloShell();
            MainViewModel vm = hello.Shell;
            List<string> texts = RenderedTexts(vm, "dev.example.hello.hub");
            HubTabViewModel hub = vm.HostViewModel("dev.example.hello.hub")!;
            using (Assert.Multiple())
            {
                await Assert.That(AssemblyLoadContext.GetLoadContext(hello.Set.Packs.Single().Pack.GetType().Assembly))
                    .IsTypeOf<ExternalLoadContext>();
                await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains("dev.example.hello.hub");
                await Assert.That(hub.Sections.Sections.Select(s => s.Header)).IsEquivalentTo(_helloSections,
                    TUnit.Assertions.Enums.CollectionOrdering.Matching);
                await Assert.That(hub.Sections.Sections.All(s => ExtensionGuards.For(s) is not null)).IsTrue()
                    .Because("each section is the guarded copy, run as the extension's");
                await Assert.That(texts).Contains("HELLO");
                await Assert.That(texts).Contains("First section");
            }
        });

    [Test]
    public async Task AnExternalExtensionsChip_RaisedOffTheUiThread_ShowsOnTheStripInTheHostsChip() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using ExternalHello hello = ExternalHelloShell();
            MainViewModel vm = hello.Shell;
            HostStatusChip chip = hello.Set.StatusChips.Single();
            await Assert.That(vm.Chips).DoesNotContain(chip.Chip).Because("nothing greeted yet");

            Type chipType = hello.Set.Packs.Single().Pack.GetType().Assembly.GetType("HelloExtension.HelloChip", true)!;
            object source = hello.Services.GetRequiredService(chipType);
            await Task.Run(() => chipType.GetMethod("Greeted")!.Invoke(source, ["match.dem"]));
            Dispatcher.UIThread.RunJobs();

            using (Assert.Multiple())
            {
                await Assert.That(vm.Chips).Contains(chip.Chip);
                await Assert.That(chip.Chip.Label).IsEqualTo("Hello · match.dem");
                await Assert.That(chip.Chip.DotState).IsEqualTo(StatusChipDotState.Good);
            }
        });

    private static WorkspaceTabDescriptor Section(string id, string header, int order, string hostId) => new()
    {
        TabId = id,
        Header = header,
        Order = order,
        HostId = hostId,
        ViewModelFactory = () => new PlaceholderTabViewModel(),
        ViewFactory = () => new TextBlock { Text = $"{header} section view" }
    };

    // A pack with at most one hub and one module of sections; nothing else.
    private sealed class FakeHubPack(string id, string featureId, HubTabContribution? hub, params WorkspaceTabDescriptor[] sections)
        : IExtension, IManifestSource
    {
        public string Id => id;
        public string FeatureId => featureId;
        public ExtensionManifest Manifest => FakeManifests.For(Id);
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
            if (hub is not null)
            {
                contributions.HubTab(hub);
            }

            if (sections.Length > 0)
            {
                contributions.Tabs(new SectionsModule(id + ".sections", sections));
            }
        }
    }

    private sealed class SectionsModule(string id, params WorkspaceTabDescriptor[] sections) : IWorkspaceModule
    {
        public string Id => id;
        public string DisplayName => "Fake sections";
        public Version ContractVersion => new(1, 0, 0);

        public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host) => sections;
    }

    private sealed class FakeGate : IFeatureGate
    {
        public Dictionary<string, bool> Answers { get; } = new(StringComparer.Ordinal);

        public UserCategory Category => UserCategory.Developer;

        public int HiddenCount => 0;

        public bool IsEnabled(string featureId) => !Answers.TryGetValue(featureId, out bool value) || value;

        public event EventHandler? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
