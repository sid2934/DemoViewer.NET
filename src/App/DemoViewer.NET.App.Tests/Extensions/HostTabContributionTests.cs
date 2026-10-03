#region

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     Host tabs as contributions (item 12), apart from the Strat Book: a fake pack declares one host tab and
///     two sections that name it, and the shell builds the strip from <see cref="PackContributionSet" />
///     alone. Nothing here knows the pack beyond its id.
/// </summary>
[Category("Render")]
public class HostTabContributionTests
{
    private const string PackId = "pack.fake";
    private const string HostId = "fake.hub";

    private static readonly string[] _bothSections = ["fake.alpha", "fake.beta"];

    private static (MainViewModel Shell, FakeGate Gate) NewShell(bool packOn = true)
    {
        FakePack pack = new();
        PackContributionSet set = new([pack], new ServiceCollection().BuildServiceProvider());
        ModuleRegistry registry = new();
        foreach (IWorkspaceModule module in set.Packs.Single().Modules)
        {
            registry.Register(module);
        }

        FakeGate gate = new();
        if (!packOn)
        {
            gate.Answers[PackId] = false;
        }

        MainViewModel vm = new(null, registry, TestLibraries.Empty(), null, gate, hostTabs: set.HostTabs);
        vm.RestoreSession();
        return (vm, gate);
    }

    [Test]
    public async Task TheCollector_StampsTheHostTab_WithThePackFeatureId()
    {
        PackContributionSet set = new([new FakePack()], new ServiceCollection().BuildServiceProvider());
        HostTabContribution host = set.HostTabs.Single();
        using (Assert.Multiple())
        {
            await Assert.That(host.HostId).IsEqualTo(HostId);
            await Assert.That(host.TabId).IsEqualTo(HostId);
            await Assert.That(host.FeatureId).IsEqualTo(PackId).Because("a null feature id takes the owning pack's");
            await Assert.That(host.RailLabel).IsEqualTo("FAKE RAIL");
        }
    }

    [Test]
    public async Task AFakePack_HostsItsTwoSections_AndTheRailRendersBoth() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (MainViewModel vm, _) = NewShell();
            List<string> texts = [];
            try
            {
                Window window = new() { Width = 1280, Height = 800, Content = new MainView { DataContext = vm } };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                vm.TrySelectTab(HostId);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                texts.AddRange(window.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text ?? "")
                    .Where(t => t.Length > 0));
                window.Close();

                IHostTabViewModel host = vm.HostViewModel(HostId)!;
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains(HostId)
                        .Because("the host tab comes from the contribution, not from the shell");
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain("fake.alpha");
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain("fake.beta");
                    await Assert.That(host.Sections.Sections.Select(s => s.TabId)).IsEquivalentTo(_bothSections);
                    await Assert.That(host.RailLabel).IsEqualTo("FAKE RAIL").Because("the shell sets it from the contribution");
                    await Assert.That(texts).Contains("FAKE RAIL").Because("the band binds the VM's label, nothing hardcoded");
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
                        .Because("the host's own feature id is the pack's, and off means no tab");
                    await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("builtin.library")
                        .Because("a host that goes away lands on Library, not on whatever sorts before it");
                    await Assert.That(vm.TrySelectTab("fake.beta")).IsFalse()
                        .Because("a section of a hidden host is not here");
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
                IHostTabViewModel host = vm.HostViewModel(HostId)!;
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
                        .Because("the host VM exists from the strip build so a gate flip has somewhere to reconcile into");
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
            registry.Register(new OrphanModule());
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

    // A pack with one host tab and one module of two sections; the host's view is built in code so the
    // test owns what the rail shows.
    private sealed class FakePack : IFeaturePack
    {
        public string Id => "net.demoviewer.test.fakepack";
        public string FeatureId => PackId;
        public IEnumerable<FeatureDescriptor> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IPackContributions contributions, IServiceProvider sp)
        {
            contributions.HostTab(new HostTabContribution(HostId, HostId, "Fake", 7, "FAKE RAIL",
                () => new FakeHostViewModel(), () => new FakeHostView()));
            contributions.Module(new SectionsModule());
        }
    }

    private sealed class SectionsModule : IWorkspaceModule
    {
        public string Id => "net.demoviewer.test.fakesections";
        public string DisplayName => "Fake sections";
        public Version ContractVersion => new(1, 0, 0);

        public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
        {
            yield return Section("fake.beta", "Beta", 1);
            yield return Section("fake.alpha", "Alpha", 0);
        }

        private static WorkspaceTabDescriptor Section(string id, string header, int order) => new()
        {
            TabId = id,
            Header = header,
            Order = order,
            HostId = HostId,
            ViewModelFactory = () => new PlaceholderTabViewModel(),
            ViewFactory = () => new TextBlock { Text = $"{header} section view" }
        };
    }

    private sealed class OrphanModule : IWorkspaceModule
    {
        public string Id => "net.demoviewer.test.orphan";
        public string DisplayName => "Orphan";
        public Version ContractVersion => new(1, 0, 0);

        public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
        {
            yield return new WorkspaceTabDescriptor
            {
                TabId = "orphan.section",
                Header = "Orphan",
                HostId = "nobody.hosts.this",
                ViewFactory = () => new TextBlock()
            };
        }
    }

    private sealed class FakeHostViewModel : IHostTabViewModel
    {
        public TabSectionHost Sections { get; } = new(autoSelectFirst: true);

        public string RailLabel { get; set; } = "";

        public void OnActivated(IModuleContext context) => Sections.OnHostActivated(context);

        public void OnDeactivated() => Sections.OnHostDeactivated();
    }

    // The rail band, the section list and the selected section's content, bound the way the real hub binds:
    // the band reads the VM's RailLabel, which the shell set from the contribution.
    private sealed class FakeHostView : DockPanel
    {
        public FakeHostView()
        {
            TextBlock band = new();
            band.Bind(TextBlock.TextProperty, new Binding("RailLabel"));
            SetDock(band, Dock.Top);
            ListBox rail = new()
            {
                ItemTemplate = new FuncDataTemplate<WorkspaceTabDescriptor>((d, _) => new TextBlock { Text = d.Header })
            };
            rail.Bind(ItemsControl.ItemsSourceProperty, new Binding("Sections.Sections"));
            rail.Bind(SelectingItemsControl.SelectedItemProperty, new Binding("Sections.SelectedSection") { Mode = BindingMode.TwoWay });
            SetDock(rail, Dock.Left);
            ContentControl content = new();
            content.Bind(ContentControl.ContentProperty, new Binding("Sections.SelectedSection.ActiveContent"));
            Children.Add(band);
            Children.Add(rail);
            Children.Add(content);
        }
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
