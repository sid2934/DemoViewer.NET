#region

using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The fixture format's two promises: every field of <see cref="Scene2DFrame" /> survives a round
///     trip, and JSON members this build does not know survive one too.
///     <para>
///         <see cref="RoundTrip_PreservesEveryFrameField" /> walks the frame type by reflection rather
///         than naming its members, so adding a field to <c>Scene2DFrame</c> without serializing it
///         fails here instead of silently dropping out of every fixture and every golden.
///     </para>
/// </summary>
public class SceneFixtureTests
{
    [Test]
    public async Task RoundTrip_PreservesEveryFrameField()
    {
        SceneFixture original = new()
        {
            Frame = SampleFrame(),
            Time = new SceneTime(4096, 64, 64.0, 0.015625, true),
            Camera = ViewportTransform.Fit(800, 600, -1000, -1000, 1000, 1000),
            Size = new SKSizeI(800, 600),
            MapName = "de_nuke",
            MapVersion = "crc-1234",
            SourceDemoId = "unit-test",
            Notes = "round trip"
        };

        SceneFixture restored = RoundTrip(original);

        await Assert.That(restored.SchemaVersion).IsEqualTo(SceneFixture.CurrentSchemaVersion);
        await Assert.That(restored.Time).IsEqualTo(original.Time);
        await Assert.That(restored.Size).IsEqualTo(original.Size);
        await Assert.That(restored.MapName).IsEqualTo("de_nuke");
        await Assert.That(restored.MapVersion).IsEqualTo("crc-1234");
        await Assert.That(restored.SourceDemoId).IsEqualTo("unit-test");
        await Assert.That(restored.Notes).IsEqualTo("round trip");
        await Assert.That(restored.Camera.CenterX).IsEqualTo(original.Camera.CenterX);
        await Assert.That(restored.Camera.BaseScale).IsEqualTo(original.Camera.BaseScale);

        // The reflection walk: every public instance property of Scene2DFrame must differ from the empty
        // frame in the sample AND match after the round trip. The first half is what makes the second
        // half meaningful — a field the sample never populates would round-trip trivially.
        List<string> notExercised = [];
        List<string> notPreserved = [];
        foreach (PropertyInfo property in typeof(Scene2DFrame)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? sample = property.GetValue(original.Frame);
            object? empty = property.GetValue(Scene2DFrame.Empty);
            object? actual = property.GetValue(restored.Frame);

            if (Describe(sample) == Describe(empty))
            {
                notExercised.Add(property.Name);
            }

            if (Describe(sample) != Describe(actual))
            {
                notPreserved.Add($"{property.Name}: expected {Describe(sample)}, got {Describe(actual)}");
            }
        }

        await Assert.That(notExercised).IsEmpty();
        await Assert.That(notPreserved).IsEmpty();
    }

    [Test]
    public async Task Read_UnknownMember_IsPreservedOnWrite()
    {
        const string json = """
                            {
                              "schemaVersion": "playback2d-scene/1",
                              "futureThing": { "nested": [1, 2, 3] },
                              "size": { "width": 100, "height": 50 },
                              "frame": { "followSlot": 7 }
                            }
                            """;

        SceneFixture fixture;
        using (MemoryStream input = new(Encoding.UTF8.GetBytes(json)))
        {
            fixture = SceneFixtureSerializer.Read(input);
        }

        using MemoryStream output = new();
        SceneFixtureSerializer.Write(fixture, output);
        string written = Encoding.UTF8.GetString(output.ToArray());

        using JsonDocument document = JsonDocument.Parse(written);
        await Assert.That(document.RootElement.TryGetProperty("futureThing", out JsonElement future)).IsTrue();
        await Assert.That(future.GetProperty("nested").GetArrayLength()).IsEqualTo(3);
        await Assert.That(fixture.Frame.FollowSlot).IsEqualTo(7);
    }

    [Test]
    public async Task Read_MissingOptionalMembers_UsesDefaults()
    {
        const string json = """{ "frame": { } }""";

        using MemoryStream input = new(Encoding.UTF8.GetBytes(json));
        SceneFixture fixture = SceneFixtureSerializer.Read(input);

        await Assert.That(fixture.SchemaVersion).IsEqualTo(SceneFixture.CurrentSchemaVersion);
        await Assert.That(fixture.Frame.Markers.Count).IsEqualTo(0);
        await Assert.That(fixture.Frame.FollowSlot).IsEqualTo(-1);
        await Assert.That(fixture.Frame.GameInfo).IsEqualTo(SceneGameInfo.Empty);
        await Assert.That(fixture.Frame.Map.MapName).IsEqualTo("");
        await Assert.That(fixture.Frame.Vision.IsAvailable).IsFalse();
        await Assert.That(fixture.Annotations).IsNull();
    }

    [Test]
    public async Task ReadFile_EachCommittedFixture_Parses()
    {
        IReadOnlyList<string> paths = FixtureCorpus.ScenePaths();
        await Assert.That(paths.Count).IsGreaterThanOrEqualTo(3);

        foreach (string path in paths)
        {
            SceneFixture fixture = SceneFixture.Load(path);
            await Assert.That(fixture.SchemaVersion).IsEqualTo(SceneFixture.CurrentSchemaVersion);
            await Assert.That(fixture.Size.Width).IsGreaterThan(0);
            await Assert.That(fixture.Camera.BaseScale).IsGreaterThan(0);
        }
    }

