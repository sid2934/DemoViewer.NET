#region

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
///     <b>What an export is framed by.</b> Every pane is BORN fitted to <c>WorldBounds.Default</c> — the
///     ±3000 placeholder <c>PaneSet.Reconcile</c> has to use, because it runs before any frame has been
///     read — and until this landed, nothing in the export path ever re-framed it: the camera script is
///     empty in both front ends unless the user pinned one, <c>AdvanceCameras</c> is off so no rig steps,
///     and <c>FitAll</c> was never called. Every exported video was composed by a placeholder; de_nuke
///     only looked plausible because halving the fit scale for two stacked bands happened to land near
///     the map.
///     <para>
///         The live window always had the missing step (<c>Scene2DHost</c>'s one-shot fit "once real
///         positions exist"). <see cref="HeadlessSceneRenderer.AutoFitOnFirstMapBounds" /> is its
///         offscreen twin, opt-in so a golden's camera-as-data is never overwritten.
///     </para>
/// </summary>
public class ExportInitialFitTests
{
    /// <summary>
    ///     The regression. A source whose first frame carries no map extent and no markers, followed by
    ///     frames that carry both: the panes must move off the placeholder onto the map, on the first
    ///     frame that can say where the map is.
    /// </summary>
    [Test]
    public async Task AnExport_RefitsOntoTheMap_OnTheFirstFrameThatKnowsWhereItIs()
    {
        SceneFixture real = SyntheticScenes.FullSceneBudget();
        CameraProbeLayer probe = new();

        await RunAsync(probe, [Blank(real), real, real, real]);

        await Assert.That(probe.Frames.Count).IsEqualTo(4);

        ViewportTransform placeholder = probe.Frames[0][0];
        ViewportTransform fitted = probe.Frames[1][0];

        WorldBounds map = real.Frame.Map.NetworkedBounds!.Value;
        ViewportTransform expected = ViewportTransform.Fit(placeholder.ViewWidth, placeholder.ViewHeight,
            map.MinX, map.MinY, map.MaxX, map.MaxY);

        Console.WriteLine($"[fit] pane 0 scale {placeholder.BaseScale:F5} → {fitted.BaseScale:F5} " +
                          $"(expected {expected.BaseScale:F5}), centre " +
                          $"({placeholder.CenterX:F0},{placeholder.CenterY:F0}) → " +
                          $"({fitted.CenterX:F0},{fitted.CenterY:F0})");

        // Born on the ±3000 placeholder…
        await Assert.That(placeholder.BaseScale)
            .IsEqualTo(ViewportTransform.Fit(placeholder.ViewWidth, placeholder.ViewHeight,
                WorldBounds.Default.MinX, WorldBounds.Default.MinY,
                WorldBounds.Default.MaxX, WorldBounds.Default.MaxY).BaseScale)
            .Within(1e-9)
            .Because("a pane arranged before any frame was read can only fit the placeholder extent");

        // …and re-fitted onto the map's own rectangle the moment one arrives. This is the assertion that
        // fails without AutoFitOnFirstMapBounds: the two scales were identical.
        await Assert.That(fitted.BaseScale).IsEqualTo(expected.BaseScale).Within(1e-9);
        await Assert.That(fitted.CenterX).IsEqualTo(expected.CenterX).Within(1e-6);
        await Assert.That(fitted.CenterY).IsEqualTo(expected.CenterY).Within(1e-6);
        await Assert.That(fitted.BaseScale).IsNotEqualTo(placeholder.BaseScale);
    }

