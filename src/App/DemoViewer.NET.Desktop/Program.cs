#region

using Avalonia;
using Avalonia.Threading;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.LiveSync;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Options;
using Velopack;

#endregion

namespace DemoViewer.NET.Desktop;

internal sealed class Program
{
    // Avalonia configuration, don't remove; also used by visual designer.
    /// <summary>Build avalonia app.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    /// <summary>Main.</summary>
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack install/update/uninstall hook handling (docs/distribution). MUST be the very
        // first thing Main does: on a hook invocation (--veloapp-install, etc.) it runs the hook and
        // exits the process before any Avalonia/CSVG/SynchronizationContext init would run. On a
        // normal launch it returns immediately. Unpackaged/dev runs (no Velopack metadata) are a
        // no-op, so this is safe under `dotnet run` and the headless UI-capture host too.
        VelopackApp.Build().Run();

        // Last-chance crash log: an unhandled exception aborts the process, and on macOS the OS
        // report (.ips) carries only unsymbolicated JIT frames. Persist the MANAGED stack.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject);

        // CSVG live sync: the engine lives in the desktop-only
        // DemoViewer.NET.LiveSync project (CSVG + ASP.NET Core: the App/Browser projects must
        // never reference it), so this host injects its factory through the AppHostHooks static
        // seam before the lifetime starts. App.axaml.cs invokes it once the shell exists.
        AppHostHooks.LiveSyncFactory = static shell => new LiveSyncService(shell);

        // Reel generation: same seam. The concrete LiveSyncService
        // is handed through so the F1↔F3b single-CS2 interlock can suspend an active sync session;
        // job log lines surface in the Output panel's "Live Sync" channel.
        AppHostHooks.ReelJobFactory = static (shell, liveSync) => new ReelJobService(
            liveSync as LiveSyncService,
            App.Services?.GetService(typeof(HeavyJobGate))
                as HeavyJobGate,
            App.Services?.GetService(typeof(IOptionsMonitor<AppSettings>))
                as IOptionsMonitor<AppSettings>,
            line => Dispatcher.UIThread.Post(() =>
                shell.Output.BuildTest.Append(new OutputRow(
                    -1, "REEL", "INFO", line))));

        // In-app updater, same static seam, same reason: the Velopack package is referenced only by
        // this project, so nothing Velopack-typed may appear in the App project (WASM poison for the
        // Browser head). VelopackApp.Build().Run() above handles install/update HOOKS only; it never
        // contacts a server. Without this factory the published releases.{channel}.json feeds would
        // go on being written and never read, which is exactly the state v0.5.1 shipped in.
        AppHostHooks.UpdateServiceFactory = static () => new VelopackUpdateService();

        // DEMOVIEWER_PROFILE=1 attaches the analysis profiling listeners (Meter counters + phase-timeline
        // spans) for the whole app session and dumps a combined (session-aggregate) report on exit.
        // Default (env unset): a null session, no listeners, no cost. The report goes to Console.Out, so
        // on Windows (this is a WinExe) it only appears when launched from a terminal or via `dotnet run`.
        // Live / per-moment capture without any of this is available via dotnet-counters / dotnet-trace
        // (see docs/profiling.md).
        using ProfilingSession? session = ProfilingSession.StartFromEnvironment();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void WriteCrashLog(object exceptionObject)
    {
        try
        {
            string? path = AppPaths.CrashLogFile;
            if (path is null)
            {
                return; // no filesystem (WASM), never on desktop, but be safe
            }

            File.AppendAllText(path,
                $"──── {DateTime.Now:yyyy-MM-dd HH:mm:ss} ────{Environment.NewLine}{exceptionObject}{Environment.NewLine}");
        }
        catch
        {
            // last-chance logging must never mask the original crash
        }
    }
}
