#region

using System.Runtime.InteropServices;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Rendering.Interop;

/// <summary>
///     The EGL/GLES entry points this phase binds, as delegates.
///     <para>
///         <see cref="CallingConvention.Winapi" /> throughout: it resolves to <c>__stdcall</c> on Windows
///         and <c>cdecl</c> everywhere else, which is precisely what <c>KHRONOS_APIENTRY</c> expands to.
///         Hard-coding <c>Cdecl</c> would happen to work on x64 — where there is only one convention —
///         and break on win-x86, a RID this repo's ANGLE package ships a binary for.
///     </para>
///     <para>
///         Strings are passed as already-marshalled <see cref="IntPtr" /> rather than as <c>string</c>:
///         it keeps the P/Invoke free of charset and best-fit-mapping questions, and there is exactly one
///         such call (<c>eglGetProcAddress</c>).
///     </para>
/// </summary>
/// <param name="name">The ANSI symbol name to resolve.</param>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate IntPtr EglGetProcAddressFn(IntPtr name);

/// <summary><c>eglGetDisplay</c>.</summary>
/// <param name="displayId">The native display, or <c>EGL_DEFAULT_DISPLAY</c> (zero).</param>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate IntPtr EglGetDisplayFn(IntPtr displayId);

/// <summary><c>eglGetPlatformDisplayEXT</c>.</summary>
/// <param name="platform">The EGL platform enum.</param>
/// <param name="nativeDisplay">The platform's native display handle, often zero.</param>
/// <param name="attributes">A pinned, <c>EGL_NONE</c>-terminated attribute list, or zero.</param>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate IntPtr EglGetPlatformDisplayExtFn(uint platform, IntPtr nativeDisplay,
    IntPtr attributes);

/// <summary><c>eglInitialize</c>.</summary>
/// <param name="display">The display to initialize.</param>
/// <param name="major">Receives the EGL major version.</param>
/// <param name="minor">Receives the EGL minor version.</param>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int EglInitializeFn(IntPtr display, out int major, out int minor);

/// <summary><c>eglBindAPI</c>.</summary>
/// <param name="api">The client API to bind, e.g. <c>EGL_OPENGL_ES_API</c>.</param>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int EglBindApiFn(uint api);

/// <summary><c>eglChooseConfig</c>.</summary>
/// <param name="display">The initialized display.</param>
/// <param name="attributes">An <c>EGL_NONE</c>-terminated attribute list.</param>
/// <param name="configs">Receives up to <paramref name="configSize" /> configs.</param>
/// <param name="configSize">The capacity of <paramref name="configs" />.</param>
/// <param name="configCount">Receives how many configs matched.</param>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int EglChooseConfigFn(IntPtr display, int[] attributes, IntPtr[] configs,
    int configSize, out int configCount);

/// <summary><c>eglCreatePbufferSurface</c>.</summary>
/// <param name="display">The initialized display.</param>
/// <param name="config">The chosen config.</param>
/// <param name="attributes">An <c>EGL_NONE</c>-terminated attribute list.</param>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate IntPtr EglCreatePbufferSurfaceFn(IntPtr display, IntPtr config, int[] attributes);

/// <summary><c>eglCreateContext</c>.</summary>
/// <param name="display">The initialized display.</param>
/// <param name="config">The chosen config.</param>
/// <param name="shareContext">A context to share objects with, or zero.</param>
/// <param name="attributes">An <c>EGL_NONE</c>-terminated attribute list.</param>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate IntPtr EglCreateContextFn(IntPtr display, IntPtr config, IntPtr shareContext,
    int[] attributes);

/// <summary><c>eglMakeCurrent</c>.</summary>
/// <param name="display">The initialized display.</param>
/// <param name="draw">The draw surface.</param>
/// <param name="read">The read surface.</param>
/// <param name="context">The context to make current.</param>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int EglMakeCurrentFn(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);

/// <summary><c>eglDestroySurface</c>.</summary>
/// <param name="display">The display the surface belongs to.</param>
/// <param name="surface">The surface to destroy.</param>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int EglDestroySurfaceFn(IntPtr display, IntPtr surface);

/// <summary><c>eglDestroyContext</c>.</summary>
/// <param name="display">The display the context belongs to.</param>
/// <param name="context">The context to destroy.</param>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int EglDestroyContextFn(IntPtr display, IntPtr context);

/// <summary><c>eglTerminate</c>.</summary>
/// <param name="display">The display to terminate.</param>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int EglTerminateFn(IntPtr display);

/// <summary><c>eglGetError</c>.</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int EglGetErrorFn();

/// <summary><c>glGetString</c>, for the renderer/vendor/version strings a bug report needs.</summary>
/// <param name="name">One of <c>GL_VENDOR</c>, <c>GL_RENDERER</c>, <c>GL_VERSION</c>.</param>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate IntPtr GlGetStringFn(uint name);
