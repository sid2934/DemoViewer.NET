#region

using System.Collections.Immutable;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The three camera scripts. Each has one behaviour that would be easy to get wrong and invisible
///     until someone watched the video: a fixed camera that keeps pixel scale instead of world framing, a
///     follow that snaps to the origin when its target dies, and a "mirror the live view" that keeps
///     mirroring after the user pressed Start.
/// </summary>
public class CameraScriptResolverTests
{
    [Test]
    public async Task Fixed_RefitsToTheExportPaneSize_KeepingTheWorldFraming()
    {
        MapSpace space = TwoLevelSpace();
        PaneSet panes = new(new StackedLayout());
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(1920, 1080),
            new WorldBounds(-2000, -1200, 2000, 1200));

        // Captured from a 700 px-wide live pane…
        ViewportTransform live = ViewportTransform.Fit(700, 350, -1000, -800, 1000, 800);
        Dictionary<MapLevelId, ViewportTransform> byLevel = new();
        foreach (LevelPane pane in panes.Panes)
        {
            byLevel[pane.LevelId] = live;
        }

        CameraScriptResolver resolver = new(new CameraScript.Fixed(byLevel));
        resolver.Apply(panes, SyntheticScenes.FullSceneBudget().Frame, new SceneTime(0, 0, 0, 1 / 60.0, true));

        foreach (LevelPane pane in panes.Panes)
        {
            ViewportTransform applied = pane.Camera.Current;

            // …and re-viewported onto the export's band. Same world centre and the same scale: the world
            // rectangle on screen is what a user framed, not the pixel size they framed it in.
            await Assert.That(applied.ViewWidth).IsEqualTo(pane.ViewportRect.Width).Within(0.01);
            await Assert.That(applied.ViewHeight).IsEqualTo(pane.ViewportRect.Height).Within(0.01);
            await Assert.That(applied.CenterX).IsEqualTo(live.CenterX).Within(1e-6);
            await Assert.That(applied.BaseScale).IsEqualTo(live.BaseScale).Within(1e-9);
        }
    }

    [Test]
    public async Task Fixed_LeavesALevelItSaysNothingAbout_Alone()
    {
        MapSpace space = TwoLevelSpace();
        PaneSet panes = new(new StackedLayout());
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(1920, 1080),
            new WorldBounds(-2000, -1200, 2000, 1200));

        ViewportTransform untouched = panes.Panes[0].Camera.Current;

        CameraScriptResolver resolver = new(
            new CameraScript.Fixed(new Dictionary<MapLevelId, ViewportTransform>()));
        resolver.Apply(panes, SyntheticScenes.FullSceneBudget().Frame, new SceneTime(0, 0, 0, 1 / 60.0, true));

        await Assert.That(panes.Panes[0].Camera.Current.CenterX).IsEqualTo(untouched.CenterX).Within(1e-9);
        await Assert.That(resolver.MovedAnyCamera).IsFalse();
    }

    [Test]
    public async Task FollowPlayer_HoldsTheLastTransform_WhenTheSteamIdIsNotInTheDemo()
    {
        MapSpace space = TwoLevelSpace();
        PaneSet panes = new(new StackedLayout());
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(1920, 1080),
            new WorldBounds(-2000, -1200, 2000, 1200));

        ViewportTransform before = panes.Panes[0].Camera.Current;

        CameraScriptResolver resolver = new(new CameraScript.FollowPlayer(0xDEADBEEF));
        Scene2DFrame frame = SyntheticScenes.FullSceneBudget().Frame;
        for (int i = 0; i < 20; i++)
        {
            resolver.Apply(panes, frame, new SceneTime(i, i, i / 60.0, 1 / 60.0, i == 0));
        }

        // Never snap to the origin: an unresolvable target means "hold", not "reset the view".
        await Assert.That(resolver.ResolvedSlot).IsEqualTo(-1);
        await Assert.That(panes.Panes[0].Camera.Current.CenterX).IsEqualTo(before.CenterX).Within(1e-9);
        await Assert.That(panes.Panes[0].Camera.Current.CenterY).IsEqualTo(before.CenterY).Within(1e-9);
    }

    [Test]
    public async Task FollowPlayer_ResolvesASteamIdToItsSlot_AndMovesTowardIt()
    {
        MapSpace space = TwoLevelSpace();
        PaneSet panes = new(new StackedLayout());
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(1920, 1080),
            new WorldBounds(-2000, -1200, 2000, 1200));

        Scene2DFrame frame = SyntheticScenes.FullSceneBudget().Frame;
        PlayerMarker target = frame.Markers[4];

        CameraScriptResolver resolver = new(
            new CameraScript.FollowPlayer(target.SteamId, 0));

        for (int i = 0; i < 240; i++)
        {
            resolver.Apply(panes, frame, new SceneTime(i, i, i / 60.0, 1 / 60.0, i == 0));
        }

        await Assert.That(resolver.ResolvedSlot).IsEqualTo(target.Slot);

        LevelPane onTargetsLevel = panes.Panes.First(p =>
            space.LevelIndexFor(target.WorldZ) == p.LevelIndex);
        await Assert.That(onTargetsLevel.Camera.Current.CenterX).IsEqualTo(target.WorldX).Within(1.0);
    }

    [Test]
    public async Task MirrorLiveView_IgnoresLaterMutationOfTheLivePanes()
    {
        MapSpace space = TwoLevelSpace();
        PaneSet live = new(new StackedLayout());
        live.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(700, 350),
            new WorldBounds(-2000, -1200, 2000, 1200));

        // The capture, taken once at Start (plan D12).
        ImmutableArray<PaneCameraSnapshot> captured =
        [
            .. live.Panes.Select(p => new PaneCameraSnapshot(p.LevelId, p.Camera.Current, p.Camera.ManualOverride))
        ];

        double capturedCentre = captured[0].Transform.CenterX;

        // The user keeps panning the real window while the export runs.
        foreach (LevelPane pane in live.Panes)
        {
            pane.Camera.Current = pane.Camera.Current.WithPanDelta(5000, 5000);
        }

        PaneSet exporting = new(new StackedLayout());
        exporting.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(1920, 1080),
            new WorldBounds(-2000, -1200, 2000, 1200));

        CameraScriptResolver resolver = new(
            new CameraScript.MirrorLiveView(captured, LevelDisplayMode.Stacked));
        resolver.Apply(exporting, SyntheticScenes.FullSceneBudget().Frame,
            new SceneTime(0, 0, 0, 1 / 60.0, true));

        LevelPane exported = exporting.Panes.First(p => p.LevelId == captured[0].LevelId);
        await Assert.That(exported.Camera.Current.CenterX).IsEqualTo(capturedCentre).Within(1e-9);
        await Assert.That(exported.Camera.Current.PanX).IsEqualTo(captured[0].Transform.PanX).Within(1e-9);
    }

    private static MapSpace TwoLevelSpace()
    {
        MapSpace space = new();
        space.Rebuild(SyntheticScenes.BudgetFloors);
        return space;
    }
}

