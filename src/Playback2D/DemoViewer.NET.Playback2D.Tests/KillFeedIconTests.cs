#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Hud;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Hud;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The kill feed's icon path: that supplying CS2's artwork actually changes what is drawn, and that
///     supplying none leaves the shipped text feed exactly as it was.
///     <para>
///         That second half is the point of the whole opt-in design. <c>hud.killfeed</c> is in the golden
///         fixture set, so if the icons had been switched on by default every golden containing a kill
///         would have silently re-baselined. The default is asserted here rather than assumed.
///     </para>
/// </summary>
[Category("Render")]
public class KillFeedIconTests
{
    [Test]
    public async Task NoIconSource_KeepsTheTextTokens()
    {
        // Format() is the shipped text form and must not move: the export goldens were baselined on it.
        KillFeedRow row = Row();
        string text = KillFeedLayer.Format(row);

        await Assert.That(text).Contains("awp");
        await Assert.That(text).Contains("HS");
        await Assert.That(text).Contains("WB");
        await Assert.That(text).Contains("NS");
    }

    [Test]
    public async Task AnIconSource_ChangesWhatIsDrawn()
    {
        using SkiaIconSource icons = new();

        int withoutIcons = Ink(null);
        int withIcons = Ink(icons);

        // Both must draw something, and they must not draw the same thing: the icon row replaces the
        // weapon name and the HS/WB/NS tokens with artwork, so the painted area genuinely differs.
        await Assert.That(withoutIcons).IsGreaterThan(0);
        await Assert.That(withIcons).IsGreaterThan(0);
        await Assert.That(withIcons).IsNotEqualTo(withoutIcons);
    }

    [Test]
    public async Task IconSource_ResolvesWeaponsAndModifiers()
    {
        using SkiaIconSource icons = new();

        await Assert.That(icons.Lookup("equipment/awp", 12f)).IsNotNull();
        await Assert.That(icons.Lookup("modifier/headshot", 12f)).IsNotNull();

        // A wide weapon must come back wide: a source that squared its images would squash every rifle.
        SKImage awp = icons.Lookup("equipment/awp", 12f)!;
        await Assert.That((double)awp.Width / awp.Height).IsGreaterThan(3.0);
    }

    [Test]
    public async Task IconSource_UnknownAndEnvironmentKeys_AreNull()
    {
        using SkiaIconSource icons = new();

        await Assert.That(icons.Lookup("equipment/not_a_weapon", 12f)).IsNull();
        await Assert.That(icons.Lookup("equipment/world", 12f)).IsNull();
    }

    [Test]
    public async Task IconSource_CachesByKeyAndScale()
    {
        using SkiaIconSource icons = new();

        // Same key, same bucket → the identical image, because a feed redraws it every frame.
        await Assert.That(icons.Lookup("equipment/ak47", 12f))
                    .IsSameReferenceAs(icons.Lookup("equipment/ak47", 12f));
    }

    private static KillFeedRow Row() =>
        new(1000, "neo", null, "smith", "awp",
            Headshot: true, Penetrated: true, NoScope: true, ThroughSmoke: false,
            AttackerBlind: false, AttackerInAir: false, AssistedFlash: false);

    // Painted pixels for one feed row, with and without artwork.
    private static int Ink(IIconSource? icons)
    {
        HudSnapshot snapshot = new(1000, "13", 7, 5, 34.5, false, false, double.NaN,
            [Row()], []);

        using KillFeedLayer layer = new(new StubHudDataSource(snapshot), text: null, icons: icons);
        using CpuSurfaceProvider surfaces = new();
        using SKSurface surface = surfaces.CreateSurface(new SKSizeI(400, 200));
        surface.Canvas.Clear(ScenePalette.Dark.Background);

        SceneTime time = new(1000, 0, 0, 1 / 60.0, true);
        layer.Advance(in time, Scene2DFrame.Empty);
        layer.Render(surface.Canvas, Context());

        using SKImage image = surface.Snapshot();
        using SKBitmap bitmap = SKBitmap.FromImage(image);

        SKColor background = ScenePalette.Dark.Background;
        int painted = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) != background)
                {
                    painted++;
                }
            }
        }

        return painted;
    }

    private static SceneRenderContext Context() =>
        new(Scene2DFrame.Empty, default, ViewportTransform.Fit(400, 200, -100, -100, 100, 100),
            new SKRect(0, 0, 400, 200), 0, 0, 0,
            RenderPurpose.Export, ScenePalette.Dark, 1f);
}

/// <summary>
///     The icon path's steady-state allocation budget.
///     <para>
///         Design §6 requires 0 B/frame once a scene is warm, and the CI allocation bench cannot see
///         this code: it renders <c>synthetic-tenplayers</c>, which mounts no HUD layer and supplies no
///         icon source. So the feed's icon path is measured here instead. The first version of it
///         allocated an <c>SKPaint</c> and a blend-mode colour filter per icon per frame, plus a fresh
///         <c>equipment/&lt;weapon&gt;</c> string and an iterator per row per frame.
///     </para>
/// </summary>
[Category("Budget")]
public class KillFeedIconAllocationTests
{
    [Test]
    public async Task TheIconPath_AddsNoPerFrameAllocationOverTheTextFeed()
    {
        using SkiaIconSource icons = new();

        long textOnly = PerFrameBytes(null);
        long withIcons = PerFrameBytes(icons);

        Console.WriteLine($"[budget] kill feed: text {textOnly} B/frame, icons {withIcons} B/frame, "
                          + $"delta {withIcons - textOnly}");

        // Measured as a DELTA, not an absolute. Whatever the shipped text feed allocates per frame is a
        // pre-existing property of this layer and of TextBlobCache; what must not regress is the icon
        // path adding to it. An absolute 0 B assertion here would be measuring someone else's code.
        await Assert.That(withIcons - textOnly).IsLessThanOrEqualTo(0L);
    }

    private static long PerFrameBytes(IIconSource? icons)
    {
        HudSnapshot snapshot = new(1000, "13", 7, 5, 34.5, false, false, double.NaN,
        [
            new KillFeedRow(1000, "neo", "trinity", "smith", "awp",
                true, true, true, true, true, true, true),
            new KillFeedRow(1001, "morpheus", null, "agent", "ak47",
                true, false, false, true, false, true, false)
        ], []);

        using KillFeedLayer layer = new(new StubHudDataSource(snapshot), text: null, icons: icons);
        using CpuSurfaceProvider surfaces = new();
        using SKSurface surface = surfaces.CreateSurface(new SKSizeI(400, 200));
        SceneTime time = new(1000, 0, 0, 1 / 60.0, true);
        SceneRenderContext ctx = new(Scene2DFrame.Empty, default,
            ViewportTransform.Fit(400, 200, -100, -100, 100, 100),
            new SKRect(0, 0, 400, 200), 0, 0, 0, RenderPurpose.Export, ScenePalette.Dark, 1f);

        for (int i = 0; i < 120; i++)
        {
            layer.Advance(in time, Scene2DFrame.Empty);
            layer.Render(surface.Canvas, ctx);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int Frames = 240;
        for (int i = 0; i < Frames; i++)
        {
            layer.Advance(in time, Scene2DFrame.Empty);
            layer.Render(surface.Canvas, ctx);
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / Frames;
    }
}
