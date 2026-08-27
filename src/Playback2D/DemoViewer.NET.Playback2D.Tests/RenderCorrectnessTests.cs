#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Cameras;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Hud;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Playback2D.Pipeline.Hud;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Render, camera and resource correctness: one case per finding, each of them a defect the
///     1594-test suite already had, because every one of them lives in a <b>relationship</b> a unit
///     test does not instantiate: a producer feeding a camera, a palette outliving a picture, two HUD
///     layers claiming one rectangle, a cache describing a handle that has already been freed.
/// </summary>
public class RenderCorrectnessTests
{
    private const int TickRate = 64;

    // ── 8 · one non-finite coordinate poisons the camera permanently ────────────────────────────────

    [Test]
    public async Task ANonFiniteMarker_NeitherPoisonsTheCamera_NorPinsTheRenderLoopOn()
    {
        // The audit's probe, made a gate. FitAliveRig folds every alive marker into a rectangle with
        // Math.Min/Math.Max, so ONE NaN coordinate is the whole rectangle; ViewportTransform.Fit's
        // degenerate guard is `w <= double.Epsilon`, which is false for a NaN, so the NaN used to land in
        // BaseScale and CenterX. From there SliceCamera.IsSettledAt loses every comparison, the residual
        // is never snapped, and CameraAdvancer keeps reporting "still moving" — an idle tab at display
        // refresh rate, drawing nothing, for the rest of the session.
        //
        // An EMPTY compositor, deliberately: the return value has to be the camera's answer and nothing
        // else, or a layer that happens to animate would mask it.
        using SceneCompositor compositor = new();
        using CpuSurfaceProvider provider = new();
        using HeadlessSceneRenderer renderer = new(compositor, provider, new StackedLayout(),
            ScenePalette.Dark)
        {
            Size = new SKSizeI(640, 360),
            AdvanceCameras = true
        };

        Scene2DFrame frame = PoisonedFrame();
        SceneTime time = new(1000, 0, 0, 1.0 / TickRate, false);

        renderer.Advance(frame, in time); // the first advance is what mints the pane
        renderer.Panes.SetRig(static _ => FitAliveRig.Instance);

        bool keepArmed = true;
        for (int i = 0; i < 2000; i++)
        {
            keepArmed = renderer.Advance(frame, in time);
        }

        ViewportTransform camera = renderer.Panes.Panes[0].Camera.Current;
        Console.WriteLine($"[nan-camera] keepArmed={keepArmed} centerX={camera.CenterX} " +
                          $"baseScale={camera.BaseScale}");

        await Assert.That(double.IsFinite(camera.CenterX)).IsTrue();
        await Assert.That(double.IsFinite(camera.CenterY)).IsTrue();
        await Assert.That(double.IsFinite(camera.BaseScale)).IsTrue();
        await Assert.That(keepArmed).IsFalse()
            .Because("2000 frames is long past convergence; a loop still armed here never stops");
    }

    [Test]
    public async Task APoisonedCamera_RecoversOnTheNextFiniteTarget()
    {
        // The other half of permanence: `a + (b - a) * t` is NaN for every t once a is, so a camera that
        // was corrupted before the guards existed — or by a producer that writes Current directly — could
        // never lerp back onto a finite target. It lands on it instead.
        SliceCamera poisoned = new(new ViewportTransform(640, 360, double.NaN, double.NaN,
            double.NaN, 1, 0, 0));
        ViewportTransform target = ViewportTransform.Fit(640, 360, -1000, -1000, 1000, 1000);

        SliceCamera stepped = poisoned.StepToward(target, 0.1);

        await Assert.That(stepped.Current.CenterX).IsEqualTo(target.CenterX);
        await Assert.That(stepped.Current.BaseScale).IsEqualTo(target.BaseScale);
        await Assert.That(stepped.IsSettledAt(target)).IsTrue();
    }

