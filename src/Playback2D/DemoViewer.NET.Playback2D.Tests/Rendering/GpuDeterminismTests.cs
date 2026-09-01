#region

using System.Security.Cryptography;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests.Rendering;

/// <summary>
///     Design §11's determinism requirement, per backend: two runs of the same request must produce
///     byte-identical frames. Without it a golden is a coin flip and an export cannot be reproduced from
///     its request.
///     <para>
///         Byte-identical <i>within</i> a backend, perceptually equal <i>across</i> backends: those are
///         two different promises, and conflating them is how a suite ends up either flaky or blind.
///         <c>SceneRendererTests.Render_Twice_ProducesByteIdenticalPixels</c> holds the CPU half of this;
///         here is the GPU half, plus the same-provider-twice case a fresh context could otherwise hide.
///     </para>
/// </summary>
[Category("Gpu")]
[NotInParallel(ProbeSerialization.Key)]
public class GpuDeterminismTests
{
    [Test]
    public async Task SameFixture_TwiceOnOneProvider_IsByteIdentical()
    {
        GpuFixtureRender.RequireGpu();
        SceneFixture fixture = FixtureCorpus.Load("synthetic-tenplayers");

        using GpuSurfaceProvider provider = GpuFixtureRender.CreateProviderOrSkip();
        byte[] first = GpuFixtureRender.RenderPng(provider, fixture, new SKSizeI(640, 360));
        byte[] second = GpuFixtureRender.RenderPng(provider, fixture, new SKSizeI(640, 360));

        await Assert.That(Hash(second)).IsEqualTo(Hash(first));
    }

    /// <summary>
    ///     And across provider lifetimes: a fresh EGL context and a fresh <c>GRContext</c> must rasterise
    ///     the same scene the same way, or every export would depend on how many exports preceded it.
    /// </summary>
    [Test]
    public async Task SameFixture_OnTwoSuccessiveProviders_IsByteIdentical()
    {
        GpuFixtureRender.RequireGpu();
        SceneFixture fixture = FixtureCorpus.Load("synthetic-utility");

        byte[] first;
        using (GpuSurfaceProvider provider = GpuFixtureRender.CreateProviderOrSkip())
        {
            first = GpuFixtureRender.RenderPng(provider, fixture, new SKSizeI(640, 360));
        }

        byte[] second;
        using (GpuSurfaceProvider provider = GpuFixtureRender.CreateProviderOrSkip())
        {
            second = GpuFixtureRender.RenderPng(provider, fixture, new SKSizeI(640, 360));
        }

        await Assert.That(Hash(second)).IsEqualTo(Hash(first));
    }

    private static string Hash(byte[] png) => Convert.ToHexString(SHA256.HashData(png));
}