    [Test]
    public async Task SaveAndLoad_RoundTripsThroughAFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pb2d-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "sample.scene.json");
        try
        {
            SceneFixture original = new()
            {
                Frame = SampleFrame(),
                Size = new SKSizeI(320, 180)
            };
            original.Save(path);

            SceneFixture restored = SceneFixture.Load(path);
            await Assert.That(restored.Frame.Markers.Count).IsEqualTo(original.Frame.Markers.Count);
            await Assert.That(restored.Size).IsEqualTo(original.Size);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    private static SceneFixture RoundTrip(SceneFixture fixture)
    {
        using MemoryStream stream = new();
        SceneFixtureSerializer.Write(fixture, stream);
        stream.Position = 0;
        return SceneFixtureSerializer.Read(stream);
    }

    // A frame in which EVERY member differs from Scene2DFrame.Empty — see the reflection walk above.
    private static Scene2DFrame SampleFrame() => new()
    {
        Time = new SceneTime(1234, 56, 19.28, 0.015625, true),
        Markers =
        [
            new PlayerMarker(0, 2, 10.5f, -20.25f, 64f, 91.5f, RingState.Shooting, 0.75, "NE", true,
                -12.5f, 0.4f, 76561197960265728),
            new PlayerMarker(5, 3, -300f, 400f, 128f, 12f, RingState.Dead, 1.0, "KI", false)
        ],
        AreaEffects =
        [
            new AreaEffect(AreaEffectKind.Smoke, 1f, 2f, 3f, 144f),
            new AreaEffect(AreaEffectKind.Fire, -4f, -5f, -6f, 28f)
        ],
        Trails =
        [
            new GrenadeTrail
            {
                Kind = GrenadeKind.Molotov,
                LastTick = 1230,
                Alpha = 0.5,
                Points =
                {
                    new GrenadeTrailPoint(1f, 2f, 3f),
                    new GrenadeTrailPoint(4f, 5f, 6f)
                }
            }
        ],
        Bomb = new BombMarker(7f, 8f, 9f, 0.42, true, 0.6),
        KillFeed =
        [
            new KillFeedRow(1200, "NE", "MO", "KI", "ak47", true, true, false, true, false, true, false)
        ],
        GameInfo = new SceneGameInfo("Live", "Planted", 4, 3, 12.5, "0:13", true, true, "with kit", 3.5,
            "0:04", 2, 1),
        Map = new SceneMapInfo
        {
            MapName = "de_nuke",
            NetworkedBounds = new WorldBounds(-2000, -1500, 2500, 1800),
            ObservedBounds = new WorldBounds(-1000, -900, 1100, 950),
            SectionHeights = [1.81, 51.54, 287.0, 376.0],
            Radars =
            [
                new MapRadarImage
                {
                    Name = "de_nuke_radar.png",
                    Bounds = new WorldBounds(-3000, -3000, 3000, 3000),
                    MinZ = -500,
                    MaxZ = 100
                }
            ]
        },
        Vision = new SceneVision
        {
            IsAvailable = true,
            Cones =
            [
                new VisionCone
                {
                    Slot = 0,
                    Team = 2,
                    ApexX = 1f,
                    ApexY = 2f,
                    ApexZ = 3f,
                    Fan = [new ConePoint(10f, 11f), new ConePoint(12f, 13f)]
                }
            ],
            Sightlines = [new Sightline(0, 2, 1f, 2f, 3f, 4f, 5f, 6f)]
        },
        FollowSlot = 5
    };

    /// <summary>
    ///     The corpus is committed text and <c>.gitattributes</c> pins it to LF, so the writer must emit
    ///     LF on every platform. <c>JsonWriterOptions.NewLine</c> defaults to <c>Environment.NewLine</c>,
    ///     which made every Windows App-suite run rewrite <c>nuke-multilevel.scene.json</c> with CRLF —
    ///     invisible in <c>git status</c> (staging normalises it back), permanent in the working tree.
    /// </summary>
    [Test]
    public async Task Write_UsesLfLineEndings_OnEveryPlatform()
    {
        using MemoryStream stream = new();
        SceneFixtureSerializer.Write(new SceneFixture
        {
            Frame = SampleFrame()
        }, stream);

        string json = Encoding.UTF8.GetString(stream.ToArray());

        await Assert.That(json).Contains("\n").Because("the writer is indented, so it has line endings");
        await Assert.That(json.Contains('\r')).IsFalse()
            .Because("a CRLF fixture is a permanently dirty working tree on Windows");
    }

    // A structural description, because the frame's collections are reference types and the value types
    // inside them are records — comparing the rendered shape is both readable in a failure message and
    // insensitive to which concrete list implementation the serializer chose.
    private static string Describe(object? value) => value switch
    {
        null => "<null>",
        string s => s,
        // The two frame members that are plain classes: without these they would compare equal to the
        // empty frame's by reference-ToString, and the "every member is exercised" half would pass
        // vacuously.
        SceneMapInfo m =>
            $"Map({m.MapName},{m.NetworkedBounds},{m.ObservedBounds}," +
            $"{Describe(m.SectionHeights)},{Describe(m.Radars)})",
        SceneVision v => $"Vision({v.IsAvailable},{Describe(v.Cones)},{Describe(v.Sightlines)})",
        IEnumerable list => "[" + string.Join(", ", Flatten(list)) + "]",
        _ => value.ToString() ?? "<null>"
    };

    private static IEnumerable<string> Flatten(IEnumerable list)
    {
        foreach (object? item in list)
        {
            yield return item switch
            {
                GrenadeTrail t => $"Trail({t.Kind},{t.LastTick},{t.Alpha},{Describe(t.Points)})",
                MapRadarImage r => $"Radar({r.Name},{r.Bounds},{r.MinZ},{r.MaxZ})",
                VisionCone c => $"Cone({c.Slot},{c.Team},{c.ApexX},{c.ApexY},{c.ApexZ},{Describe(c.Fan)})",
                _ => Describe(item)
            };
        }
    }
}
