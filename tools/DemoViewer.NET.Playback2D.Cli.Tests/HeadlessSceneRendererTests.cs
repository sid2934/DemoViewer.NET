#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
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
///     same body without the surface creation. Pinning them together is what stops the CLI from growing
///     a second render path by accident.
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
        using SceneCompositor compositor = SceneLayerCatalog.Create();
        using HeadlessSceneRenderer renderer = new(provider, compositor)
        {
            Camera = fixture.Camera
        };

        SceneTime time = fixture.Time;
        byte[] whole = renderer.RenderPng(fixture.Frame, in time, size, RenderPurpose.Export);

        using SKSurface surface = provider.CreateSurface(size);
        renderer.RenderInto(surface, fixture.Frame, in time, RenderPurpose.Export);
        using SKImage image = surface.Snapshot();
        using MemoryStream stream = new();
        SceneRenderer.WritePng(image, stream);

        await Assert.That(stream.ToArray()).IsEquivalentTo(whole);
    }

    [Test]
    public async Task Catalog_RejectsAnUnknownLayerId()
    {
        await Assert.That(Throws.Capture<ArgumentException>(() => SceneLayerCatalog.Create(["not-a-layer"]).Dispose())).IsNotNull();
    }

    [Test]
    public async Task Catalog_AcceptsBareAndPrefixedSpellings()
    {
        string known = SceneLayerCatalog.KnownLayerIds[0];
        string bare = known[SceneLayerCatalog.IdPrefix.Length..];

        using SceneCompositor prefixed = SceneLayerCatalog.Create([known]);
        using SceneCompositor plain = SceneLayerCatalog.Create([bare]);

        await Assert.That(prefixed.Layers.Count).IsEqualTo(1);
        await Assert.That(plain.Layers.Count).IsEqualTo(1);
        await Assert.That(plain.Layers[0].Id).IsEqualTo(known);
    }

    [Test]
    public async Task Catalog_ExcludeSubtracts()
    {
        using SceneCompositor all = SceneLayerCatalog.Create();
        using SceneCompositor without = SceneLayerCatalog.Create(null, [SceneLayerCatalog.KnownLayerIds[0]]);

        await Assert.That(without.Layers.Count).IsEqualTo(all.Layers.Count - 1);
    }

    [Test]
    public async Task Backend_IsReportedFromTheProvider()
    {
        using CpuSurfaceProvider provider = new();
        using SceneCompositor compositor = SceneLayerCatalog.Create();
        using HeadlessSceneRenderer renderer = new(provider, compositor);

        await Assert.That(renderer.Backend).IsEqualTo(RenderBackend.CpuRaster);
    }
}
