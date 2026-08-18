#region

using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.Services.Update;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.Services;

/// <summary>
///     Static injection seam for host-specific services whose implementations live in projects
///     the App project cannot reference.
///     The Desktop entry point assigns the hooks in <c>Program.Main</c> BEFORE the Avalonia
///     lifetime starts; <c>App.OnFrameworkInitializationCompleted</c> consumes them once the
///     shell view-model exists. Unset hooks (Browser, tests, designer) mean the feature is
///     absent — consumers must null-tolerate.
///     <para>
///         Contrast with the <see cref="IWindowService" /> precedent: both of its impls live in
///         this project and are lifetime-branch-selected. This seam exists for impls that CANNOT
///         live here (DemoViewer.NET.LiveSync carries CSVG + ASP.NET Core — WASM poison for the
///         Browser head, which references this project directly).
///     </para>
/// </summary>
public static class AppHostHooks
{
    /// <summary>
    ///     Factory for the desktop live CS2 sync engine. Assigned by
    ///     <c>DemoViewer.NET.Desktop.Program.Main</c>; invoked once per app run in the desktop
    ///     lifetime branch after the shell <see cref="MainViewModel" /> is constructed. The App
    ///     disposes the returned service on shutdown.
    /// </summary>
    public static Func<MainViewModel, ILiveSyncService>? LiveSyncFactory { get; set; }

    /// <summary>
    ///     Factory for the desktop reel-generation job service.
    ///     Assigned alongside <see cref="LiveSyncFactory" />; invoked with the shell and the
    ///     (possibly null) live-sync engine so the F1↔F3b single-CS2 interlock can suspend an
    ///     active sync session. Null on Browser/tests — reel generation absent.
    /// </summary>
    public static Func<MainViewModel, ILiveSyncService?, IReelJobService>? ReelJobFactory { get; set; }

    /// <summary>
    ///     Factory for the in-app updater. Same seam, same reason as the two above: the
    ///     <c>Velopack</c> package is referenced only by <c>DemoViewer.NET.Desktop</c>, so the
    ///     implementation cannot live in this project. Takes no shell argument — the updater is
    ///     independent of demo state, and Settings needs it before any demo is loaded.
    ///     <para>
    ///         Null on Browser, tests and designer → no update check, no banner, and Settings says
    ///         updates are unavailable rather than showing a dead button.
    ///     </para>
    /// </summary>
    public static Func<IUpdateService>? UpdateServiceFactory { get; set; }
}
