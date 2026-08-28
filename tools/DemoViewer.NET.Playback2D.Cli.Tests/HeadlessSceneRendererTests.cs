#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     The Pipeline facade. The load-bearing assertion is that its two paths agree: the whole-image
///     <c>Render</c> delegates to Core's <c>SceneRenderer</c>, while the split
///     <c>Advance</c>/<c>Render(surface, …)</c> pair the bench command needs is a re-statement of the
///     same body without the surface creation. Pinning them together stops the CLI growing a second
///     render path by accident.
/// </summary>
public class HeadlessSceneRendererTests
{
    [Test]
    public async Task Render_MatchesRenderInto()
    {
        SceneFixture fixture = SceneFixture.Load(
            Path.Combine(Dv2d.CorpusDirectory, "scenes", "duel-mirage-b.scene.json"));
        SKSizeI size = fixture.Size;

        using CpuSurfaceProvider provider = new();
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using HeadlessSceneRenderer renderer = new(provider, compositor)
        {
            Camera = fixture.Camera
        };

        SceneTime time = fixture.Time;
        byte[] whole = renderer.RenderPng(fixture.Frame, in time, size);

        using SKSurface surface = provider.CreateSurface(size);
        renderer.RenderInto(surface, fixture.Frame, in time);
        using SKImage image = surface.Snapshot();
        using MemoryStream stream = new();
        SceneRenderer.WritePng(image, stream);

        await Assert.That(stream.ToArray()).IsEquivalentTo(whole);
    }

    [Test]
    public async Task Catalog_RejectsAnUnknownLayerId()
    {
        await Assert.That(Throws.Capture<ArgumentException>(() => SceneLayerCatalog.CreateSceneStack(["not-a-layer"]).Dispose())).IsNotNull();
    }

    /// <summary>
    ///     A typo in <c>--exclude-layers</c> is exactly as wrong as one in <c>--layers</c>, and silently
    ///     subtracting nothing is the failure mode that hides it. <c>Create</c> checked this side and
    ///     <c>CreateSceneStack</c> did not; the fold kept the stricter half.
    /// </summary>
    [Test]
    public async Task Catalog_RejectsAnUnknownExcludeId()
    {
        await Assert.That(Throws.Capture<ArgumentException>(() => SceneLayerCatalog.CreateSceneStack(null, ["not-a-layer"]).Dispose())).IsNotNull();
    }

    /// <summary>
    ///     Named ids, <b>never <c>KnownLayerIds[0]</c></b>. Deriving the input from the catalog's own
    ///     first entry passes identically whether the catalog holds one layer or eleven, so it cannot
    ///     tell that the stack has shrunk to a debug grid.
    /// </summary>
    [Test]
    public async Task Catalog_AcceptsBareAndPrefixedSpellings()
    {
        using SceneCompositor prefixed = SceneLayerCatalog.CreateSceneStack([SceneLayerIds.Markers]);
        using SceneCompositor plain = SceneLayerCatalog.CreateSceneStack(["markers"]);

        await Assert.That(prefixed.Layers.Count).IsEqualTo(1);
        await Assert.That(plain.Layers.Count).IsEqualTo(1);
        await Assert.That(plain.Layers[0].Id).IsEqualTo(SceneLayerIds.Markers);
    }

    /// <summary>
    ///     A HUD id is spelled <c>hud.clock</c>, not <c>playback2d.hud.clock</c>: it already carries a
    ///     namespace, so <see cref="SceneLayerCatalog.Normalize" /> leaves it alone. Pinned here because
    ///     the un-prefixed spelling is the one a persisted export preset stores.
    /// </summary>
    [Test]
    public async Task Catalog_LeavesAnAlreadyNamespacedIdAlone()
    {
        await Assert.That(SceneLayerCatalog.Normalize("hud.clock")).IsEqualTo(SceneLayerIds.HudClock);
        await Assert.That(SceneLayerCatalog.Normalize("markers")).IsEqualTo(SceneLayerIds.Markers);
    }

    /// <summary>
    ///     The default stack is <b>the scene</b> (the seven non-opt-in ids) and the four opt-in ones are
    ///     absent unless named AND fed. With the debug grid registered the count was 1.
    /// </summary>
    [Test]
    public async Task Catalog_DefaultStackIsTheSevenSceneLayers()
    {
        using SceneCompositor scene = SceneLayerCatalog.CreateSceneStack();

        string[] expected = [.. SceneLayerCatalog.SceneStackIds.Where(id => !SceneLayerIds.OptIn.Contains(id))];
        await Assert.That(scene.Layers.Select(l => l.Id).Order().ToArray())
            .IsEquivalentTo(expected.Order().ToArray());
        await Assert.That(expected.Length).IsEqualTo(7);
    }

    /// <summary>
    ///     An opt-in id that is asked for but has no source registers nothing rather than an empty box.
    ///     The CLI refuses it one layer out (<c>SceneRenderPlan.RequireFeedableOptIns</c>) so a command
    ///     line cannot silently mean less than it says; the compositor's own answer is this.
    /// </summary>
    [Test]
    public async Task Catalog_SkipsAnOptInLayerWithNoSource()
    {
        using SceneCompositor starved = SceneLayerCatalog.CreateSceneStack(
            [SceneLayerIds.Markers, SceneLayerIds.HudClock, SceneLayerIds.Annotations]);

        await Assert.That(starved.Layers.Select(l => l.Id).ToArray())
            .IsEquivalentTo(new[]
            {
                SceneLayerIds.Markers
            });
    }

    [Test]
    public async Task Catalog_ExcludeSubtracts()
    {
        using SceneCompositor all = SceneLayerCatalog.CreateSceneStack();
        using SceneCompositor without = SceneLayerCatalog.CreateSceneStack(null, [SceneLayerIds.Radar]);

        await Assert.That(without.Layers.Count).IsEqualTo(all.Layers.Count - 1);
        await Assert.That(without.Layers.Any(l => l.Id == SceneLayerIds.Radar)).IsFalse();
    }

    [Test]
    public async Task Backend_IsReportedFromTheProvider()
    {
        using CpuSurfaceProvider provider = new();
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using HeadlessSceneRenderer renderer = new(provider, compositor);

        await Assert.That(renderer.Backend).IsEqualTo(RenderBackend.CpuRaster);
    }
}
