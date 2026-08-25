namespace DemoViewer.NET.Playback2D.Core.Rendering.Interop;

/// <summary>
///     One live, current EGL context and the 1×1 pbuffer it draws nothing into.
///     <para>
///         <b>Thread-affine by nature, not by policy.</b> EGL makes a context current on exactly one
///         thread; this type simply owns the handles. The affinity <i>guard</i> lives one level up in
///         <see cref="GpuSurfaceProvider" />, where the public API is.
///     </para>
/// </summary>
internal sealed class EglContext : IDisposable
{
    private readonly EglBindings _egl;
    private readonly int _ownerThreadId;
    private IntPtr _context;
    private IntPtr _display;
    private IntPtr _surface;

    /// <summary>Wraps handles that are already current on the calling thread.</summary>
    /// <param name="egl">The bound entry points.</param>
    /// <param name="kind">Which display platform produced this context.</param>
    /// <param name="display">The initialized EGL display.</param>
    /// <param name="surface">The 1×1 pbuffer surface.</param>
    /// <param name="context">The current EGL context.</param>
    /// <param name="renderer"><c>GL_RENDERER</c>.</param>
    /// <param name="vendor"><c>GL_VENDOR</c>.</param>
    /// <param name="version"><c>GL_VERSION</c>.</param>
    public EglContext(EglBindings egl, EglBackendKind kind, IntPtr display, IntPtr surface,
        IntPtr context, string? renderer, string? vendor, string? version)
    {
        _egl = egl;
        _display = display;
        _surface = surface;
        _context = context;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        Kind = kind;
        Renderer = renderer;
        Vendor = vendor;
        Version = version;
    }

    /// <summary>Which display platform produced this context.</summary>
    public EglBackendKind Kind { get; }

    /// <summary><c>GL_RENDERER</c> — the string that tells a real GPU from WARP or llvmpipe.</summary>
    public string? Renderer { get; }

    /// <summary><c>GL_VENDOR</c>.</summary>
    public string? Vendor { get; }

    /// <summary><c>GL_VERSION</c>.</summary>
    public string? Version { get; }

    /// <summary>Resolves a GL entry point for Skia's interface assembly.</summary>
    /// <param name="name">The symbol to resolve.</param>
    public IntPtr GetProcAddress(string name) => _egl.GetProcAddress(name);

    /// <summary>
    ///     Releases the context, the pbuffer and the display, in that order. Idempotent; every call is
    ///     guarded so a double dispose after a driver failure cannot compound it.
    ///     <para>
    ///         Safe from any thread. <c>eglDestroyContext</c>, <c>eglDestroySurface</c> and
    ///         <c>eglTerminate</c> are display-scoped rather than thread-scoped, and the spec defines
    ///         destruction of a still-current object as deferred rather than undefined — so the only
    ///         thread-sensitive call here, <c>eglMakeCurrent</c>, is the one that is skipped off-thread.
    ///     </para>
    /// </summary>
    public void Dispose()
    {
        if (_display == IntPtr.Zero)
        {
            return;
        }

        // Un-current first: destroying a current context is legal but defers the teardown, and a
        // deferred teardown is exactly what the 20-cycle reliability gate is trying to catch. Off the
        // owning thread there is nothing current to release — and doing it anyway would clear whatever
        // context THAT thread happens to own.
        if (Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            _egl.MakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }

        if (_context != IntPtr.Zero)
        {
            _egl.DestroyContext(_display, _context);
            _context = IntPtr.Zero;
        }

        if (_surface != IntPtr.Zero)
        {
            _egl.DestroySurface(_display, _surface);
            _surface = IntPtr.Zero;
        }

        _egl.Terminate(_display);
        _display = IntPtr.Zero;
    }
}
