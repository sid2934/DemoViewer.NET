#region

using DemoViewer.NET.Playback2D.Core.Rendering;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The contract baseline (design §5.8): every golden is authored on this provider, so its surface
///     format is load-bearing: an RGBA8888 premultiplied surface of exactly the requested size.
/// </summary>
public class CpuSurfaceProviderTests
{
    [Test]
    public async Task CreateSurface_ReturnsRgba8888Premul_OfRequestedSize()
    {
        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(320, 180));

        SKImageInfo info = surface.PeekPixels().Info;
        await Assert.That(provider.Backend).IsEqualTo(RenderBackend.CpuRaster);
        await Assert.That(info.Width).IsEqualTo(320);
        await Assert.That(info.Height).IsEqualTo(180);
        await Assert.That(info.ColorType).IsEqualTo(SKColorType.Rgba8888);
        await Assert.That(info.AlphaType).IsEqualTo(SKAlphaType.Premul);
    }

    [Test]
    public async Task Clear_ThenReadPixels_ReturnsClearColor()
    {
        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(8, 8));

        SKColor expected = new(0x21, 0x43, 0x65);
        surface.Canvas.Clear(expected);
        provider.Flush(surface);

        await Assert.That(surface.PeekPixels().GetPixelColor(4, 4)).IsEqualTo(expected);
    }

    [Test]
    public async Task CreateSurface_DegenerateSize_IsClampedToOnePixel()
    {
        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(0, -4));

        SKImageInfo info = surface.PeekPixels().Info;
        await Assert.That(info.Width).IsEqualTo(1);
        await Assert.That(info.Height).IsEqualTo(1);
    }

    [Test]
    public async Task Flush_IsNoOp_AndDoesNotThrow()
    {
        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(4, 4));
        surface.Canvas.Clear(SKColors.Red);

        provider.Flush(surface);
        provider.Flush(surface);

        await Assert.That(surface.PeekPixels().GetPixelColor(1, 1)).IsEqualTo(SKColors.Red);
    }

    [Test]
    public async Task Dispose_IsIdempotent()
    {
        CpuSurfaceProvider provider = new();
        provider.Dispose();
        provider.Dispose();

        // Disposal owns nothing, so the provider stays usable. The point is that neither call throws.
        using SKSurface surface = provider.CreateSurface(new SKSizeI(2, 2));
        await Assert.That(surface).IsNotNull();
    }
}
