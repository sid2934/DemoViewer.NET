#region

using Avalonia.Media;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core.Timeline;
using DemoViewer.NET.ViewModels;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The 2D tab's contribution surface (item 16) over a fake contribution: a band-menu entry and a pane
///     arrive together on attach and leave together on detach, the entry's action opens the pane, one side
///     pane shows at a time and a closed pane's view model is disposed, and the host follows the gate live.
/// </summary>
public class PlaybackContributionTests
{
    private static readonly TimelineBandViewModel RoundBand =
        new(new TimelineBand("round", 0, 10, "7", "round 7", 0), 0, 1, Brushes.Gray);

    private static (Playback2DTabViewModel Vm, Playback2DFakeContext Ctx) Tab()
    {
        Playback2DFakeContext ctx = new();
        Playback2DTabViewModel vm = new();
        vm.OnActivated(ctx);
        return (vm, ctx);
    }

    private static PlaybackContributionHost Host(IFeatureGate? gate, params IPlaybackContribution[] contributions) =>
        new([(new FakePack(), contributions)], gate);

    [Test]
    public async Task Attach_AddsTheEntryAndThePane_AndTheEntryOpensThePane()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Tab();
        FakeContribution fake = new();

        using IDisposable binding = Host(null, fake).Attach(vm.Surface, ctx);
        IReadOnlyList<MenuEntry> menu = vm.Timeline.MenuFor(RoundBand);

        using (Assert.Multiple())
        {
            await Assert.That(fake.Context).IsSameReferenceAs(ctx);
            await Assert.That(menu.Select(m => m.Header)).IsEquivalentTo([FakeContribution.Header]);
            await Assert.That(vm.Surface.Panes.Count).IsEqualTo(1);
            await Assert.That(vm.Surface.SidePane).IsNull();
        }

        menu[0].Run();

        using (Assert.Multiple())
        {
            await Assert.That(vm.Surface.SidePane).IsTypeOf<FakePaneViewModel>();
            await Assert.That(fake.Pane!.IsOpen).IsTrue();
        }

