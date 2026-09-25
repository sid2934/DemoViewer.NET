#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Playback2D.Pipeline.Hud;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.TestSupport;
using SkiaSharp;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Golden images for the Strat Book canvas (step-authoring.md §7): the App-suite capture that projects a
///     strat through <see cref="StratSceneProjection" /> and plays it through <see cref="StratFrameSource" />,
///     the same pieces <see cref="StratExportJob" /> renders a GIF with. Gated by
///     <c>PB2D_GOLDEN_UPDATE=1</c> exactly like <c>Playback2DGoldenCaptureTests</c>: a missing golden
///     otherwise fails the test rather than silently writing one.
///     <para>
///         <b>The strat reader lives in the App, so `dv2d` cannot open a `.dvstrat.json`; it does not need
///         to</b> (design §7). Each entry is still registered in <c>tests/fixtures/playback2d/manifest.json</c>
///         so <c>dv2d fixture list</c> and a reviewer scanning the corpus see it, but <c>pending: true</c>,
///         for the same reason <c>nuke-multilevel</c> is: every entry here names <c>hud.clock</c> in its
///         layer set, and `render`/`golden`/`bench` refuse every <c>hud.*</c> id outright (dv2d.md, "Current
///         limitations": "a fixture carries no clock, scoreboard or kill timeline"). This suite, not
///         <c>dv2d golden verify</c>, is the entry's only gate.
///     </para>
/// </summary>
[NotInParallel]
public class StratGoldenCaptureTests
{
    private const string UpdateEnvVar = "PB2D_GOLDEN_UPDATE";

    // The schema sample's own step ticks (StratSceneProjectionTests): 1:30, 1:16, 1:05 and 1:00 on the
    // 115 s clock the sample declares.
    private const int Smoke = 1600, Molly = 2496, Peek = 3200, All = 3520;

    // Between the molotov step and the peek step, but before the smoke step's 18 s (1152-tick) area
    // effect expires at tick 2752: A is mid-interpolation toward its peek keyframe, B/C/O1 are each held
    // at their one keyframe, the smoke disc is still up, the molotov step's arrow sits at full opacity
    // (its fade-out only begins after its window closes at Molly's neighbour, tick Peek-1) and the peek
    // step's text has not started fading in (that starts 8 ticks before Peek). The literal arithmetic
    // midpoint of Molly and Peek, 2848, shows none of that: the smoke is already gone by then, and
    // fades sit OUTSIDE a step's window (design §3.5), so nothing is ever seen mid-fade at a boundary
    // between two adjacent, non-overlapping windows.
    private const int Mid = 2750;

    private static readonly SKSizeI Square = new(StratExportJob.DefaultWidth, StratExportJob.DefaultHeight);

    private static readonly string[] _layerIds =
    [
        SceneLayerIds.Radar, SceneLayerIds.Trails, SceneLayerIds.AreaEffects, SceneLayerIds.Vision,
        SceneLayerIds.Markers, SceneLayerIds.Bomb, SceneLayerIds.FloorLabel, SceneLayerIds.Annotations,
        SceneLayerIds.HudClock
    ];

    [Test]
    public async Task Step1_TokensAtKeyframes_StrokesFull_OpponentDrawn()
    {
        StratDocument document = Fixture();
        await CaptureAndCompare("strat-mirage-exec-step1", document, StratPath.MainLine(document), Smoke, Square,
            "PENDING for dv2d: this entry names hud.clock, which render/golden/bench refuse outright (a " +
            "fixture carries no clock; dv2d.md, 'Current limitations'), so only StratGoldenCaptureTests can " +
            "verify it. The smoke step's tick: A, B and the opponent O1 at their first keyframes, the " +
            "molotov's arrow not yet visible (it starts at Molly), the smoke step's own arrow at full " +
            "opacity (right at its window's FromTick, so no fade-in remains), hud.clock at 1:30. " +
            "step-authoring.md §7 describes this tick as reading 1:55; that number predates the schema " +
            "sample's atSeconds values, and the sample (schema-v1.sample.dvstrat.json) is what this entry " +
            "actually projects, so the code's 1:30 is what is pinned here.");
    }

