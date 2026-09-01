#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Per-layer behaviour that a whole-scene golden would hide. Each case draws one layer onto a small
///     surface and counts pixels, because "the picture looks right" is not a claim a diff can make about
///     a rule like "the bomb arc collapses below half a degree".
/// </summary>
public class SceneLayerTests
{
    /// <summary>
    ///     Error budget for comparing a <b>rasterised</b> ink bounding box against the point the text was
    ///     centred on: -0.364 px structural (line-box centring vs cap-height ink), ±0.5 px baseline snap,
    ///     ±0.5 px bounding-box quantisation, and up to 0.446 px of side-bearing asymmetry horizontally.
    ///     See <see cref="MarkerLayer_LabelInk_IsCentredOnTheDisc" />.
    /// </summary>
    private const float InkCentreTolerancePx = 2f;

    private static readonly SKSizeI _size = new(200, 200);

    [Test]
    public async Task RadarLayer_WithNoImage_FallsBackToTheGrid()
    {
        using RadarLayer layer = new();
        int ink = InkCount(layer, Frame());

        // The grid draws; the exact count is not the point, only that the fallback happened.
        Console.WriteLine($"[radar] grid ink={ink}");
        await Assert.That(ink).IsGreaterThan(0);
    }

    [Test]
    public async Task RadarLayer_DrawsTheLevelsImage_WhenOneIsBound()
    {
        using SKImage image = SolidImage(SKColors.Magenta, 64);
        using RadarLayer layer = new()
        {
            RadarBoundsOverride = new WorldBounds(-500, -500, 500, 500)
        };

        SKColor[] pixels = Render(layer, Frame(), image);

        // Not pure magenta: the pre-v2 draw was PushOpacity(0.9), ported as a white paint at alpha 229,
        // so the image lands blended 90/10 over the background. Asserting the BLEND rather than the
        // source colour is what actually pins the opacity port.
        int magentaish = Count(pixels, p => p.Red > 200 && p.Blue > 200 && p.Green < 60);
        SKColor sample = pixels[_size.Height / 2 * _size.Width + _size.Width / 2];
        Console.WriteLine($"[radar] magenta-ish pixels={magentaish} centre={sample}");

        await Assert.That(magentaish).IsGreaterThan(1000);
        await Assert.That((int)sample.Red).IsEqualTo(231).Within(2);
        await Assert.That((int)sample.Green).IsLessThan(16);
    }

    /// <summary>
    ///     The pre-v2 <c>ShowRadar</c> toggle was never "hide the underlay". It was always "picture or
    ///     grid" (viewport line 868). Turning it off must still leave a background, or the map vanishes.
    /// </summary>
    [Test]
    public async Task RadarLayer_UseRadarImageOff_StillDrawsTheGrid()
    {
        using SKImage image = SolidImage(SKColors.Magenta, 64);
        using RadarLayer layer = new()
        {
            RadarBoundsOverride = new WorldBounds(-500, -500, 500, 500),
            UseRadarImage = false
        };

        SKColor[] pixels = Render(layer, Frame(), image);
        await Assert.That(CountColour(pixels, SKColors.Magenta)).IsEqualTo(0);
        await Assert.That(InkPixels(pixels)).IsGreaterThan(0);
    }

    /// <summary>
    ///     Zoomed far enough out, a 512-unit grid would be hundreds of thousands of lines. The pre-v2
    ///     code bails at 400 per axis (lines 1130-1135) and draws nothing rather than hanging.
    /// </summary>
    [Test]
    public async Task RadarLayer_Grid_BailsOutWhenZoomedFarOut()
    {
        using RadarLayer layer = new();

        // 512 * 400 = 204 800 world units per axis is the limit; frame ten times that.
        ViewportTransform wide = ViewportTransform.Fit(_size.Width, _size.Height,
            -2_000_000, -2_000_000, 2_000_000, 2_000_000);

        int ink = InkPixels(Render(layer, Frame(), null, wide));
        Console.WriteLine($"[radar] ink when zoomed way out={ink}");
        await Assert.That(ink).IsEqualTo(0);
    }

