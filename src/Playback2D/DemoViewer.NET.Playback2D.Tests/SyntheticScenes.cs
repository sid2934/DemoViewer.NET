#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Hand-authored scenes, for the cases no captured demo covers.
///     <para>
///         <c>full-scene-budget</c> is the benchmark and allocation standard: every layer carrying real
///         work at once: ten players, four grenade trails, twelve area effects, solved vision, a
///         planted bomb, over two levels. It is deliberately synthetic. A budget fixture's job is to
///         make every layer do its worst, not to look like any particular round, and a captured frame
///         that happens to be quiet would let a real regression through.
///     </para>
/// </summary>
internal static class SyntheticScenes
{
    /// <summary>The corpus name of the budget scene.</summary>
    public const string FullSceneBudgetName = "full-scene-budget";

    /// <summary>
    ///     Authoritative floor bands for the budget scene, standing in for a map bundle so the two-level
    ///     layout is deterministic rather than dependent on the Z histogram warming up.
    /// </summary>
    public static IReadOnlyList<FloorSlice> BudgetFloors { get; } =
    [
        new(-700, -100),
        new(-100, 500)
    ];

    /// <summary>
    ///     <c>sin</c> and <c>cos</c> for a fixture that gets <b>committed as text</b> and then compared
    ///     character by character against a regeneration on another machine.
    ///     <para>
    ///         .NET does not promise bit-identical transcendental results across platforms. It forwards
    ///         to the host's libm, and glibc's <c>sinf</c> and the Windows CRT's disagree in the last
    ///         bit. Through <c>MathF</c> that lands directly in the emitted float:
    ///         <c>"y": -1991.9182</c> on Windows against <c>-1991.9183</c> on Linux, which is one float
    ///         ulp at this magnitude and enough to fail an exact comparison. Rounding the result to a
    ///         decimal grid does not fix it. It only moves the coin flip to the grid boundary.
    ///     </para>
    ///     <para>
    ///         Computing in <b>double</b> and narrowing once does. The two platforms' doubles differ by
    ///         at most an ulp of a double (~2⁻⁵²), and the cast to float discards ~28 bits below that, so
    ///         they have to round to the same float unless the exact result sits within 2⁻⁵² of a float
    ///         rounding boundary. Verified rather than assumed: the regenerated fixture is byte-identical
    ///         on Windows and on Ubuntu, and <c>BudgetFixtureCorpusTests</c> is the standing check.
    ///     </para>
    /// </summary>
    /// <param name="radians">The angle.</param>
    private static float Sin(double radians) => (float)Math.Sin(radians);

    /// <inheritdoc cref="Sin" />
    /// <param name="radians">The angle.</param>
    private static float Cos(double radians) => (float)Math.Cos(radians);

