#region

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DemoViewer.NET.Playback2D.Core.Rendering.Interop;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Rendering;

/// <summary>
///     Windowless GPU-backed surfaces over an EGL context: ANGLE/D3D11 on Windows, EGL surfaceless on
///     Linux (plans/C2-gpu-provider.md §6.2).
///     <para>
///         <b>THREAD-AFFINE.</b> An EGL context is current on exactly one thread, so
///         <see cref="CreateSurface" /> and <see cref="Flush" /> must be called on the thread that
///         created the instance; anything else throws <see cref="InvalidOperationException" />
///         immediately. That trade is deliberate: it converts a class of undebuggable driver crashes
///         into one attributable exception, and it costs nothing: an export session already runs on a
///         single background thread. <see cref="Dispose" /> is the one documented exception, and is safe
///         from anywhere; see its remarks for why that asymmetry is right rather than lax.
///     </para>
///     <para>
///         <b>Opportunistic, never required.</b> The CPU provider is the contract baseline (design §10
///         risk 7); this type exists to be faster when it can be, and to get out of the way when it
///         cannot. <see cref="TryCreate" /> reports failure as data and never throws.
///     </para>
/// </summary>
public sealed class GpuSurfaceProvider : IRenderSurfaceProvider
{
    private readonly EglContext _context;
    private readonly GRContext _gr;
    private readonly int _ownerThreadId;
    private bool _disposed;

    private GpuSurfaceProvider(EglContext context, GRContext gr)
    {
        _context = context;
        _gr = gr;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        Backend = Egl.BackendFor(context.Kind);
        RendererName = context.Renderer ?? "unknown";
        VendorName = context.Vendor;
        VersionName = context.Version;
    }

    /// <summary><c>GL_RENDERER</c>, for logs and bug reports. Never null; "unknown" when EGL had none.</summary>
    public string RendererName { get; }

    /// <summary><c>GL_VENDOR</c>, when the driver reported one.</summary>
    public string? VendorName { get; }

    /// <summary><c>GL_VERSION</c>, when the driver reported one.</summary>
    public string? VersionName { get; }

    /// <inheritdoc />
    public RenderBackend Backend { get; }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    ///     On a thread other than the creating one, after disposal, or when Skia declines the surface.
    /// </exception>
    public SKSurface CreateSurface(SKSizeI size)
    {
        EnsureUsable();

        int width = Math.Max(1, size.Width);
        int height = Math.Max(1, size.Height);
        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);

