#region

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Rendering.Interop;

/// <summary>
///     The loaded EGL/GLES entry points for one process.
///     <para>
///         Bound through <c>NativeLibrary.TryGetExport</c> rather than <c>DllImport</c> so a missing
///         symbol is a boolean, not a first-call exception; and marshalled with
///         <see cref="CallingConvention.Winapi" />, which is <c>__stdcall</c> on Windows and
///         <c>cdecl</c> elsewhere: exactly what <c>KHRONOS_APIENTRY</c> expands to.
///     </para>
/// </summary>
internal sealed class EglBindings
{
    private readonly IntPtr[] _exportSources;

    private EglBindings(IntPtr[] exportSources, string libraryPath)
    {
        _exportSources = exportSources;
        LibraryPath = libraryPath;
    }

    /// <summary>The library the EGL entry points came from, the first thing to check in a bug report.</summary>
    public string LibraryPath { get; }

    /// <summary><c>eglGetProcAddress</c>, the address source Skia's GL interface is assembled from.</summary>
    public required EglGetProcAddressFn GetProcAddressRaw { get; init; }

    /// <summary><c>eglGetDisplay</c>.</summary>
    public required EglGetDisplayFn GetDisplay { get; init; }

    /// <summary><c>eglGetPlatformDisplayEXT</c>, or null where the extension is absent.</summary>
    public EglGetPlatformDisplayExtFn? GetPlatformDisplayExt { get; init; }

    /// <summary><c>eglInitialize</c>.</summary>
    public required EglInitializeFn Initialize { get; init; }

    /// <summary><c>eglBindAPI</c>.</summary>
    public required EglBindApiFn BindApi { get; init; }

    /// <summary><c>eglChooseConfig</c>.</summary>
    public required EglChooseConfigFn ChooseConfig { get; init; }

    /// <summary><c>eglCreatePbufferSurface</c>.</summary>
    public required EglCreatePbufferSurfaceFn CreatePbufferSurface { get; init; }

    /// <summary><c>eglCreateContext</c>.</summary>
    public required EglCreateContextFn CreateContext { get; init; }

    /// <summary><c>eglMakeCurrent</c>.</summary>
    public required EglMakeCurrentFn MakeCurrent { get; init; }

    /// <summary><c>eglDestroySurface</c>.</summary>
    public required EglDestroySurfaceFn DestroySurface { get; init; }

    /// <summary><c>eglDestroyContext</c>.</summary>
    public required EglDestroyContextFn DestroyContext { get; init; }

    /// <summary><c>eglTerminate</c>.</summary>
    public required EglTerminateFn Terminate { get; init; }

    /// <summary><c>eglGetError</c>.</summary>
    public required EglGetErrorFn GetError { get; init; }

    /// <summary>
    ///     Loads EGL from the first candidate that works and binds every entry point. Never throws.
    /// </summary>
    /// <param name="bindings">The bound entry points on success.</param>
    /// <param name="reason">A <c>no-egl-library</c>-prefixed explanation on failure.</param>
    public static bool TryLoad([NotNullWhen(true)] out EglBindings? bindings, out string reason)
    {
        bindings = null;
        List<string> attempts = [];

        foreach (string candidate in Candidates())
        {
            if (!TryLoadLibrary(candidate, out IntPtr egl))
            {
                attempts.Add(candidate);
                continue;
            }

            // On Linux the GLES entry points live in a second library; on Windows av_libglesv2.dll is a
            // merged EGL+GLESv2 build and this simply finds nothing extra.
            IntPtr[] sources = TryLoadLibrary("libGLESv2.so.2", out IntPtr gles)
                ? [egl, gles]
                : [egl];

            if (TryBind(sources, candidate, out bindings))
            {
                reason = "loaded";
                return true;
            }

            attempts.Add(candidate + " (loaded, but entry points are missing)");
        }

        reason = string.Create(CultureInfo.InvariantCulture,
            $"no-egl-library: tried {string.Join(", ", attempts)}");
        return false;
    }

    /// <summary>
    ///     Resolves one GL/EGL symbol. Direct exports are tried first because they are always correct;
    ///     <c>eglGetProcAddress</c> then covers the extensions, and older ANGLE builds that return null
    ///     for core entry points are handled by that ordering rather than by hoping.
    /// </summary>
    /// <param name="name">The symbol to resolve.</param>
    public IntPtr GetProcAddress(string name)
    {
        foreach (IntPtr source in _exportSources)
        {
            if (NativeLibrary.TryGetExport(source, name, out IntPtr address))
            {
                return address;
            }
        }

        IntPtr utf8 = Marshal.StringToHGlobalAnsi(name);
        try
        {
            return GetProcAddressRaw(utf8);
        }
        finally
        {
            Marshal.FreeHGlobal(utf8);
        }
    }

