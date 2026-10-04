#region

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.LiveSync;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Startup;
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
    {
        // The XAML previewer calls this without Main, so it declares the same packs; a no-op after Main.
        // Behind a factory: after Main a staged copy may be the configured one, and the shipped type must
        // then stay untouched.
        FeaturePacks.ConfigureIfUnset(static () => [new StratBookPack()]);
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }

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
        // The after-update hook carries the pre-v2 graph-breakpoint file forward. Those records
        // identify a node by NAME, which rewrites exactly to the new game-scope key form, so the user
        // keeps the breakpoints and the conditions they authored on them.
        //
        // Velopack's install/update hooks are WINDOWS-ONLY (CA1416), so this is the ideal path, not the
        // only one: GraphBreakpointStore's constructor makes the same call on every platform. Nothing
        // reads the old file either way, the store having moved to GraphBreakpoints.v2.json, so this is
        // housekeeping and a hook that never fires costs nothing.
        VelopackApp builder = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
        {
            builder = builder.OnAfterUpdateFastCallback(static _ => GraphBreakpointStore.MigrateLegacyFile());
        }

        builder.Run();

        // The extensions this build ships, each replaced by a newer copy staged
        // under <config root>/extensions/ when one is compatible and trusted. The app assembly
        // references none of them; the composition root and the static registries read this list, so it is
        // declared before anything Avalonia-side runs. The shipped pack sits behind a factory: a method that
        // mentions StratBookPack loads the shipped assembly when it is compiled, and the loader must decide
        // before that happens, so nothing else in Main may name the type.
        //
        // Safe mode loads none of them, the shipped one included: --safe-mode asks for it, and the launch
        // guard turns it on when the previous launch never finished starting, crashed inside an extension or
        // froze until it was killed. Third-party extensions load beside the shipped one, and an unverified
        // copy only while the user has allowed unverified extensions in Settings.
        LaunchGuard launch = LaunchGuard.Begin(AppPaths.ConfigRoot, args).Install();
        bool allowUnverified = ExtensionStartup.ReadAllowUnverified(AppPaths.SettingsFile);
        ExtensionStartupResult extensions = ExtensionStartup.Resolve(
            AppPaths.ConfigRoot,
            [ShippedPack.BesideApp(StratBookPack.PackId, static () => new StratBookPack(), [RoundFactsFingerprint.RulesetId])],
            ExtensionHost.Current,
            TrustPolicy.ForLaunch(allowUnverified),
            PublisherKeys.Current,
            allowUnverified,
            launch.Decision.IsActive);
        FeaturePacks.ConfigureResolved(extensions.Statuses, extensions.ExternalRejected, extensions.ClaimedRulesets);

        // Last-chance crash log: an unhandled exception aborts the process, and on macOS the OS
        // report (.ips) carries only unsymbolicated JIT frames. Persist the MANAGED stack, and tell the
        // launch guard which extension it was in, if any.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            WriteCrashLog(e.ExceptionObject);
            if (e.ExceptionObject is Exception ex)
            {
                launch.RecordCrash(ex, FeaturePacks.Statuses.Select(st =>
                    new ExtensionIdentity(st.Pack.GetType().Assembly, st.Manifest?.Name ?? st.Pack.Id, st.Pack.FeatureId)));
            }
        };

        AppHostHooks.Restart = () => Restart(launch);

        // CSVG live sync: the engine lives in the desktop-only
        // DemoViewer.NET.LiveSync project (CSVG + ASP.NET Core: the App/Browser projects must
        // never reference it), so this host injects its factory through the AppHostHooks static
        // seam before the lifetime starts. App.axaml.cs invokes it once the shell exists.
        AppHostHooks.LiveSyncFactory = static shell => new LiveSyncService(shell);

        // Reel generation: same seam. The concrete LiveSyncService
        // is handed through so the single-CS2 interlock can suspend an active sync session;
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
        // Live / per-moment capture without any of this is available via dotnet-counters / dotnet-trace.
        using ProfilingSession? session = ProfilingSession.StartFromEnvironment();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        launch.MarkExited();
    }

    // The same executable with the same arguments minus --safe-mode. Under `dotnet run` the process is the
    // dotnet host, whose first argument is the app's own assembly.
    private static void Restart(LaunchGuard launch)
    {
        if (Environment.ProcessPath is not { } executable)
        {
            return;
        }

        launch.MarkExited();
        string[] commandLine = Environment.GetCommandLineArgs();
        ProcessStartInfo start = new(executable) { UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(commandLine[0]);
        }

        foreach (string arg in commandLine.Skip(1).Where(a => !LaunchGuard.IsSafeModeArgument(a)))
        {
            start.ArgumentList.Add(arg);
        }

        using Process? started = Process.Start(start);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
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
