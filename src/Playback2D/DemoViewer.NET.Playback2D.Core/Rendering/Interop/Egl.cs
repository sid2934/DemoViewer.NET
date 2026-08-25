#region

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Rendering.Interop;

/// <summary>
///     A hand-bound EGL, ~15 entry points wide (plans/C2-gpu-provider.md §2.3, C2.2).
///     <para>
///         <b>Why by hand.</b> Core references SkiaSharp and nothing else — that constraint is what lets
///         export, the CLI and CI render without a window, and it is enforced by
///         <c>ArchitectureTests</c>. Silk.NET or OpenTK would break it, and would then have to be fought
///         anyway over the non-standard library name: on Windows the EGL we link is Avalonia's merged
///         <c>av_libglesv2.dll</c>, not <c>libEGL.dll</c>.
///     </para>
///     <para>
///         <b>Why not <c>DllImport</c>.</b> A missing DLL must be a clean probe failure reported as data,
///         not a <c>DllNotFoundException</c> thrown from the first call on somebody's export thread.
///         <c>NativeLibrary.TryLoad</c> + <c>TryGetExport</c> gives exactly that: everything here returns
///         false with a reason, and nothing here throws.
///     </para>
/// </summary>
internal static class Egl
{
    /// <summary>
    ///     An absolute path to an EGL library to use instead of the shipped one. When set it is the
    ///     <b>only</b> candidate: silently falling through to the default would turn "test this other
    ///     ANGLE build" into "test the usual one", which is worse than failing.
    /// </summary>
    public const string LibraryOverrideVariable = "DV2D_ANGLE_LIBRARY";

    private const int EglFalse = 0;
    private const int EglNone = 0x3038;
    private const int EglAlphaSize = 0x3021;
    private const int EglBlueSize = 0x3022;
    private const int EglGreenSize = 0x3023;
    private const int EglRedSize = 0x3024;
    private const int EglDepthSize = 0x3025;
    private const int EglStencilSize = 0x3026;
    private const int EglSurfaceType = 0x3033;
    private const int EglPbufferBit = 0x0001;
    private const int EglRenderableType = 0x3040;
    private const int EglOpenGlEs2Bit = 0x0004;
    private const int EglHeight = 0x3056;
    private const int EglWidth = 0x3057;
    private const int EglContextClientVersion = 0x3098;
    private const uint EglOpenGlEsApi = 0x30A0;

    private const uint EglPlatformAngleAngle = 0x3202;
    private const int EglPlatformAngleTypeAngle = 0x3203;
    private const int EglPlatformAngleTypeD3D11Angle = 0x3208;
    private const uint EglPlatformSurfacelessMesa = 0x31DD;

    private const uint GlVendor = 0x1F00;
    private const uint GlRenderer = 0x1F01;
    private const uint GlVersion = 0x1F02;

    private static readonly Lock _gate = new();
    private static EglBindings? _bindings;
    private static string? _loadFailure;

    /// <summary>
    ///     The backends to try, in order, for a host platform. First success wins, and the order is the
    ///     design's: ANGLE on Windows; surfaceless first on Linux, because surfaceless is what makes a
    ///     container work without an X server or a DRM node.
    /// </summary>
    /// <param name="platform">The host platform.</param>
    public static IReadOnlyList<EglBackendKind> CandidatesFor(ProbeHostPlatform platform) =>
        platform switch
        {
            ProbeHostPlatform.Windows => [EglBackendKind.AngleD3D11],
            ProbeHostPlatform.Linux => [EglBackendKind.SurfacelessMesa, EglBackendKind.DefaultDisplay],
            _ => []
        };

    /// <summary>
    ///     Loads the EGL library once per process, caching success and failure alike. Never throws.
    /// </summary>
    /// <param name="bindings">The bound entry points, when the load succeeded.</param>
    /// <param name="reason">
    ///     Why it failed, prefixed <c>no-egl-library</c> so the string a user pastes into a bug report
    ///     already says which half of the stack is missing.
    /// </param>
    public static bool TryLoad([NotNullWhen(true)] out EglBindings? bindings, out string reason)
    {
        lock (_gate)
        {
            if (_bindings is not null)
            {
                bindings = _bindings;
                reason = "loaded";
                return true;
            }

            if (_loadFailure is not null)
            {
                bindings = null;
                reason = _loadFailure;
                return false;
            }

            if (EglBindings.TryLoad(out EglBindings? loaded, out string failure))
            {
                _bindings = loaded;
                bindings = loaded;
                reason = "loaded";
                return true;
            }

            _loadFailure = failure;
            bindings = null;
            reason = failure;
            return false;
        }
    }

