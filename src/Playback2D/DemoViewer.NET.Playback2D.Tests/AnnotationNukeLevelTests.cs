#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Level-anchored ink against the REAL two-floor Nuke frame, over the full seven-layer stack.
///     <para>
///         The synthetic cases in <c>AnnotationLayerTests</c> prove the filtering RULE; these prove it
///         holds against the level set an actual demo produces: bands derived from the map's networked
///         section heights and the baked bundle, where getting the id wrong would put a callout on the
///         wrong floor of the map it was drawn on.
///     </para>
///     <para>
///         Ink is measured as a DELTA against a render of the same frame with an empty document, inside
///         each pane's real rectangle. The scene already draws red team discs on Nuke's lower floor, so
///         an absolute red count would be measuring the markers as much as the ink.
///     </para>
/// </summary>
public class AnnotationNukeLevelTests
{
    private const uint Red = 0xFFFF0000;
    private const uint Green = 0xFF00FF00;

    private static readonly SKSizeI _size = new(400, 400);

    [Test]
    public async Task NukeTwoFloors_EachStrokeDrawsOnlyOnItsOwnFloor()
    {
        SceneFixture fixture = LoadNuke();

        AnnotationDocument document = new();
        AnnotationSession session = new(document);
        using SceneStage stage = new(_size, extra: new AnnotationLayer(session));
        stage.TryBindMap(fixture.MapName);

        HeadlessSceneRenderer renderer = stage.Renderer;
        SceneTime time = fixture.Time;
        renderer.Advance(fixture.Frame, in time);

        IReadOnlyList<MapLevel> levels = renderer.Levels.Space.Levels;
        if (levels.Count < 2)
        {
            throw new SkipTestException(
                $"nuke-multilevel resolved {levels.Count} level(s); this case needs the two-floor split");
        }

        Console.WriteLine($"[nuke-ink] levels={levels.Count} " +
                          string.Join(" ", levels.Select(l => $"{l.Id.Key}:[{l.ZMin:F0}..{l.ZMax:F0}]")));

        renderer.SetAllCameras(fixture.Camera);
        SKColor[] baseline = Draw(renderer, fixture);

        SKRect lowerBand = BandOf(renderer, levels[0].Id);
        SKRect upperBand = BandOf(renderer, levels[1].Id);

        // One stroke per floor, anchored the way DrawTool anchors: the level's QUANTIZED lower Z.
        WorldBounds bounds = fixture.Frame.Map.ObservedBounds;
        float midX = (float)((bounds.MinX + bounds.MaxX) / 2);
        float midY = (float)((bounds.MinY + bounds.MaxY) / 2);

        document.Apply(new DocDelta.Add(Stroke(levels[0], midX, midY, Red), 0));
        document.Apply(new DocDelta.Add(Stroke(levels[1], midX, midY, Green), 1));

        SKColor[] inked = Draw(renderer, fixture);

        int lowerRed = Added(inked, baseline, lowerBand, IsRed);
        int upperGreen = Added(inked, baseline, upperBand, IsGreen);
        int lowerGreen = Added(inked, baseline, lowerBand, IsGreen);
        int upperRed = Added(inked, baseline, upperBand, IsRed);

        Console.WriteLine($"[nuke-ink] added lower red={lowerRed} green={lowerGreen} | " +
                          $"upper red={upperRed} green={upperGreen}");

        await Assert.That(lowerRed).IsGreaterThan(50);
        await Assert.That(upperGreen).IsGreaterThan(50);
        await Assert.That(lowerGreen).IsEqualTo(0)
            .Because("the upper floor's callout must not bleed onto the lower band");
        await Assert.That(upperRed).IsEqualTo(0);
    }

