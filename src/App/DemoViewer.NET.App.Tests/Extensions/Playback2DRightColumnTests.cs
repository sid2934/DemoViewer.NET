#region

using Avalonia.Controls;
using Avalonia.Input;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.ViewModels;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The 2D tab's right-column hosting (items 17 and 18) over fake contributions: panels show in order while
///     open, several at once, each behind its own gate and, when bound to one, behind a contributed mode; a
///     closed panel's view model is disposed; the host follows the pack gate live and detaching removes every
///     panel; a panel's focus scope routes keys and actions; the cards collapse while a panel shows.
/// </summary>
public class Playback2DRightColumnTests
{
    private const string NarrowGate = "pack.fake.narrow";

    private static ModeToggle Mode() => new("fake.mode", "Mode", "A fake mode");

    private static (Playback2DTabViewModel Vm, Playback2DFakeContext Ctx) Tab()
    {
        Playback2DFakeContext ctx = new() { Gate = new FakeModuleFeatureGate() };
        Playback2DTabViewModel vm = new();
        vm.OnActivated(ctx);
        return (vm, ctx);
    }

    private static PlaybackContributionHost Host(IFeatureGate? gate, params IPlaybackContribution[] contributions) =>
        new([(new FakePack(), contributions)], gate);

    [Test]
    public async Task OpenPanels_ShowInOrder_SeveralAtOnce_AndOnlyWhileTheirModeIsOn()
    {
        (Playback2DTabViewModel vm, _) = Tab();
        ModeToggle mode = Mode();
        using IDisposable toggle = vm.Surface.AddModeToggle(mode);
        IPanelHandle second = vm.Surface.AddPanel(2, () => new FakePanelViewModel("second"), mode: mode);
        IPanelHandle first = vm.Surface.AddPanel(1, () => new FakePanelViewModel("first"), mode: mode);
        IPanelHandle third = vm.Surface.AddPanel(3, () => new FakePanelViewModel("third"), mode: mode);

        await Assert.That(vm.IsReviewAvailable).IsFalse().Because("a panel that is not open shows nothing");

        third.Open();
        first.Open();
        second.Open();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Surface.Panels.Select(p => ((FakePanelViewModel)p.Content!).Name))
                .IsEquivalentTo(["first", "second", "third"]);
            await Assert.That(vm.Surface.Panels.Select(p => p.IsShown)).IsEquivalentTo([false, false, false])
                .Because("the mode is off");
            await Assert.That(vm.IsReviewAvailable).IsTrue();
            await Assert.That(vm.IsCardStrip).IsFalse();
        }

        mode.IsOn = true;
        using (Assert.Multiple())
        {
            await Assert.That(vm.Surface.Panels.Select(p => p.IsShown)).IsEquivalentTo([true, true, true]);
            await Assert.That(first.IsShown).IsTrue();
            await Assert.That(vm.IsCardStrip).IsTrue();
        }

        first.Open();
        await Assert.That(vm.Surface.Panels.Count).IsEqualTo(3).Because("Open on an open panel is a no-op");

        // A panel bound to no mode shows as soon as it is open; disposing a bound one drops its mode subscription.
        mode.IsOn = false;
        IPanelHandle free = vm.Surface.AddPanel(0, () => new FakePanelViewModel("free"));
        free.Open();
        first.Dispose();
        mode.IsOn = true;
        using (Assert.Multiple())
        {
            await Assert.That(free.IsShown).IsTrue();
            await Assert.That(first.IsShown).IsFalse();
            await Assert.That(vm.Surface.Panels.Select(p => ((FakePanelViewModel)p.Content!).Name))
                .IsEquivalentTo(["free", "second", "third"]);
        }

        vm.Dispose();
    }

    [Test]
    public async Task Close_DisposesTheViewModel_AndKeepsTheOthers()
    {
        (Playback2DTabViewModel vm, _) = Tab();
        IPanelHandle a = vm.Surface.AddPanel(0, () => new FakePanelViewModel("a"));
        IPanelHandle b = vm.Surface.AddPanel(1, () => new FakePanelViewModel("b"));
        a.Open();
        b.Open();
        FakePanelViewModel opened = (FakePanelViewModel)vm.Surface.Panels[0].Content!;
        int closed = 0;
        a.Closed += () => closed++;

        a.Close();
        using (Assert.Multiple())
        {
            await Assert.That(opened.Disposed).IsTrue();
            await Assert.That(closed).IsEqualTo(1);
            await Assert.That(a.IsOpen).IsFalse();
            await Assert.That(a.IsShown).IsFalse();
            await Assert.That(b.IsOpen).IsTrue();
            await Assert.That(vm.Surface.Panels.Count).IsEqualTo(1);
        }

        a.Open();
        await Assert.That(vm.Surface.Panels[0].Content).IsNotSameReferenceAs(opened).Because("reopening builds afresh");

        vm.Dispose();
    }

    [Test]
    public async Task AGatedPanel_FollowsItsFeature_AndReviewAvailabilityFollowsTheGatesOn()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Tab();
        IPanelHandle gated = vm.Surface.AddPanel(0, () => new FakePanelViewModel("gated"), featureId: NarrowGate);
        int shownChanges = 0;
        gated.ShownChanged += () => shownChanges++;
        gated.Open();
        await Assert.That(gated.IsShown).IsTrue();

        ctx.Gate!.SetEnabled(NarrowGate, false);
        using (Assert.Multiple())
        {
            await Assert.That(gated.IsOpen).IsTrue().Because("the gate hides; it does not close");
            await Assert.That(gated.IsShown).IsFalse();
            await Assert.That(vm.IsReviewAvailable).IsFalse().Because("the only panel's gate is off");
            await Assert.That(vm.IsCardStrip).IsFalse();
        }

        ctx.Gate.SetEnabled(NarrowGate, true);
        using (Assert.Multiple())
        {
            await Assert.That(gated.IsShown).IsTrue();
            await Assert.That(vm.IsReviewAvailable).IsTrue();
            await Assert.That(shownChanges).IsEqualTo(3);
        }

        vm.Dispose();
    }

    [Test]
    public async Task TheHost_FollowsThePackGateLive_DisposingPanelsOnTheWayOff()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Tab();
        FakeContribution fake = new();
        FakeGate gate = new() { On = false };

        using IDisposable binding = Host(gate, fake).Attach(vm.Surface, ctx);
        await Assert.That(vm.Surface.Panels).IsEmpty().Because("the pack is off");

        gate.On = true;
        gate.Raise();
        FakePanelViewModel built = (FakePanelViewModel)vm.Surface.Panels.Single().Content!;
        await Assert.That(fake.Attached).IsEqualTo(1);

        gate.On = false;
        gate.Raise();
        using (Assert.Multiple())
        {
            await Assert.That(fake.Detached).IsEqualTo(1);
            await Assert.That(vm.Surface.Panels).IsEmpty();
            await Assert.That(built.Disposed).IsTrue();
            await Assert.That(vm.IsReviewAvailable).IsFalse();
        }

        vm.Dispose();
    }

    [Test]
    public async Task Detach_RemovesEveryPanel_AndTheTabsDisposeDetaches()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Tab();
        FakeContribution fake = new();
        IDisposable binding = Host(null, fake).Attach(vm.Surface, ctx);
        FakePanelViewModel built = (FakePanelViewModel)vm.Surface.Panels.Single().Content!;
        IPanelHandle handle = fake.Panel!;

        binding.Dispose();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Surface.Panels).IsEmpty();
            await Assert.That(built.Disposed).IsTrue();
            await Assert.That(handle.IsOpen).IsFalse();
            await Assert.That(fake.Detached).IsEqualTo(1);
        }

        FakeContribution second = new();
        Playback2DTabViewModel owned = new() { Contributions = Host(null, second) };
        owned.OnActivated(new Playback2DFakeContext { Gate = new FakeModuleFeatureGate() });
        FakePanelViewModel ownedPanel = (FakePanelViewModel)owned.Surface.Panels.Single().Content!;
        owned.Dispose();
        using (Assert.Multiple())
        {
            await Assert.That(second.Detached).IsEqualTo(1);
            await Assert.That(ownedPanel.Disposed).IsTrue();
        }

        vm.Dispose();
    }

    [Test]
    public async Task AContributedView_IsPresentedWithTheViewModelAsItsDataContext()
    {
        (Playback2DTabViewModel vm, _) = Tab();
        IPanelHandle panel = vm.Surface.AddPanel(0, () => new FakePanelViewModel("viewed"), () => new TextBlock());
        panel.Open();

        Playback2DPanel item = vm.Surface.Panels.Single();
        using (Assert.Multiple())
        {
            await Assert.That(item.View).IsTypeOf<TextBlock>();
            await Assert.That(((TextBlock)item.View!).DataContext).IsSameReferenceAs(item.Content);
        }

        panel.Close();
        await Assert.That(item.View).IsNull();
        vm.Dispose();
    }

    [Test]
    public async Task KeysGoToTheHandlersFirst_ActionsToAFocusedPanelFirst_AndToTheRestLast()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Tab();
        ModeToggle mode = Mode();
        mode.IsOn = true;
        IPanelHandle panel = vm.Surface.AddPanel(0, () => new FakePanelViewModel("keys"), mode: mode);
        panel.Open();
        List<Key> keys = [];
        List<Playback2DAction> actions = [];
        using IDisposable keyHandler = vm.Surface.AddKeyHandler((k, _) =>
        {
            keys.Add(k);
            return k == Key.D1;
        });
        using IDisposable actionHandler = vm.Surface.AddActionHandler(a =>
        {
            actions.Add(a);
            return a is Playback2DAction.TogglePlay or Playback2DAction.TagNote;
        });

        using (Assert.Multiple())
        {
            await Assert.That(vm.Surface.TryHandleKey(Key.D1, KeyModifiers.None)).IsTrue();
            await Assert.That(vm.Surface.TryHandleKey(Key.D2, KeyModifiers.None)).IsFalse();
            await Assert.That(keys).IsEquivalentTo([Key.D1, Key.D2]);
        }

        // Unfocused: the tab's own actions never reach the handler; its unknown ones do.
        await Assert.That(vm.ExecuteAction(Playback2DAction.TogglePlay)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(ctx.PlayCount).IsEqualTo(1).Because("the tab played; the handler was not asked");
            await Assert.That(actions).IsEmpty();
            await Assert.That(vm.ExecuteAction(Playback2DAction.TagNote)).IsTrue();
            await Assert.That(actions).IsEquivalentTo([Playback2DAction.TagNote]);
            await Assert.That(vm.Surface.HasKeyboard).IsFalse();
        }

        // Focused: the handler is asked first and can take a core action.
        panel.HasKeyboard = true;
        actions.Clear();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Surface.HasKeyboard).IsTrue();
            await Assert.That(vm.ExecuteAction(Playback2DAction.TogglePlay)).IsTrue();
            await Assert.That(ctx.PlayCount).IsEqualTo(1).Because("the focused panel took play");
            await Assert.That(vm.ExecuteAction(Playback2DAction.StepForward)).IsTrue().Because("what it declines falls to the tab");
            await Assert.That(actions).IsEquivalentTo([Playback2DAction.TogglePlay, Playback2DAction.StepForward]);
        }

        mode.IsOn = false;
        await Assert.That(vm.Surface.HasKeyboard).IsFalse().Because("a hidden panel cannot hold the keyboard");

        vm.Dispose();
    }

    private sealed class FakeContribution : IPlaybackContribution
    {
        public int Attached { get; private set; }
        public int Detached { get; private set; }
        public IPanelHandle? Panel { get; private set; }

        public void Attach(IPlaybackSurface surface, IModuleContext context)
        {
            Attached++;
            Panel = surface.AddPanel(0, () => new FakePanelViewModel("contributed"));
            Panel.Open();
        }

        public void Detach()
        {
            Detached++;
            Panel?.Dispose();
            Panel = null;
        }
    }

    private sealed class FakePanelViewModel(string name) : ViewModelBase, IDisposable
    {
        public string Name => name;
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakePack : IFeaturePack
    {
        public string Id => "net.demoviewer.pack.fake";
        public string FeatureId => "pack.fake";
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