    [Test]
    public async Task Mid_LinearInterpolation_HeldSlot_SmokeDisc()
    {
        StratDocument document = Fixture();
        await CaptureAndCompare("strat-mirage-exec-mid", document, StratPath.MainLine(document), Mid, Square,
            "PENDING for dv2d: this entry names hud.clock, which render/golden/bench refuse outright (a " +
            "fixture carries no clock; dv2d.md, 'Current limitations'), so only StratGoldenCaptureTests can " +
            "verify it. Between the molotov and peek steps: A linearly interpolating toward its peek " +
            "keyframe (past the molotov step's 2.5 s hold, about a fifth of the way to the peek position), " +
            "B, C and O1 each held at their one keyframe, the smoke step's area effect still up (it expires " +
            "at tick 2752), the molotov step's added arrow at full opacity and the peek step's text not yet " +
            "fading in. Not the literal midpoint of the two steps' ticks (2848): see the class remarks for " +
            "why that tick shows neither the smoke disc nor A in motion.");
    }

    [Test]
    public async Task Mid_720p_PinsTheExportPresetSize()
    {
        StratDocument document = Fixture();
        await CaptureAndCompare("strat-mirage-exec-mid-720p", document, StratPath.MainLine(document), Mid,
            new SKSizeI(1280, 720),
            "PENDING for dv2d: same reason as strat-mirage-exec-mid (hud.clock). The same tick as " +
            "strat-mirage-exec-mid, at the 720p export preset (Playback2DExportDialogViewModel." +
            "SizePresets) rather than the default GIF square, so a preset size is pinned somewhere in the " +
            "corpus. Named without the '@1280x720' step-authoring.md §7 uses: GoldenCorpusEntry.GoldenPath " +
            "already appends '@{width}x{height}' to the entry name, so that literal name would have written " +
            "'strat-mirage-exec-mid@1280x720@1280x720.png'.");
    }

    [Test]
    public async Task Branch_PathSwitch_InheritedPositions()
    {
        StratDocument document = Fixture();
        Guid branchId = document.Branches[0].Id;
        IReadOnlyList<StratPathStep>? path = StratPath.Through(document, branchId, null);
        if (path is null)
        {
            throw new InvalidOperationException("the sample's branch did not resolve to a path");
        }

        await CaptureAndCompare("strat-mirage-exec-branch", document, path, All, Square,
            "PENDING for dv2d: this entry names hud.clock, which render/golden/bench refuse outright (a " +
            "fixture carries no clock; dv2d.md, 'Current limitations'), so only StratGoldenCaptureTests can " +
            "verify it. The branch's target step (the sample's branch skips the peek step straight to " +
            "'all'): every token holds the position it inherited from the smoke step, since none of A, B, " +
            "C or O1 has a second keyframe on this path. hud.clock at 1:00.");
    }

    /// <summary>
    ///     The schema sample (StratTestData.SchemaSample, the checked-in schema-v1.sample.dvstrat.json), with
    ///     a second arrow added to the molotov step: step-authoring.md §7 asks for "two arrows, one text, one
    ///     smoke landing, one opponent token" projected into the corpus, and the sample as committed carries
    ///     only one arrow (on the smoke step) alongside its one text (on the peek step), its one smoke
    ///     landing and its one opponent token (O1). A fresh instance every call, so this never touches the
    ///     shared fixture other suites pin byte-for-byte.
    /// </summary>
    private static StratDocument Fixture()
    {
        StratDocument document = SchemaSample();
        document.Steps[1].Strokes =
        [
            JsonNode.Parse(
                """{ "kind": "arrow", "space": { "kind": "world", "levelMinZ": -256 }, "points": [[380, -1700], [180, -1900]], "color": "#4FC3F7", "width": 3 }""")!
                .AsObject()
        ];

        // The checked-in sample has no slot that ever visibly moves: A's only other keyframe is on the
        // peek step, two steps after its smoke-step one, and the molotov step between them (this one)
        // owns that gap (TokenTrackBuilder's "the step that shapes the move" rule) with
        // interpolation "hold", so A snaps instead of lerping (which is what StratSceneProjectionTests
        // pins for slot C, correctly, on the checked-in file). Clearing it here, on this test's own copy
        // only, makes A actually interpolate between the molotov and peek steps, which "linear
        // interpolation" in the mid-tick entry's note needs to be true rather than aspirational. C is
        // unaffected: its own track never has a second keyframe on either path this suite plays, so
        // TokenTrack.TrySample always takes the "hold forever" branch for it regardless of this field.
        document.Steps[1].Interpolation = null;
        return document;
    }