    /// <summary>
    ///     A rebuild that moves a band: the stroke follows its floor through <c>RemapWorldLevels</c>, the
    ///     layer's per-level pictures are re-keyed with it, and no undo slot is consumed (decision D6).
    /// </summary>
    [Test]
    public async Task NukeTwoFloors_RemapMovesTheStrokeToTheRebuiltLevel()
    {
        SceneFixture fixture = LoadNuke();

        AnnotationDocument document = new();
        AnnotationSession session = new(document);
        AnnotationLayer layer = new(session);
        using SceneStage stage = new(_size, extra: layer);
        stage.TryBindMap(fixture.MapName);

        HeadlessSceneRenderer renderer = stage.Renderer;
        SceneTime time = fixture.Time;
        renderer.Advance(fixture.Frame, in time);

        IReadOnlyList<MapLevel> levels = renderer.Levels.Space.Levels;
        if (levels.Count < 2)
        {
            throw new SkipTestException("nuke-multilevel did not resolve two floors");
        }

        renderer.SetAllCameras(fixture.Camera);
        SKColor[] baseline = Draw(renderer, fixture);

        SKRect lowerBand = BandOf(renderer, levels[0].Id);
        SKRect upperBand = BandOf(renderer, levels[1].Id);

        WorldBounds bounds = fixture.Frame.Map.ObservedBounds;
        float midX = (float)((bounds.MinX + bounds.MaxX) / 2);
        float midY = (float)((bounds.MinY + bounds.MaxY) / 2);

        double lower = MapSpace.QuantizeZ(levels[0].ZMin);
        double upper = MapSpace.QuantizeZ(levels[1].ZMin);
        document.Apply(new DocDelta.Add(Stroke(levels[0], midX, midY, Red), 0));

        SKColor[] beforeRemap = Draw(renderer, fixture);
        await Assert.That(Added(beforeRemap, baseline, lowerBand, IsRed)).IsGreaterThan(50);

        int undoBefore = document.UndoDepth;
        document.RemapWorldLevels(new Dictionary<double, double>
        {
            [lower] = upper
        });
        layer.InvalidateLevels();

        await Assert.That(((SpaceRef.World)document.Elements[0].Space).LevelMinZ).IsEqualTo(upper);
        await Assert.That(document.UndoDepth).IsEqualTo(undoBefore);

        SKColor[] afterRemap = Draw(renderer, fixture);

        int movedTo = Added(afterRemap, baseline, upperBand, IsRed);
        int leftBehind = Added(afterRemap, baseline, lowerBand, IsRed);
        Console.WriteLine($"[nuke-ink] after remap: upper={movedTo} lower={leftBehind}");

        await Assert.That(movedTo).IsGreaterThan(50)
            .Because("after the remap the stroke belongs to the upper band");
        await Assert.That(leftBehind).IsEqualTo(0);
    }

    private static SceneFixture LoadNuke()
    {
        string path = Path.Combine(FixtureCorpus.Root, "scenes", "nuke-multilevel.scene.json");
        if (!File.Exists(path))
        {
            throw new SkipTestException($"no captured scene at {path}");
        }

        return SceneFixture.Load(path);
    }

    private static SKColor[] Draw(HeadlessSceneRenderer renderer, SceneFixture fixture)
    {
        SceneTime time = fixture.Time;
        renderer.Advance(fixture.Frame, in time);
        renderer.Render();

        using SKImage image = SKImage.FromEncodedData(renderer.SnapshotPng());
        using SKBitmap bitmap = SKBitmap.FromImage(image);
        return bitmap.Pixels;
    }

    private static AnnotationElement Stroke(MapLevel level, float x, float y, uint colour) =>
        new(Guid.NewGuid(), AnnotationKind.Freehand,
            new AnnotationStyle(colour, 90f, 1f),
            new SpaceRef.World(MapSpace.QuantizeZ(level.ZMin)),
            TimeEnvelope.Static,
            [
                new InkPoint(x - 300, y, 0.5f), new InkPoint(x, y + 60, 0.5f),
                new InkPoint(x + 300, y, 0.5f)
            ],
            null);

    private static SKRect BandOf(HeadlessSceneRenderer renderer, MapLevelId id) =>
        renderer.Panes.FindById(id)?.ViewportRect
        ?? throw new InvalidOperationException($"no pane arranged for level {id.Key}");

    // Pixels matching the predicate inside a band that were NOT matching in the baseline render.
    private static int Added(SKColor[] inked, SKColor[] baseline, SKRect band, Func<SKColor, bool> match)
    {
        int top = Math.Max(0, (int)Math.Ceiling(band.Top));
        int bottom = Math.Min(_size.Height, (int)Math.Floor(band.Bottom));
        int count = 0;

        for (int y = top; y < bottom; y++)
        {
            for (int x = 0; x < _size.Width; x++)
            {
                int i = y * _size.Width + x;
                if (match(inked[i]) && !match(baseline[i]))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static bool IsRed(SKColor p) => p.Red > 150 && p.Green < 90 && p.Blue < 90;

    private static bool IsGreen(SKColor p) => p.Green > 150 && p.Red < 90 && p.Blue < 90;
}