    /// <summary>Builds the budget scene.</summary>
    public static SceneFixture FullSceneBudget()
    {
        List<PlayerMarker> markers = new(10);
        for (int i = 0; i < 10; i++)
        {
            int team = i < 5 ? 2 : 3;
            // Spread across both Z bands so the level filter and both panes carry load.
            float z = i % 2 == 0 ? -400f : 100f;
            markers.Add(new PlayerMarker(i, team,
                -1600f + i * 340f, -900f + i % 4 * 420f, z,
                i * 36f,
                (RingState)(i % 5), 1.0 - i * 0.05, Label(i), i % 7 != 0,
                i * 3f - 10f, i % 3 * 0.5f, (ulong)(76561190000000000 + i)));
        }

        List<AreaEffect> effects = new(12);
        for (int i = 0; i < 12; i++)
        {
            bool smoke = i % 3 == 0;
            effects.Add(new AreaEffect(smoke ? AreaEffectKind.Smoke : AreaEffectKind.Fire,
                -1200f + i * 210f, -700f + i % 5 * 300f, i % 2 == 0 ? -400f : 100f,
                smoke ? 144f : 28f));
        }

        List<GrenadeTrail> trails = new(4);
        for (int t = 0; t < 4; t++)
        {
            GrenadeTrail trail = new()
            {
                Kind = (GrenadeKind)(t % 5),
                Alpha = 1.0 - t * 0.2,
                LastTick = 1000
            };

            // 64 points each, arcing ACROSS the Z boundary so the run splitter does real work on both
            // panes rather than emitting one run and stopping.
            for (int p = 0; p < 64; p++)
            {
                float progress = p / 63f;
                trail.Points.Add(new GrenadeTrailPoint(
                    -1500f + t * 400f + progress * 2600f,
                    -800f + Sin(progress * Math.PI) * 900f,
                    -450f + Sin(progress * Math.PI * 2) * 700f));
            }

            trails.Add(trail);
        }

        List<VisionCone> cones = new(5);
        for (int c = 0; c < 5; c++)
        {
            ConePoint[] fan = new ConePoint[26];
            PlayerMarker m = markers[c];
            for (int r = 0; r < fan.Length; r++)
            {
                float degrees = m.YawDegrees - 53f + 106f * r / (fan.Length - 1);
                double radians = degrees * (Math.PI / 180.0);
                float range = 800f + r % 7 * 260f;
                fan[r] = new ConePoint(m.WorldX + Cos(radians) * range,
                    m.WorldY + Sin(radians) * range);
            }

            cones.Add(new VisionCone
            {
                Slot = m.Slot,
                Team = m.Team,
                ApexX = m.WorldX,
                ApexY = m.WorldY,
                ApexZ = m.WorldZ,
                Fan = fan
            });
        }

        List<Sightline> sightlines = new(10);
        for (int i = 0; i < 5; i++)
        {
            PlayerMarker viewer = markers[i];
            PlayerMarker target = markers[9 - i];
            sightlines.Add(new Sightline(viewer.Slot, viewer.Team,
                viewer.WorldX, viewer.WorldY, viewer.WorldZ,
                target.WorldX, target.WorldY, target.WorldZ));
        }

        Scene2DFrame frame = new()
        {
            Time = new SceneTime(12800, 6400, 200.0, 1.0 / 64, false),
            Markers = markers,
            AreaEffects = effects,
            Trails = trails,
            Bomb = new BombMarker(420f, -260f, -400f, 0.62, true, 0.35),
            GameInfo = new SceneGameInfo("Live", "Planted", 7, 6, 24.5, "0:24",
                true, true, "kit", 3.2, "0:03", 4, 3),
            Map = new SceneMapInfo
            {
                MapName = "synthetic_budget",
                NetworkedBounds = new WorldBounds(-2400, -1600, 2400, 1600),
                ObservedBounds = new WorldBounds(-2000, -1200, 2000, 1200),
                SectionHeights = [-450, -100, 400]
            },
            Vision = new SceneVision
            {
                IsAvailable = true,
                Cones = cones,
                Sightlines = sightlines
            },
            FollowSlot = 3
        };

        return new SceneFixture
        {
            Frame = frame,
            Time = frame.Time,
            Camera = ViewportTransform.Fit(1920, 540, -2000, -1200, 2000, 1200),
            Size = new SKSizeI(1920, 1080),
            MapName = "synthetic_budget",
            SourceDemoId = null,
            Notes = "Hand-authored worst-case load for the frame-time and allocation budgets: 10 markers, " +
                    "4 trails of 64 points crossing both Z bands, 12 area effects, 5 solved vision cones " +
                    "and 5 sightlines, a defusing bomb, over 2 levels at 1080p. Not a picture of any real " +
                    "round — a fixture that happens to be quiet would let a regression through."
        };
    }

    private static string Label(int slot) => slot switch
    {
        0 => "AA",
        1 => "BB",
        2 => "CC",
        3 => "DD",
        4 => "EE",
        5 => "FF",
        6 => "GG",
        7 => "HH",
        8 => "II",
        _ => "JJ"
    };
}
