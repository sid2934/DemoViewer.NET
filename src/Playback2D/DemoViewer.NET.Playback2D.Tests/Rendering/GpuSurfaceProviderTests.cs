#region

using DemoViewer.NET.Playback2D.Core.Rendering;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests.Rendering;

/// <summary>
///     The GPU provider's own contract (plans/C2-gpu-provider.md §7.2). Every case skips with the probe's
///     reason on a machine without a backend, and the suite being green in that state is not a gap — it
///     is the design's rule that GPU is opportunistic and never required (§10 risk 7).
///     <para>
///         Serialised on <see cref="ProbeSerialization.Key" />: an EGL context is current on one thread,
///         and TUnit's parallel runner would otherwise hand a second provider to a thread the first one
///         owns.
///     </para>
/// </summary>
[Category("Gpu")]
[NotInParallel(ProbeSerialization.Key)]
public class GpuSurfaceProviderTests
{
    [Test]
    public async Task CreateSurface_ReturnsRgba8888Premul_OfRequestedSize()
    {
        using GpuSurfaceProvider provider = GpuFixtureRender.CreateProviderOrSkip();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(320, 180));

        using SKImage snapshot = surface.Snapshot();
        await Assert.That(provider.Backend).IsNotEqualTo(RenderBackend.CpuRaster);
        await Assert.That(snapshot.Width).IsEqualTo(320);
        await Assert.That(snapshot.Height).IsEqualTo(180);
        await Assert.That(snapshot.ColorType).IsEqualTo(SKColorType.Rgba8888);
        await Assert.That(snapshot.AlphaType).IsEqualTo(SKAlphaType.Premul);
    }

    /// <summary>
    ///     The test that catches a wrong flush/submit order. A GPU surface read back before the driver
    ///     has done the work returns whatever was in the texture, which is usually plausible-looking
    ///     black — so this asserts a specific colour, not "not empty".
    /// </summary>
    [Test]
    public async Task ReadPixels_RoundTripsAKnownFill()
    {
        using GpuSurfaceProvider provider = GpuFixtureRender.CreateProviderOrSkip();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(64, 64));

        surface.Canvas.Clear(SKColors.Red);
        provider.Flush(surface);

        SKImageInfo info = new(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKBitmap bitmap = new(info);
        bool read = surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);

        await Assert.That(read).IsTrue();
        await Assert.That(bitmap.GetPixel(32, 32)).IsEqualTo(SKColors.Red);
    }

    /// <summary>
    ///     Gate G1 as a test: twenty full create → render → read → dispose cycles at 1080p in one
    ///     process. A backend that leaks or crashes on the nineteenth cycle is a backend that fails
    ///     halfway through somebody's export, which is worse than never having worked.
    /// </summary>
    [Test]
    public async Task TwentyCycles_CreateRenderReadDispose_AreStable()
    {
        using GpuSurfaceProvider provider = GpuFixtureRender.CreateProviderOrSkip();
        SKImageInfo info = new(1920, 1080, SKColorType.Rgba8888, SKAlphaType.Premul);

        for (int i = 0; i < 20; i++)
        {
            using SKSurface surface = provider.CreateSurface(new SKSizeI(1920, 1080));
            surface.Canvas.Clear(new SKColor((byte)(i * 8), 0x20, 0x40));
            provider.Flush(surface);

            using SKBitmap bitmap = new(info);
            bool read = surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);

            await Assert.That(read).IsTrue();
            await Assert.That(bitmap.GetPixel(960, 540).Red).IsEqualTo((byte)(i * 8));
        }
    }

    [Test]
    public async Task CreateAfterDispose_Recovers()
    {
        GpuSurfaceProvider first = GpuFixtureRender.CreateProviderOrSkip();
        using (SKSurface surface = first.CreateSurface(new SKSizeI(16, 16)))
        {
            surface.Canvas.Clear(SKColors.Blue);
            first.Flush(surface);
        }

        first.Dispose();

        using GpuSurfaceProvider second = GpuFixtureRender.CreateProviderOrSkip();
        using SKSurface again = second.CreateSurface(new SKSizeI(16, 16));
        again.Canvas.Clear(SKColors.Green);
        second.Flush(again);

        using SKImage snapshot = again.Snapshot();
        await Assert.That(snapshot.Width).IsEqualTo(16);
    }

    [Test]
    public async Task Dispose_IsIdempotent()
    {
        GpuSurfaceProvider provider = GpuFixtureRender.CreateProviderOrSkip();

        provider.Dispose();
        provider.Dispose();

        await Assert.That(Assert.Throws<ObjectDisposedException>(
            () => provider.CreateSurface(new SKSizeI(4, 4)))).IsNotNull();
    }

    /// <summary>
    ///     The §2.7 guard. Without it, a caller that hops threads gets an undebuggable driver crash
    ///     somewhere else entirely; with it, the exception names the two thread ids.
    /// </summary>
    [Test]
    public async Task CrossThreadUse_Throws()
    {
        using GpuSurfaceProvider provider = GpuFixtureRender.CreateProviderOrSkip();

        Exception? caught = await Task.Factory.StartNew(() =>
        {
            try
            {
                using SKSurface surface = provider.CreateSurface(new SKSizeI(8, 8));
                return null;
            }
            catch (InvalidOperationException e)
            {
                return (Exception?)e;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Message).Contains("thread-affine");
    }

    /// <summary>
    ///     The documented asymmetry: <c>Dispose</c> is the one member that must work from anywhere. A
    ///     <c>using</c> scope containing an <c>await</c> disposes on whichever thread the continuation
    ///     resumed on — which is what an export session writing frames to a sink does — so a guard here
    ///     would make the provider unusable from the code it exists to serve.
    /// </summary>
    [Test]
    public async Task Dispose_OffTheOwningThread_DoesNotThrow()
    {
        GpuSurfaceProvider provider = GpuFixtureRender.CreateProviderOrSkip();
        using (SKSurface surface = provider.CreateSurface(new SKSizeI(32, 32)))
        {
            surface.Canvas.Clear(SKColors.Orange);
            provider.Flush(surface);
        }

        Exception? caught = await Task.Factory.StartNew(() =>
        {
            try
            {
                provider.Dispose();
                return null;
            }
            catch (Exception e)
            {
                return (Exception?)e;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        await Assert.That(caught).IsNull();
    }
}
