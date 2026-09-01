#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Vision;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     <see cref="VisionLayer" /> has <b>two</b> sources, and the guard is that it reads whichever one
///     has data.
///     <para>
///         The defect this pins: a layer wired into three shipped commands, registered by name, drawing
///         nothing. <c>VisionLayer</c> read an
///         <see cref="IVisionSolver" /> and only that, while <c>SceneVision</c>'s own doc said the layer
///         "only draws the result" of a solve done upstream, so a fixture carrying two solved cones
///         rendered an empty frame, and <c>dv2d golden verify</c> compared that empty frame against a
///         committed picture of the same empty frame, indefinitely.
///     </para>
///     <para>
///         These are <b>Advance</b> assertions rather than pixel ones on purpose. Three goldens now
///         contain cones and would move if this broke, but a golden says "something changed"; these say
///         which source won and what it carried. The allocation half needs no case here: the projection
///         runs on <c>duel-mirage-b</c>, which is exactly the fixture <c>BenchAllocationTests</c> gates
///         at 0 B/frame.
///     </para>
/// </summary>
public class VisionLayerSourceTests
{
    private static readonly SceneTime _time = new(1000, 0, 0, 1.0 / 64, false);

    /// <summary>The fixture shape: cones with world fans, sightlines with resolved endpoints.</summary>
    private static Scene2DFrame PreSolvedFrame() => new()
    {
        Markers =
        [
            new PlayerMarker(0, 2, -2280f, 90f, -170f, 20f, RingState.Team, 1.0, "AL", true),
            new PlayerMarker(6, 3, -1520f, 610f, -170f, 205f, RingState.Team, 1.0, "GA", true)
        ],
        Vision = new SceneVision
        {
            IsAvailable = true,
            Cones =
            [
                new VisionCone
                {
                    Slot = 0,
                    Team = 2,
                    ApexX = -2280f,
                    ApexY = 90f,
                    ApexZ = -106f,
                    Fan = [new ConePoint(-1700f, 540f), new ConePoint(-1560f, 400f)]
                }
            ],
            Sightlines = [new Sightline(0, 2, -2280f, 90f, -106f, -1520f, 610f, -106f)]
        }
    };

    [Test]
    public async Task WithNoSolver_ItDrawsTheFramesPreSolvedVision()
    {
        using VisionLayer layer = new(null);
        layer.Advance(in _time, PreSolvedFrame());

        await Assert.That(layer.Solution.IsAvailable).IsTrue()
            .Because("a frame that carries solved geometry is a source, not an absence");
        await Assert.That(layer.Solution.Cones.Count).IsEqualTo(1);
        await Assert.That(layer.SightlineCount).IsEqualTo(1);

        // The fan is copied verbatim into the flat ray buffer the renderer walks: x,y per point, in
        // order, because the polygon is filled in fan order and a transposition would draw a bow tie.
        ConePolygon cone = layer.Solution.Cones[0];
        float[] rays = cone.RayEndsXY.ToArray();
        await Assert.That(cone.RayCount).IsEqualTo(2);
        await Assert.That(rays.Length).IsEqualTo(4);
        await Assert.That(rays[0]).IsEqualTo(-1700f);
        await Assert.That(rays[1]).IsEqualTo(540f);
        await Assert.That(rays[2]).IsEqualTo(-1560f);
        await Assert.That(rays[3]).IsEqualTo(400f);
        await Assert.That(cone.ApexX).IsEqualTo(-2280f);
    }

    [Test]
    public async Task ItIsAlsoEnabledByDefault_LikeEveryOtherLayerInTheStack()
    {
        // The other half of "registered and dark". VisionLayer was the one layer defaulting to disabled,
        // so even a fed one drew nothing until Scene2DHost, the sole caller of SetEnabled for it,
        // pushed the user's toggle. Nothing in dv2d ever did.
        using VisionLayer layer = new(null);
        await Assert.That(layer.IsEnabled).IsTrue();
    }