/// <summary>
///     Determinism, asserted on <b>pre-encode RGBA frame hashes</b> (plan D13). <c>libvpx-vp9</c> and
///     <c>libx264</c> are not bit-reproducible across thread counts and versions, so comparing encoded
///     files would test ffmpeg rather than the renderer. The contract is that two runs of the same
///     request produce the same pixels.
/// </summary>
public class ExportDeterminismTests
{
    [Test]
    public async Task TwoRunsOfTheSameRequest_ProduceIdenticalFrameHashes()
    {
        IReadOnlyList<string> first = await HashRun(1 / 60.0);
        IReadOnlyList<string> second = await HashRun(1 / 60.0);

        await Assert.That(first.Count).IsEqualTo(second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            await Assert.That(first[i]).IsEqualTo(second[i]);
        }
    }

    /// <summary>
    ///     The negative control. Without it, a harness that hashed a constant would pass the case above
    ///     and prove nothing at all.
    ///     <para>
    ///         It moves a <b>marker</b> rather than the timestep. A different <c>DeltaSeconds</c> over a
    ///         repeated static frame really does produce identical pixels, the marker smoother settles
    ///         after the first frame and then has nothing left to interpolate, so asserting otherwise
    ///         would be asserting a bug.
    ///     </para>
    /// </summary>
    [Test]
    public async Task MovingAMarker_ProducesDifferentHashes()
    {
        IReadOnlyList<string> still = await HashRun(1 / 60.0);
        IReadOnlyList<string> moved = await HashRun(1 / 60.0, 40f);

        bool anyDifference = false;
        for (int i = 0; i < Math.Min(still.Count, moved.Count); i++)
        {
            anyDifference |= !string.Equals(still[i], moved[i], StringComparison.Ordinal);
        }

        await Assert.That(anyDifference).IsTrue();
    }

    private static async Task<IReadOnlyList<string>> HashRun(double deltaSeconds, float markerDriftPerFrame = 0f)
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        HashingFrameSink sink = new();

        SceneFixture fixture = SyntheticScenes.FullSceneBudget();
        SceneFixture[] frames = new SceneFixture[16];
        for (int i = 0; i < frames.Length; i++)
        {
            SceneTime time = new(fixture.Time.Tick + i, fixture.Time.FrameIndex + i,
                fixture.Time.DemoSeconds + i * deltaSeconds, deltaSeconds, i == 0);

            frames[i] = fixture with
            {
                Time = time,
                Frame = markerDriftPerFrame == 0f
                    ? fixture.Frame
                    : Drifted(fixture.Frame, time, markerDriftPerFrame * i)
            };
        }

        await new SceneExportSession(compositor).RunAsync(
            ExportFixtures.Request(frames.Length, size: new SKSizeI(64, 48)),
            new FixtureFrameSource(frames), sink, surfaces, null, CancellationToken.None);

