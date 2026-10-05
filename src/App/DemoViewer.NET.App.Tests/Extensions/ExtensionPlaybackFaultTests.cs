#region

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core.Timeline;
using Sdk = DemoViewer.NET.Extensions.Sdk.Playback;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     An SDK playback contribution whose every handler, track, lane, menu and factory throws, hosted on a real
///     2D tab: nothing reaches the tab, each fault is counted against the extension, and a per-frame
///     handler that throws on every frame counts once.
/// </summary>
[NotInParallel]
public class ExtensionPlaybackFaultTests
{
    private static readonly TimelineBandViewModel RoundBand =
        new(new TimelineBand("round", 0, 10, "7", "round 7", 0), 0, 1, Brushes.Gray);

    private static (Playback2DTabViewModel Vm, FaultRig Rig, SdkPlaybackContribution Hosted, ThrowingContribution Fake) Attach()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
        FaultRig rig = new();
        ThrowingContribution fake = new();
        SdkPlaybackContribution hosted = new(fake, rig.Guard);
        hosted.Attach(vm.Surface, ctx);
        return (vm, rig, hosted, fake);
    }

    [Test]
    public async Task APlayheadHandlerThatThrowsEveryFrame_LeavesTheFrameLoopRunning_AndCountsOnce()
    {
        (Playback2DTabViewModel vm, FaultRig rig, SdkPlaybackContribution hosted, _) = Attach();
        int laterHandler = 0;
        using IDisposable later = vm.Surface.OnPlayheadChanged(_ => laterHandler++);
        int before = rig.Faults.StateOf("pack.fake").Count;

        for (int tick = 0; tick < 100; tick++)
        {
            vm.Surface.NotifyPlayheadChanged(tick);
        }

        using (Assert.Multiple())
        {
            await Assert.That(laterHandler).IsEqualTo(100).Because("a host handler after the extension's still runs");
            await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(before + 1);
        }

        hosted.Detach();
        vm.Dispose();
    }

    [Test]
    public async Task KeysActionsAndTheDemoChange_AreContained()
    {
        (Playback2DTabViewModel vm, FaultRig rig, SdkPlaybackContribution hosted, _) = Attach();

        bool key = vm.Surface.TryHandleKey(Key.F9, KeyModifiers.None);
        bool action = vm.Surface.TryExecute("");
        vm.Surface.NotifyDemoChanged();

        using (Assert.Multiple())
        {
            await Assert.That(key).IsFalse();
            await Assert.That(action).IsFalse();
            await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsGreaterThanOrEqualTo(3);
        }

        hosted.Detach();
        vm.Dispose();
    }

    [Test]
    public async Task ABandMenuThatThrows_ShowsNoEntries_AndAThrowingEntryIsContained()
    {
        (Playback2DTabViewModel vm, FaultRig rig, SdkPlaybackContribution hosted, ThrowingContribution fake) = Attach();

        IReadOnlyList<MenuEntry> menu = vm.Timeline.MenuFor(RoundBand);
        await Assert.That(menu.Select(m => m.Header)).IsEquivalentTo([ThrowingContribution.EntryHeader])
            .Because("the throwing menu contributed nothing; the good one its entry");

        menu[0].Run();
        await Assert.That(rig.Faults.StateOf("pack.fake").LastSite).IsEqualTo("menu entry");

        hosted.Detach();
        await Assert.That(fake.Detached).IsTrue();
        vm.Dispose();
    }

    [Test]
    public async Task ALaneWhoseTrackThrows_StillBuildsTheTimeline()
    {
        (Playback2DTabViewModel vm, FaultRig rig, SdkPlaybackContribution hosted, _) = Attach();

        IReadOnlyList<string> lanes = [.. vm.Timeline.Lanes.Select(l => l.Track.Id)];
        _ = vm.Timeline.LaneBands.ToList();

        using (Assert.Multiple())
        {
            await Assert.That(lanes).Contains("pack.fake.track").Because("a throwing Id reads as the extension's fallback id");
            await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsGreaterThanOrEqualTo(1);
        }

        hosted.Detach();
        vm.Dispose();
    }

    [Test]
    public async Task APanelWhoseFactoriesThrow_ShowsThePlaceholder()
    {
        (Playback2DTabViewModel vm, FaultRig rig, SdkPlaybackContribution hosted, ThrowingContribution fake) = Attach();

        fake.Panel!.Open();
        object? content = vm.Surface.Panels.Single().View;

        using (Assert.Multiple())
        {
            await Assert.That(content).IsTypeOf<TextBlock>();
            await Assert.That(((TextBlock)content!).Text).Contains("Fake could not show this panel");
        }

        hosted.Detach();
        vm.Dispose();
    }

    private sealed class ThrowingContribution : Sdk.IPlaybackContribution
    {
        public const string EntryHeader = "Throws when run";

        private readonly List<IDisposable> _owned = [];

        public Sdk.IPanelHandle? Panel { get; private set; }
        public bool Detached { get; private set; }

        public void Attach(Sdk.IPlaybackSurface surface, IModuleContext context)
        {
            _owned.Add(surface.OnPlayheadChanged(_ => throw new InvalidOperationException("playhead")));
            _owned.Add(surface.OnDemoChanged(() => throw new InvalidOperationException("demo")));
            _owned.Add(surface.AddKeyHandler((_, _) => throw new InvalidOperationException("key")));
            _owned.Add(surface.AddActionHandler(_ => throw new InvalidOperationException("action")));
            _owned.Add(surface.AddBandMenu(_ => throw new InvalidOperationException("menu")));
            _owned.Add(surface.AddBandMenu(_ => [new Sdk.MenuEntry(EntryHeader, () => throw new InvalidOperationException("entry"))]));
            _owned.Add(surface.AddLane(new ThrowingTrack(), new ThrowingLane()));
            Panel = surface.AddPanel(0, () => throw new InvalidOperationException("panel vm"),
                () => throw new InvalidOperationException("panel view"));
            _owned.Add(Panel);
        }

        public void Detach()
        {
            Detached = true;
            throw new InvalidOperationException("detach");
        }
    }

    private sealed class ThrowingTrack : Sdk.ITimelineTrack
    {
        public string Id => throw new InvalidOperationException("id");
        public string DisplayName => "Throwing";

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public bool IsAvailable(Sdk.ITimelineData data) => true;

        public IReadOnlyList<Sdk.TimelineBand> BuildBands(Sdk.ITimelineData data) => throw new InvalidOperationException("bands");
    }

    private sealed class ThrowingLane : Sdk.ILaneBehaviour
    {
        public IEnumerable<Sdk.MenuEntry> MenuFor(Sdk.PlaybackBand band) => throw new InvalidOperationException("lane menu");
    }

}