    private static async Task CaptureAndCompare(string name, StratDocument document,
        IReadOnlyList<StratPathStep> path, int tick, SKSizeI size, string notes)
    {
        StratSceneProjection projection = StratSceneProjection.Build(document, path);

        AnnotationDocument frozen = new();
        frozen.Reset([.. projection.Elements]);
        AnnotationSession ink = new(frozen);

        StratExportCapture capture = new(projection, new TokenTrackSet(projection.Tracks), ink, document.Map,
            document.Name, WorldBounds.Default);

        using LoadedMapAsset? asset = MapAssetPipeline.TryLoad(document.Map);
        if (asset is null)
        {
            throw new SkipTestException($"no baked bundle for '{document.Map}'; run AssetBaker first");
        }

        StratSceneSpec spec = StratExportJob.BuildSpec(capture, asset, tick, tick, StratExportJob.DefaultFps, 1.0);
        StratFrameSource source = new(spec);
        SceneTime time = source.TimeAt(0);
        Scene2DFrame frame = source.FrameAt(0);

        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack(_layerIds, null, null,
            new StratHudDataSource(spec.RoundSeconds), spec.Ink);
        using CpuSurfaceProvider surfaces = new();
        using HeadlessSceneRenderer renderer = new(surfaces, compositor)
        {
            AutoFitOnFirstMapBounds = true,
            AdvanceCameras = false
        };
        renderer.Levels.SetAuthoritativeFloors(asset.Floors);
        renderer.Levels.RadarBinder = new MapRadarBinder(asset);

        byte[] png = renderer.RenderPng(frame, in time, size);
        ViewportTransform camera = renderer.Panes.Panes.Count > 0
            ? renderer.Panes.Panes[0].Camera.Current
            : default;

        string corpus = CorpusRoot();
        string scenePath = Path.Combine(corpus, "scenes", $"{name}.scene.json");
        string goldenPath = Path.Combine(corpus, "goldens", "cpu",
            $"{name}@{size.Width}x{size.Height}.png");
        string annotationStem = Path.Combine(corpus, "annotations", name);

        bool updating = string.Equals(Environment.GetEnvironmentVariable(UpdateEnvVar), "1",
            StringComparison.Ordinal);

        if (updating)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(scenePath)!);
            new SceneFixture
            {
                Frame = frame,
                Time = time,
                Camera = camera,
                Size = size,
                MapName = document.Map,
                MapVersion = asset.Bundle.MapVersion,
                SourceDemoId = "schema-v1.sample.dvstrat.json, extended (step-authoring.md §7)",
                Notes = notes
            }.Save(scenePath);

            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            await File.WriteAllBytesAsync(goldenPath, png);

            bool saved = await new AnnotationStore(null, static _ => "").SaveAsync(annotationStem,
                new DemoIdentity("", $"{name}.scene.json", 0), projection.Clock, projection.Elements);
            if (!saved)
            {
                throw new InvalidOperationException($"could not write the annotation sidecar for '{name}'");
            }

            GoldenCorpus.Upsert(corpus, new GoldenCorpusEntry(name, scenePath, size, document.Map,
                asset.Bundle.MapVersion, _layerIds, GoldenBudget.Default, true)
            {
                Notes = notes
            });

            Console.WriteLine($"[golden] wrote {scenePath}");
            Console.WriteLine($"[golden] wrote {goldenPath} ({png.Length} bytes)");
            return;
        }

        if (!File.Exists(goldenPath))
        {
            throw new InvalidOperationException(
                $"no golden at {goldenPath}. Regenerate deliberately with " +
                $"{UpdateEnvVar}=1 dotnet run --project src/App/DemoViewer.NET.App.Tests -c Release " +
                "-- --treenode-filter \"/*/*/StratGoldenCaptureTests/*\".");
        }

        byte[] expected = await File.ReadAllBytesAsync(goldenPath);
        int labels = frame.Markers.Count(m => !string.IsNullOrEmpty(m.Label));
        GoldenComparison result = GoldenImageComparer.Compare(expected, png,
            GoldenTolerance.ForLabelledFrame(size.Width, size.Height, labels));
        Console.WriteLine($"[golden] {name} labels={labels} {result.Summary}");

        if (!result.Match)
        {
            string artifacts = Path.Combine(AppContext.BaseDirectory, "artifacts");
            Directory.CreateDirectory(artifacts);
            await File.WriteAllBytesAsync(Path.Combine(artifacts, $"{name}.actual.png"), png);
        }

        await Assert.That(result.FailureReason).IsNull();
        await Assert.That(result.Match).IsTrue();
    }

    /// <summary>The corpus root: <c>tests/fixtures/playback2d</c> beside <c>DemoViewer.NET.slnx</c>.</summary>
    private static string CorpusRoot()
    {
        string repo = DemoTestHelper.FindRepoRoot()
                      ?? throw new SkipTestException("repo root not found from the test output directory");
        return Path.Combine(repo, "tests", "fixtures", "playback2d");
    }
}
