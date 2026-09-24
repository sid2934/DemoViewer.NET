#region

using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Zones;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The outline builder and the layer over the synthetic set: a boundary edge has no same-place
///     area on both sides, a T-junction is not a boundary, labels sit at the area-weighted centroid,
///     the layer draws the same pixels twice, draws one floor per pane, and draws a custom zone in
///     its own style.
/// </summary>
[Category("Render")]
public class ZoneOutlineLayerTests
{
    private const int Width = 320;
    private const int Height = 320;

    private static readonly ViewportTransform _camera = ViewportTransform.Fit(Width, Height, -150, -200, 450, 300);

    [Test]
    public async Task BoundaryEdges_HaveNoSamePlacePartner_AndTJunctionsAreInterior()
    {
        PlaceResolver resolver = new(ZoneFixtures.Build());

        IReadOnlyList<PlaceOutline> upper = resolver.OutlinesFor(ZoneFixtures.Upper);
        await Assert.That(upper.Select(o => o.Name).ToArray()).IsEquivalentTo(Names("Ramp", "Hut", "Outside"));
        await Assert.That(ReferenceEquals(upper, resolver.OutlinesFor(ZoneFixtures.Upper))).IsTrue();

        PlaceOutline ramp = upper.Single(o => o.Name == "Ramp");
        Console.WriteLine("[outline] Ramp: " + string.Join("  ", ramp.Edges.Select(e => $"({e.A.X},{e.A.Y})-({e.B.X},{e.B.Y})")));

        // The seam between areas 1 and 2 (x = 100) and the T-junction along y = 0 (area 7 under both 1
        // and 2) are interior to Ramp and must not be drawn; the seam with Hut (x = 200) must be.
        await Assert.That(ramp.Edges.Any(e => OnVertical(e, 100, 0, 100))).IsFalse();
        await Assert.That(ramp.Edges.Any(e => OnHorizontal(e, 0, 0, 200))).IsFalse();
        await Assert.That(ramp.Edges.Any(e => OnVertical(e, 200, 0, 100))).IsTrue();
        await Assert.That(upper.Single(o => o.Name == "Hut").Edges.Any(e => OnVertical(e, 200, 0, 100))).IsTrue();

        // Area-weighted: areas 1, 2 (10 000 each) and 7 (20 000) put the label at (100, 0).
        await Assert.That(ramp.LabelAt.X).IsEqualTo(100f);
        await Assert.That(ramp.LabelAt.Y).IsEqualTo(0f);
        await Assert.That(upper.Single(o => o.Name == "Outside").LabelAt).IsEqualTo(new Vector2(50, 150));

        // The general property, for every edge of every place on both floors: stepping off the edge
        // to either side lands in that place's areas on at most one side.
        foreach (double floor in new[] { ZoneFixtures.Upper, ZoneFixtures.Lower })
        {
            foreach (PlaceOutline outline in resolver.OutlinesFor(floor))
            {
                ZoneArea[] areas = [.. resolver.Zones.Areas.Where(a => a.PlaceId == outline.PlaceId && a.FloorKey.Equals(floor))];
                foreach ((Vector2 a, Vector2 b) in outline.Edges)
                {
                    Vector2 mid = (a + b) / 2;
                    Vector2 dir = Vector2.Normalize(b - a);
                    Vector2 normal = new(-dir.Y, dir.X);
                    bool left = areas.Any(z => z.ContainsXy(mid.X + normal.X, mid.Y + normal.Y));
                    bool right = areas.Any(z => z.ContainsXy(mid.X - normal.X, mid.Y - normal.Y));
                    await Assert.That(left && right).IsFalse();
                }
            }
        }

        // The lower floor holds one place and the island holds none.
        await Assert.That(resolver.OutlinesFor(ZoneFixtures.Lower).Select(o => o.Name).ToArray())
            .IsEquivalentTo(Names("Tunnels"));
        await Assert.That(resolver.OutlinesFor(4096).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Layer_IsDeterministic_AndDrawsSomething()
    {
        PlaceResolver resolver = new(ZoneFixtures.Build());
        using ZoneOutlineLayer layer = new(resolver);
        SceneRenderContext ctx = TestContexts.For(Scene2DFrame.Empty, _camera, Width, Height);

        byte[] first = Render(layer, ctx);
        byte[] second = Render(layer, ctx);
        await Assert.That(Hash(second)).IsEqualTo(Hash(first));
        await Assert.That(layer.PictureRecordCount).IsEqualTo(2); // one picture per floor, recorded once

        using ZoneOutlineLayer empty = new();
        byte[] blank = Render(empty, ctx);
        await Assert.That(Hash(first)).IsNotEqualTo(Hash(blank));

        layer.DrawLabels = false;
        byte[] noLabels = Render(layer, ctx);
        await Assert.That(Hash(noLabels)).IsNotEqualTo(Hash(first));
        await Assert.That(Hash(noLabels)).IsNotEqualTo(Hash(blank));

        await Assert.That(layer.Id).IsEqualTo(SceneLayerIds.Zones);
        await Assert.That(layer.Slot).IsEqualTo(LayerSlot.Overlay);
        await Assert.That(SceneLayerIds.OptIn.Contains(SceneLayerIds.Zones)).IsTrue();
    }

    [Test]
    public async Task Layer_DrawsOneFloorPerPane_AndEveryFloorOnASinglePane()
    {
        PlaceResolver resolver = new(ZoneFixtures.Build());
        using ZoneOutlineLayer layer = new(resolver)
        {
            DrawLabels = false
        };

        MapSpace space = new();
        space.Rebuild([new FloorSlice(-100000, -528), new FloorSlice(-528, 100000)]);
        SceneRenderContext all = TestContexts.For(Scene2DFrame.Empty, _camera, Width, Height);
        SceneRenderContext lower = PaneFor(all, space, 0);
        SceneRenderContext upper = PaneFor(all, space, 1);

        byte[] everything = Render(layer, all);
        byte[] lowerPng = Render(layer, lower);
        byte[] upperPng = Render(layer, upper);

        await Assert.That(Hash(lowerPng)).IsNotEqualTo(Hash(upperPng));
        await Assert.That(Ink(lowerPng)).IsLessThan(Ink(upperPng));
        await Assert.That(Ink(everything)).IsGreaterThanOrEqualTo(Ink(upperPng));
    }

    [Test]
    public async Task CustomZone_DrawsInADistinctStyle_AndASwapRerecords()
    {
        ZoneSet baked = ZoneFixtures.Build();
        ZoneOverlayDocument overlay = new(1, null, null,
            [new ZoneOverlayZone("E-box", ZoneFixtures.Upper, [200, 0, 300, 0, 300, 100, 200, 100], null, null, false)],
            [], []);
        ZoneSet effective = ZoneOverlayApplier.Apply(baked, overlay, Encoding.UTF8.GetBytes("{}"), "o").Effective;

        using ZoneOutlineLayer layer = new(new PlaceResolver(baked))
        {
            DrawLabels = false
        };
        SceneRenderContext ctx = TestContexts.For(Scene2DFrame.Empty, _camera, Width, Height);

        byte[] bakedPng = Render(layer, ctx);
        int before = layer.ContentVersion;

        layer.Resolver = new PlaceResolver(effective);
        byte[] customPng = Render(layer, ctx);

        await Assert.That(layer.ContentVersion).IsEqualTo(before + 1);
        await Assert.That(Hash(customPng)).IsNotEqualTo(Hash(bakedPng));

        // The custom colour appears where the baked one did not: the T-side hue the layer reserves.
        SKColor custom = ScenePalette.Dark.TeamT;
        await Assert.That(CountColour(customPng, custom)).IsGreaterThan(0);
        await Assert.That(CountColour(bakedPng, custom)).IsEqualTo(0);

        layer.Resolver = layer.Resolver;
        await Assert.That(layer.ContentVersion).IsEqualTo(before + 1);
    }

    private static SceneRenderContext PaneFor(in SceneRenderContext all, MapSpace space, int index)
    {
        MapLevel level = space.Levels[index];
        return all with
        {
            LevelIndex = index,
            LevelMinZ = level.ZMin,
            LevelMaxZ = level.ZMax,
            Levels = space,
            Pane = new LevelPaneSnapshot(level.Id, index, level, _camera, SKRect.Create(Width, Height), 0)
        };
    }

    private static byte[] Render(ZoneOutlineLayer layer, SceneRenderContext ctx)
    {
        using SKSurface surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Black);
        layer.Render(surface.Canvas, ctx);
        using SKImage image = surface.Snapshot();
        using SKPixmap pixels = image.PeekPixels();
        return pixels.GetPixelSpan().ToArray();
    }

    private static int Ink(byte[] rgba)
    {
        int count = 0;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i] != 0 || rgba[i + 1] != 0 || rgba[i + 2] != 0)
            {
                count++;
            }
        }

        return count;
    }

    // Pixels with the custom colour's hue ordering (the T-side orange is red over green over blue; the
    // label grey the baked outline uses is the opposite), at any coverage: an anti-aliased hairline
    // over black keeps the channel ordering at every alpha, so the test asks for that, not a value.
    private static int CountColour(byte[] rgba, SKColor colour)
    {
        bool descending = colour.Red > colour.Green && colour.Green > colour.Blue;
        int count = 0;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            if (r + g + b < 30)
            {
                continue;
            }

            if (descending == (r > g && g > b && r - b > 20))
            {
                count++;
            }
        }

        return count;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool OnVertical((Vector2 A, Vector2 B) e, float x, float y0, float y1) =>
        Math.Abs(e.A.X - x) < 0.01f && Math.Abs(e.B.X - x) < 0.01f &&
        Math.Min(e.A.Y, e.B.Y) >= y0 - 0.01f && Math.Max(e.A.Y, e.B.Y) <= y1 + 0.01f;

    private static bool OnHorizontal((Vector2 A, Vector2 B) e, float y, float x0, float x1) =>
        Math.Abs(e.A.Y - y) < 0.01f && Math.Abs(e.B.Y - y) < 0.01f &&
        Math.Min(e.A.X, e.B.X) >= x0 - 0.01f && Math.Max(e.A.X, e.B.X) <= x1 + 0.01f;

    // Through a call rather than an inline array so the analyzer's constant-array rule stays quiet.
    private static string[] Names(params string[] names) => names;
}
