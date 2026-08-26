#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.TestSupport;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     Regressions found by the C1 independent review. Each case failed before the fix it guards.
/// </summary>
[NotInParallel]
public class ReviewRegressionTests
{
    /// <summary>
    ///     One player, so the frame derives a floor band and therefore a pane.
    ///     <para>
    ///         Since the C1 merge the headless renderer is <b>B1's pane pipeline</b>: levels come from the
    ///         frame's observed player Z (or a bundle's nav floors), and a frame with neither has no
    ///         levels, no panes, and nothing to draw into. A clock probe needs a pane to be rendered in,
    ///         so these cases hand it a marker. The assertion — the context carries the <i>injected</i>
    ///         clock, not the frame's — is unchanged.
    ///     </para>
    /// </summary>
    private static PlayerMarker[] OneMarker =>
        [new PlayerMarker(0, 2, 0, 0, 0, 0, RingState.Team, 0, "p", true)];

    /// <summary>
    ///     <c>RenderInto</c> must render with the clock it was handed, not with whatever clock happens to
    ///     be stamped on the frame.
    ///     <para>
    ///         The two clocks are the same for a <see cref="SceneFixture" />, which is why
    ///         <c>HeadlessSceneRendererTests.Render_MatchesRenderInto</c> could not see this: it passes
    ///         <c>fixture.Time</c> for a frame whose own <c>Time</c> is that same value. They are
    ///         <b>not</b> the same on the demo path — <see cref="TrackerFrameSource.TimeAt" /> derives
    ///         <c>DeltaSeconds</c> from fps/speed and authors <c>IsDiscontinuity</c>, while the frame's
    ///         own <c>Time</c> comes from <c>SceneFrameBuilder</c>. Dropping the injected clock is exactly
    ///         the §5.1 determinism failure this phase exists to prevent, and it would have surfaced as
    ///         "bench and golden disagree" the moment a B1 layer read <c>ctx.Time</c>.
    ///     </para>
    /// </summary>
    [Test]
    public async Task RenderInto_PassesTheInjectedClockToTheRenderContext()
    {
        SceneTime frameClock = new(100, 100, 1.0, 1.0 / 64, false);
        SceneTime injected = new(999, 4242, 66.5, 1.0 / 30, true);

        Scene2DFrame frame = new() { Time = frameClock, Markers = OneMarker };

        using CpuSurfaceProvider provider = new();
        using SceneCompositor compositor = new();
        ClockProbeLayer probe = new();
        compositor.Add(probe);

        using HeadlessSceneRenderer renderer = new(provider, compositor);
        using SKSurface surface = provider.CreateSurface(new SKSizeI(16, 16));

        renderer.RenderInto(surface, frame, in injected, RenderPurpose.Export);

        await Assert.That(probe.AdvancedWith).IsEqualTo(injected);
        await Assert.That(probe.RenderedWith).IsEqualTo(injected);
    }

    /// <summary>
    ///     The whole-image path already honoured the injected clock; pin it so the two cannot drift apart
    ///     again in the other direction.
    /// </summary>
    [Test]
    public async Task Render_PassesTheInjectedClockToTheRenderContext()
    {
        SceneTime injected = new(7, 7, 0.5, 1.0 / 64, true);
        Scene2DFrame frame = new() { Time = new SceneTime(1, 1, 0, 0, false), Markers = OneMarker };

        using CpuSurfaceProvider provider = new();
        using SceneCompositor compositor = new();
        ClockProbeLayer probe = new();
        compositor.Add(probe);

        using HeadlessSceneRenderer renderer = new(provider, compositor);
        using SKImage image = renderer.Render(frame, in injected, new SKSizeI(16, 16), RenderPurpose.Export);

        await Assert.That(probe.RenderedWith).IsEqualTo(injected);
    }

