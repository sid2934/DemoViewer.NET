#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Core;

#endregion

namespace DemoViewer.NET.AppTests.Extensions.StratBook;

/// <summary>
///     Find Rounds Like This and the Situations result walk as a playback contribution (item 20): the
///     toolbar item exists only with the pack on and a demo's map name open, and follows a demo change
///     live; its Run hands the current frame to <see cref="IFindRoundsLikeThis" />, the same funnel the
///     toolbar button, the overflow menu entry and Ctrl+F all share through <c>Surface.TryExecute</c>; its
///     label and tooltip carry the resolved gesture and follow a rebind; and J/K dispatch to
///     <see cref="ISituationResultWalk" />, gated by the Situations tab's own feature. The seam-level
///     behaviour (the token round-trip, the shipped seam's tab switch) is <c>FindRoundsLikeThisTests</c> and
///     <c>SituationResultWalkTests</c>; this is the contribution's own wiring.
/// </summary>
[NotInParallel]
public class SituationsPlaybackContributionTests
{
    private static (Playback2DTabViewModel Vm, Playback2DFakeContext Ctx, SituationsPlaybackContribution Situations) Tab(
        IFeatureGate? gate = null, Action<Playback2DFakeContext>? configure = null)
    {
        SituationsPlaybackContribution situations = new();
        PlaybackContributionHost host = new([(new StratBookPack(), [situations])], gate);
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) =
            Playback2DTimelineHarness.Tab(contributions: host, configure: configure);
        return (vm, ctx, situations);
    }

    [Test]
    public async Task PackOff_HasNoToolbarItem_NoMenuEntry_AndTheWalkKeysAreUnhandled()
    {
        FakeGate gate = new() { On = false };
        (Playback2DTabViewModel vm, _, SituationsPlaybackContribution situations) =
            Tab(gate, c => c.MapName = "de_dust2");

        using (Assert.Multiple())
        {
            await Assert.That(situations.ToolbarItem).IsNull();
            await Assert.That(vm.Surface.ToolbarItems).IsEmpty();
            await Assert.That(vm.Surface.HasToolbarItems).IsFalse();
            await Assert.That(vm.ExecuteAction(Playback2DAction.FindRoundsLikeThis)).IsFalse();
            await Assert.That(vm.ExecuteAction(Playback2DAction.NextSituationResult)).IsFalse();
        }

        gate.On = true;
        gate.Raise();
        using (Assert.Multiple())
        {
            await Assert.That(situations.ToolbarItem).IsNotNull().Because("the pack came on with a map already open");
            await Assert.That(vm.Surface.ToolbarItems).IsEquivalentTo([situations.ToolbarItem]);
        }

        vm.Dispose();
    }

    [Test]
    public async Task ToolbarItem_IsPresentOnlyWithADemosMapName_AndFollowsADemoChangeLive()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx, SituationsPlaybackContribution situations) =
            Tab(configure: c => c.MapName = null);

        await Assert.That(vm.Surface.ToolbarItems).IsEmpty().Because("no map name yet, same condition the key always required");

        ctx.MapName = "de_dust2";
        ctx.RaiseDemoReset();
        using (Assert.Multiple())
        {
            await Assert.That(situations.ToolbarItem).IsNotNull();
            await Assert.That(vm.Surface.ToolbarItems).IsEquivalentTo([situations.ToolbarItem]);
        }

        ctx.MapName = null;
        ctx.RaiseDemoReset();
        await Assert.That(vm.Surface.ToolbarItems).IsEmpty().Because("the map closed");

        vm.Dispose();
    }

    [Test]
    public async Task Run_CallsIFindRoundsLikeThis_Once_WithTheCurrentFrame_AndRefusesWithoutOneOrAFrame()
    {
        // A bare context with no roster (not the harness's three-player fake): the activation resync then
        // builds an empty scene, which is the state before the first push.
        RecordingSeam seam = new();
        SituationsPlaybackContribution situations = new();
        PlaybackContributionHost host = new([(new StratBookPack(), [situations])], null);
        Playback2DTabViewModel vm = new() { Contributions = host };
        Playback2DFakeContext ctx = new() { MapName = "de_dust2" };
        ctx.SetService<IFindRoundsLikeThis>(seam);
        vm.OnActivated(ctx);

        // Nothing pushed yet: the scene is empty, so the seam is never asked.
        await Assert.That(vm.ExecuteAction(Playback2DAction.FindRoundsLikeThis)).IsFalse();
        await Assert.That(seam.Calls.Count).IsEqualTo(0);

        ctx.PushPlacedMarkers((1, 3, 600, -400, 64, "BombsiteA"), (6, 2, -900, 200, 64, "Lobby"));

        situations.ToolbarItem!.Command!.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(seam.Calls.Count).IsEqualTo(1);
            (string map, SceneTime time, IReadOnlyList<PlayerMarker> markers) = seam.Calls[0];
            await Assert.That(map).IsEqualTo("de_dust2");
            await Assert.That(time.Tick).IsEqualTo(ctx.CurrentTick).Because("the frame the markers belong to");
            await Assert.That(markers.Select(m => m.Place ?? "?")).IsEquivalentTo(["BombsiteA", "Lobby"]);
        }

        // Ctrl+F and the toolbar button run the same funnel.
        await Assert.That(vm.ExecuteAction(Playback2DAction.FindRoundsLikeThis)).IsTrue();
        await Assert.That(seam.Calls.Count).IsEqualTo(2);

        vm.Dispose();
    }

    [Test]
    public async Task TheLabelAndTheToolTip_ReadTheResolvedProfile_AndFollowARebind()
    {
        (Playback2DTabViewModel vm, _, SituationsPlaybackContribution situations) = Tab(configure: c => c.MapName = "de_dust2");
        ToolbarItem item = situations.ToolbarItem!;

        using (Assert.Multiple())
        {
            await Assert.That(item.Label).IsEqualTo("Find rounds like this (Ctrl+F)");
            await Assert.That(item.Tooltip).StartsWith("Find rounds like this (Ctrl+F): ");
        }

        vm.ApplyKeymapOverrides(["FindRoundsLikeThis=Ctrl+Shift+S"]);

        using (Assert.Multiple())
        {
            await Assert.That(item.Label).IsEqualTo("Find rounds like this (Ctrl+Shift+S)");
            await Assert.That(item.Tooltip).StartsWith("Find rounds like this (Ctrl+Shift+S): ");
        }

        vm.Dispose();
    }

    [Test]
    public async Task JK_DispatchToTheWalkSeam_GatedByTheSituationsTabFeature()
    {
        RecordingWalk walk = new();
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx, SituationsPlaybackContribution _) = Tab(
            configure: c =>
            {
                c.MapName = "de_dust2";
                c.SetService<ISituationResultWalk>(walk);
            });

        using (Assert.Multiple())
        {
            await Assert.That(vm.ExecuteAction(Playback2DAction.NextSituationResult)).IsTrue();
            await Assert.That(vm.ExecuteAction(Playback2DAction.PrevSituationResult)).IsTrue();
            await Assert.That(walk.Directions).IsEquivalentTo([1, -1]);
        }

        // With nothing to walk to, the seam's own answer leaves the key unhandled.
        walk.Answer = false;
        await Assert.That(vm.ExecuteAction(Playback2DAction.NextSituationResult)).IsFalse();
        await Assert.That(walk.Directions.Count).IsEqualTo(3);

        // Gated off: the Situations tab's own feature, not the pack's, and the seam is never asked.
        walk.Answer = true;
        ctx.Gate!.SetEnabled(SituationsModule.TabFeatureId, false);
        await Assert.That(vm.ExecuteAction(Playback2DAction.NextSituationResult)).IsFalse();
        await Assert.That(walk.Directions.Count).IsEqualTo(3);

        vm.Dispose();
    }

    private sealed class RecordingSeam : IFindRoundsLikeThis
    {
        public List<(string Map, SceneTime Time, IReadOnlyList<PlayerMarker> Markers)> Calls { get; } = [];

        public bool Show(string map, SceneTime time, IReadOnlyList<PlayerMarker> markers)
        {
            Calls.Add((map, time, [.. markers]));
            return markers.Any(m => m.IsAlive);
        }
    }

    private sealed class RecordingWalk : ISituationResultWalk
    {
        public List<int> Directions { get; } = [];

        public bool Answer { get; set; } = true;

        public bool Walk(int direction)
        {
            Directions.Add(direction);
            return Answer;
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