    /// <summary>
    ///     One shot, not every frame. A fit that re-ran would be a second hand on the wheel: it would
    ///     fight the camera script on every frame, and it would make the framing drift as
    ///     <c>ObservedBounds</c> widened with the players.
    /// </summary>
    [Test]
    public async Task TheFit_HappensOnce_AndTheFramingHoldsForTheRestOfTheRun()
    {
        SceneFixture real = SyntheticScenes.FullSceneBudget();
        SceneFixture wider = real with
        {
            Frame = With(real.Frame, new SceneMapInfo
            {
                MapName = real.Frame.Map.MapName,
                NetworkedBounds = real.Frame.Map.NetworkedBounds,
                SectionHeights = real.Frame.Map.SectionHeights,
                Radars = real.Frame.Map.Radars,

                // The players wander: ObservedBounds only ever widens. A fit that re-ran would follow it.
                ObservedBounds = new WorldBounds(-9000, -9000, 9000, 9000)
            })
        };

        CameraProbeLayer probe = new();
        await RunAsync(probe, [Blank(real), real, wider, wider]);

        for (int i = 2; i < probe.Frames.Count; i++)
        {
            await Assert.That(probe.Frames[i][0].BaseScale)
                .IsEqualTo(probe.Frames[1][0].BaseScale).Within(1e-9);
            await Assert.That(probe.Frames[i][0].CenterX)
                .IsEqualTo(probe.Frames[1][0].CenterX).Within(1e-6);
        }
    }

    /// <summary>
    ///     Every level, not just the first. A pane born mid-export — a player taking the lift on Nuke —
    ///     used to be fitted to whatever <c>ObservedBounds</c> had accumulated, which on frame one is the
    ///     placeholder. Both bands must show the same world rectangle, or a two-floor export is two
    ///     different maps stacked.
    /// </summary>
    [Test]
    public async Task EveryBand_IsFramedByTheSameWorldRectangle()
    {
        SceneFixture real = SyntheticScenes.FullSceneBudget();
        CameraProbeLayer probe = new();

        await RunAsync(probe, [Blank(real), real, real]);

        IReadOnlyList<ViewportTransform> bands = probe.Frames[1];
        await Assert.That(bands.Count).IsGreaterThan(1).Because("the budget floors make two stacked bands");

        for (int i = 1; i < bands.Count; i++)
        {
            await Assert.That(bands[i].CenterX).IsEqualTo(bands[0].CenterX).Within(1e-6);
            await Assert.That(bands[i].CenterY).IsEqualTo(bands[0].CenterY).Within(1e-6);
        }
    }

    /// <summary>
    ///     The flag is opt-in, and off is what a golden and a single-frame <c>dv2d render</c> get: their
    ///     camera is DATA, and a fit that overwrote it would silently re-baseline every golden.
    ///     <para>
    ///         This is also the negative control for the case above. Same two frames, flag off: the pane
    ///         born on the placeholder <b>stays</b> there when the map arrives, which is exactly the bug
    ///         every export shipped with.
    ///     </para>
    /// </summary>
    [Test]
    public async Task WithTheFlagOff_ThePlaceholderFramingSurvives()
    {
        SceneFixture real = SyntheticScenes.FullSceneBudget();

        using SceneCompositor compositor = new();
        CameraProbeLayer probe = new();
        compositor.Add(probe);

        using CpuSurfaceProvider surfaces = new();
        using HeadlessSceneRenderer renderer = new(surfaces, compositor) { Size = new SKSizeI(320, 180) };
        renderer.Levels.SetAuthoritativeFloors(SyntheticScenes.BudgetFloors);

        await Assert.That(renderer.AutoFitOnFirstMapBounds).IsFalse().Because("off is the default");

        Advance(renderer, Blank(real));
        Advance(renderer, real);

        ViewportTransform born = probe.Frames[0][0];
        ViewportTransform later = probe.Frames[1][0];

        await Assert.That(born.BaseScale)
            .IsEqualTo(ViewportTransform.Fit(born.ViewWidth, born.ViewHeight,
                WorldBounds.Default.MinX, WorldBounds.Default.MinY,
                WorldBounds.Default.MaxX, WorldBounds.Default.MaxY).BaseScale)
            .Within(1e-9);
        await Assert.That(later.BaseScale).IsEqualTo(born.BaseScale).Within(1e-9)
            .Because("nothing but the opt-in fit ever re-frames a headless pane");
    }

