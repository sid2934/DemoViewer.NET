#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using SkiaSharp;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Playback2DTests.Rendering;

/// <summary>
///     Shared plumbing for the backend suites: the skip guard every GPU case opens with, and one render
///     path both providers go through.
///     <para>
///         The render path is deliberately <i>identical</i> to <c>SceneGoldenTests.Render</c> — same
///         compositor, same layer, same context. A parity suite that assembled the scene differently
///         would be comparing two pipelines and calling the difference a backend difference.
///     </para>
/// </summary>
internal static class GpuFixtureRender
{
    /// <summary>
    ///     Skips the calling test when this machine has no GPU backend, naming the probe's reason so a
    ///     skipped run is still a diagnosis. A no-GPU machine reaching this cleanly is itself the test
    ///     that the probe reports failure as data rather than throwing.
    /// </summary>
    public static RenderSurfaceProbe RequireGpu()
    {
        RenderSurfaceProbe probe = RenderSurfaceProviderFactory.Probe();
        if (!probe.GpuAvailable)
        {
            throw new SkipTestException($"No GPU surface backend on this machine: {probe.Reason}");
        }

        return probe;
    }

    /// <summary>Creates a GPU provider on the calling thread, or skips with the failure reason.</summary>
    public static GpuSurfaceProvider CreateProviderOrSkip()
    {
        RequireGpu();
        if (!GpuSurfaceProvider.TryCreate(out GpuSurfaceProvider? provider, out string reason))
        {
            throw new SkipTestException($"the GPU backend probed available but would not create: {reason}");
        }

        return provider;
    }

    /// <summary>Renders one fixture through a provider and returns the encoded PNG.</summary>
    /// <param name="provider">The backend under test.</param>
    /// <param name="fixture">The scene to draw.</param>
    /// <param name="size">Output size in pixels.</param>
    public static byte[] RenderPng(IRenderSurfaceProvider provider, SceneFixture fixture, SKSizeI size)
    {
        SceneRenderer renderer = new(provider);
        using SceneCompositor compositor = new();
        compositor.Add(new DebugGridLayer());

        SceneTime time = fixture.Time;
        SceneRenderContext ctx = TestContexts.For(fixture.Frame, fixture.Camera, size.Width, size.Height);
        using SKImage image = renderer.Render(compositor, fixture.Frame, in time, in ctx, size);

        using MemoryStream stream = new();
        SceneRenderer.WritePng(image, stream);
        return stream.ToArray();
    }

    /// <summary>Writes a failing comparison's three images where CI can upload them.</summary>
    /// <param name="name">A stem identifying the case.</param>
    /// <param name="expectedPng">The CPU (authoritative) image.</param>
    /// <param name="actualPng">The GPU image.</param>
    /// <param name="diffPng">The diff, when the comparer could make one.</param>
    public static string WriteArtifacts(string name, byte[] expectedPng, byte[] actualPng,
        byte[]? diffPng)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "artifacts", "backend-parity");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name + ".cpu.png"), expectedPng);
        File.WriteAllBytes(Path.Combine(directory, name + ".gpu.png"), actualPng);
        if (diffPng is not null)
        {
            File.WriteAllBytes(Path.Combine(directory, name + ".diff.png"), diffPng);
        }

        return directory;
    }
}
