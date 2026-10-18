#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Modules;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Shell;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     An open on a tab that reads no entity state decodes nothing for the shell: the game clock comes off the
///     open's rules run and the entity seek waits for a tab that reads it. The clock must be the one the
///     replay to the first freeze end computes, on every benchmark demo, and the owed seek must land where a
///     fresh replay does once the Entity Tracking tab is shown.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class GameClockOpenRealDemoTests
{
    private static string BenchmarkDemo(int which)
    {
        string folder = Path.Combine(DemoTestHelper.FindRepoRoot() ?? "", "demos", "benchmarks");
        List<string> demos = Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*.dem", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)]
            : [];
        return which < demos.Count ? demos[which] : throw new SkipTestException($"fewer than {which + 1} demos under demos/benchmarks");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task TheOpen_SettlesTheReplaysClock_AndOwesTheEntitySeek(int which)
    {
        string demo = BenchmarkDemo(which);

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());
            await Assert.That(vm.SelectedTab?.TabId).IsEqualTo("builtin.library");
            await vm.AutoLoadDemoAsync(demo);
            await Assert.That(vm.HasFile).IsTrue();

            List<DemoFrame> frames = [.. vm.Frames];
            int tickRate = vm.Playback.TickRate;
            int firstFreezeEnd = vm.Navigator.EventBoundaryFramesByName["round_freeze_end"][0];
            (double expected, bool valid) = GameClock.ComputeClockBase(frames, firstFreezeEnd, tickRate);
            Console.WriteLine($"{Path.GetFileName(demo)}: first freeze end frame {firstFreezeEnd}, clockBase {expected:R}");
            await Assert.That(valid).IsTrue();

            // No tracker exists, so an unsettled clock would read a base of 0 here.
            await Assert.That(vm.Playback.AuthoritativeTracker).IsNull().Because("the open owes its entity seek");
            ModuleContext context = (ModuleContext)vm.ModuleContext!;
            foreach (int frame in new[] { 0, firstFreezeEnd, frames.Count / 2, frames.Count - 1 })
            {
                int tick = frames[frame].ServerTick;
                await Assert.That(context.CurtimeSeconds(tick)).IsEqualTo(tick / (double)tickRate - expected);
            }

            // Showing the Entity Tracking tab pays the owed seek at the open's frame.
            int at = vm.Playback.CurrentFrameIndex;
            await Assert.That(at).IsGreaterThanOrEqualTo(0);
            await Assert.That(vm.TrySelectTab("builtin.entity")).IsTrue();
            EntityTracker? seeked = await WaitForTrackerAt(vm, at);
            await Assert.That(seeked).IsNotNull();

            await Assert.That(vm.EntityTab.EntityStatusText).StartsWith($"Tick {seeked!.CurrentTick} ").Because("the tab rebuilt from the paid seek");

            // The open lands before any entity exists; a seek from the tab must still match a fresh replay.
            vm.Navigation.SeekToFrame(firstFreezeEnd);
            seeked = await WaitForTrackerAt(vm, firstFreezeEnd);
            await Assert.That(seeked).IsNotNull();
            EntityTracker fresh = new();
            fresh.ReplayToIndex(firstFreezeEnd, frames);
            Dictionary<int, EntityState> reference = fresh.CurrentEntities.AllIndexed().ToDictionary(x => x.Index, x => x.Entity);
            Dictionary<int, EntityState> shown = seeked!.CurrentEntities.AllIndexed().ToDictionary(x => x.Index, x => x.Entity);
            await Assert.That(reference.Count).IsGreaterThan(0);
            await Assert.That(shown.Count).IsEqualTo(reference.Count);
            foreach ((int idx, EntityState re) in reference)
            {
                await Assert.That(shown.TryGetValue(idx, out EntityState? se)).IsTrue();
                await Assert.That(se!.ClassName).IsEqualTo(re.ClassName);
                await Assert.That(se.Serial).IsEqualTo(re.Serial);
            }

            await Assert.That(vm.EntityTab.EntityGroups.Count).IsGreaterThan(0);
        });
    }

    [Test]
    public async Task AnOpenOnTheEntityTab_SeeksAsItAlwaysDid()
    {
        string demo = BenchmarkDemo(0);

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());
            await Assert.That(vm.TrySelectTab("builtin.entity")).IsTrue();
            await vm.AutoLoadDemoAsync(demo);
            await Assert.That(vm.HasFile).IsTrue();

            int at = vm.Playback.CurrentFrameIndex;
            await Assert.That(at).IsGreaterThanOrEqualTo(0);
            EntityTracker? seeked = await WaitForTrackerAt(vm, at);
            await Assert.That(seeked).IsNotNull().Because("a tab that reads entity state takes the open's seek at once");
        });
    }

    private static async Task<EntityTracker?> WaitForTrackerAt(MainViewModel vm, int frameIndex)
    {
        for (int i = 0; i < 400; i++)
        {
            if (vm.Playback.AuthoritativeTracker is { } t && t.CurrentFrameIndex == frameIndex && !vm.EntityTab.IsSeekingEntities)
            {
                return t;
            }

            await Task.Delay(25);
        }

        return vm.Playback.AuthoritativeTracker;
    }
}