    /// <summary>
    ///     The other half of the fix: a pane that is BORN after the map is known is born fitted to the
    ///     map's own rectangle rather than to whatever the players have wandered over so far. That is the
    ///     mid-export level birth — a player takes the lift on Nuke — which the one-shot fit has already
    ///     spent by the time it happens.
    /// </summary>
    [Test]
    public async Task APaneBornWithTheMapAlreadyKnown_IsBornFittedToIt()
    {
        SceneFixture real = SyntheticScenes.FullSceneBudget();

        using SceneCompositor compositor = new();
        CameraProbeLayer probe = new();
        compositor.Add(probe);

        using CpuSurfaceProvider surfaces = new();
        using HeadlessSceneRenderer renderer = new(surfaces, compositor) { Size = new SKSizeI(320, 180) };
        renderer.Levels.SetAuthoritativeFloors(SyntheticScenes.BudgetFloors);

        Advance(renderer, real);

        ViewportTransform born = probe.Frames[0][0];
        WorldBounds map = real.Frame.Map.NetworkedBounds!.Value;

        // The networked rectangle, NOT the narrower ObservedBounds the birth extent used to be.
        await Assert.That(born.BaseScale)
            .IsEqualTo(ViewportTransform.Fit(born.ViewWidth, born.ViewHeight,
                map.MinX, map.MinY, map.MaxX, map.MaxY).BaseScale)
            .Within(1e-9);
    }

    /// <summary>
    ///     An explicit camera still wins. The fit runs BEFORE the pin and before the camera policy, so a
    ///     user who framed A site and asked for "mirror the live view" is never overruled by it.
    /// </summary>
    [Test]
    public async Task AnExplicitCamera_OverridesTheFit_OnTheSameFrame()
    {
        SceneFixture real = SyntheticScenes.FullSceneBudget();
        ViewportTransform pinned = ViewportTransform.Fit(320, 90, -400, -300, 400, 300);

        using SceneCompositor compositor = new();
        CameraProbeLayer probe = new();
        compositor.Add(probe);

        using CpuSurfaceProvider surfaces = new();
        using HeadlessSceneRenderer renderer = new(surfaces, compositor)
        {
            Size = new SKSizeI(320, 180),
            AutoFitOnFirstMapBounds = true,
            Camera = pinned
        };
        renderer.Levels.SetAuthoritativeFloors(SyntheticScenes.BudgetFloors);

        Advance(renderer, real);

        ViewportTransform drawn = probe.Frames[0][0];
        await Assert.That(drawn.BaseScale).IsEqualTo(pinned.BaseScale).Within(1e-9);
        await Assert.That(drawn.CenterX).IsEqualTo(pinned.CenterX).Within(1e-6);
    }

    // ── helpers ──

    private static void Advance(HeadlessSceneRenderer renderer, SceneFixture fixture)
    {
        SceneTime time = fixture.Time;
        renderer.Advance(fixture.Frame, in time);
        renderer.Render();
    }

    private static async Task RunAsync(CameraProbeLayer probe, SceneFixture[] fixtures)
    {
        using SceneCompositor compositor = new();
        compositor.Add(probe);
        using CpuSurfaceProvider surfaces = new();

        // The floors a real export always supplies from the map bundle, so the pane set is two stable
        // bands rather than a Z histogram warming up under the assertions.
        SceneExportSession session = new(compositor)
        {
            AuthoritativeFloors = SyntheticScenes.BudgetFloors
        };

        await session.RunAsync(
            ExportFixtures.Request(fixtures.Length, size: new SKSizeI(320, 180)),
            new FixtureFrameSource(fixtures), new RecordingFrameSink(), surfaces, null,
            CancellationToken.None);
    }

    /// <summary>
    ///     The state an export's first frames really are in: the tracker has been seeded but nothing has
    ///     published a world extent and no pawn has a position yet, so <c>SceneMapInfo</c> is still all
    ///     defaults. Timed and sized like the real fixture so only the map and the markers differ.
    /// </summary>
    private static SceneFixture Blank(SceneFixture like) =>
        like with
        {
            Frame = new Scene2DFrame
            {
                Time = like.Frame.Time,
                Markers = [],
                Map = new SceneMapInfo { MapName = like.Frame.Map.MapName }
            }
        };

    private static Scene2DFrame With(Scene2DFrame source, SceneMapInfo map) => new()
    {
        Time = source.Time,
        Markers = source.Markers,
        AreaEffects = source.AreaEffects,
        Trails = source.Trails,
        Bomb = source.Bomb,
        KillFeed = source.KillFeed,
        GameInfo = source.GameInfo,
        Map = map,
        Vision = source.Vision,
        FollowSlot = source.FollowSlot
    };
}