    /// <summary>
    ///     Stands up an EGL display + context of one kind and makes it current on the calling thread.
    ///     Returns failure as data; every EGL error is folded into <paramref name="reason" />.
    /// </summary>
    /// <param name="kind">Which display platform to ask for.</param>
    /// <param name="context">The live context, when this returns true. The caller disposes it.</param>
    /// <param name="reason">The probe reason on success, or the failure detail.</param>
    public static bool TryCreateContext(EglBackendKind kind, [NotNullWhen(true)] out EglContext? context,
        out string reason)
    {
        context = null;

        if (!TryLoad(out EglBindings? egl, out reason))
        {
            return false;
        }

        IntPtr display = IntPtr.Zero;
        IntPtr surface = IntPtr.Zero;
        IntPtr handle = IntPtr.Zero;
        bool handedOver = false;

        try
        {
            if (!TryGetDisplay(egl, kind, out display, out reason))
            {
                return false;
            }

            if (egl.Initialize(display, out _, out _) == EglFalse)
            {
                reason = Detail(kind, egl, "eglInitialize failed");
                return false;
            }

            if (egl.BindApi(EglOpenGlEsApi) == EglFalse)
            {
                reason = Detail(kind, egl, "eglBindAPI(EGL_OPENGL_ES_API) failed");
                return false;
            }

            int[] configAttributes =
            [
                EglSurfaceType, EglPbufferBit,
                EglRenderableType, EglOpenGlEs2Bit,
                EglRedSize, 8, EglGreenSize, 8, EglBlueSize, 8, EglAlphaSize, 8,
                EglDepthSize, 0, EglStencilSize, 8,
                EglNone
            ];
            IntPtr[] configs = new IntPtr[1];
            if (egl.ChooseConfig(display, configAttributes, configs, 1, out int configCount) ==
                EglFalse || configCount < 1)
            {
                reason = Detail(kind, egl, "eglChooseConfig found no RGBA8888 pbuffer config");
                return false;
            }

            IntPtr config = configs[0];

            // A 1x1 pbuffer, never surfaceless, even where surfaceless contexts exist: Skia renders into
            // its own FBO-backed surfaces regardless, so this is a formality that costs one pixel and
            // buys compatibility with drivers that refuse EGL_NO_SURFACE (plan §2.9).
            int[] surfaceAttributes = [EglWidth, 1, EglHeight, 1, EglNone];
            surface = egl.CreatePbufferSurface(display, config, surfaceAttributes);
            if (surface == IntPtr.Zero)
            {
                reason = Detail(kind, egl, "eglCreatePbufferSurface(1x1) failed");
                return false;
            }

            // ES3 first, ES2 as the floor: Skia's GLES backend is happy with either, and asking for 3
            // where it exists avoids a needlessly reduced feature set.
            handle = CreateContext(egl, display, config, 3);
            if (handle == IntPtr.Zero)
            {
                handle = CreateContext(egl, display, config, 2);
            }

            if (handle == IntPtr.Zero)
            {
                reason = Detail(kind, egl, "eglCreateContext failed for both ES3 and ES2");
                return false;
            }

            if (egl.MakeCurrent(display, surface, surface, handle) == EglFalse)
            {
                reason = Detail(kind, egl, "eglMakeCurrent failed");
                return false;
            }

            context = new EglContext(egl, kind, display, surface, handle,
                egl.GetString(GlRenderer), egl.GetString(GlVendor), egl.GetString(GlVersion));
            reason = ReasonFor(kind);
            handedOver = true;
            return true;
        }
        finally
        {
            // Success hands every handle to the EglContext; anything else must not leak a display.
            if (!handedOver)
            {
                if (handle != IntPtr.Zero)
                {
                    egl.DestroyContext(display, handle);
                }

                if (surface != IntPtr.Zero)
                {
                    egl.DestroySurface(display, surface);
                }

                if (display != IntPtr.Zero)
                {
                    egl.Terminate(display);
                }
            }
        }
    }