    [Test]
    public async Task APreSolvedSightline_CarriesItsEndpoints_RatherThanASecondSlot()
    {
        // SceneVision.Sightline has no target slot at all: whoever solved it resolved both ends. The
        // segment must therefore say "use these coordinates" rather than name a slot the renderer would
        // fail to resolve and silently skip, which is what a -1 with no endpoints would do.
        using VisionLayer layer = new(null);
        layer.Advance(in _time, PreSolvedFrame());

        SightlineSegment line = layer.Solution.Sightlines[0];
        await Assert.That(line.HasWorldEndpoints).IsTrue();
        await Assert.That(line.TargetSlot).IsEqualTo(-1);
        await Assert.That(line.ViewerTeam).IsEqualTo(2).Because("the viewer's team colours the line");
        await Assert.That(line.ViewerX).IsEqualTo(-2280f);
        await Assert.That(line.TargetY).IsEqualTo(610f);
    }

    [Test]
    public async Task ASolverSegment_KeepsTheSlotForm_SoRenderTimeSmoothingStillWins()
    {
        // The other direction, and the reason the endpoints are optional rather than required: a live
        // solver deliberately does NOT resolve endpoints, because the line must meet the SMOOTHED dots
        // and those are not known until Render. The five-argument form must therefore stay "resolve the
        // slots", or every app-side sightline would snap to a raw sample.
        SightlineSegment fromSolver = new(0, 2, -170f, 6, -170f);
        await Assert.That(fromSolver.HasWorldEndpoints).IsFalse();
    }

    [Test]
    public async Task ASolverThatSolved_WinsOverTheFramesPreSolvedVision()
    {
        // Both sources present is a state nothing produces today, the app's frames carry SceneVision.Off,
        // but "draw both" would double every cone the day something does, so the rule is stated and
        // pinned rather than left to whichever branch happens to run last.
        using VisionLayer layer = new(new StubSolver(3));
        layer.Advance(in _time, PreSolvedFrame());

        await Assert.That(layer.Solution.Cones.Count).IsEqualTo(3)
            .Because("the live solver is authoritative when it produced a solution");
    }

    [Test]
    public async Task ASolverWithNoEngine_FallsBackToTheFrame_RatherThanDrawingNothing()
    {
        // VisibilityEngineSolver.Solve clears and returns with IsAvailable false when no engine is loaded
        // for the map, indistinguishable from having no solver at all, which is why the fallback tests
        // the RESULT and not `_solver is null`.
        using VisionLayer layer = new(new StubSolver(0));
        layer.Advance(in _time, PreSolvedFrame());

        await Assert.That(layer.Solution.Cones.Count).IsEqualTo(1);
        await Assert.That(layer.Solution.IsAvailable).IsTrue();
    }

    [Test]
    public async Task AnOffFrame_ClearsTheSolution()
    {
        using VisionLayer layer = new(null);
        layer.Advance(in _time, PreSolvedFrame());
        layer.Advance(in _time, new Scene2DFrame()); // Vision defaults to SceneVision.Off

        await Assert.That(layer.Solution.IsAvailable).IsFalse();
        await Assert.That(layer.Solution.Cones.Count).IsEqualTo(0);
        await Assert.That(layer.SightlineCount).IsEqualTo(0);
    }

    // Enough of an IVisionSolver to say "I produced N cones" or "I produced nothing".
    private sealed class StubSolver : IVisionSolver
    {
        private readonly int _cones;

        public StubSolver(int cones) => _cones = cones;

        public bool IsReady => _cones > 0;

        public void Solve(Scene2DFrame frame, VisionSolution into)
        {
            into.Clear();
            if (_cones == 0)
            {
                return;
            }

            for (int i = 0; i < _cones; i++)
            {
                ConePolygon cone = into.AddCone(i, 2, i * 10f, 0f, 0f, 2);
                cone.RayEndsWritable.Clear();
            }

            into.IsAvailable = true;
        }
    }
}
