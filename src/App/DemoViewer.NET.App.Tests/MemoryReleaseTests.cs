#region

using System.Runtime.CompilerServices;
using DemoViewer.NET.Models;
using DemoViewer.NET.Modules;
using Cs2DemoKit.Parser;
using Cs2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Close-demo memory gate. The user-visible promise of the Close Demo action is that the RAM an open
///     demo occupies comes back — so this asserts COLLECTABILITY, not field-nulling: a checklist of "the
///     fields I remembered to null are null" cannot prove completeness, because the parser slices frames
///     ZERO-COPY into the demo byte buffer and any ONE surviving frame reference pins the entire file. A
///     weak reference still alive after close NAMES a missed root. (It earned its keep immediately: the
///     first run caught <c>ReplayTabViewModel.SelectedTickFrame</c> pinning the whole demo.)
///     <para>
///         The demo graph must never be reachable from a local in the test body — a plain
///         <c>ParsedDemo parsed = …</c> is hoisted into the async state machine (or simply kept alive on
///         the frame in Debug builds) and the test would pass vacuously. Hence <see cref="CaptureRefs" />:
///         a non-async, non-inlined helper whose locals die with its frame, returning only weak references.
///     </para>
/// </summary>
[NotInParallel]
public class MemoryReleaseTests
{
    [Test]
    public async Task CloseDemo_ReleasesParsedDemoAndFrameGraph()
    {
        string demo = DemoTestHelper.RequireDemo();
        DemoRefs refs = default;

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel? vm = new(library: TestLibraries.Empty());
            await vm.AutoLoadDemoAsync(demo);
            refs = CaptureRefs(vm);
            await Assert.That(refs.Parsed.IsAlive).IsTrue().Because("sanity: the demo IS loaded here");

            await vm.CloseDemoCommand.ExecuteAsync(null);

            // Null the only remaining strong root. This local IS hoisted into the state machine (it is
            // used across awaits) and the state-machine box outlives this body — so nulling is required,
            // not cosmetic.
            vm = null;
        });

        Collect();

        using (Assert.Multiple())
        {
            await Assert.That(refs.Parsed.IsAlive)
                .IsFalse()
                .Because("the closed demo's ParsedDemo must be collectable — a live one means some shell "
                         + "or tab field still references it (see MainViewModel.UnloadDemoState)");
            await Assert.That(refs.FirstFrame.IsAlive)
                .IsFalse()
                .Because("frames slice zero-copy into the demo byte buffer, so ONE surviving frame pins "
                         + "the whole file — this is the assertion that actually guards the RAM promise");
            await Assert.That(refs.Schema.IsAlive)
                .IsFalse()
                .Because("the runtime schema (flattened serializers) is demo-scale and reached through "
                         + "both ParsedDemo and any surviving EntityTracker");
        }
    }

    /// <summary>
    ///     The load-only test above cannot see the caches that only EXIST after interaction — the
    ///     <see cref="EntityTracker" /> and its baselines/snapshots, the entity inspector + delta log, the
    ///     parser card build. Clearing an empty collection is indistinguishable from a no-op, so this test
    ///     seeks and inspects FIRST, then closes. That is also the realistic session shape: open, seek
    ///     around, inspect, close — and the tracker (per-class instance baselines, class shapes, entity
    ///     snapshots) is one of the largest holders in the whole app.
    /// </summary>
    [Test]
    public async Task CloseDemo_AfterSeekAndInspect_ReleasesTrackerAndInteractionCaches()
    {
        string demo = DemoTestHelper.RequireDemo();
        InteractedRefs refs = default;

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel? vm = new(library: TestLibraries.Empty());
            await vm.AutoLoadDemoAsync(demo);
            await Assert.That(vm.Frames.Count).IsGreaterThan(60);

            // Seek: builds the authoritative tracker (checkpoint replay), the entity node/group trees,
            // the prev-tick delta snapshot, and the parser card build for the landed frame.
            const int target = 40;
            vm.Navigation.SeekToFrame(target);
            EntityTracker? tracker = await WaitForTrackerAt(vm, target);
            await Assert.That(tracker).IsNotNull().Because("sanity: the seek must have produced a tracker");
            await Assert.That(vm.EntityTab.EntityListItems.Count).IsGreaterThan(0);

            // Inspect: fills EntityInspector (relationship tree) + DeltaLog for the selected entity.
            // Skip header rows — those carry no Entity.
            EntityState? entity = vm.EntityTab.EntityListItems.First(i => i.Entity is not null).Entity!;
            vm.EntityTab.SelectedEntityItem = entity;

            refs = CaptureInteractedRefs(vm);
            // Drop the locals: both are (or may be) hoisted into the async state machine, whose box
            // outlives this body — leaving either set would make the weak-ref assertions vacuous.
            tracker = null;
            entity = null;

            await vm.CloseDemoCommand.ExecuteAsync(null);
            vm = null;
        });

        Collect();

        using (Assert.Multiple())
        {
            await Assert.That(refs.Tracker.IsAlive)
                .IsFalse()
                .Because("the EntityTracker holds per-class instance baselines, class shapes and entity "
                         + "snapshots — it exists ONLY after a seek, so no other test would catch it");
            await Assert.That(refs.Parsed.IsAlive)
                .IsFalse()
                .Because("an interacted-with demo must release exactly like a freshly-loaded one");
            await Assert.That(refs.FirstFrame.IsAlive)
                .IsFalse()
                .Because("one live frame pins the whole demo byte buffer, whatever populated it");
            await Assert.That(refs.SeekedEntity.IsAlive)
                .IsFalse()
                .Because("the inspected entity is reachable from the inspector tree, the delta log and "
                         + "the entity list — every one of those roots has to let go");
        }
    }

    [Test]
    public async Task CloseDemo_ResetsShellToTheNoDemoState()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());
            await vm.AutoLoadDemoAsync(demo);
            await Assert.That(vm.HasFile).IsTrue();
            await Assert.That(vm.CloseDemoCommand.CanExecute(null)).IsTrue();

            await vm.CloseDemoCommand.ExecuteAsync(null);

            using (Assert.Multiple())
            {
                await Assert.That(vm.HasFile).IsFalse();
                await Assert.That(vm.Frames).IsEmpty();
                await Assert.That(vm.FrameRows).IsEmpty();
                await Assert.That(vm.SelectedFrame).IsNull();
                await Assert.That(vm.Playback.HasDemo).IsFalse();
                await Assert.That(vm.Playback.TotalFrames).IsEqualTo(0);
                await Assert.That(vm.Playback.CurrentFrameIndex).IsEqualTo(-1);
                await Assert.That(vm.ReplayTab.TickGroups).IsEmpty();
                await Assert.That(vm.ParserTab.MessageCards).IsEmpty();
                await Assert.That(vm.EntityTab.EntityListItems).IsEmpty();
                await Assert.That(vm.StatsTab.HasStats).IsFalse();
                await Assert.That(vm.GameEventFilters).IsEmpty();
                await Assert.That(((ICurrentDemoSource)vm.ModuleContext!).CurrentDemo).IsNull();
                // The command re-gates itself off the now-false HasFile (the toolbar button disables).
                await Assert.That(vm.CloseDemoCommand.CanExecute(null)).IsFalse();
            }

            // …and a load after a close still works — the reset is not one-way.
            await vm.AutoLoadDemoAsync(demo);
            await Assert.That(vm.HasFile).IsTrue();
            await Assert.That(vm.Frames.Count).IsGreaterThan(0);
        });
    }

    /// <summary>
    ///     Takes weak references to the loaded demo's demo-scale objects. Non-async and non-inlined so its
    ///     <c>ParsedDemo</c> local lives on a frame that is gone by the time the caller resumes.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static DemoRefs CaptureRefs(MainViewModel vm)
    {
        ParsedDemo parsed = ((ICurrentDemoSource)vm.ModuleContext!).CurrentDemo!;
        return new DemoRefs(
            new WeakReference(parsed),
            new WeakReference(parsed.Frames[0]),
            new WeakReference(parsed.Schema));
    }

    /// <summary>
    ///     Weak-refs the interaction-created state as well as the demo. Same non-async / non-inlined
    ///     discipline as <see cref="CaptureRefs" /> — none of these objects may outlive this frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static InteractedRefs CaptureInteractedRefs(MainViewModel vm)
    {
        ParsedDemo parsed = ((ICurrentDemoSource)vm.ModuleContext!).CurrentDemo!;
        return new InteractedRefs(
            new WeakReference(parsed),
            new WeakReference(parsed.Frames[0]),
            new WeakReference(vm.Playback.AuthoritativeTracker!),
            new WeakReference(vm.EntityTab.SelectedEntityItem!));
    }

    /// <summary>Pumps the dispatcher until the async seek has published a tracker at the target frame.</summary>
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

    /// <summary>Full blocking compacting collection, repeated so finalizer-queue resurrections go too.</summary>
    private static void Collect()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
        }
    }

    private readonly record struct DemoRefs(
        WeakReference Parsed,
        WeakReference FirstFrame,
        WeakReference Schema);

    private readonly record struct InteractedRefs(
        WeakReference Parsed,
        WeakReference FirstFrame,
        WeakReference Tracker,
        WeakReference SeekedEntity);
}
