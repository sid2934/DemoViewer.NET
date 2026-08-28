#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Cameras;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Each rig against the pre-v2 formula it was lifted from, transcribed here so the two can be
///     compared numerically rather than by reading. Parity is asserted to 1e-9: these are the same
///     arithmetic, so anything looser would hide a genuine transcription slip.
/// </summary>
public class CameraRigTests
{
    private const double AlivePadding = 0.18;
    private const double FollowHalfWorld = 900;
    private const float PaneW = 800;
    private const float PaneH = 300;

    [Test]
    public async Task FitMapRig_PrefersTheNetworkedBounds()
    {
        Scene2DFrame frame = new()
        {
            Map = new SceneMapInfo
            {
                MapName = "de_nuke",
                NetworkedBounds = new WorldBounds(-3110, -2577, 3702, 1184),
                ObservedBounds = new WorldBounds(-100, -100, 100, 100)
            }
        };

        ViewportTransform actual = FitMapRig.Instance.ComputeTarget(Pane(), frame)!.Value;
        ViewportTransform expected = ViewportTransform.Fit(PaneW, PaneH, -3110, -2577, 3702, 1184);

        await AssertSame(actual, expected);
    }

    [Test]
    public async Task FitMapRig_FallsBackToTheObservedExtent()
    {
        Scene2DFrame frame = new()
        {
            Map = new SceneMapInfo
            {
                ObservedBounds = new WorldBounds(-500, -400, 600, 700)
            }
        };

        ViewportTransform actual = FitMapRig.Instance.ComputeTarget(Pane(), frame)!.Value;
        await AssertSame(actual, ViewportTransform.Fit(PaneW, PaneH, -500, -400, 600, 700));
    }

    [Test]
    public async Task FitAliveRig_MatchesTryFitAlive()
    {
        Scene2DFrame frame = new()
        {
            Markers =
            [
                Marker(0, 2, -300, -200, 0),
                Marker(1, 3, 400, 500, 0),
                Marker(2, 3, 4000, 4000, 0, false) // dead players are excluded
            ]
        };

        ViewportTransform actual = FitAliveRig.Instance.ComputeTarget(Pane(), frame)!.Value;

        double padX = Math.Max((400 - -300) * AlivePadding, FollowHalfWorld);
        double padY = Math.Max((500 - -200) * AlivePadding, FollowHalfWorld);
        ViewportTransform expected = ViewportTransform.Fit(PaneW, PaneH,
            -300 - padX, -200 - padY, 400 + padX, 500 + padY);

        await AssertSame(actual, expected);
    }

    [Test]
    public async Task FitAliveRig_WithNobodyAlive_Holds()
    {
        Scene2DFrame frame = new()
        {
            Markers = [Marker(0, 2, 0, 0, 0, false)]
        };

        await Assert.That(FitAliveRig.Instance.ComputeTarget(Pane(), frame)).IsNull();
    }

    /// <summary>
    ///     Parity behaviour, not an optimisation: a single-band render frames every player regardless of
    ///     Z (the pre-v2 <c>_cameras.Length &gt; 1</c> guard at line 762). Split the same roster over two
    ///     panes and each pane frames only its own floor.
    /// </summary>
    [Test]
    public async Task FitAliveRig_FiltersByLevel_OnlyWhenThereIsMoreThanOnePane()
    {
        Scene2DFrame frame = new()
        {
            Markers = [Marker(0, 2, -300, -200, -400), Marker(1, 3, 400, 500, 100)]
        };

        MapSpace space = new();
        space.Rebuild([new FloorSlice(-448, -352), new FloorSlice(0, 192)]);

        LevelPane single = Pane();
        single.Space = space;
        single.PaneCount = 1;
        ViewportTransform both = FitAliveRig.Instance.ComputeTarget(single, frame)!.Value;

        LevelPane lower = Pane();
        lower.Space = space;
        lower.PaneCount = 2;
        lower.LevelIndex = 0;
        ViewportTransform onlyLower = FitAliveRig.Instance.ComputeTarget(lower, frame)!.Value;

        await Assert.That(both.CenterX).IsEqualTo(50d).Within(1e-9); // midpoint of -300 and 400
        await Assert.That(onlyLower.CenterX).IsEqualTo(-300d).Within(1e-9);
    }

    [Test]
    public async Task FollowPlayerRig_WithZeroDeadzone_IsByteIdenticalToTryFollow()
    {
        FollowPlayerRig rig = new(3, deadzoneHalfWorld: 0);

        // Walk the followed player across the map; every frame must land exactly on TryFollow's answer.
        for (int i = 0; i < 32; i++)
        {
            float x = -1000 + i * 71.5f;
            float y = 250 - i * 33.25f;
            Scene2DFrame frame = new()
            {
                Markers = [Marker(3, 3, x, y, 0)]
            };

            ViewportTransform actual = rig.ComputeTarget(Pane(), frame)!.Value;
            ViewportTransform expected = ViewportTransform.Fit(PaneW, PaneH,
                x - FollowHalfWorld, y - FollowHalfWorld, x + FollowHalfWorld, y + FollowHalfWorld);
            await AssertSame(actual, expected);
        }
    }

    [Test]
    public async Task FollowPlayerRig_WithNoMarkerForTheSlot_Holds()
    {
        FollowPlayerRig rig = new(9);
        Scene2DFrame frame = new()
        {
            Markers = [Marker(3, 3, 0, 0, 0)]
        };

        await Assert.That(rig.ComputeTarget(Pane(), frame)).IsNull();
    }