    /// <summary>The probe reason string a successful backend reports.</summary>
    /// <param name="kind">The backend that succeeded.</param>
    public static string ReasonFor(EglBackendKind kind) => kind switch
    {
        EglBackendKind.AngleD3D11 => "angle-d3d11",
        EglBackendKind.SurfacelessMesa => "egl-surfaceless",
        _ => "egl-default-display"
    };

    /// <summary>The Skia backend identity a successful EGL kind maps to.</summary>
    /// <param name="kind">The backend that succeeded.</param>
    public static RenderBackend BackendFor(EglBackendKind kind) =>
        kind == EglBackendKind.AngleD3D11 ? RenderBackend.Angle : RenderBackend.OpenGl;

    /// <summary>
    ///     Forgets the cached load so a test can point <c>DV2D_ANGLE_LIBRARY</c> somewhere else. The
    ///     native handle is deliberately <b>not</b> freed: a live <c>GRContext</c> elsewhere in the
    ///     process is still calling into it, and unloading under it would be a crash rather than a
    ///     failed test.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (_gate)
        {
            _bindings = null;
            _loadFailure = null;
        }
    }

    private static bool TryGetDisplay(EglBindings egl, EglBackendKind kind, out IntPtr display,
        out string reason)
    {
        display = IntPtr.Zero;
        reason = "";

        switch (kind)
        {
            case EglBackendKind.AngleD3D11:
            {
                if (egl.GetPlatformDisplayExt is null)
                {
                    reason = Detail(kind, egl, "eglGetPlatformDisplayEXT is not exported");
                    return false;
                }

                int[] attributes = [EglPlatformAngleTypeAngle, EglPlatformAngleTypeD3D11Angle, EglNone];
                display = WithPinnedAttributes(attributes,
                    pointer => egl.GetPlatformDisplayExt(EglPlatformAngleAngle, IntPtr.Zero, pointer));
                break;
            }

            case EglBackendKind.SurfacelessMesa:
            {
                if (egl.GetPlatformDisplayExt is null)
                {
                    reason = Detail(kind, egl, "eglGetPlatformDisplayEXT is not exported");
                    return false;
                }

                display = egl.GetPlatformDisplayExt(EglPlatformSurfacelessMesa, IntPtr.Zero,
                    IntPtr.Zero);
                break;
            }

            default:
                display = egl.GetDisplay(IntPtr.Zero);
                break;
        }

        if (display == IntPtr.Zero)
        {
            reason = Detail(kind, egl, "no EGL display");
            return false;
        }

        return true;
    }

    private static IntPtr CreateContext(EglBindings egl, IntPtr display, IntPtr config, int clientVersion)
    {
        int[] attributes = [EglContextClientVersion, clientVersion, EglNone];
        return egl.CreateContext(display, config, IntPtr.Zero, attributes);
    }

    private static IntPtr WithPinnedAttributes(int[] attributes, Func<IntPtr, IntPtr> body)
    {
        GCHandle pin = GCHandle.Alloc(attributes, GCHandleType.Pinned);
        try
        {
            return body(pin.AddrOfPinnedObject());
        }
        finally
        {
            pin.Free();
        }
    }

    private static string Detail(EglBackendKind kind, EglBindings egl, string what) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{ReasonFor(kind)}: {what} (eglGetError 0x{egl.GetError():X4})");
}

/// <summary>Which EGL display platform a context attempt asks for.</summary>
internal enum EglBackendKind
{
    /// <summary>ANGLE over D3D11 — the Windows path, and the one whose binary already ships.</summary>
    AngleD3D11,

    /// <summary>
    ///     <c>EGL_PLATFORM_SURFACELESS_MESA</c> — no X, no DRM node, which is what makes the Linux
    ///     container story work at all.
    /// </summary>
    SurfacelessMesa,

    /// <summary><c>eglGetDisplay(EGL_DEFAULT_DISPLAY)</c> — whatever the driver considers default.</summary>
    DefaultDisplay
}