        vm.Dispose();
    }

    [Test]
    public async Task Detach_RemovesTheEntryAndThePane_AndDisposesTheOpenPane()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Tab();
        FakeContribution fake = new();
        IDisposable binding = Host(null, fake).Attach(vm.Surface, ctx);
        vm.Timeline.MenuFor(RoundBand)[0].Run();
        FakePaneViewModel opened = (FakePaneViewModel)vm.Surface.SidePane!;
        int closed = 0;
        fake.Pane!.Closed += () => closed++;

        binding.Dispose();

        using (Assert.Multiple())
        {
            await Assert.That(fake.Detached).IsEqualTo(1);
            await Assert.That(vm.Timeline.MenuFor(RoundBand)).IsEmpty();
            await Assert.That(vm.Surface.Panes).IsEmpty();
            await Assert.That(vm.Surface.SidePane).IsNull();
            await Assert.That(opened.Disposed).IsTrue();
            await Assert.That(closed).IsEqualTo(1);
        }

        vm.Dispose();
    }

    [Test]
    public async Task TheHost_FollowsTheGate_AttachingAndDetachingLive()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Tab();
        FakeContribution fake = new();
        FakeGate gate = new() { On = false };

        using IDisposable binding = Host(gate, fake).Attach(vm.Surface, ctx);
        await Assert.That(vm.Timeline.MenuFor(RoundBand)).IsEmpty().Because("the pack is off");
        await Assert.That(fake.Attached).IsEqualTo(0);

        gate.On = true;
        gate.Raise();
        await Assert.That(vm.Timeline.MenuFor(RoundBand).Count).IsEqualTo(1).Because("the pack came on");
        vm.Timeline.MenuFor(RoundBand)[0].Run();
        FakePaneViewModel opened = (FakePaneViewModel)vm.Surface.SidePane!;

        gate.Raise();
        await Assert.That(fake.Attached).IsEqualTo(1).Because("a change that keeps the pack on attaches nothing twice");

        gate.On = false;
        gate.Raise();
        using (Assert.Multiple())
        {
            await Assert.That(fake.Detached).IsEqualTo(1);
            await Assert.That(vm.Timeline.MenuFor(RoundBand)).IsEmpty();
            await Assert.That(vm.Surface.SidePane).IsNull().Because("the pack going off closes its pane");
            await Assert.That(opened.Disposed).IsTrue();
        }

        vm.Dispose();
    }

    [Test]
    public async Task TheTab_AttachesOnFirstActivation_AndDetachesOnDispose()
    {
        FakeContribution fake = new();
        Playback2DTabViewModel vm = new() { Contributions = Host(null, fake) };
        Playback2DFakeContext ctx = new();
        await Assert.That(fake.Attached).IsEqualTo(0).Because("nothing attaches before the context arrives");

        vm.OnActivated(ctx);
        vm.OnDeactivated();
        vm.OnActivated(ctx);
        await Assert.That(fake.Attached).IsEqualTo(1).Because("once per tab view-model, not per activation");
        await Assert.That(vm.Timeline.MenuFor(RoundBand).Count).IsEqualTo(1);

        vm.Dispose();
        using (Assert.Multiple())
        {
            await Assert.That(fake.Detached).IsEqualTo(1);
            await Assert.That(vm.Timeline.MenuFor(RoundBand)).IsEmpty();
        }
    }

    [Test]
    public async Task OneSidePaneAtATime_AndOpeningAnOpenPaneRebuildsIt()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Tab();
        FakeContribution first = new();
        FakeContribution second = new();
        using IDisposable binding = Host(null, first, second).Attach(vm.Surface, ctx);

        first.Pane!.Open();
        FakePaneViewModel a = (FakePaneViewModel)vm.Surface.SidePane!;
        first.Pane.Open();
        FakePaneViewModel a2 = (FakePaneViewModel)vm.Surface.SidePane!;
        using (Assert.Multiple())
        {
            await Assert.That(a2).IsNotSameReferenceAs(a).Because("Open on an open pane builds a fresh view model");
            await Assert.That(a.Disposed).IsTrue();
        }

        second.Pane!.Open();
        using (Assert.Multiple())
        {
            await Assert.That(first.Pane.IsOpen).IsFalse();
            await Assert.That(second.Pane.IsOpen).IsTrue();
            await Assert.That(a2.Disposed).IsTrue();
        }

        vm.Surface.CloseSidePane();
        using (Assert.Multiple())
        {
            await Assert.That(second.Pane.IsOpen).IsFalse();
            await Assert.That(vm.Surface.SidePane).IsNull();
        }

        vm.Dispose();
    }

    [Test]
    public async Task TheTabsLifecycle_ClosesAnOpenPane_OnDeactivationAndDemoReset()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Tab();
        FakeContribution fake = new();
        using IDisposable binding = Host(null, fake).Attach(vm.Surface, ctx);

        fake.Pane!.Open();
        ctx.RaiseDemoReset();
        await Assert.That(vm.Surface.SidePane).IsNull().Because("another demo replaced the one the pane worked on");

        fake.Pane.Open();
        vm.OnDeactivated();
        await Assert.That(vm.Surface.SidePane).IsNull();
        await Assert.That(vm.Timeline.MenuFor(RoundBand).Count).IsEqualTo(1).Because("deactivation keeps the contribution");

        vm.Dispose();
    }

    [Test]
    public async Task TheRightColumn_IsAPanel_NotASidePane()
    {
        (Playback2DTabViewModel vm, _) = Tab();

        IPaneHandle handle = vm.Surface.AddPane(PanePlacement.RightColumn, 0, () => new FakePaneViewModel());
        using (Assert.Multiple())
        {
            await Assert.That(handle).IsAssignableTo<IPanelHandle>();
            await Assert.That(vm.Surface.Panes).IsEmpty().Because("a right-column pane is not a side pane");
        }

        handle.Open();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Surface.Panels.Count).IsEqualTo(1);
            await Assert.That(vm.Surface.SidePane).IsNull();
        }

        vm.Dispose();
    }

    private sealed class FakeContribution : IPlaybackContribution
    {
        public const string Header = "Fake entry";
        private IDisposable? _menu;

        public int Attached { get; private set; }
        public int Detached { get; private set; }
        public IModuleContext? Context { get; private set; }
        public IPaneHandle? Pane { get; private set; }

        public void Attach(IPlaybackSurface surface, IModuleContext context)
        {
            Attached++;
            Context = context;
            _menu = surface.AddBandMenu(_ => [new MenuEntry(Header, () => Pane!.Open())]);
            Pane = surface.AddPane(PanePlacement.Side, 0, () => new FakePaneViewModel());
        }

        public void Detach()
        {
            Detached++;
            _menu?.Dispose();
            Pane?.Dispose();
            _menu = null;
            Pane = null;
            Context = null;
        }
    }

    private sealed class FakePaneViewModel : ViewModelBase, IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakePack : IFeaturePack
    {
        public string Id => "net.demoviewer.pack.fake";
        public string FeatureId => "pack.fake";
        public ExtensionManifest Manifest => FakeManifests.For(Id);
        public IEnumerable<FeatureDescriptor> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IPackContributions contributions, IServiceProvider sp)
        {
        }
    }

    private sealed class FakeGate : IFeatureGate
    {
        public bool On { get; set; } = true;
        public UserCategory Category => UserCategory.PowerUser;
        public int HiddenCount => 0;
        public bool IsEnabled(string featureId) => On;
        public event EventHandler? Changed;
        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