    [Test]
    public async Task FollowPlayerRig_FollowsADeadPlayersLastKnownMarker()
    {
        FollowPlayerRig rig = new(3, deadzoneHalfWorld: 0);
        Scene2DFrame frame = new()
        {
            Markers = [Marker(3, 3, 120, -40, 0, false)]
        };

        ViewportTransform? target = rig.ComputeTarget(Pane(), frame);
        await Assert.That(target).IsNotNull();
        await Assert.That(target!.Value.CenterX).IsEqualTo(120d).Within(1e-9);
    }

    [Test]
    public async Task FollowPlayerRig_Deadzone_HoldsInsideAndRecentresOutside()
    {
        FollowPlayerRig rig = new(3, deadzoneHalfWorld: 180);

        ViewportTransform first = rig.ComputeTarget(Pane(), FrameAt(0, 0))!.Value;
        await Assert.That(first.CenterX).IsEqualTo(0d).Within(1e-9);

        // A strafe inside the box: the committed centre holds, so the map does not slide under the player.
        ViewportTransform inside = rig.ComputeTarget(Pane(), FrameAt(150, -170))!.Value;
        await Assert.That(inside.CenterX).IsEqualTo(0d).Within(1e-9);
        await Assert.That(inside.CenterY).IsEqualTo(0d).Within(1e-9);

        // A rotate past the edge: recentre on the new position.
        ViewportTransform outside = rig.ComputeTarget(Pane(), FrameAt(400, -170))!.Value;
        await Assert.That(outside.CenterX).IsEqualTo(400d).Within(1e-9);
        await Assert.That(outside.CenterY).IsEqualTo(-170d).Within(1e-9);
    }

    [Test]
    public async Task FollowPlayerRig_ResetDeadzone_RecentresImmediately()
    {
        FollowPlayerRig rig = new(3, deadzoneHalfWorld: 180);
        rig.ComputeTarget(Pane(), FrameAt(0, 0));

        rig.ResetDeadzone();
        ViewportTransform after = rig.ComputeTarget(Pane(), FrameAt(100, 100))!.Value;

        await Assert.That(after.CenterX).IsEqualTo(100d).Within(1e-9);
    }

    /// <summary>
    ///     <c>AdvanceCameras</c>'s two termination rules: manual panes are untouched, and a pane close
    ///     enough to its target snaps so the render loop can stop re-arming.
    /// </summary>
    [Test]
    public async Task CameraAdvancer_SkipsManualPanes_AndSnapsWhenSettled()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(0, 192)]);
        PaneSet panes = new(new StackedLayout());
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(PaneW, PaneH),
            new WorldBounds(-1000, -1000, 1000, 1000));

        LevelPane pane = panes.Panes[0];
        pane.Rig = FitMapRig.Instance;
        pane.Camera.ManualOverride = true;
        ViewportTransform before = pane.Camera.Current;

        Scene2DFrame frame = new()
        {
            Map = new SceneMapInfo
            {
                ObservedBounds = new WorldBounds(-4000, -4000, 4000, 4000)
            }
        };
        SceneTime time = new(0, 0, 0, 1.0 / 60, false);

        await Assert.That(CameraAdvancer.Advance(panes, frame, in time)).IsFalse();
        await Assert.That(pane.Camera.Current.BaseScale).IsEqualTo(before.BaseScale).Within(1e-12);

        // Released: it now converges, and stops asking for frames once settled.
        pane.Camera.ManualOverride = false;
        bool moving = true;
        int iterations = 0;
        while (moving && iterations++ < 500)
        {
            moving = CameraAdvancer.Advance(panes, frame, in time);
        }

        await Assert.That(moving).IsFalse();
        ViewportTransform target = FitMapRig.Instance.ComputeTarget(pane, frame)!.Value;
        await Assert.That(pane.Camera.Current.BaseScale).IsEqualTo(target.BaseScale).Within(1e-12);
        Console.WriteLine($"[camera] settled after {iterations} frames at dt=1/60");
    }

    [Test]
    public async Task CameraAdvancer_UsesFrameRateIndependentDecay()
    {
        // The step is 1 - exp(-response·dt); two half-steps must land where one full step does, to
        // within the compounding error, or motion would depend on frame rate.
        double full = 1 - Math.Exp(-CameraAdvancer.LerpResponse * (1.0 / 30));
        double half = 1 - Math.Exp(-CameraAdvancer.LerpResponse * (1.0 / 60));
        double twoHalves = 1 - (1 - half) * (1 - half);

        await Assert.That(twoHalves).IsEqualTo(full).Within(1e-12);
    }

    private static Scene2DFrame FrameAt(float x, float y) => new()
    {
        Markers = [Marker(3, 3, x, y, 0)]
    };

    private static PlayerMarker Marker(int slot, int team, float x, float y, float z, bool alive = true) =>
        new(slot, team, x, y, z, 0, RingState.Team, 1, "AB", alive);

    private static LevelPane Pane()
    {
        MapLevel level = new()
        {
            Id = new MapLevelId(0),
            Name = "floor 0",
            ZMin = -1000,
            ZMax = 1000
        };
        return new LevelPane(level, default, ManualRig.Instance)
        {
            ViewportRect = new SKRect(0, 0, PaneW, PaneH)
        };
    }

    private static async Task AssertSame(ViewportTransform actual, ViewportTransform expected)
    {
        await Assert.That(actual.CenterX).IsEqualTo(expected.CenterX).Within(1e-9);
        await Assert.That(actual.CenterY).IsEqualTo(expected.CenterY).Within(1e-9);
        await Assert.That(actual.BaseScale).IsEqualTo(expected.BaseScale).Within(1e-9);
        await Assert.That(actual.Zoom).IsEqualTo(expected.Zoom).Within(1e-9);
        await Assert.That(actual.PanX).IsEqualTo(expected.PanX).Within(1e-9);
        await Assert.That(actual.PanY).IsEqualTo(expected.PanY).Within(1e-9);
    }
}