    [Test]
    public async Task TheFrameBuilder_RefusesANonFiniteSample_RatherThanWideningIntoIt()
    {
        // _observed is only ever WIDENED — there is no re-seed — and WorldBounds.Extend is Math.Min /
        // Math.Max, both of which propagate NaN. So the gate has to be the entry point, and this is it.
        SceneFrameBuilder builder = new();
        FakeEntity pawn = new FakeEntity("CCSPlayerPawn").With("m_iHealth", 100);

        FakePlayer broken = new()
        {
            Slot = 0,
            Team = 2,
            Pawn = pawn,
            WorldPosition = (float.NaN, 5f, 64f)
        };
        FakePlayer sound = new()
        {
            Slot = 1,
            Team = 3,
            Pawn = pawn,
            WorldPosition = (100f, 200f, 64f)
        };

        Build(builder, Input([broken], 1, TickRate));
        WorldBounds afterPoison = builder.ObservedBounds;

        Build(builder, Input([sound], 2, 2 * TickRate));
        WorldBounds afterGood = builder.ObservedBounds;

        Build(builder, Input([broken], 3, 3 * TickRate));
        WorldBounds afterPoisonAgain = builder.ObservedBounds;

        Console.WriteLine($"[nan-observe] poisoned={afterPoison} good={afterGood} again={afterPoisonAgain}");

        // The rejected sample leaves the extent exactly where it was — unseeded first, then untouched.
        await Assert.That(afterPoison).IsEqualTo(WorldBounds.Default);
        await Assert.That(afterGood).IsEqualTo(new WorldBounds(100, 200, 100, 200));
        await Assert.That(afterPoisonAgain).IsEqualTo(afterGood);
    }

    // ── 16 · the picture-cache key carries no palette ───────────────────────────────────────────────

    [Test]
    public async Task APaletteSwap_DropsThePictures_SoAPerCameraLayerRedraws()
    {
        // A PerCamera recording bakes in whatever colours the layer read out of ctx.Palette. The key is
        // (LevelId, LayerId, ContentVersion, CameraEpoch) — none of which move on a theme switch — so the
        // old theme replayed forever at an unchanged epoch. Scene2DHost got away with it by calling
        // InvalidateCaches() by hand; HeadlessSceneRenderer.Palette merely SAID it did.
        PaletteFillLayer layer = new();
        using SceneCompositor compositor = new();
        compositor.Add(layer);

        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(64, 64));

        compositor.Render(surface.Canvas, Submission(ScenePalette.Dark));
        await Assert.That(PixelAt(surface, 32, 32)).IsEqualTo(ScenePalette.Dark.MinorGrid);

        // Same palette, same epoch: still one recording. The fix must not turn the cache off.
        compositor.Render(surface.Canvas, Submission(ScenePalette.Dark));
        await Assert.That(layer.RenderCalls).IsEqualTo(1);

        compositor.Render(surface.Canvas, Submission(ScenePalette.Light));

        Console.WriteLine($"[palette-cache] renders={layer.RenderCalls} " +
                          $"pixel={PixelAt(surface, 32, 32)}");

