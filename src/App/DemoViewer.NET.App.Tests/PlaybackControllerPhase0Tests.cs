#region

using Cs2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Phase 0 regression gate (modular-UI framework): the <c>PlaybackController</c> becomes the
///     single position-move code path, but discrete navigation must behave identically. These tests
///     drive a real demo through the headless shell and assert the controller's observable position
///     tracks the selected frame, and that a single seek does not double-fire the entity seek.
/// </summary>
[NotInParallel]
public class PlaybackControllerPhase0Tests
{
    [Test]
    public async Task SeekToFrame_KeepsControllerPositionInSyncWithSelectedFrame()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());
            await vm.AutoLoadDemoAsync(demo);

            await Assert.That(vm.Frames.Count).IsGreaterThan(0);

            // Discrete navigation via the Navigation seam now routes through the controller.
            int target = Math.Min(5, vm.Frames.Count - 1);
            vm.Navigation.SeekToFrame(target);

            // The Parser-tab master selection is the demo frame at the target index…
            await Assert.That(vm.SelectedFrame).IsEqualTo(vm.Frames[target]);
            // …and the controller's observable position matches (single code path, in sync).
            await Assert.That(vm.Playback.CurrentFrameIndex).IsEqualTo(target);
            await Assert.That(vm.Playback.CurrentTick).IsEqualTo(vm.Frames[target].ServerTick);
            await Assert.That(vm.Playback.HasDemo).IsTrue();
            await Assert.That(vm.Playback.TickRate).IsGreaterThan(0);
        });
    }

    [Test]
    public async Task StepForward_StepsAuthoritativeTrackerInPlace_NotRebuildFromZero()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());
            await vm.AutoLoadDemoAsync(demo);
            await Assert.That(vm.Frames.Count).IsGreaterThan(60);

            int target = 40;
            vm.Navigation.SeekToFrame(target);

            // The discrete seek is async (debounce + Task.Run). Pump the dispatcher until the
            // authoritative tracker has been published and sits exactly at the target frame.
            EntityTracker? t = await WaitForTrackerAt(vm, target);
            await Assert.That(t).IsNotNull();

            // ── The discriminating assertion ──
            // StepForward must advance the SAME tracker instance one frame (O(1)), NOT rebuild a
            // fresh one from zero. ReferenceEquals catches the "StepForward delegates to a discrete
            // SeekToFrame" regression that frame-index checks alone would miss.
            vm.Playback.StepForward();

            await Assert.That(ReferenceEquals(t, vm.Playback.AuthoritativeTracker)).IsTrue();
            await Assert.That(t!.CurrentFrameIndex).IsEqualTo(target + 1);
            await Assert.That(vm.Playback.CurrentFrameIndex).IsEqualTo(target + 1);

            // …and the in-place-stepped entity set equals an independent seek-to-(N+1).
            EntityTracker fresh = new();
            fresh.AdvanceToIndex(target + 1, vm.Frames.ToList());

            Dictionary<int, EntityState> stepped =
                t.CurrentEntities.AllIndexed().ToDictionary(x => x.Index, x => x.Entity);
            Dictionary<int, EntityState> reference =
                fresh.CurrentEntities.AllIndexed().ToDictionary(x => x.Index, x => x.Entity);

            await Assert.That(stepped.Count).IsEqualTo(reference.Count);
            foreach ((int idx, EntityState re) in reference)
            {
                await Assert.That(stepped.ContainsKey(idx)).IsTrue();
                EntityState se = stepped[idx];
                await Assert.That(se.ClassName).IsEqualTo(re.ClassName);
                await Assert.That(se.Serial).IsEqualTo(re.Serial);
                await Assert.That(se.Fields.Count).IsEqualTo(re.Fields.Count);
            }
        });
    }

    /// <summary>Pumps the headless dispatcher until the authoritative tracker reaches frame N (or times out).</summary>
    private static async Task<EntityTracker?> WaitForTrackerAt(MainViewModel vm, int frameIndex)
    {
        for (int i = 0; i < 200; i++)
        {
            if (vm.Playback.AuthoritativeTracker is { } t && t.CurrentFrameIndex == frameIndex)
            {
                return t;
            }

            await Task.Delay(25);
        }

        return vm.Playback.AuthoritativeTracker;
    }

    [Test]
    public async Task StepForwardAndBack_MatchDiscreteFrameMoves()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());
            await vm.AutoLoadDemoAsync(demo);
            await Assert.That(vm.Frames.Count).IsGreaterThan(3);

            vm.Navigation.SeekToFrame(2);
            await Assert.That(vm.Playback.CurrentFrameIndex).IsEqualTo(2);

            vm.Playback.StepForward();
            await Assert.That(vm.Playback.CurrentFrameIndex).IsEqualTo(3);
            await Assert.That(vm.SelectedFrame).IsEqualTo(vm.Frames[3]);

            vm.Playback.StepBack();
            await Assert.That(vm.Playback.CurrentFrameIndex).IsEqualTo(2);
            await Assert.That(vm.SelectedFrame).IsEqualTo(vm.Frames[2]);
        });
    }
}