        return sink.FrameHashes;
    }

    // A copy of the frame with every marker slid along X. Frames are init-only by design, so this builds
    // a new one over the same lists rather than mutating a published frame.
    private static Scene2DFrame Drifted(Scene2DFrame source, SceneTime time, float dx)
    {
        List<PlayerMarker> markers = new(source.Markers.Count);
        foreach (PlayerMarker marker in source.Markers)
        {
            markers.Add(marker with
            {
                WorldX = marker.WorldX + dx
            });
        }

        return new Scene2DFrame
        {
            Time = time,
            Markers = markers,
            AreaEffects = source.AreaEffects,
            Trails = source.Trails,
            Bomb = source.Bomb,
            KillFeed = source.KillFeed,
            GameInfo = source.GameInfo,
            Map = source.Map,
            Vision = source.Vision,
            FollowSlot = source.FollowSlot
        };
    }
}

/// <summary>
///     Design §6's allocation contract, applied to the export loop: after warm-up, a rendered and
///     written frame allocates nothing on the export thread.
/// </summary>
public class ExportAllocationTests
{
    [Test]
    [Category("Budget")]
    public async Task ASteadyStateExport_AllocatesNothingPerFrame()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        SceneExportSession session = new(compositor)
        {
            // What the production export always sets when a map bundle exists, which is every export of
            // a real demo with assets on disk. Without it the level set is re-derived from the Z
            // histogram on every push, and FloorSplitter.Slices allocates a fresh List each time
            // (measured below, and reported as a carry-forward rather than swallowed).
            AuthoritativeFloors = SyntheticScenes.BudgetFloors
        };

        // Built OUTSIDE the measured window: the fixture, the frame array and the request are the
        // caller's setup, not the loop's steady state, and counting them would measure the harness.
        FixtureFrameSource source = ExportFixtures.Source(1024);
        ExportRequest request = ExportFixtures.Request(1024, size: new SKSizeI(64, 48));

        // Warm-up run: JIT, the surface, the pooled buffer, the picture caches and the text blobs are all
        // one-time costs, and charging them to the budget would make the gate meaningless.
        await Measure(session, source, request with
        {
            EndFrame = 63
        }, surfaces);

        // TWO runs of different lengths, differenced. A single run cannot separate the loop's per-frame
        // cost from a run's own fixed setup, one compositor scope, one camera resolver, one renderer,
        // one async state machine, and §6's budget is about the former.
        long shortRun = await Measure(session, source, request with
        {
            EndFrame = 511
        }, surfaces);
        long longRun = await Measure(session, source, request with
        {
            EndFrame = 1023
        }, surfaces);

        long extra = longRun - shortRun;
        double perFrame = extra / 512.0;
        Console.WriteLine($"[alloc] export {perFrame:F2} bytes/frame " +
                          $"(512 frames: {shortRun} B, 1024 frames: {longRun} B, delta {extra} B)");

        // The ceiling is 64 BYTES for the extra 512 frames, not 64 per frame. B1 characterised this
        // exactly (its deviation 14): a single 48-byte allocation appears once, at a varying iteration
        // past ~150, with no gen-0 collection in the window and never a second time: the runtime tiering
        // the loop body, not the scene allocating. Charging it to the budget would either make the gate
        // flaky or force the budget above zero, and zero-per-frame is the assertion worth having.
        await Assert.That(extra).IsLessThanOrEqualTo(64L);
    }

    private static async Task<long> Measure(SceneExportSession session, FixtureFrameSource source,
        ExportRequest request, CpuSurfaceProvider surfaces)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        await session.RunAsync(request, source, new NullSink(), surfaces, null, CancellationToken.None);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    ///     The same export with <b>no</b> map bundle, measured and reported rather than asserted.
    ///     <para>
    ///         Without authoritative floors the level set is re-derived from the Z histogram on every
    ///         push, and <c>FloorSplitter.Slices</c> hands back a freshly computed <c>List&lt;FloorSlice&gt;</c>
    ///         each time. Measured at ~656 bytes/frame, independent of resolution and of everything B4
    ///         added. It is B1's to close (the same cost lands on every interactive push of a map with no
    ///         baked bundle); the export path avoids it because it always supplies the bundle's floors
    ///         when one exists. Printed, not gated, so it cannot flap a required check.
    ///     </para>
    /// </summary>
    [Test]
    [Category("Budget")]
    public async Task WithoutABundle_TheLevelDerivationAllocates_AndTheFigureIsReported()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        SceneExportSession session = new(compositor);

        const int frames = 256;
        FixtureFrameSource source = ExportFixtures.Source(frames);
        ExportRequest request = ExportFixtures.Request(frames, size: new SKSizeI(64, 48));

        await session.RunAsync(request with
            {
                EndFrame = 63
            }, source, new NullSink(), surfaces, null,
            CancellationToken.None);

        long before = GC.GetAllocatedBytesForCurrentThread();
        await session.RunAsync(request, source, new NullSink(), surfaces, null, CancellationToken.None);
        long after = GC.GetAllocatedBytesForCurrentThread();

        double perFrame = (after - before) / (double)frames;
        Console.WriteLine($"[alloc] export without a bundle: {perFrame:F1} bytes/frame (B1 carry-forward)");
        await Assert.That(perFrame).IsGreaterThanOrEqualTo(0.0);
    }

    /// <summary>Accepts frames and does nothing. The cheapest possible sink, so the figure is the loop's.</summary>
    private sealed class NullSink : IFrameSink
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