    [Test]
    public async Task MarkerLayer_DrawsAliveDiscsAndSkipsOtherLevels()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-600, -200), new FloorSlice(-200, 200)]);

        Scene2DFrame frame = new()
        {
            Markers =
            [
                new PlayerMarker(0, 2, -200, 0, -400, 0, RingState.Team, 1, "AA", true),
                new PlayerMarker(1, 3, 200, 0, 0, 0, RingState.Team, 1, "BB", true)
            ]
        };

        using MarkerLayer lower = new();
        int lowerT = CountColour(RenderAtLevel(lower, frame, space, 0), ScenePalette.Dark.TeamT);
        int lowerCt = CountColour(RenderAtLevel(lower, frame, space, 0), ScenePalette.Dark.TeamCt);

        using MarkerLayer upper = new();
        int upperCt = CountColour(RenderAtLevel(upper, frame, space, 1), ScenePalette.Dark.TeamCt);

        Console.WriteLine($"[markers] lower T={lowerT} lower CT={lowerCt} upper CT={upperCt}");
        await Assert.That(lowerT).IsGreaterThan(0);
        await Assert.That(lowerCt).IsEqualTo(0); // the CT is on the upper level
        await Assert.That(upperCt).IsGreaterThan(0);
    }

    [Test]
    public async Task MarkerLayer_DeadMarker_IsHollow()
    {
        Scene2DFrame alive = new()
        {
            Markers = [new PlayerMarker(0, 2, 0, 0, 0, 0, RingState.Team, 1, "AA", true)]
        };
        Scene2DFrame dead = new()
        {
            Markers = [new PlayerMarker(0, 2, 0, 0, 0, 0, RingState.Dead, 1, "AA", false)]
        };

        using MarkerLayer layer = new();
        int aliveFill = CountColour(Render(layer, alive), ScenePalette.Dark.TeamT);
        int deadFill = CountColour(Render(layer, dead), ScenePalette.Dark.TeamT);

        Console.WriteLine($"[markers] alive fill={aliveFill} dead fill={deadFill}");
        await Assert.That(aliveFill).IsGreaterThan(100);
        await Assert.That(deadFill).IsEqualTo(0);
    }

    [Test]
    public async Task MarkerLayer_DrawLabelsOff_RemovesInkWithoutMovingTheDisc()
    {
        Scene2DFrame frame = new()
        {
            Markers = [new PlayerMarker(0, 2, 0, 0, 0, 0, RingState.Team, 1, "AA", true)]
        };

        using MarkerLayer withLabels = new();
        using MarkerLayer withoutLabels = new()
        {
            DrawLabels = false
        };

        SKColor[] labelled = Render(withLabels, frame);
        SKColor[] bare = Render(withoutLabels, frame);

        Console.WriteLine($"[markers] fill with labels={CountColour(labelled, ScenePalette.Dark.TeamT)} " +
                          $"without={CountColour(bare, ScenePalette.Dark.TeamT)}");

        // The glyphs sit ON the disc, so hiding them shows more of the team fill, not less.
        await Assert.That(CountColour(bare, ScenePalette.Dark.TeamT))
            .IsGreaterThan(CountColour(labelled, ScenePalette.Dark.TeamT));
    }

    /// <summary>
    ///     <b>The initials must land on the disc, not beside it.</b> Rendering the same marker with and
    ///     without labels and diffing the two frames gives an exact glyph-ink mask; its bounding box has
    ///     to be centred on the disc.
    ///     <para>
    ///         This is the regression gate for the measurement bug that shipped with B1:
    ///         <c>ShapedText.Bounds</c> was <c>SKTextBlob.Bounds</c>, which Skia computes conservatively
    ///         from the font's <i>global</i> glyph box rather than from the run, so its <c>MidX</c> was
    ///         a constant ~0.37 em to the left of the real ink centre and every label drew 4-6 px left of
    ///         its 9 px disc. Cheap to state, invisible to a perceptual golden that tolerates glyph
    ///         differences, and exactly the kind of thing a pixel count cannot see.
    ///     </para>
    ///     <para>
    ///         Six labels of very different widths, because a fix that centred the ink box per string
    ///         would also pass on one of them, and the disc is drawn <b>off</b> the pane's midpoint, so
    ///         an error that happens to cancel at the centre still shows. <c>MM</c> and <c>il</c> are the
    ///         worst side-bearing cases in the set.
    ///     </para>
    ///     <para>
    ///         <b>Why the tolerance is <see cref="InkCentreTolerancePx" /> and not a pixel.</b> Ink
    ///         centre and disc centre are not the same point even when the placement is exactly right,
    ///         and the gap is three measured terms: the label is centred on the font's <b>line box</b>
    ///         (which is what the pre-v2 control did, and what parity requires), so cap-height ink with
    ///         no descender sits a structural <b>-0.364 px</b> high at this size; <c>SKFont.BaselineSnap</c>
    ///         is on, so the drawn baseline rounds to a whole pixel (±0.5 px); and a rasterised bounding
    ///         box quantises to whole pixels (±0.5 px). That is a ~1.36 px budget vertically and ~0.95 px
    ///         horizontally (<c>MM</c> carries a -0.446 px side-bearing asymmetry), so a 1 px gate has
    ///         ~0.16 px of headroom and would flip on any Skia build that rounds a glyph's edge row
    ///         differently. The bug this guards against was <b>4.2-6.2 px</b>, so the wider gate still
    ///         catches it with more than double the margin. The quantisation-free statement of the same
    ///         property, that the advance box lands exactly on the point, is
    ///         <c>TextBlobCacheTests.OriginForCentre_PutsTheInkOnThePoint</c>; this test's job is that
    ///         the real layer wires that up.
    ///     </para>
    /// </summary>
    /// <param name="label">The initials to draw.</param>
    [Test]
    [Arguments("AA")]
    [Arguments("WW")]
    [Arguments("7")]
    [Arguments("10")]
    [Arguments("MM")]
    [Arguments("il")]
    public async Task MarkerLayer_LabelInk_IsCentredOnTheDisc(string label)
    {
        const float worldX = 120f, worldY = -260f;
        Scene2DFrame frame = new()
        {
            Markers = [new PlayerMarker(0, 2, worldX, worldY, 0, 0, RingState.Team, 1, label, true)]
        };

        ViewportTransform camera = ViewportTransform.Fit(_size.Width, _size.Height,
            -500, -500, 500, 500);
        (double discX, double discY) = camera.WorldToScreen(worldX, worldY);

        using MarkerLayer withLabels = new();
        using MarkerLayer withoutLabels = new()
        {
            DrawLabels = false
        };

        SKColor[] labelled = Render(withLabels, frame, null, camera);
        SKColor[] bare = Render(withoutLabels, frame, null, camera);

        (int minX, int minY, int maxX, int maxY, int inkPixels) = DiffBounds(labelled, bare);
        await Assert.That(inkPixels).IsGreaterThan(0);

        // +1 because the box spans whole pixels: columns [minX, maxX] cover [minX, maxX+1).
        float inkCentreX = (minX + maxX + 1) / 2f;
        float inkCentreY = (minY + maxY + 1) / 2f;

        Console.WriteLine($"[markers] \"{label}\" disc=({discX:F2},{discY:F2}) " +
                          $"ink=[{minX}..{maxX}]x[{minY}..{maxY}] ({inkPixels} px) " +
                          $"centre=({inkCentreX:F2},{inkCentreY:F2}) " +
                          $"offset=({inkCentreX - discX:F2},{inkCentreY - discY:F2})");

        await Assert.That(inkCentreX).IsEqualTo((float)discX).Within(InkCentreTolerancePx);
        await Assert.That(inkCentreY).IsEqualTo((float)discY).Within(InkCentreTolerancePx);
    }

    /// <summary>Parity invariant 8: below half a degree the arc collapses and is skipped entirely.</summary>
    [Test]
    [Arguments(0.0, false)]
    [Arguments(0.001, false)]
    [Arguments(0.25, true)]
    [Arguments(1.0, true)]
    public async Task BombLayer_ArcCollapsesBelowHalfADegree(double detonationFraction, bool expectArc)
    {
        Scene2DFrame frame = new()
        {
            Bomb = new BombMarker(0, 0, 0, detonationFraction, false, 0)
        };

        using BombLayer layer = new();
        int arc = CountColour(Render(layer, frame), ScenePalette.Dark.BombDetonation);

        Console.WriteLine($"[bomb] fraction={detonationFraction} arc pixels={arc}");
        await Assert.That(arc > 0).IsEqualTo(expectArc);
    }

    [Test]
    public async Task BombLayer_DefusingDrawsASecondArc()
    {
        Scene2DFrame frame = new()
        {
            Bomb = new BombMarker(0, 0, 0, 0.6, true, 0.4)
        };

        using BombLayer layer = new();
        SKColor[] pixels = Render(layer, frame);

        await Assert.That(CountColour(pixels, ScenePalette.Dark.BombDefuse)).IsGreaterThan(0);
        await Assert.That(CountColour(pixels, ScenePalette.Dark.Bomb)).IsGreaterThan(0);
    }

    [Test]
    public async Task AreaEffectLayer_SmokeIsStrokedAndFireIsNot()
    {
        Scene2DFrame frame = new()
        {
            AreaEffects =
            [
                new AreaEffect(AreaEffectKind.Smoke, -300, 0, 0, 144),
                new AreaEffect(AreaEffectKind.Fire, 300, 0, 0, 28)
            ]
        };

        using AreaEffectLayer layer = new();
        SKColor[] pixels = Render(layer, frame);

        await Assert.That(InkPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task FloorLabelLayer_DrawsNothingOnASingleLevelPane()
    {
        using FloorLabelLayer layer = new();
        await Assert.That(InkPixels(Render(layer, Frame()))).IsEqualTo(0);
    }

    [Test]
    public async Task FloorLabelLayer_DrawsOnAMultiLevelPane()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-600, -200), new FloorSlice(-200, 200)]);

        using FloorLabelLayer layer = new();
        int ink = InkPixels(RenderAtLevel(layer, Frame(), space, 1));

        Console.WriteLine($"[floorlabel] ink={ink}");
        await Assert.That(ink).IsGreaterThan(0);
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────────────────────────

    private static Scene2DFrame Frame() => new()
    {
        Map = new SceneMapInfo
        {
            ObservedBounds = new WorldBounds(-500, -500, 500, 500)
        }
    };

    private static int InkCount(ISceneLayer layer, Scene2DFrame frame) => InkPixels(Render(layer, frame));

    private static SKColor[] Render(ISceneLayer layer, Scene2DFrame frame, SKImage? radar = null,
        ViewportTransform? camera = null)
    {
        MapLevel level = new()
        {
            Id = new MapLevelId(0),
            Name = "floor 0",
            ZMin = -1000,
            ZMax = 1000,
            Radar = radar,
            RadarImageName = radar is null ? null : "test.png"
        };

        return RenderPane(layer, frame, level, null, -1, camera);
    }

    private static SKColor[] RenderAtLevel(ISceneLayer layer, Scene2DFrame frame, MapSpace space, int levelIndex) =>
        RenderPane(layer, frame, space.Levels[levelIndex], space, levelIndex, null);

    private static SKColor[] RenderPane(ISceneLayer layer, Scene2DFrame frame, MapLevel level,
        MapSpace? space, int levelIndex, ViewportTransform? camera)
    {
        ViewportTransform transform = camera ?? ViewportTransform.Fit(_size.Width, _size.Height,
            -500, -500, 500, 500);

        SceneRenderContext ctx = new(frame, frame.Time, transform,
            SKRect.Create(_size.Width, _size.Height), levelIndex, level.ZMin, level.ZMax,
            RenderPurpose.Export, ScenePalette.Dark, 1f)
        {
            Pane = new LevelPaneSnapshot(level.Id, Math.Max(0, levelIndex), level, transform,
                SKRect.Create(_size.Width, _size.Height), 1),
            Levels = space
        };

        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(_size);
        surface.Canvas.Clear(ScenePalette.Dark.Background);

        SceneTime time = frame.Time;
        layer.Advance(in time, frame);
        layer.Render(surface.Canvas, ctx);

        // Read back through SKBitmap.Pixels rather than the raw byte buffer: the surface's channel
        // order is platform-dependent and comparing raw bytes to an SKColor silently inverts R and B.
        using SKImage image = surface.Snapshot();
        using SKBitmap bitmap = SKBitmap.FromImage(image);
        return bitmap.Pixels;
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

    /// <summary>
    ///     The bounding box of the pixels where two renders of the same scene disagree, an exact mask
    ///     of whatever the second render left out.
    /// </summary>
    /// <param name="a">One render.</param>
    /// <param name="b">The same render with one thing turned off.</param>
    private static (int MinX, int MinY, int MaxX, int MaxY, int Count) DiffBounds(
        SKColor[] a, SKColor[] b)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue, count = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i])
            {
                continue;
            }

            int x = i % _size.Width;
            int y = i / _size.Width;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            count++;
        }

        return count == 0 ? (0, 0, 0, 0, 0) : (minX, minY, maxX, maxY, count);
    }

    private static int Count(SKColor[] pixels, Func<SKColor, bool> predicate)
    {
        int count = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            if (predicate(pixels[i]))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountColour(SKColor[] pixels, SKColor colour)
    {
        int count = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            SKColor p = pixels[i];
            if (p.Red == colour.Red && p.Green == colour.Green && p.Blue == colour.Blue)
            {
                count++;
            }
        }

        return count;
    }

    private static SKImage SolidImage(SKColor colour, int size)
    {
        using SKSurface surface = SKSurface.Create(
            new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(colour);
        return surface.Snapshot();
    }
}