    /// <summary>
    ///     A <see cref="ResolvedBackend" /> must report the backend it resolved even after its provider is
    ///     gone.
    ///     <para>
    ///         <c>GoldenCommand</c> captures the first entry's <c>ResolvedBackend</c>, disposes the plan
    ///         (and with it the provider) at the end of every loop iteration, and then reads
    ///         <c>backend.Backend</c> when it builds the summary payload. That is a use-after-dispose.
    ///         It is inert with <see cref="CpuSurfaceProvider" /> (a constant property and a no-op
    ///         <c>Dispose</c>), but C2's <c>GpuSurfaceProvider</c> owns an EGL context, and the registry
    ///         (§3.7) has <c>BackendResolver</c>'s single construction site handing that provider over
    ///         unchanged.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ResolvedBackend_ReportsItsBackend_AfterTheProviderIsDisposed()
    {
        using DisposeTrackingProvider provider = new();
        ResolvedBackend resolved = new(provider, "cpu", null);

        RenderBackend before = resolved.Backend;
        provider.Dispose();

        await Assert.That(before).IsEqualTo(RenderBackend.CpuRaster);
        await Assert.That(resolved.Backend).IsEqualTo(RenderBackend.CpuRaster);
    }

    /// <summary>
    ///     The tracker → <c>SceneFrameInput</c> adapter runs once per exported frame, so every byte it
    ///     allocates is on the §6 budget. The lambda handed to <c>PawnLookup.ForEachLivePawn</c> captures
    ///     <c>this</c>, and Roslyn caches only a <b>fully non-capturing</b> lambda — so it allocated a
    ///     fresh delegate on every single frame.
    ///     <para>
    ///         <b>Measured on the committed <c>assets/tour</c> demo:</b> 424 bytes/frame before the fix,
    ///         360 after — the delegate was exactly 64 of them. The 360-byte residue is <b>not C1's</b>:
    ///         bisected, it is 72 bytes inside <c>PawnLookup.ForEachLivePawn</c> and ~24 bytes per boxed
    ///         <c>EntityState</c> field read, both in the pinned CS2DemoKit 0.10.0 package and both shared
    ///         with the App's own <c>ModuleContext</c> join. B4's export loop inherits that floor; closing
    ///         it needs a package-side allocation-free read path, not a Pipeline change.
    ///     </para>
    ///     <para>
    ///         The bound sits between the two measurements. <c>[Category("Budget")]</c> for the same
    ///         reason <c>BenchAllocationTests</c> is (plan risk R6): an allocation figure must never be
    ///         able to flap a required CI check.
    ///     </para>
    /// </summary>
    [Test]
    [Category("Budget")]
    [Category("RealDemo")]
    public async Task TrackerSceneSnapshot_Refresh_AllocatesNoPerFrameDelegate()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(Dv2d.RequireDemo());
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        EntityTracker tracker = new();
        int start = frames.Count / 4;
        for (int i = 0; i <= start; i++)
        {
            tracker.AdvanceOneFrame(frames[i]);
        }

        TrackerSceneSnapshot snapshot = new();

        // Warm the pools, the label cache and the JIT before measuring.
        for (int i = 0; i < 64; i++)
        {
            snapshot.Refresh(tracker);
        }

        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);

        const int iterations = 256;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            snapshot.Refresh(tracker);
        }

        long perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;

        // 424 with the per-frame delegate, 360 without; a delegate is 64 bytes.
        await Assert.That(perFrame).IsLessThanOrEqualTo(384);
    }

    /// <summary>Records the clock each phase was given.</summary>
    private sealed class ClockProbeLayer : ISceneLayer
    {
        public SceneTime AdvancedWith { get; private set; }
        public SceneTime RenderedWith { get; private set; }

        public string Id => "playback2d.clockprobe";
        public LayerSlot Slot => LayerSlot.Underlay;
        public int Order => 0;
        public LayerCacheHint Cache => LayerCacheHint.Dynamic;
        public bool IsEnabled { get; set; } = true;
        public int ContentVersion => 0;

        public bool Advance(in SceneTime time, Scene2DFrame frame)
        {
            AdvancedWith = time;
            return false;
        }

        public void Render(SKCanvas canvas, SceneRenderContext ctx) => RenderedWith = ctx.Time;

        public void Dispose()
        {
        }
    }

    /// <summary>A provider that refuses to answer once disposed, the way a GPU provider must.</summary>
    private sealed class DisposeTrackingProvider : IRenderSurfaceProvider
    {
        private bool _disposed;

        public RenderBackend Backend
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return RenderBackend.CpuRaster;
            }
        }

        public SKSurface CreateSurface(SKSizeI size)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return SKSurface.Create(new SKImageInfo(Math.Max(1, size.Width), Math.Max(1, size.Height)));
        }

        public void Flush(SKSurface surface) => ObjectDisposedException.ThrowIf(_disposed, this);

        public void Dispose() => _disposed = true;
    }
}
