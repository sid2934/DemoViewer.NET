#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The ink layer's wet/dry split, its level filtering and its allocation budget. Rendering is done
///     onto a small CPU surface and counted in pixels — "the stroke is on the right floor" is not a claim
///     a whole-scene diff can make.
/// </summary>
public class AnnotationLayerTests
{
    private static readonly SKSizeI _size = new(200, 200);

    /// <summary>
    ///     Plan risk S3. Every delta bumps <c>Version</c>, so a drag-erase across thirty strokes would
    ///     re-record thirty times if the layer counted mutations instead of comparing the version it
    ///     last recorded.
    /// </summary>
    [Test]
    public async Task Advance_RerecordsDryPicture_OnlyOnVersionChange()
    {
        AnnotationDocument doc = new();
        AnnotationSession session = new(doc);
        using AnnotationLayer layer = new(session);

        doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(), 0));

        Scene2DFrame frame = Scene2DFrame.Empty;
        SceneTime time = default;
        layer.Advance(in time, frame);
        int afterFirst = layer.DryRecordCount;
        await Assert.That(afterFirst).IsGreaterThan(0);

        for (int i = 0; i < 20; i++)
        {
            layer.Advance(in time, frame);
        }

        await Assert.That(layer.DryRecordCount).IsEqualTo(afterFirst);

        using (doc.BeginGesture("erase"))
        {
            for (int i = 0; i < 10; i++)
            {
                doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(), 0));
            }
        }

        layer.Advance(in time, frame);
        await Assert.That(layer.DryRecordCount).IsEqualTo(afterFirst + 1)
            .Because("ten deltas inside one gesture is still one re-record");
    }

    [Test]
    public async Task WorldAnchored_DrawsOnlyInMatchingLevelPane()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-448, -384), new FloorSlice(-384, -128)]);

        AnnotationDocument doc = new();
        AnnotationSession session = new(doc);
        using AnnotationLayer layer = new(session);

        doc.Apply(new DocDelta.Add(
            AnnotationFakes.Stroke(space: new SpaceRef.World(MapSpace.QuantizeZ(-448))), 0));

        int lower = Ink(layer, Scene2DFrame.Empty, space, 0);
        int upper = Ink(layer, Scene2DFrame.Empty, space, 1);

        await Assert.That(lower).IsGreaterThan(0);
        await Assert.That(upper).IsEqualTo(0)
            .Because("a stroke drawn on the lower floor must not ghost onto the upper band");
    }

    /// <summary>
    ///     <b>Design §10 risk 5, at the one seam that actually loses the ink.</b> A floor lost and
    ///     re-found across rebuilds is minted a NEW key, because <c>MapSpace.Mint</c> walks past every key
    ///     it has ever issued — after which <c>level.Id != MapSpace.IdForZMin(level.ZMin)</c>. This layer
    ///     derived the id from the anchor's Z, so the stroke matched no pane at all and simply vanished;
    ///     a neighbour holding the old key would have drawn it on the wrong storey instead.
    ///     <para>
    ///         Both panes are asserted. "Some ink somewhere" passes on the build that puts a lower-floor
    ///         callout on the upper band, which is the worse half of the same defect.
    ///     </para>
    /// </summary>
    [Test]
    public async Task WorldAnchored_SurvivesAFloorBeingLostAndReFound()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-448, -384), new FloorSlice(-384, -128)]);
        space.Rebuild([new FloorSlice(-384, -128)]);           // the lower floor disappears…
        space.Rebuild([new FloorSlice(-448, -384), new FloorSlice(-384, -128)]); // …and comes back

        double anchor = MapSpace.QuantizeZ(space.Levels[0].ZMin);
        Console.WriteLine($"[reminted] pane={space.Levels[0].Id} " +
                          $"mintingRule={MapSpace.IdForZMin(anchor)}");
        await Assert.That(space.Levels[0].Id).IsNotEqualTo(MapSpace.IdForZMin(anchor))
            .Because("the re-found floor carries a re-minted key — that is the condition under test");

        AnnotationDocument doc = new();
        AnnotationSession session = new(doc);
        using AnnotationLayer layer = new(session);

        // Static ∧ World → the cached dry picture; time-anchored → the per-frame prepared path. Both
        // resolved the anchor the same broken way, so both are exercised.
        doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(space: new SpaceRef.World(anchor)), 0));
        doc.Apply(new DocDelta.Add(
            AnnotationFakes.Stroke(space: new SpaceRef.World(anchor), time: new TimeEnvelope(0, 500, 0, 0),
                y: 120), 1));

        int lower = Ink(layer, Scene2DFrame.Empty, space, 0, tick: 100);
        int upper = Ink(layer, Scene2DFrame.Empty, space, 1, tick: 100);

        await Assert.That(lower).IsGreaterThan(0)
            .Because("the pane is drawing the floor the stroke was drawn on, re-minted key or not");
        await Assert.That(upper).IsEqualTo(0);
    }

    [Test]
    public async Task EntityAnchored_HiddenWhileUnresolvable()
    {
        AnnotationDocument doc = new();
        AnnotationSession session = new(doc);
        using AnnotationLayer layer = new(session);

        doc.Apply(new DocDelta.Add(
            AnnotationFakes.Stroke(space: new SpaceRef.Entity(76561198000000042, 0, 0)), 0));

        await Assert.That(Ink(layer, AnnotationFakes.Frame())).IsEqualTo(0)
            .Because("no marker with that SteamId is on this frame");

        await Assert.That(Ink(layer, AnnotationFakes.Frame(
                AnnotationFakes.Marker(76561198000000042, 0, 0, 0, alive: false))))
            .IsEqualTo(0)
            .Because("§5.4: hide while the anchor is dead, never guess a last-known position");

        await Assert.That(Ink(layer, AnnotationFakes.Frame(
                AnnotationFakes.Marker(76561198000000042, 0, 0))))
            .IsGreaterThan(0);
    }

    [Test]
    public async Task EntityAnchored_TracksMarkerAcrossFrames()
    {
        AnnotationDocument doc = new();
        AnnotationSession session = new(doc);
        using AnnotationLayer layer = new(session);

        // Authored at (0,0) with a zero offset, so the stroke's first point sits on the player.
        doc.Apply(new DocDelta.Add(
            AnnotationFakes.Stroke(space: new SpaceRef.Entity(7ul, 0, 0), x: 0, y: 0), 0));

        SKColor[] first = RenderPixels(layer, AnnotationFakes.Frame(
            AnnotationFakes.Marker(7ul, -300, 0)));
        SKColor[] second = RenderPixels(layer, AnnotationFakes.Frame(
            AnnotationFakes.Marker(7ul, 300, 0)));

        int firstLeft = InkOnSide(first, left: true);
        int firstRight = InkOnSide(first, left: false);
        int secondLeft = InkOnSide(second, left: true);
        int secondRight = InkOnSide(second, left: false);

        await Assert.That(firstLeft).IsGreaterThan(firstRight);
        await Assert.That(secondRight).IsGreaterThan(secondLeft)
            .Because("the stroke follows its player across the map, by SteamId");
    }

    [Test]
    public async Task TimeAnchored_FadesWithTheEnvelope()
    {
        AnnotationDocument doc = new();
        AnnotationSession session = new(doc);
        using AnnotationLayer layer = new(session);

        doc.Apply(new DocDelta.Add(
            AnnotationFakes.Stroke(time: new TimeEnvelope(100, 200, 10, 10)), 0));

        await Assert.That(Ink(layer, Scene2DFrame.Empty, tick: 80)).IsEqualTo(0);
        await Assert.That(Ink(layer, Scene2DFrame.Empty, tick: 150)).IsGreaterThan(0);
        await Assert.That(Ink(layer, Scene2DFrame.Empty, tick: 260)).IsEqualTo(0);

        // Mid-ramp is visible but dimmer than the plateau.
        SKColor plateau = Brightest(RenderPixels(layer, Scene2DFrame.Empty, tick: 150));
        SKColor ramp = Brightest(RenderPixels(layer, Scene2DFrame.Empty, tick: 95));
        await Assert.That((int)ramp.Red).IsLessThan(plateau.Red);
    }

    [Test]
    public async Task RevealOnFadeIn_DrawsOnlyTheLeadingFraction()
    {
        AnnotationDocument doc = new();
        AnnotationSession session = new(doc);
        using AnnotationLayer layer = new(session);

        AnnotationStyle style = AnnotationStyle.Default with
        {
            RevealOnFadeIn = true,
            WidthWorld = 24f
        };

        AnnotationElement element = new(Guid.NewGuid(), AnnotationKind.Freehand, style,
            new SpaceRef.World(0), new TimeEnvelope(100, 200, 100, 0), LongLine(), null);
        doc.Apply(new DocDelta.Add(element, 0));

        int quarterWay = Ink(layer, Scene2DFrame.Empty, tick: 25);
        int fullyOpen = Ink(layer, Scene2DFrame.Empty, tick: 150);

        await Assert.That(quarterWay).IsGreaterThan(0);
        await Assert.That(quarterWay).IsLessThan(fullyOpen / 2)
            .Because("a quarter of the way into the ramp only a quarter of the stroke has been drawn");
    }

    [Test]
    public async Task WetStroke_DrawsOnlyInOriginPane()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-448, -384), new FloorSlice(-384, -128)]);

        AnnotationDocument doc = new();
        AnnotationSession session = new(doc);
        using AnnotationLayer layer = new(session);

        session.Wet.Begin(AnnotationStyle.Default, new SpaceRef.World(MapSpace.QuantizeZ(-448)),
            space.Levels[0].Id, new InkPoint(0, 0, 0.5f));
        session.Wet.Append(new InkPoint(120, 40, 0.5f));
        session.Wet.Append(new InkPoint(240, 0, 0.5f));

        await Assert.That(Ink(layer, Scene2DFrame.Empty, space, 0)).IsGreaterThan(0);
        await Assert.That(Ink(layer, Scene2DFrame.Empty, space, 1)).IsEqualTo(0);
    }

    [Test]
    public async Task Advance_ReturnsTrue_OnlyWhileWetStrokeActive()
    {
        AnnotationDocument doc = new();
        AnnotationSession session = new(doc);
        using AnnotationLayer layer = new(session);
        doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(time: new TimeEnvelope(100, 200, 10, 10)), 0));

        Scene2DFrame frame = Scene2DFrame.Empty;
        SceneTime time = default;

        await Assert.That(layer.Advance(in time, frame)).IsFalse()
            .Because("a fade needs no animation loop — a tick change already repaints, and an idle " +
                     "tab that keeps re-arming burns a core in the background");

        session.Wet.Begin(AnnotationStyle.Default, new SpaceRef.World(0), null,
            new InkPoint(0, 0, 0.5f));
        await Assert.That(layer.Advance(in time, frame)).IsTrue();

        session.Wet.Clear();
        await Assert.That(layer.Advance(in time, frame)).IsFalse();
    }

    [Test]
    public async Task InvalidateLevels_DropsTheDryPictures()
    {
        AnnotationDocument doc = new();
        AnnotationSession session = new(doc);
        using AnnotationLayer layer = new(session);
        doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(), 0));

        Scene2DFrame frame = Scene2DFrame.Empty;
        SceneTime time = default;
        layer.Advance(in time, frame);
        await Assert.That(layer.DryPictureCount).IsGreaterThan(0);

        layer.InvalidateLevels();
        await Assert.That(layer.DryPictureCount).IsEqualTo(0);

        layer.Advance(in time, frame);
        await Assert.That(layer.DryPictureCount).IsGreaterThan(0);
    }

    /// <summary>
    ///     §6's budget. 512 Advance+Render frames with no active stroke must allocate nothing — measured
    ///     on the SECOND of two identical windows, for the reason B1 records in its deviation 14.
    /// </summary>
    [Test]
    [Category("Budget")]
    public async Task SteadyState_ZeroAllocations()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-448, -384), new FloorSlice(-384, -128)]);

        AnnotationDocument doc = new();
        AnnotationSession session = new(doc);
        using AnnotationLayer layer = new(session);

        for (int i = 0; i < 12; i++)
        {
            doc.Apply(new DocDelta.Add(
                AnnotationFakes.Stroke(space: new SpaceRef.World(MapSpace.QuantizeZ(-448)),
                    x: i * 20f), i));
        }

        doc.Apply(new DocDelta.Add(
            AnnotationFakes.Stroke(space: new SpaceRef.Entity(7ul, 0, 0)), doc.Elements.Count));
        doc.Apply(new DocDelta.Add(
            AnnotationFakes.Stroke(time: new TimeEnvelope(0, 10_000, 8, 8)), doc.Elements.Count));

        Scene2DFrame frame = AnnotationFakes.Frame(AnnotationFakes.Marker(7ul, 0, 0, -400));
        SceneRenderContext ctx = Context(frame, space, 0, 100);

        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(_size);

        long first = Measure(layer, surface.Canvas, frame, in ctx);
        long second = Measure(layer, surface.Canvas, frame, in ctx);

        Console.WriteLine($"[annotations] alloc window1={first} B window2={second} B");
        await Assert.That(second).IsEqualTo(0);
    }

    private static long Measure(AnnotationLayer layer, SKCanvas canvas, Scene2DFrame frame,
        in SceneRenderContext ctx)
    {
        SceneTime time = ctx.Time;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 512; i++)
        {
            layer.Advance(in time, frame);
            layer.Render(canvas, ctx);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static InkPoint[] LongLine()
    {
        InkPoint[] points = new InkPoint[40];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new InkPoint(-400 + i * 20f, 0, 0.5f);
        }

        return points;
    }

    private static int Ink(AnnotationLayer layer, Scene2DFrame frame, MapSpace? space = null,
        int levelIndex = -1, int tick = 0) =>
        InkPixels(RenderPixels(layer, frame, space, levelIndex, tick));

    private static SKColor[] RenderPixels(AnnotationLayer layer, Scene2DFrame frame,
        MapSpace? space = null, int levelIndex = -1, int tick = 0)
    {
        SceneRenderContext ctx = Context(frame, space, levelIndex, tick);

        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(_size);
        surface.Canvas.Clear(ScenePalette.Dark.Background);

        SceneTime time = ctx.Time;
        layer.Advance(in time, frame);
        layer.Render(surface.Canvas, ctx);

        using SKImage image = surface.Snapshot();
        using SKBitmap bitmap = SKBitmap.FromImage(image);
        return bitmap.Pixels;
    }

    private static SceneRenderContext Context(Scene2DFrame frame, MapSpace? space, int levelIndex,
        int tick)
    {
        MapLevel level = space is not null && levelIndex >= 0
            ? space.Levels[levelIndex]
            : new MapLevel
            {
                Id = new MapLevelId(0),
                Name = "floor 0",
                ZMin = -1000,
                ZMax = 1000
            };

        ViewportTransform transform = ViewportTransform.Fit(_size.Width, _size.Height,
            -500, -500, 500, 500);

        SceneTime time = frame.Time with
        {
            Tick = tick
        };

        return new SceneRenderContext(frame, time, transform, SKRect.Create(_size.Width, _size.Height),
            levelIndex, level.ZMin, level.ZMax, RenderPurpose.Export, ScenePalette.Dark, 1f)
        {
            Pane = new LevelPaneSnapshot(level.Id, Math.Max(0, levelIndex), level, transform,
                SKRect.Create(_size.Width, _size.Height), 1),
            Levels = space
        };
    }

    private static int InkPixels(SKColor[] pixels)
    {
        SKColor background = ScenePalette.Dark.Background;
        int count = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            SKColor p = pixels[i];
            if (p.Red != background.Red || p.Green != background.Green || p.Blue != background.Blue)
            {
                count++;
            }
        }

        return count;
    }

    private static int InkOnSide(SKColor[] pixels, bool left)
    {
        SKColor background = ScenePalette.Dark.Background;
        int count = 0;
        for (int y = 0; y < _size.Height; y++)
        {
            for (int x = 0; x < _size.Width; x++)
            {
                bool onSide = left ? x < _size.Width / 2 : x >= _size.Width / 2;
                if (!onSide)
                {
                    continue;
                }

                SKColor p = pixels[(y * _size.Width) + x];
                if (p.Red != background.Red || p.Green != background.Green || p.Blue != background.Blue)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static SKColor Brightest(SKColor[] pixels)
    {
        SKColor best = default;
        int bestSum = -1;
        for (int i = 0; i < pixels.Length; i++)
        {
            int sum = pixels[i].Red + pixels[i].Green + pixels[i].Blue;
            if (sum > bestSum)
            {
                bestSum = sum;
                best = pixels[i];
            }
        }

        return best;
    }
}