        // budgeted: false. An export holds one surface for a whole run, so Skia's resource budget has
        // nothing useful to say about it. sampleCount 0 keeps this matching the CPU provider: the layers
        // anti-alias their own geometry, and MSAA would be a second, different rasterisation to explain
        // away in the parity diff. TopLeft origin so Snapshot/ReadPixels need no flip.
        SKSurface? surface = SKSurface.Create(_gr, false, info, 0, GRSurfaceOrigin.TopLeft);
        if (surface is null)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"the {Backend} backend could not create a {width}x{height} surface " +
                $"(renderer '{RendererName}')"));
        }

        return surface;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">On a thread other than the creating one.</exception>
    public void Flush(SKSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        EnsureUsable();

        // Order matters and is the thing a wrong readback blames: record the surface's work, hand it to
        // the driver, then block until the driver has actually done it. Confirmed against hardware in
        // C2.11. Until then this is the conservative sequence, not the fast one.
        surface.Flush(true);
        _gr.Flush(true);
        _gr.Submit(true);
    }

    /// <summary>
    ///     Tears the GPU context down. Unlike every other member this is
    ///     <b>
    ///         safe from any thread and
    ///         never throws
    ///     </b>
    ///     , and the asymmetry is deliberate.
    ///     <para>
    ///         A guard here would make the type unusable from ordinary asynchronous code: a
    ///         <c>using</c> whose scope contains an <c>await</c> disposes on whichever thread the
    ///         continuation resumed on, which is exactly what an export session that writes frames to a
    ///         sink does. Throwing would also replace the in-flight exception in a failing <c>using</c>
    ///         block with a less interesting one.
    ///     </para>
    ///     <para>
    ///         It is <i>correct</i> off-thread, not merely quiet: <c>AbandonContext</c> drops Skia's GL
    ///         objects without issuing a single GL call, the only safe thing to do from a thread with no
    ///         current context, and the EGL teardown below destroys the context those objects lived in,
    ///         so nothing leaks. Compare <see cref="CreateSurface" /> and <see cref="Flush" />, where a
    ///         wrong-thread call is a driver crash in waiting and throwing is the right answer.
    ///     </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // GRContext first either way: it holds GL objects that only exist while the EGL context is
        // current, and tearing the context down under it is how a driver crash on exit gets written.
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            _gr.AbandonContext();
        }

        _gr.Dispose();
        _context.Dispose();
    }

    /// <summary>
    ///     Creates a provider on the calling thread, or explains why it could not. Never throws.
    /// </summary>
    /// <param name="provider">The live provider on success. The caller disposes it.</param>
    /// <param name="reason">
    ///     The probe reason on success (<c>angle-d3d11</c>, <c>egl-surfaceless</c>, …), or the failure
    ///     detail: <c>no-egl-library: …</c> or <c>all-backends-failed: …</c>.
    /// </param>
    public static bool TryCreate([NotNullWhen(true)] out GpuSurfaceProvider? provider, out string reason)
    {
        provider = null;

        if (!Egl.TryLoad(out _, out reason))
        {
            return false;
        }

        List<string> failures = [];
        foreach (EglBackendKind kind in Egl.CandidatesFor(HostPlatform()))
        {
            if (!Egl.TryCreateContext(kind, out EglContext? context, out string why))
            {
                failures.Add(why);
                continue;
            }

            if (TryAttachSkia(context, out GpuSurfaceProvider? attached, out string skiaFailure))
            {
                provider = attached;
                reason = Egl.ReasonFor(kind);
                return true;
            }

            context.Dispose();
            failures.Add(skiaFailure);
        }

        reason = failures.Count == 0
            ? "all-backends-failed: no EGL backend is defined for this platform"
            : string.Create(CultureInfo.InvariantCulture,
                $"all-backends-failed: {string.Join("; ", failures)}");
        return false;
    }

    private static bool TryAttachSkia(EglContext context,
        [NotNullWhen(true)] out GpuSurfaceProvider? provider, out string reason)
    {
        provider = null;

        GRGlInterface? glInterface = context.Kind == EglBackendKind.AngleD3D11
            ? GRGlInterface.CreateAngle(context.GetProcAddress)
            : GRGlInterface.CreateGles(context.GetProcAddress);

        if (glInterface is null)
        {
            reason = string.Create(CultureInfo.InvariantCulture,
                $"{Egl.ReasonFor(context.Kind)}: Skia could not assemble a GL interface");
            return false;
        }

        GRContext? gr = GRContext.CreateGl(glInterface);
        if (gr is null)
        {
            glInterface.Dispose();
            reason = string.Create(CultureInfo.InvariantCulture,
                $"{Egl.ReasonFor(context.Kind)}: GRContext.CreateGl returned null");
            return false;
        }

        provider = new GpuSurfaceProvider(context, gr);
        reason = Egl.ReasonFor(context.Kind);
        return true;
    }

    private static ProbeHostPlatform HostPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return ProbeHostPlatform.Windows;
        }

        return OperatingSystem.IsLinux() ? ProbeHostPlatform.Linux : ProbeHostPlatform.Other;
    }

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureOwningThread();
    }

    private void EnsureOwningThread()
    {
        if (Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            return;
        }

        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
            $"{nameof(GpuSurfaceProvider)} is thread-affine: it was created on thread " +
            $"{_ownerThreadId} and was used from thread {Environment.CurrentManagedThreadId}. " +
            $"Create one provider per render thread."));
    }
}