        await Assert.That(layer.RenderCalls).IsEqualTo(2);
        await Assert.That(PixelAt(surface, 32, 32)).IsEqualTo(ScenePalette.Light.MinorGrid);
    }

    [Test]
    public async Task TheRadarGrid_FollowsAPaletteSwap_AtAnUnchangedCameraEpoch()
    {
        // The production instance of the same defect: RadarLayer is the only shipped PerCamera layer, and
        // its grid half writes ctx.Palette.MinorGrid/MajorGrid straight into the picture.
        //
        // Rendered through the framed single-pane path with the canvas cleared to a FIXED colour, not
        // through the submission path: the submission fills the background from its own palette outside
        // the picture, so two submissions differ in their background whether or not the cached grid moved
        // — which would have made this pass against the very bug it is here to catch.
        using SceneCompositor compositor = new();
        compositor.Add(new RadarLayer());

        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(128, 128));

        // One local per call: SceneRenderContext arrives by `in`, and a call's return value has no
        // ref-safe scope to bind to.
        SceneRenderContext darkCtx = PanedContext(ScenePalette.Dark);
        surface.Canvas.Clear(SKColors.Black);
        compositor.Render(surface.Canvas, in darkCtx);
        byte[] dark = Snapshot(surface);

        SceneRenderContext lightCtx = PanedContext(ScenePalette.Light);
        surface.Canvas.Clear(SKColors.Black);
        compositor.Render(surface.Canvas, in lightCtx);
        byte[] light = Snapshot(surface);

        await Assert.That(light).IsNotEquivalentTo(dark)
            .Because("a dark grid surviving a light swap at the same epoch is exactly the cache-key defect this guards");
    }

    // ── 17 · the single-pane Render pins every PerCamera key to a default pane ───────────────────────

    [Test]
    public async Task TheSinglePaneRender_DoesNotCacheAgainstAnUnframedPane()
    {
        // Most callers of this overload leave ctx.Pane at default — LevelId lv-0, CameraEpoch 0 — so
        // every PerCamera key was the SAME key however far the camera had moved, and frame 1's pane-local
        // pixels replayed forever. Latent only because SceneRenderer has no production caller.
        WorldSquareLayer layer = new();
        using SceneCompositor compositor = new();
        compositor.Add(layer);

        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(128, 128));

        ViewportTransform camera = ViewportTransform.Fit(128, 128, -512, -512, 512, 512);
        SceneRenderContext first = TestContexts.For(Scene2DFrame.Empty, camera, 128, 128);
        surface.Canvas.Clear(ScenePalette.Dark.Background);
        compositor.Render(surface.Canvas, in first);
        int firstColumn = ColumnOfInk(surface);

        SceneRenderContext second = TestContexts.For(Scene2DFrame.Empty, camera.WithPanDelta(30, 0),
            128, 128);
        surface.Canvas.Clear(ScenePalette.Dark.Background);
        compositor.Render(surface.Canvas, in second);
        int secondColumn = ColumnOfInk(surface);

        Console.WriteLine($"[single-pane] ink column {firstColumn} → {secondColumn}, " +
                          $"renders={layer.RenderCalls}");

        await Assert.That(layer.RenderCalls).IsEqualTo(2);
        await Assert.That(secondColumn - firstColumn).IsEqualTo(30).Within(1);
    }

    [Test]
    public async Task TheSinglePaneRender_StillCaches_WhenTheCallerFramedAPane()
    {
        // The bypass is aimed at the unframed default, not at the overload: a caller that supplies a real
        // pane snapshot (the export HUD suite does) has a key that varies with its camera, and taking its
        // cache away would be a second regression dressed as a fix.
        WorldSquareLayer layer = new();
        using SceneCompositor compositor = new();
        compositor.Add(layer);

        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(128, 128));

        ViewportTransform camera = ViewportTransform.Fit(128, 128, -512, -512, 512, 512);
        SceneRenderContext ctx = TestContexts.For(Scene2DFrame.Empty, camera, 128, 128) with
        {
            Pane = new LevelPaneSnapshot(new MapLevelId(3), 0, Level(), camera,
                new SKRect(0, 0, 128, 128), 7)
        };

        compositor.Render(surface.Canvas, in ctx);
        compositor.Render(surface.Canvas, in ctx);

        await Assert.That(layer.RenderCalls).IsEqualTo(1);
    }

    // ── 22 · RadarLayer.ScaledFor leaves a dangling disposed SKImage ─────────────────────────────────

    [Test]
    public async Task AFailedResample_LeavesNoCacheEntry_RatherThanADisposedHandle()
    {
        // ScaledFor disposed _scaled first and reassigned it last, while _scaledFrom/_scaledWidth/
        // _scaledHeight went on describing it in between. Anything that failed in that window — a null
        // from SKSurface.Create, which this method can ask for up to 8192² × 4 bytes from, or a throw out
        // of the resample — left the hit branch handing a freed SKImage to DrawImage. That is an access
        // violation inside Skia, not an exception the frame loop can catch, which is why this asserts on
        // the cache's own state BEFORE it would draw again.
        using SKImage source = SolidImage(64, 64, SKColors.Magenta);
        using RadarLayer layer = new()
        {
            CacheScaledImage = true,
            RadarBoundsOverride = new WorldBounds(-100, -100, 100, 100)
        };

        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(128, 128));

        // A good frame first, so there is something live to be left dangling.
        layer.Render(surface.Canvas, RadarContext(source, 128, 128));
        await Assert.That(layer.ScaledCacheSizeForTest).IsNotEqualTo((0, 0));

        // Now the resample cannot be made. The size differs, so this misses the cache and takes the path
        // that used to free the live image and keep describing it.
        layer.SetSurfaceFactoryForTest(static _ => null);
        layer.Render(surface.Canvas, RadarContext(source, 96, 96));

        await Assert.That(layer.ScaledCacheSizeForTest).IsEqualTo((0, 0))
            .Because("a cache entry that survives its own image is the access violation");

        // And a throw is the same story: DropScaled runs before the factory, so nothing is left behind.
        layer.SetSurfaceFactoryForTest(static _ => throw new InvalidOperationException("no surface"));
        InvalidOperationException? thrown = null;
        try
        {
            layer.Render(surface.Canvas, RadarContext(source, 112, 112));
        }
        catch (InvalidOperationException e)
        {
            thrown = e;
        }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(layer.ScaledCacheSizeForTest).IsEqualTo((0, 0));

        // Recovery: the very size that failed re-resamples cleanly once the surface is available again.
        layer.SetSurfaceFactoryForTest(null);
        layer.Render(surface.Canvas, RadarContext(source, 96, 96));

        Console.WriteLine($"[radar-cache] recovered at {layer.ScaledCacheSizeForTest}");
        await Assert.That(layer.ScaledCacheSizeForTest).IsNotEqualTo((0, 0));
    }

    // ── 23 · TextBlobCache can dispose SKTypeface.Default ────────────────────────────────────────────

    [Test]
    public async Task AMissingTypefaceResource_BorrowsTheFallback_AndNeverDisposesIt()
    {
        // LoadEmbeddedTypeface falls back to the process-wide SKTypeface.Default when the manifest
        // resource is absent, and Dispose used to dispose _typeface unconditionally. Every layer builds
        // its own cache when no shared one is passed, so a packaging fault whose intended cost was "the
        // wrong font" unref'd the singleton once per cache and killed text rendering process-wide.
        //
        // The fallback is substituted rather than being the real singleton on purpose: a suite that
        // proved this by destroying SKTypeface.Default would report the regression as every OTHER text
        // test failing, in whatever order they happened to run.
        using SKTypeface standIn = LoadEmbeddedFace();

        using (TextBlobCache orphan = new(16, "DemoViewer.NET.Playback2D.Core.Assets.NoSuchFace.ttf",
                   standIn))
        {
            await Assert.That(ReferenceEquals(orphan.Typeface, standIn)).IsTrue();
            await Assert.That(orphan.OwnsTypeface).IsFalse();
            await Assert.That(orphan.Get("AB", 12f)).IsNotNull();
        }

        // Disposed twice over, exactly as several layers sharing one fallback would.
        using (TextBlobCache second = new(16, "DemoViewer.NET.Playback2D.Core.Assets.NoSuchFace.ttf",
                   standIn))
        {
            _ = second.Typeface;
        }

        Console.WriteLine($"[typeface] borrowed face handle after two disposes: {standIn.Handle}");

        await Assert.That(standIn.Handle).IsNotEqualTo(IntPtr.Zero);
        using SKFont font = new(standIn, 12f);
        await Assert.That(SKTextBlob.Create("still here", font)).IsNotNull();
    }

    [Test]
    public async Task TheEmbeddedFace_IsOwned_AndIsStillDisposed()
    {
        // The other side of the ownership test: the normal path must keep releasing what it loaded, or
        // the fix would have traded a process-wide crash for a native leak per layer.
        SKTypeface loaded;
        using (TextBlobCache cache = new())
        {
            await Assert.That(cache.OwnsTypeface).IsTrue();
            loaded = cache.Typeface;
        }

        await Assert.That(loaded.Handle).IsEqualTo(IntPtr.Zero);
    }

    // ── 9 · hud.roster's CT column and hud.killfeed occupy the same rectangle ────────────────────────

    [Test]
    public async Task TheRosterAndTheKillFeed_DoNotOverlap_OnAShortPane()
    {
        // 1280×360 is the top band of a 1280×720 two-level stacked export — the case both layers' own doc
        // comments cite. The roster centres a 5-card column over the whole pane and the feed runs ~159 px
        // down from the top edge, both against the right edge; the feed is Order 80 against the roster's
        // 65, so it painted straight over the cards. Neither suite saw it because each layer was only
        // ever mounted alone.
        SKSizeI size = new(1280, 360);
        HudSnapshot snapshot = ExportFixtures.Hud(6, roster: ExportFixtures.Roster()) with
        {
            Roster = ExportFixtures.Roster()
        };

        bool[] roster = PaintMask(new RosterLayer(new StubHudDataSource(snapshot)), size);
        bool[] feed = PaintMask(new KillFeedLayer(new StubHudDataSource(snapshot)), size);

        int rosterInk = Count(roster), feedInk = Count(feed), overlap = 0;
        for (int i = 0; i < roster.Length; i++)
        {
            if (roster[i] && feed[i])
            {
                overlap++;
            }
        }

        Console.WriteLine($"[hud-collide] {size.Width}x{size.Height} roster={rosterInk} px " +
                          $"feed={feedInk} px overlap={overlap} px");

        await Assert.That(rosterInk).IsGreaterThan(0).Because("the roster must still draw, not withdraw");
        await Assert.That(feedInk).IsGreaterThan(0);
        await Assert.That(overlap).IsEqualTo(0);
    }

    [Test]
    public async Task OnAPaneTallEnoughForBoth_TheRosterKeepsItsCentring()
    {
        // The reservation is taken unconditionally — a layer cannot see its siblings — so it has to be a
        // NO-OP wherever a centred roster already clears the feed. At 720p it does, and the strips must
        // still be centred on the pane rather than shoved into the lower two thirds of the frame.
        SKSizeI size = new(1280, 720);
        HudSnapshot snapshot = ExportFixtures.Hud(0, roster: ExportFixtures.Roster());

        bool[] mask = PaintMask(new RosterLayer(new StubHudDataSource(snapshot)), size);
        (int top, int bottom) = VerticalExtent(mask, size.Width);
        double centre = (top + bottom) / 2.0;

        Console.WriteLine($"[hud-centring] roster ink rows {top}..{bottom}, centre {centre:F1} " +
                          $"against pane centre {size.Height / 2.0:F1}");

        await Assert.That(Math.Abs(centre - (size.Height / 2.0))).IsLessThan(2.0);
    }

    // ── 32a · SceneFrameBuilder.Reset does not reset LastRoster ──────────────────────────────────────

    [Test]
    public async Task ResettingTheBuilder_ClearsTheRoster_LikeEveryOtherCacheItHolds()
    {
        // TrackerFrameSource.LastRoster is assigned straight from here, so a roster left standing across
        // a demo reset is the previous match's pooled list — still populated, still readable, and read by
        // any HUD source built before the next Build.
        SceneFrameBuilder builder = new();
        FakeEntity pawn = new FakeEntity("CCSPlayerPawn").With("m_iHealth", 100);
        FakePlayer player = new()
        {
            Slot = 0,
            Team = 2,
            Pawn = pawn,
            WorldPosition = (10f, 20f, 64f)
        };

        Build(builder, Input([player], 1, TickRate));
        await Assert.That(builder.LastRoster.Count).IsEqualTo(1);

        builder.Reset();

        await Assert.That(builder.LastRoster.Count).IsEqualTo(0)
            .Because("every other cache Reset touches is cleared; this one was the exception");
    }

    // ── 32b · TimelineHudDataSource's tick cache can hand back the previous frame's roster ───────────

    [Test]
    public async Task TwoFramesAtOneTick_EachSeeItsOwnRoster()
    {
        // CS2 emits several demo frames per tick, so two consecutive OUTPUT frames can map to one tick.
        // The snapshot was cached by tick alone, so the second frame drew the first frame's cards — and
        // the builder double-buffers, so the stale reference is the other slot's list, still holding the
        // older state rather than aliasing the newer one.
        SceneFrameBuilder builder = new();
        TimelineHudDataSource source = new([], TickRate, static _ => ClockReading.Unknown,
            rosterAt: _ => builder.LastRoster);

        FakePlayer Hurt(int health) => new()
        {
            Slot = 0,
            Team = 2,
            Pawn = new FakeEntity("CCSPlayerPawn").With("m_iHealth", health),
            WorldPosition = (10f, 20f, 64f)
        };

        // Two demo frames, ONE tick.
        Build(builder, Input([Hurt(100)], 1, 5000));
        int first = source.At(5000).Roster[0].Health;

        Build(builder, Input([Hurt(23)], 2, 5000));
        int second = source.At(5000).Roster[0].Health;

        Console.WriteLine($"[hud-tick-cache] health at tick 5000: frame1={first} frame2={second}");

        await Assert.That(first).IsEqualTo(100);
        await Assert.That(second).IsEqualTo(23);
    }

    [Test]
    public async Task TwoFramesAtOneTick_EachSeeTheirOwnClock()
    {
        // The same cache, and the half that used to be wrong: LastGameInfo moves with the frame, not
        // with the tick.
        ClockReading reading = ClockReading.From(new SceneGameInfo("Live", "—", 7, 6, 30, "0:30",
            false, false, "—", double.NaN, "—", 3, 2));
        TimelineHudDataSource source = new([], TickRate, _ => reading);

        await Assert.That(source.At(4200).TScore).IsEqualTo(3);

        reading = ClockReading.From(new SceneGameInfo("Live", "—", 7, 6, 29, "0:29",
            false, false, "—", double.NaN, "—", 4, 2));

        await Assert.That(source.At(4200).TScore).IsEqualTo(4);
    }

    // ── 24 · MapSpaceFactory.Update allocates on the histogram path ──────────────────────────────────

    [Test]
    public async Task TheHistogramPath_AllocatesNothingPerFrame()
    {
        // The branch every user without a baked map bundle is on, and the one the §6 budget gate has
        // never measured: BudgetTests calls SetAuthoritativeFloors first and takes the short-circuit.
        // FloorSplitter.Observe marks the histogram dirty for EVERY marker, so the Slices read below
        // recomputed in full every frame — a List, an int[] and two more Lists, measured at 552 B/frame.
        //
        // Kept out of [Category("Budget")] on purpose: this is a hundred milliseconds, and it is the one
        // gate for the branch.
        MapSpaceFactory factory = new();
        Scene2DFrame frame = TwoFloorFrame();

        for (int i = 0; i < 256; i++)
        {
            factory.Update(frame);
        }

        await Assert.That(factory.Space.Levels.Count).IsEqualTo(2)
            .Because("the histogram must actually have found both bands, or this measures nothing");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Two identical windows, and the SECOND is asserted on — the same discipline BudgetTests uses and
        // for the same reason: the first window occasionally shows one small allocation at a varying
        // iteration, which is the runtime tiering the loop body rather than the code under test. Charging
        // that to the budget would make the gate flaky or push it above zero.
        long warm = MeasureUpdates(factory, frame);
        long steady = MeasureUpdates(factory, frame);

        Console.WriteLine($"[alloc] histogram path: warm {warm} B, steady {steady} B over 512 updates " +
                          $"({steady / 512.0:F2} B/frame)");

        await Assert.That(steady).IsEqualTo(0);
    }

    private static long MeasureUpdates(MapSpaceFactory factory, Scene2DFrame frame)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 512; i++)
        {
            factory.Update(frame);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public async Task TheHistogramStillRepublishesItsBands_WhenTheyActuallyMove()
    {
        // The allocation went away by re-publishing the band list only when the bands changed. That is
        // load-bearing in BOTH directions: MapSpaceFactory.SameBands short-circuits on ReferenceEquals,
        // so a list refilled in place would have made a real split invisible to the rebuild. A second
        // floor appearing must still reach the space.
        MapSpaceFactory factory = new();
        Scene2DFrame ground = OneFloorFrame();

        for (int i = 0; i < 64; i++)
        {
            factory.Update(ground);
        }

        await Assert.That(factory.Space.Levels.Count).IsEqualTo(1);

        Scene2DFrame both = TwoFloorFrame();
        bool changed = false;
        for (int i = 0; i < 64; i++)
        {
            changed |= factory.Update(both);
        }

        await Assert.That(changed).IsTrue();
        await Assert.That(factory.Space.Levels.Count).IsEqualTo(2);
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────

    // One alive marker whose X is NaN, one sound marker beside it. Z is finite on both: the floor
    // histogram is a separate producer with its own guard, and mixing the two would make a failure here
    // ambiguous.
    private static Scene2DFrame PoisonedFrame() => new()
    {
        Time = new SceneTime(1000, 0, 0, 1.0 / TickRate, false),
        Markers =
        [
            new PlayerMarker(0, 2, float.NaN, 0f, 0f, 0f, RingState.Team, 1, "AA", true, 0, 0, 0),
            new PlayerMarker(1, 3, 500f, 300f, 0f, 0f, RingState.Team, 1, "BB", true, 0, 0, 0)
        ],
        Map = new SceneMapInfo
        {
            MapName = "synthetic_nan",
            ObservedBounds = new WorldBounds(-1000, -1000, 1000, 1000)
        }
    };

    // Ten markers on one Z band, and ten on another far enough apart that the density-valley split finds
    // two floors. The bands are what the histogram derives; nothing here is authoritative.
    private static Scene2DFrame TwoFloorFrame() => FrameWithMarkers(-400f, 400f);

    private static Scene2DFrame OneFloorFrame() => FrameWithMarkers(-400f, -400f);

    private static Scene2DFrame FrameWithMarkers(float lowerZ, float upperZ)
    {
        List<PlayerMarker> markers = new(10);
        for (int i = 0; i < 10; i++)
        {
            markers.Add(new PlayerMarker(i, i < 5 ? 2 : 3,
                -1000f + (i * 200f), -500f + (i % 3 * 300f), i < 5 ? lowerZ : upperZ,
                0f, RingState.Team, 1, "PP", true, 0, 0, 0));
        }

        return new Scene2DFrame
        {
            Time = new SceneTime(1000, 0, 0, 1.0 / TickRate, false),
            Markers = markers,
            Map = new SceneMapInfo
            {
                MapName = "synthetic_floors",
                ObservedBounds = new WorldBounds(-1200, -800, 1200, 800)
            }
        };
    }

    private static MapLevel Level() => new()
    {
        Id = new MapLevelId(3),
        Name = "floor",
        ZMin = -1000,
        ZMax = 1000
    };

    // A single-pane context that HAS framed its pane, so picture caching is live: the palette gate and
    // the unframed-pane bypass are separate mechanisms and a test of one must not ride on the other.
    private static SceneRenderContext PanedContext(ScenePalette palette)
    {
        MapLevel level = Level();
        ViewportTransform camera = ViewportTransform.Fit(128, 128, -512, -512, 512, 512);
        return new SceneRenderContext(Scene2DFrame.Empty, default, camera,
            new SKRect(0, 0, 128, 128), -1, -1000, 1000, RenderPurpose.Interactive, palette, 1f)
        {
            Pane = new LevelPaneSnapshot(level.Id, 0, level, camera, new SKRect(0, 0, 128, 128), 1)
        };
    }

    private static SceneSubmission Submission(ScenePalette palette)
    {
        MapLevel level = Level();
        LevelPaneSnapshot pane = new(level.Id, 0, level,
            ViewportTransform.Fit(128, 128, -512, -512, 512, 512), new SKRect(0, 0, 128, 128), 1);
        return new SceneSubmission(1, Scene2DFrame.Empty, default, [pane], palette,
            RenderPurpose.Interactive, new SKRect(0, 0, 128, 128), 1f);
    }

    // A pane framed on a level carrying `radar`, at the size the destination rectangle should round to.
    // The world rectangle and the viewport are the same square, so destination ≈ the pane.
    private static SceneRenderContext RadarContext(SKImage radar, int width, int height)
    {
        MapLevel level = new()
        {
            Id = new MapLevelId(1),
            Name = "floor",
            ZMin = -100,
            ZMax = 100,
            Radar = radar
        };

        ViewportTransform camera = ViewportTransform.Fit(width, height, -100, -100, 100, 100, margin: 0);
        return new SceneRenderContext(Scene2DFrame.Empty, default, camera,
            new SKRect(0, 0, width, height), -1, -100, 100, RenderPurpose.Export, ScenePalette.Dark, 1f)
        {
            Pane = new LevelPaneSnapshot(level.Id, 0, level, camera, new SKRect(0, 0, width, height), 1)
        };
    }

    private static SKImage SolidImage(int width, int height, SKColor color)
    {
        using SKSurface surface = SKSurface.Create(new SKImageInfo(width, height,
            SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(color);
        return surface.Snapshot();
    }

    private static SKTypeface LoadEmbeddedFace()
    {
        using Stream stream = typeof(TextBlobCache).Assembly
            .GetManifestResourceStream(TextBlobCache.TypefaceResourceName)!;
        return SKTypeface.FromStream(stream)!;
    }

    // Which pixels one layer painted, as a flat mask over the pane. Two masks intersected is how "these
    // two layers claim the same rectangle" is asserted without depending on which one paints last.
    private static bool[] PaintMask(ISceneLayer layer, SKSizeI size)
    {
        using (layer)
        {
            using CpuSurfaceProvider provider = new();
            using SKSurface surface = provider.CreateSurface(size);
            surface.Canvas.Clear(ScenePalette.Dark.Background);

            SceneTime time = new(1000, 0, 0, 1 / 60.0, true);
            layer.Advance(in time, Scene2DFrame.Empty);
            layer.Render(surface.Canvas, new SceneRenderContext(Scene2DFrame.Empty, time,
                ViewportTransform.Fit(size.Width, size.Height, -100, -100, 100, 100),
                new SKRect(0, 0, size.Width, size.Height), 0, 0, 0, RenderPurpose.Export,
                ScenePalette.Dark, 1f));

            using SKImage image = surface.Snapshot();
            using SKBitmap bitmap = SKBitmap.FromImage(image);

            bool[] mask = new bool[size.Width * size.Height];
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    mask[(y * size.Width) + x] = bitmap.GetPixel(x, y) != ScenePalette.Dark.Background;
                }
            }

            return mask;
        }
    }

    private static int Count(bool[] mask)
    {
        int painted = 0;
        foreach (bool set in mask)
        {
            if (set)
            {
                painted++;
            }
        }

        return painted;
    }

    private static (int Top, int Bottom) VerticalExtent(bool[] mask, int width)
    {
        int top = -1, bottom = -1;
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i])
            {
                continue;
            }

            int y = i / width;
            if (top < 0)
            {
                top = y;
            }

            bottom = y;
        }

        return (top, bottom);
    }

    private static SKColor PixelAt(SKSurface surface, int x, int y)
    {
        using SKImage image = surface.Snapshot();
        using SKBitmap bitmap = SKBitmap.FromImage(image);
        return bitmap.GetPixel(x, y);
    }

    private static byte[] Snapshot(SKSurface surface)
    {
        using SKImage image = surface.Snapshot();
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static int ColumnOfInk(SKSurface surface)
    {
        using SKImage image = surface.Snapshot();
        using SKBitmap bitmap = SKBitmap.FromImage(image);
        int y = bitmap.Height / 2;
        for (int x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, y) != ScenePalette.Dark.Background)
            {
                return x;
            }
        }

        return -1;
    }

    // SceneFrameInput is a ref struct; an `in` parameter cannot bind a call's return value.
    private static Scene2DFrame Build(SceneFrameBuilder builder, SceneFrameInput input) =>
        builder.Build(in input);

    private static SceneFrameInput Input(IReadOnlyList<IPlayerState> players, int frameIndex, int tick) =>
        new()
        {
            Players = players,
            Entities = new FakeEntityView(),
            FrameIndex = frameIndex,
            Tick = tick,
            TickRate = TickRate,
            CurtimeSeconds = tick / (double)TickRate,
            LabelForSlot = static slot => "P" + slot,
            SteamIdForSlot = static slot => 76561197960265728UL + (ulong)slot
        };

    /// <summary>A <c>PerCamera</c> layer whose only content is a palette colour. The palette-key gate.</summary>
    private sealed class PaletteFillLayer : ISceneLayer
    {
        public int RenderCalls { get; private set; }
        public string Id => "test.palette";
        public LayerSlot Slot => LayerSlot.Underlay;
        public int Order => 0;
        public LayerCacheHint Cache => LayerCacheHint.PerCamera;
        public bool IsEnabled { get; set; } = true;
        public int ContentVersion => 0;

        public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

        public void Render(SKCanvas canvas, SceneRenderContext ctx)
        {
            RenderCalls++;
            using SKPaint paint = new();
            paint.Color = ctx.Palette.MinorGrid;
            paint.IsAntialias = false;
            canvas.DrawRect(ctx.PaneBounds, paint);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A <c>PerCamera</c> layer that draws at a world position, so a stale replay is visible.</summary>
    private sealed class WorldSquareLayer : ISceneLayer
    {
        public int RenderCalls { get; private set; }
        public string Id => "test.percamera.world";
        public LayerSlot Slot => LayerSlot.World;
        public int Order => 0;
        public LayerCacheHint Cache => LayerCacheHint.PerCamera;
        public bool IsEnabled { get; set; } = true;
        public int ContentVersion => 0;

        public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

        public void Render(SKCanvas canvas, SceneRenderContext ctx)
        {
            RenderCalls++;

            // Drawn in PANE-LOCAL screen space, through the camera — which is exactly what a PerCamera
            // recording bakes in, and exactly what goes stale when the key does not carry the camera.
            (double x0, double y0) = ctx.Transform.WorldToScreen(-64, 64);
            (double x1, double y1) = ctx.Transform.WorldToScreen(64, -64);

            using SKPaint paint = new();
            paint.Color = SKColors.Red;
            paint.IsAntialias = false;
            canvas.DrawRect(new SKRect((float)x0, (float)y0, (float)x1, (float)y1), paint);
        }

        public void Dispose()
        {
        }
    }
}