    /// <summary>Reads a <c>glGetString</c> value, or null when the symbol or the value is absent.</summary>
    /// <param name="name">One of <c>GL_VENDOR</c>, <c>GL_RENDERER</c>, <c>GL_VERSION</c>.</param>
    public string? GetString(uint name)
    {
        IntPtr address = GetProcAddress("glGetString");
        if (address == IntPtr.Zero)
        {
            return null;
        }

        GlGetStringFn glGetString = Marshal.GetDelegateForFunctionPointer<GlGetStringFn>(address);
        IntPtr value = glGetString(name);
        return value == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(value);
    }

    private static IEnumerable<string> Candidates()
    {
        string? overridePath = Environment.GetEnvironmentVariable(Egl.LibraryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            // Deliberately the ONLY candidate: an override that silently falls through would test the
            // default build while claiming to test the named one.
            yield return overridePath;

            yield break;
        }

        if (OperatingSystem.IsWindows())
        {
            // Avalonia's merged EGL+GLESv2 ANGLE build ships in the app tree already; libEGL.dll is the
            // system/vendor ANGLE a user may have next to a Chromium install.
            yield return "av_libglesv2.dll";
            yield return "libEGL.dll";

            yield break;
        }

        if (OperatingSystem.IsLinux())
        {
            yield return "libEGL.so.1";
            yield return "libEGL.so";
        }
    }

    private static bool TryLoadLibrary(string name, out IntPtr handle)
    {
        try
        {
            return NativeLibrary.TryLoad(name, typeof(EglBindings).Assembly, null, out handle);
        }
        catch (Exception e) when (e is ArgumentException or BadImageFormatException)
        {
            handle = IntPtr.Zero;
            return false;
        }
    }

    private static bool TryBind(IntPtr[] sources, string libraryPath,
        [NotNullWhen(true)] out EglBindings? bindings)
    {
        bindings = null;

        if (!TryGet(sources, "eglGetProcAddress", out EglGetProcAddressFn? getProcAddress) ||
            !TryGet(sources, "eglGetDisplay", out EglGetDisplayFn? getDisplay) ||
            !TryGet(sources, "eglInitialize", out EglInitializeFn? initialize) ||
            !TryGet(sources, "eglBindAPI", out EglBindApiFn? bindApi) ||
            !TryGet(sources, "eglChooseConfig", out EglChooseConfigFn? chooseConfig) ||
            !TryGet(sources, "eglCreatePbufferSurface", out EglCreatePbufferSurfaceFn? createPbuffer) ||
            !TryGet(sources, "eglCreateContext", out EglCreateContextFn? createContext) ||
            !TryGet(sources, "eglMakeCurrent", out EglMakeCurrentFn? makeCurrent) ||
            !TryGet(sources, "eglDestroySurface", out EglDestroySurfaceFn? destroySurface) ||
            !TryGet(sources, "eglDestroyContext", out EglDestroyContextFn? destroyContext) ||
            !TryGet(sources, "eglTerminate", out EglTerminateFn? terminate) ||
            !TryGet(sources, "eglGetError", out EglGetErrorFn? getError))
        {
            return false;
        }

        TryGet(sources, "eglGetPlatformDisplayEXT", out EglGetPlatformDisplayExtFn? platformDisplay);

        bindings = new EglBindings(sources, libraryPath)
        {
            GetProcAddressRaw = getProcAddress,
            GetDisplay = getDisplay,
            GetPlatformDisplayExt = platformDisplay,
            Initialize = initialize,
            BindApi = bindApi,
            ChooseConfig = chooseConfig,
            CreatePbufferSurface = createPbuffer,
            CreateContext = createContext,
            MakeCurrent = makeCurrent,
            DestroySurface = destroySurface,
            DestroyContext = destroyContext,
            Terminate = terminate,
            GetError = getError
        };
        return true;
    }

    private static bool TryGet<TDelegate>(IntPtr[] sources, string name,
        [NotNullWhen(true)] out TDelegate? binding) where TDelegate : Delegate
    {
        foreach (IntPtr source in sources)
        {
            if (NativeLibrary.TryGetExport(source, name, out IntPtr address) && address != IntPtr.Zero)
            {
                binding = Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
                return true;
            }
        }

        binding = null;
        return false;
    }
}
