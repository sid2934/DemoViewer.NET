#region

using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.GameIcons;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.Updates;
using DemoViewer.NET.Features;
using DemoViewer.NET.Models;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.RuleWorkbench;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.Startup;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Dependencies;
using DemoViewer.NET.Services.Diagnostics;
using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Theming;
using DemoViewer.NET.ViewModels.Diagnostics;
using DemoViewer.NET.ViewModels.Highlights;
using DemoViewer.NET.ViewModels.Settings;
using DemoViewer.NET.ViewModels.Setup;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views;
using DemoViewer.NET.Views.RuleWorkbench;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET;

/// <summary>App.</summary>
public class App : Application
{
    // Re-entrancy tripwire for BuildShell. Deliberately NOT [ThreadStatic]: the recursion it guards
    // against HOPS THREADS (ServiceProvider's StackGuard.RunOnEmptyStack moves to a fresh thread as the
    // stack deepens), so a per-thread flag would never see it. The shell is resolved on the UI thread, so
    // a plain static is not a cross-thread hazard here.
    private static bool _shellUnderConstruction;

    /// <summary>
    ///     The application's composition-root service provider, set once by <see cref="BuildServices(IWindowService, IReadOnlyList{IExtension})" />
    ///     during framework init. A deliberate service-locator seam so later Settings / first-run-wizard
    ///     commands can resolve the long-lived <see cref="SettingsService" /> /
    ///     <c>IOptionsMonitor&lt;AppSettings&gt;</c> without threading them through every view-model.
    ///     <c>null</c> only before init (e.g. the XAML designer). The app uses a bare Microsoft.Extensions
    ///     DI container as the single composition root.
    /// </summary>
    public static IServiceProvider? Services { get; private set; }

    /// <inheritdoc />
    public override void Initialize()
    {
        // Before anything can resolve an app-data path. AppPaths also claims this from a module
        // initializer, but that fires on first use of a type in this assembly, and the rules
        // loader in CS2DemoKit.Analysis can resolve the user-rules directory without touching one.
        // Claiming it here too makes the order explicit instead of incidental.
        AppPaths.ClaimConfigDirectoryName();

        // Before any view is built: a MapView made earlier has no renderer and stays empty.
        Extensions.Sdk.Ui.Controls.MapViewHost.Factory = static () => new Modules.Playback2D.HostedMapView();

        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // No DataAnnotations plugin to strip: Avalonia 12 makes that validation opt-in through
            // AppBuilder.WithDataAnnotationsValidation(), which this app does not call, so the
            // CommunityToolkit is the only validator and there is nothing to duplicate.
            MainWindow window = new();
            // Owner-lookup defers to the live MainWindow so the parse-chain window can be
            // parented (and centred) without the service holding a hard window reference.
            DesktopWindowService windowService = new(() => window);
            // The DI container is the single composition root: it constructs + HOLDS the
            // ModuleRegistry and resolves the shell.
            ServiceProvider services = BuildServices(windowService);
            _backstops ??= ExtensionBackstops.Install(services.GetRequiredService<ExtensionFaults>());
            WireTheme(services); // apply persisted theme + keep it live
            MainViewModel viewModel = services.GetRequiredService<MainViewModel>();
            WireDiagnosticsLogging(services, viewModel); // internal ILogger pillar -> Diagnostics tab + file
            LogExtensionStatuses();
            // The one whole-library check of the session: every pass is asked about every known demo, off
            // the UI thread, so work left from the last session or owed by a changed configuration starts
            // without waiting for a demo to change. After that only events feed the scheduler.
            services.GetRequiredService<DemoScheduler>().RecheckAll();
            // Drop an unfinished download and the staged versions the running copy supersedes. A
            // background queue item; nothing waits on it.
            _ = services.GetService<ExtensionUpdateService>()?.CleanupOnStartAsync();
            // One-off re-encode of pre-gzip record sidecars: no parse, a background queue job that steps aside
            // between batches, marker-gated once a pass converts everything it found. Held back so the startup
            // loads are not competing for the disk.
            if (!OperatingSystem.IsBrowser())
            {
                DemoCacheStore demoCache = services.GetRequiredService<DemoCacheStore>();
                IDemoProcessingQueue queue = services.GetRequiredService<IDemoProcessingQueue>();
                _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(
                    _ => SidecarFormatMigration.Submit(queue, demoCache, [demoCache.ConvertLegacyRecord]), TaskScheduler.Default);
            }
            // Careful: host services MUST attach BEFORE RestoreSession. RestoreSession activates the persisted tab,
            // and a restored-active Reels tab builds HighlightsTabViewModel (→ HighlightReelDialogViewModel),
            // which captures Shell().ReelJob / Shell().ReelJobStatus ONCE in its constructor. Attaching after
            // restore left that capture null for the whole session whenever Reels was the last-active tab.
            // Generate became a silent no-op (CS2 never launched) and the inline reel chip stayed dead. These
            // factories depend only on the shell singleton (line above) and App.Services (set in BuildServices),
            // never on restored state, so hoisting them ahead of restore is safe.

            // CSVG live sync: the engine impl lives in the desktop-only
            // DemoViewer.NET.LiveSync project (CSVG + ASP.NET Core = WASM poison), which this
            // project cannot reference; the Desktop entry point injects a factory via the
            // AppHostHooks static seam before the lifetime starts. Unset on Browser/tests.
            ILiveSyncService? liveSync = null;
            if (AppHostHooks.LiveSyncFactory is { } liveSyncFactory)
            {
                liveSync = liveSyncFactory(viewModel);
                viewModel.AttachLiveSync(liveSync);
            }

            // CSVG reel generation: same static-seam pattern as
            // Live Sync. The job service takes the (possibly null) live-sync engine so the single-CS2
            // interlock can suspend an active session. Unset on Browser/tests → reel generation absent.
            IReelJobService? reelJob = null;
            if (AppHostHooks.ReelJobFactory is { } f)
            {
                reelJob = f(viewModel, liveSync);
                viewModel.AttachReelJob(reelJob);
            }

            // 2D video export. Everything reusable is in Core/Pipeline and
            // the 2D tab composes the job itself. What it cannot see through IModuleContext, such as the frame
            // list, the heavy-job gate, and whether Live Sync or a reel already owns the machine, is
            // handed to it here, the same way the live-sync HUD projection and the speed lock are.
            //
            // Not an AppHostHooks entry: every implementation involved lives in THIS project, which is
            // exactly the distinction that seam's doc-comment draws. Null host on Browser (the feature is
            // gated off there anyway) and in the designer → the tab's Export affordance stays hidden.
            if (viewModel.ModuleContext is ModuleContext moduleContext)
            {
                SettingsService settings = services.GetRequiredService<SettingsService>();
                moduleContext.SetExportHost(new Playback2DExportHost(
                    () => viewModel.Playback.Frames,
                    services.GetRequiredService<HeavyJobGate>(),
                    // IsSessionActive only: OwnsSessionResources is internal to the desktop-only LiveSync
                    // project. The narrower predicate still refuses every case a user can create: a
                    // faulted session holding the gRPC host for retry is the gap, and the gate's own reel
                    // check covers the overlap that actually costs CPU.
                    () => liveSync?.State.IsSessionActive == true,
                    () => reelJob?.Status.IsRunning == true,
                    () => settings.Current,
                    settings.Write,

                    // The 2D tab is lazy and the shell is not, so the chip cannot be attached here the
                    // way the reel's is. The shell hands over the mount point and the tab calls it on the
                    // first Export: see Playback2DExportHost.MountStatusChip.
                    viewModel.AttachPlayback2DExportStatus,
                    viewModel.OpenOutputFolder));
            }

            // The highlight-scan chip. Attached from the container's instance so the strip shows a
            // running library scan even when the Reels tab has never been opened (module tab VMs are lazy).
            viewModel.AttachHighlightScanStatus(services.GetRequiredService<HighlightScanStatusViewModel>());

            // Match Overview's [ + ] stages into the Reels tray. A locator, not a reference: the
            // Reels tab is lazy, and staging must work before it has ever been opened.
            viewModel.ReelTrayLocator = services.GetRequiredService<HighlightsTabViewModel>;

            // Session restore runs HERE, not in the shell ctor: it activates the persisted tab, and tab
            // activation may resolve the shell, which only works once the singleton above is cached (and now
            // once the host services above are attached). Still before the DataContext is set, so the UI binds
            // to already-restored state (see RestoreSession).
            viewModel.RestoreSession();

            // v0.6.0: apply persisted window geometry before the window ever shows. Sizes are DIPs,
            // Position is PHYSICAL pixels (two unit systems, never mixed); a saved position is reused
            // only when it still lands on a connected screen, so a detached monitor cannot strand the
            // window off-desktop.
            if (viewModel.RestoredWindowBounds is { } savedBounds)
            {
                ApplyWindowBounds(window, savedBounds);
            }

            // Track the last-NORMAL bounds live: a maximized exit must persist the size it would
            // RESTORE to (not the maximized size), and a minimized exit must persist nothing new.
            WindowBoundsState? lastNormalBounds = null;

            void CaptureNormalBounds()
            {
                if (window.WindowState != WindowState.Normal)
                {
                    return;
                }

                double w = double.IsFinite(window.Width) ? window.Width : window.Bounds.Width;
                double h = double.IsFinite(window.Height) ? window.Height : window.Bounds.Height;
                if (w < 200 || h < 150)
                {
                    return; // pre-layout / degenerate sizes are not a user's choice
                }

                lastNormalBounds = new WindowBoundsState(w, h, window.Position.X, window.Position.Y, false);
            }

            window.PositionChanged += (_, _) => CaptureNormalBounds();
            window.PropertyChanged += (_, args) =>
            {
                if (args.Property == TopLevel.ClientSizeProperty || args.Property == Window.WindowStateProperty)
                {
                    CaptureNormalBounds();
                }
            };

            // Idle mode is DESKTOP-ONLY (no real memory pressure on WASM; the global input hook / demo-close
            // semantics differ). Start it here, after the shell exists. The WASM branch never calls this.
            viewModel.StartIdleMonitoring();

            // Launch update check (desktop-only: the Browser head has no installed build to update).
            // Fire-and-forget on purpose: this is one HTTPS request to the GitHub release feed, and
            // the window must never wait on it. No-op unless Desktop supplied the service.
            viewModel.StartUpdateCheck();

            // Post-update "What's new" gate (v0.6.0). Desktop-only like the update check. The gate
            // itself is cheap (one settings read + at most one write) and the notes fetch happens
            // lazily when the window opens, so it never delays the shell, but it must NOT run here.
            // The window it opens is OWNED by the main window, and Avalonia throws
            // "Cannot show window with non-visible owner" if the owner has not been shown yet, which
            // is exactly the state at this point in framework-init (v0.7.1 crashed on launch for every
            // upgrading user this way). Deferred to a one-shot Opened hook, posted for the same
            // re-entrancy reason as the first-run wizard below. Registered BEFORE the wizard's hook so
            // the original ordering (What's-New gate first) is preserved; on a first run the gate
            // records the version and stays silent anyway.
            void ShowWhatsNewOnce(object? sender, EventArgs e)
            {
                window.Opened -= ShowWhatsNewOnce;
                Dispatcher.UIThread.Post(viewModel.StartWhatsNewCheck);
            }

            window.Opened += ShowWhatsNewOnce;

            // The launch counts as started once the window opened and the UI thread kept answering for a
            // few seconds; from then on the watchdog records a freeze the user has to kill.
            if (LaunchGuard.Current is { } launch)
            {
                void MarkRunningOnce(object? sender, EventArgs e)
                {
                    window.Opened -= MarkRunningOnce;
                    DispatcherTimer.RunOnce(() =>
                    {
                        launch.MarkRunning();
                        _watchdog ??= new UiWatchdog(launch.MarkHung);
                    }, TimeSpan.FromSeconds(5));
                }

                window.Opened += MarkRunningOnce;
            }

            window.Content = new MainView();
            window.DataContext = viewModel;
            desktop.MainWindow = window;

            // Persist the session on exit. ShutdownRequested fires before the
            // window tears down, so the VM snapshot still reflects live state.
            //
            // CSVG teardown must also run here: a live-sync session owns a CS2 process and a
            // temporarily patched CS2 install, and a running reel job owns strictly more
            // (CS2 + OBS capture + the same patched install). Only their teardown paths stop the
            // processes and restore the install. The event cannot await, so the first request
            // that finds anything to tear down is cancelled, teardown runs asynchronously
            // (bounded: shutdown must never hang on a stuck CS2 kill), and shutdown is
            // re-triggered. Repeat requests (a user hammering Cmd+Q on an apparent hang) keep
            // being cancelled and JOIN the in-flight teardown: never a second teardown, never an
            // exit while CS2 kill / install restore is still mid-flight.
            bool csvgTornDown = false;
            bool csvgTeardownStarted = false;

            // Runs only on the request that really exits, after any CSVG or export teardown: a store that
            // throws or hangs here must never cost the machine its restored CS2 install.
            static void FlushStores(ServiceProvider services)
            {
                // Shutdown is a strat commit trigger; the user-truth stores defer their
                // index to it. The Review Queue is core (Reels uses it too), so its flush stays unconditional
                // here; the Strat Book's and the Tag Store's run through the pack's lifecycle below.
                // Idempotent, so a re-fired request writes nothing new.
                ILogger log = DiagnosticsLog.CreateLogger(AppLog.ShellCategory);
                Action[] flushes =
                [
                    () => services.GetService<ReviewQueue>()?.Flush(TimeSpan.FromSeconds(5)),
                    () =>
                    {
                        // Every pack's lifecycle, unconditionally: whether OnEnabledAsync ran at startup is
                        // not whether there is anything to flush now (a pack turned on and off in session,
                        // or one whose tab was opened and written to). Each lifecycle's own "is live" guards
                        // decide what, if anything, to touch; one lifecycle's failure must not skip the others.
                        foreach (IExtension pack in FeaturePacks.Compatible)
                        {
                            try
                            {
                                // Resolving it runs the extension's constructor, which can throw too.
                                services.GetKeyedService<IExtensionLifecycle>(pack.Id)?.OnShutdown(TimeSpan.FromSeconds(5));
                            }
                            catch (Exception ex)
                            {
                                AppLog.OperationFailed(log, "shutdown flush", ex);
                            }
                        }
                    }
                ];
                foreach (Action flush in flushes)
                {
                    try
                    {
                        flush();
                    }
                    catch (Exception ex)
                    {
                        AppLog.OperationFailed(log, "shutdown flush", ex);
                    }
                }
            }
            desktop.ShutdownRequested += (_, e) =>
            {
                // Geometry snapshot first (idempotent: this handler can re-fire after a cancelled
                // CSVG-teardown request): current bounds if Normal, else the tracked last-Normal
                // bounds, with the maximized flag re-applied separately.
                CaptureNormalBounds();
                if (lastNormalBounds is { } normalBounds)
                {
                    viewModel.WindowBounds = normalBounds with
                    {
                        Maximized = window.WindowState is WindowState.Maximized or WindowState.FullScreen
                    };
                }

                viewModel.SaveSession();

                bool reelRunning = reelJob is { Status.IsRunning: true };

                // A running 2D export owns an ffmpeg subprocess and a half-written video file. Exiting
                // without cancelling orphaned the process and left the partial output on disk looking
                // like a finished export. The reel path had this teardown from day one, and the export,
                // whose Cancel had no production caller at all, had none.
                bool exportRunning = viewModel.Playback2DExportStatus is { IsRunning: true };

                if (csvgTornDown || liveSync is null && !reelRunning && !exportRunning)
                {
                    FlushStores(services);
                    return;
                }

                e.Cancel = true;
                if (csvgTeardownStarted)
                {
                    return;
                }

                csvgTeardownStarted = true;
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        // Reel first: CancelAsync awaits the job's finally-teardown (capture
                        // session stopped, install restored, host disposed).
                        if (reelJob is { Status.IsRunning: true } runningReel)
                        {
                            await runningReel.CancelAsync().WaitAsync(TimeSpan.FromSeconds(30));
                        }
                    }
                    catch
                    {
                        // Best effort: a failed restore is CSVG's `csvg restore` / doctor territory;
                        // the app must still exit.
                    }

                    try
                    {
                        // Then the 2D export: its CancelAsync awaits the job's own finally, which
                        // disposes the sink. That is what kills ffmpeg and deletes the partial file.
                        // Shorter budget than the reel's: nothing here touches a CS2 install, so the
                        // worst case is a stuck pipe rather than a machine left patched.
                        if (viewModel.Playback2DExportStatus is { IsRunning: true } runningExport)
                        {
                            await runningExport.CancelCommand.ExecuteAsync(null)
                                .WaitAsync(TimeSpan.FromSeconds(15));
                        }
                    }
                    catch
                    {
                        // Same best-effort contract; a partial file is better than a hung exit.
                    }

                    try
                    {
                        if (liveSync is not null)
                        {
                            await liveSync.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
                        }
                    }
                    catch
                    {
                        // Same best-effort contract as the reel teardown above.
                    }
                    finally
                    {
                        csvgTornDown = true;
                        desktop.Shutdown();
                    }
                });
            };

            // P2b: first-run setup wizard. DESKTOP ONLY: on WASM there is no persisted file, so
            // NeedsFirstRun is always true and auto-showing would loop every page load (see the browser
            // branch: the wizard is reachable there only via Settings). NeedsFirstRun is now driven by the
            // AppSettings.FirstRunCompleted flag (only the wizard's Finish/Skip sets it), NOT by whether
            // settings.json exists, so the library-folder migration creating the file during BuildServices
            // no longer suppresses the wizard for an UPGRADING install (that user has still never picked a
            // category and should see it; the wizard's folder step is pre-seeded with their migrated folders).
            // The wizard is shown MODAL and owned by the main window, so it must wait for the window to
            // actually open, hence the one-shot Opened hook (posted to avoid re-entrancy during the owner's
            // show sequence).
            if (services.GetRequiredService<SettingsService>().NeedsFirstRun)
            {
                void ShowWizardOnce(object? sender, EventArgs e)
                {
                    window.Opened -= ShowWizardOnce;
                    FirstRunWizardViewModel wizardVm =
                        services.GetRequiredService<Func<FirstRunWizardViewModel>>().Invoke();
                    // After setup closes, launch the Visual Walkthrough if the user opted in on the Done page.
                    // Posted so it runs after the wizard's own Completed handler tears the modal down.
                    wizardVm.Completed += (_, _) =>
                    {
                        if (wizardVm.ShouldStartWalkthrough)
                        {
                            Dispatcher.UIThread.Post(viewModel.StartWalkthrough);
                        }
                    };
                    Dispatcher.UIThread.Post(() => windowService.ShowFirstRunWizard(wizardVm));
                }

                window.Opened += ShowWizardOnce;
            }
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            // Browser / single-view host: no OS windows, no filesystem. The window service no-ops,
            // SettingsService degrades to an in-memory provider, and SessionStore self-guards, so
            // persistence is effectively in-memory for the page. Both hosts use the one DI composition
            // root; only first-party modules are registered on WASM (no filesystem / assembly probing).
            BrowserWindowService windowService = new();
            ServiceProvider services = BuildServices(windowService);
            _backstops ??= ExtensionBackstops.Install(services.GetRequiredService<ExtensionFaults>());
            WireTheme(services); // apply persisted theme + keep it live
            MainViewModel viewModel = services.GetRequiredService<MainViewModel>();
            WireDiagnosticsLogging(services, viewModel); // internal ILogger pillar -> Diagnostics tab (file no-ops on WASM)
            LogExtensionStatuses();

            // Same ordering contract as the desktop root above: after the singleton is cached, never in
            // the ctor. No-ops on WASM (fileless settings persist no session), but the call site stays so
            // the two roots do not drift.
            viewModel.RestoreSession();
            // WASM has no OS windows: the window service surfaces the Settings screen as an in-app overlay
            // on the shell (P2a-i). Wired here, after the shell VM exists.
            windowService.OnOpenSettings = viewModel.ShowSettingsOverlay;
            // P2b: the first-run wizard also surfaces as an in-app overlay. It is NOT auto-triggered on
            // WASM (no persisted file → NeedsFirstRun always true → would loop); it is reached only via
            // Settings' "Re-run first-time setup". Wired here so that relaunch path works on the browser host.
            windowService.OnShowFirstRun = viewModel.ShowFirstRunOverlay;
            Control shell = new MainView();
            shell.DataContext = viewModel;
            singleViewPlatform.MainView = shell;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    ///     The extension loader's one-time report: the head resolved the packs
    ///     in Main, before any logger existed, and recorded the outcome on each <see cref="PackStatus" />;
    ///     this writes it once the diagnostics pillar is up. One line per pack (loaded, with its source, or
    ///     incompatible), then one per staged candidate the loader refused.
    /// </summary>
    private static void LogExtensionStatuses()
    {
        ILogger log = DiagnosticsLog.CreateLogger(AppLog.ExtensionsCategory);
        foreach (PackStatus status in FeaturePacks.Statuses)
        {
            string name = status.Manifest?.Name ?? status.Pack.Id;
            string version = status.Manifest?.Version.ToString() ?? "?";
            if (status.IsCompatible)
            {
                string location = status.Source is PackSource.Staged staged ? staged.Directory : status.Pack.GetType().Assembly.Location;
                AppLog.ExtensionLoaded(log, name, version, status.Source.Label, location);
            }
            else
            {
                AppLog.ExtensionIncompatible(log, name, version, status.Source.Label, status.Problem ?? "incompatible");
            }

            foreach (LoadOutcome outcome in status.Rejected)
            {
                AppLog.ExtensionCandidateRejected(log, outcome.Directory, outcome.Failure, outcome.Detail,
                    outcome.LogDetail is null ? string.Empty : " [" + outcome.LogDetail + "]");
            }
        }
    }

    /// <summary>
    ///     Publishes the first-party diagnostics logging pillar: builds an <see cref="ILoggerFactory" />
    ///     around the <see cref="HubLoggerProvider" /> (feeding the shell's unified telemetry hub and,
    ///     off WASM, the rolling file) and assigns it to the ambient <see cref="DiagnosticsLog" /> seam
    ///     so the Analysis assembly's coarse logs surface live in the Diagnostics tab. Always wired (even
    ///     when the master switch is currently off) so a live toggle takes effect with no restart. The
    ///     provider's own <c>IsEnabled</c> is the live gate. No-op when settings are unavailable (designer).
    /// </summary>
    private static void WireDiagnosticsLogging(ServiceProvider services, MainViewModel viewModel)
    {
        IOptionsMonitor<AppSettings>? monitor = services.GetService<IOptionsMonitor<AppSettings>>();
        if (monitor is null)
        {
            return; // designer / degraded host: ambient factory stays NullLogger
        }

        // The file mirror is a launch-time decision (WriteLogFile at startup); caps are read live.
        // TryCreate no-ops on WASM (no filesystem).
        DiagnosticsSettings d0 = monitor.CurrentValue.Diagnostics;
        DiagnosticsFileLog? file = d0.EnableInternalLogging && d0.WriteLogFile
            ? DiagnosticsFileLog.TryCreate(
                () => monitor.CurrentValue.Diagnostics.FileMaxSizeKilobytes,
                () => monitor.CurrentValue.Diagnostics.FileMaxCount)
            : null;

        ILoggerFactory factory = LoggerFactory.Create(b =>
        {
            // Floor the pipeline at Trace so the provider's live IsEnabled (enabled + MinimumLogLevel)
            // is the SOLE gate. Otherwise LoggerFactory's default Information cap would pre-drop Debug.
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(new HubLoggerProvider(
                viewModel.Telemetry, file,
                () => ToLogLevel(monitor.CurrentValue.Diagnostics.MinimumLogLevel),
                () => monitor.CurrentValue.Diagnostics.EnableInternalLogging));
        });

        DiagnosticsLog.LoggerFactory = factory;

        // A missing icon key degrades to its fallback and keeps working, which is the point — but silent
        // degradation that nobody ever notices is how a whole set rots after a CS2 update. The catalogue
        // fires this ONCE per distinct key, so a key hit every frame costs one log line, not a flood.
        ILogger icons = DiagnosticsLog.CreateLogger("App.Icons");
        IconCatalogue.MissingKeyObserver = icons.IconKeyMissing;
    }

    // LiveSyncLogLevel is a 1:1 value mirror of MEL LogLevel, but map explicitly rather than cast.
    private static LogLevel ToLogLevel(LiveSyncLogLevel level) => level switch
    {
        LiveSyncLogLevel.Trace => LogLevel.Trace,
        LiveSyncLogLevel.Debug => LogLevel.Debug,
        LiveSyncLogLevel.Information => LogLevel.Information,
        LiveSyncLogLevel.Warning => LogLevel.Warning,
        LiveSyncLogLevel.Error => LogLevel.Error,
        LiveSyncLogLevel.Critical => LogLevel.Critical,
        _ => LogLevel.None
    };

    /// <summary>
    ///     Central theme system: installs the theme registry and applies the
    ///     persisted theme at startup, keeping it live. Order matters: the registry's custom-variant override
    ///     dictionaries (built-in High-Contrast / E-Girl + any user drop-in) are merged into
    ///     <c>Application.Resources</c> by <c>Install</c> BEFORE <c>ApplyTheme</c> sets the variant, so a
    ///     persisted CUSTOM theme resolves its tokens at launch instead of falling through to its base palette.
    ///     <c>ApplyTheme</c> maps <see cref="AppSettings.Theme" /> (a theme <b>id</b>) via
    ///     <see cref="ThemeRegistry.VariantFor" /> onto <c>RequestedThemeVariant</c> (case-insensitive; an
    ///     unknown id → <c>Default</c> = follow the OS). The palette (DynamicResource over the
    ///     ThemeDictionaries) and the FluentTheme both re-resolve on the change, and the code-held surfaces
    ///     (2D playback viewport, Analysis graph, syntax highlighter) repaint on their
    ///     <c>ActualThemeVariantChanged</c> hooks, so switching themes in Settings re-themes the running app
    ///     with no restart.
    /// </summary>
    // internal so AppCompositionRootTests can drive the real startup path (Reload → Install → ApplyTheme +
    // the Reloaded → repaint subscription). Otherwise the launch-time theme wiring is untested.
    internal static void WireTheme(IServiceProvider services)
    {
        ThemeRegistry registry = services.GetRequiredService<ThemeRegistry>();
        // Ensure the drop-in folder exists (so users have somewhere to add themes), then scan it BEFORE
        // Install merges the override dictionaries, so a persisted drop-in theme resolves at launch. (No
        // subscribers yet, so this startup reload paints nothing extra. ApplyTheme below does the initial paint.)
        AppPaths.EnsureThemesDirectory();
        AppPaths.EnsureZonesDirectory();
        registry.Reload();
        registry.Install(Current!);

        IOptionsMonitor<AppSettings> monitor = services.GetRequiredService<IOptionsMonitor<AppSettings>>();
        ApplyTheme(registry, monitor.CurrentValue.Theme);
        // OnChange fires synchronously on the UI thread for a self-write (Write → Reload) and on a
        // threadpool thread for an external file edit; Post marshals both to the UI thread, where
        // RequestedThemeVariant must be assigned. The subscription lives for the app's lifetime (App is
        // app-scoped), so it is intentionally not disposed.
        monitor.OnChange(s => Dispatcher.UIThread.Post(() => ApplyTheme(registry, s.Theme)));

        // A LATER Reload() (the Settings "Reload themes" affordance) repaints the running app: an edit to the
        // active theme's drop-in changes its tokens WITHOUT changing the variant, so nothing re-resolves on its
        // own. RepaintForThemeReload forces it. Subscribed AFTER the startup reload so only user-triggered
        // reloads repaint. App is app-scoped, so the handler is intentionally not unsubscribed.
        registry.Reloaded += (_, _) => RepaintForThemeReload(registry, monitor.CurrentValue.Theme);
    }

    private static void ApplyTheme(ThemeRegistry registry, string? theme) =>
        Current!.RequestedThemeVariant = registry.VariantFor(theme);

    /// <summary>
    ///     Repaints the running app after a theme reload. The syntax highlighter caches its definition per
    ///     variant, so an edit to the active variant would otherwise stay stale. <c>ClearCache</c> drops it.
    ///     Then it BOUNCES the active variant (→ <c>Default</c> → back): a same-variant re-apply short-circuits,
    ///     but the bounce forces every <c>{DynamicResource}</c> and the code-held surfaces (2D viewport, Analysis
    ///     graph, syntax highlighter, all of which repaint on <c>ActualThemeVariantChanged</c>) to re-resolve
    ///     against the reloaded override dictionaries. Proven by <c>ThemeReloadTests</c>.
    /// </summary>
    private static void RepaintForThemeReload(ThemeRegistry registry, string? activeThemeId)
    {
        if (Current is null)
        {
            return;
        }

        WorkbenchYamlHighlighting.ClearCache();
        ThemeVariant active = registry.VariantFor(activeThemeId);
        Current.RequestedThemeVariant = ThemeVariant.Default;
        Current.RequestedThemeVariant = active;
    }

    // A store's startup read: a light queue item ahead of background work, so it shows in the queue list.
    private static Func<Action, Task> StartupLoad(IServiceProvider sp, string title, string owner) =>
        load => QueueWork.Run(sp.GetRequiredService<IDemoProcessingQueue>(), QueueJobKind.StoreLoad, title, owner, _ => load(),
            DemoJobPriority.UserRequested);

    /// <summary>
    ///     Builds the app's single composition root: a bare Microsoft.Extensions DI container (NO
    ///     Microsoft.Extensions.Hosting). It owns the long-lived <see cref="SettingsService" />, a live
    ///     <c>reloadOnChange</c> ConfigurationRoot / file watcher, hence a SINGLETON, binds
    ///     <see cref="AppSettings" /> to its <c>IConfiguration</c> so <c>IOptionsMonitor&lt;AppSettings&gt;</c>
    ///     is injectable AND reflects <c>Write()→Reload()→OnChange</c> live, and constructs + HOLDS the
    ///     <see cref="ModuleRegistry" /> as a singleton. Both hosts
    ///     (desktop + WASM) call this with their host-specific <see cref="IWindowService" />.
    ///     <para>
    ///         <c>internal</c> so <c>AppCompositionRootTests</c> can build the real container and prove it
    ///         resolves. A bad/missing registration otherwise crashes only at first launch. Always invoked
    ///         on the UI thread (framework-init on desktop, the headless dispatcher in tests) because
    ///         <c>ValidateOnBuild</c> eagerly constructs the singleton <see cref="MainViewModel" /> (which
    ///         starts a <c>DispatcherTimer</c>) at build time.
    ///     </para>
    /// </summary>
    internal static ServiceProvider BuildServices(IWindowService windowService) =>
        BuildServices(windowService, FeaturePacks.Compatible);

    /// <summary>
    ///     <see cref="BuildServices(IWindowService)" /> over an explicit pack list. The packs' descriptors
    ///     join the <see cref="FeatureCatalog" /> here, once; each pack then registers its services and,
    ///     in <see cref="BuildRegistry" />, contributes its modules.
    /// </summary>
    internal static ServiceProvider BuildServices(IWindowService windowService, IReadOnlyList<IExtension> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);
        FeatureCatalog.Compose(packs);
        ServiceCollection services = ComposeServices(windowService, packs);

        // ValidateOnBuild: a missing/broken registration fails HERE (a loud construction error on the UI
        // thread at framework-init) instead of silently at the first GetRequiredService<MainViewModel>().
        // It eagerly constructs the singletons, all of which the shell resolves immediately anyway, so
        // there is no extra side-effect beyond building them a few lines earlier, on the same UI thread.
        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true
        });
        QueueWork.Ambient = provider.GetRequiredService<IDemoProcessingQueue>();
        // Force-construct the scheduler so its wiring side-effect (library.Scheduler = it) runs before any
        // rescan, independent of ValidateOnBuild's eager-construction behavior. ValidatePasses populates and
        // sorts the pass registry right away, so a cycle or an unknown After id fails here, loudly, at
        // startup, instead of waiting for the first plan to find it.
        provider.GetRequiredService<DemoScheduler>().ValidatePasses();
        // Each pack whose feature id resolves on gets its lifecycle's startup loads. A pack that is off
        // never resolves its lifecycle, so none of this runs: no index load, no Team Identity rebuild, no
        // queue item.
        StartPacks(provider);
        Services = provider;
        return provider;
    }

    /// <summary>
    ///     Starts the <see cref="PackSwitch" />: each pack whose <see cref="IExtension.FeatureId" /> resolves
    ///     on gets its <see cref="IExtensionLifecycle.OnEnabledAsync" /> (fire-and-forget: the loads are queue
    ///     items), and from then on the gate's <see cref="IFeatureGate.Changed" /> drives the lifecycle both
    ///     ways. On a fresh desktop install nothing starts until the first-run wizard has asked. Shutdown
    ///     does not read anything this records: every pack's lifecycle gets an unconditional
    ///     <see cref="IExtensionLifecycle.OnShutdown" /> instead, each deciding for itself what it actually built.
    /// </summary>
    // Lives for the process: it watches the UI thread until exit.
    private static UiWatchdog? _watchdog;

    // Lives for the process: the UI-thread, unobserved-task and binding backstops for extensions.
    private static IDisposable? _backstops;

    internal static void StartPacks(IServiceProvider provider) => provider.GetRequiredService<PackSwitch>().Start();

    /// <summary>
    ///     Every registration of the composition root, before the provider is built: the core services,
    ///     then each pack's <see cref="IExtension.Register" />. Kept apart from
    ///     <see cref="BuildServices(IWindowService, IReadOnlyList{IExtension})" /> so a test can enumerate
    ///     what is registered without constructing the singletons.
    /// </summary>
    internal static ServiceCollection ComposeServices(IWindowService windowService, IReadOnlyList<IExtension> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ServiceCollection services = new();

        // SINGLETON via a constructed instance: SettingsService holds a live reloadOnChange
        // ConfigurationRoot / file watcher and must outlive any single resolve. It is constructed eagerly
        // because `Configure<AppSettings>(IConfiguration)` binds to its live Configuration below, the
        // registration that installs the change-token source that makes IOptionsMonitor.OnChange fire on a
        // Write()→Reload(). WASM degrades to an in-memory provider (no filesystem) inside the ctor.
        SettingsService settings = new();
        services.AddSingleton(settings);
        // Before anything can write settings.json: its writes drop the keys a first-party extension moved out.
        LegacyExtensionSettings.Import(settings, AppPaths.ConfigRoot);
        services.Configure<AppSettings>(settings.Configuration);

        // The feature gate resolves per-category show/hide from FeatureCatalog + the live
        // AppSettings overrides. SINGLETON because it holds the IOptionsMonitor.OnChange subscription;
        // registered AFTER Configure<AppSettings> so IOptionsMonitor<AppSettings> is available to its ctor.
        // Type-based (not a factory lambda) so ValidateOnBuild covers its constructor call site. A broken
        // resolution then fails loudly here rather than at first use in the UI enforcement.
        services.AddSingleton<IFeatureGate, FeatureGate>();

        // The fault tracker the desktop head built before Avalonia started, or one over these packs in a
        // host that did not. The gate takes it, so a failing extension is switched off through the gate.
        ExtensionFaults faults = ExtensionFaults.Current ?? ExtensionFaults.For(packs);
        services.AddSingleton(faults);

        // The central theme registry: the single source of truth for the
        // available themes: native dark / light / system plus the built-in custom variants (High-Contrast,
        // E-Girl) and any user drop-in from <config>/themes/. SINGLETON because it OWNS the one merged
        // custom-variant override dictionary installed into Application.Resources (App.WireTheme), and both
        // the Settings picker and WireTheme must resolve themes from that same instance.
        services.AddSingleton<ThemeRegistry>();

        // The host window service instance (desktop real / browser no-op), resolved by MainViewModel.
        services.AddSingleton(windowService);

        // Settings screen VM (P2a-i): registered as a MANUAL-new FACTORY, not AddTransient. A transient
        // IDisposable resolved from the ROOT provider is captured by the root and only released at app exit;
        // this factory instead hands ownership to whoever opens Settings (the window service disposes the VM
        // on window-close / overlay-clear), so a fresh VM per open leaks nothing. Its live deps come from the
        // container so a self-write and an external edit both flow through the one SettingsService.
        services.AddSingleton<Func<SettingsViewModel>>(sp => () =>
        {
            SettingsViewModel settingsVm = new(
                sp.GetRequiredService<SettingsService>(),
                sp.GetRequiredService<IOptionsMonitor<AppSettings>>(),
                sp.GetRequiredService<IFeatureGate>(),
                sp.GetRequiredService<ThemeRegistry>(),
                // Replay-walkthrough starter: resolves the singleton shell lazily (never at ctor time, which
                // would recurse through the shell factory). Null-safe if the shell isn't built yet.
                () => Services?.GetService<MainViewModel>()?.StartWalkthrough(),
                // The settings pages the packs contribute: the Suggested Tags tuning card and the
                // Grenade Index card. Read fresh on every Settings open, same as the pages' own VMs.
                sp.GetRequiredService<PackContributionSet>().SettingsPages,
                // The Extensions "N demos will be re-indexed" notice's count; SettingsViewModel
                // watches only the first entry.
                sp.GetRequiredService<PackContributionSet>().ReindexEstimates,
                // Each pack's "delete extension data" action, one row per entry.
                sp.GetRequiredService<PackContributionSet>().DataRemovals,
                // Every declared pack's compatibility verdict: versions, and the locked row with
                // the reason for a pack that did not compose.
                FeaturePacks.Statuses,
                // The extension updater; absent where there is no config root to stage into.
                sp.GetService<ExtensionUpdateService>());
            // Says which extensions were switched off this session after errors.
            settingsVm.AttachExtensionFaults(sp.GetRequiredService<ExtensionFaults>(),
                AppPaths.LogsDir is { } logs ? () => Services?.GetService<MainViewModel>()?.OpenOutputFolder(logs) : null);
            return settingsVm;
        });

        // The extension updater: checks each extension's feed, stages a
        // newer version under <config root>/extensions/ for the loader to take at the next start. Judged by
        // the same trust policy the loader runs in Main. Desktop only: the browser has no config root.
        if (AppPaths.ConfigRoot is { } extensionsConfigRoot)
        {
            // Only the extensions this app ships update from its feed; a third-party one is the user's to replace.
            services.AddSingleton(sp => new ExtensionUpdateService(
                extensionsConfigRoot,
                [.. FeaturePacks.Statuses.Where(st => st.Source is not PackSource.External)],
                ExtensionHost.Current,
                TrustPolicy.Default,
                HttpExtensionFeedClient.Shared,
                id => ExtensionFeedSource.Resolve(sp.GetRequiredService<IOptionsMonitor<AppSettings>>().CurrentValue.Extensions.FeedUrl, id),
                sp.GetRequiredService<IDemoProcessingQueue>()));
        }

        // First-run wizard VM (P2b), a manual-new FACTORY (same rationale as the Settings factory): a fresh
        // VM per open, owned by whoever shows it. It only needs the live SettingsService (it seeds from and
        // writes through the one singleton). Used by BOTH the launch trigger and Settings' relaunch command.
        services.AddSingleton<Func<FirstRunWizardViewModel>>(sp => () =>
            new FirstRunWizardViewModel(sp.GetRequiredService<SettingsService>()));

        // The machine-wide ONE-heavy-parse gate:
        // the concurrency BACKSTOP the queue's workers and the shell's interactive load coordinate
        // through (interactive preempts background; reel sessions exclude both). MaxConcurrency default 1.
        services.AddSingleton<HeavyJobGate>();

        // The global demo-processing queue: the single source all background
        // demo parse/analyse work is pulled from, plus the awaitable highest-priority foreground open.
        // SINGLETON: it owns the worker loops and the observable item set the UI binds to. Its three
        // persisted settings (max concurrency / max queue size / background-enable) are applied from the
        // live AppSettings and re-applied on change (self-writes fire OnChange inline; external edits on a
        // threadpool thread, the queue setters are all lock-guarded, so either is safe). The OnChange
        // callback is rooted by the singleton IOptionsMonitor for the app's lifetime; nothing to dispose.
        // The one rules read highlights and round facts share, and the queue's forward pass over it. An entry
        // is read forward when every owner on it can take a forward pass; Browser keeps the retained parse.
        // round_facts is a core stamped ruleset: always on, it rides every merged run, and it stays out of
        // the highlights fingerprint because its rows are stamped under its own identity.
        // An extension's ruleset is a stamped ruleset gated by its feature, read between the shipped rules and the
        // user's. A ruleset an extension's manifest claims but this launch did not contribute (safe mode, a failed
        // load) stays stamped and off, so a user override of it never joins the highlights set.
        services.AddSingleton(sp =>
        {
            IFeatureGate? gate = sp.GetService<IFeatureGate>();
            Lazy<IReadOnlyList<ContributedRuleset>> contributed =
                new(() => sp.GetRequiredService<PackContributionSet>().Rulesets, LazyThreadSafetyMode.ExecutionAndPublication);
            return new MergedRulesBuild(() => MergedRulesBuild.LoadShippedExtensionsUser(contributed.Value), () =>
            [
                StampedRuleset.Core(RoundFactsFingerprint.RulesetId),
                .. contributed.Value.Select(c => new StampedRuleset(c.RulesetId, c.Owner, () => gate?.IsEnabled(c.FeatureId) ?? true)),
                .. FeaturePacks.ClaimedRulesets.Select(id => new StampedRuleset(id, StampedRuleset.ClaimedOwner, static () => false))
            ]);
        });
        services.AddSingleton(sp =>
        {
            // A read records the tables of the stamped rulesets whose stored outputs are stale for the demo.
            MergedRulesBuild rules = sp.GetRequiredService<MergedRulesBuild>();
            ForwardPassRunner? forward = OperatingSystem.IsBrowser()
                ? null
                : new ForwardPassRunner(rules)
                {
                    OutputsFor = path => rules.StampedOutputs(id => string.Equals(id, RoundFactsFingerprint.RulesetId, StringComparison.Ordinal)
                        ? sp.GetRequiredService<RoundFactsEvaluator>().Records(path)
                        : sp.GetRequiredService<FactsEvaluator>().Records(path, id))
                };
            DemoProcessingQueue queue = new(
                sp.GetRequiredService<HeavyJobGate>(),
                action => Dispatcher.UIThread.Post(action),
                forwardPass: forward is null ? null : forward.Run,
                parseReleased: sp.GetRequiredService<MergedRulesBuild>().Forget,
                // DI-free, like CommandRegistry.Build(packs): reads IExtension.JobKinds directly, no
                // PackContributionSet, so building the queue can never re-enter its own DI resolution
                // through a pack's Contribute (e.g. ReviewQueue resolves IDemoProcessingQueue eagerly).
                jobKinds: sp.GetRequiredService<JobKindRegistry>(),
                // Resolved at the first read, never while the queue is built.
                contentHash: path => sp.GetRequiredService<DemoCacheStore>().TryGetIndex(path)?.Sha256);
            IOptionsMonitor<AppSettings>? monitor = sp.GetService<IOptionsMonitor<AppSettings>>();
            if (monitor is not null)
            {
                void Apply(AppSettings s)
                {
                    queue.MaxConcurrency = s.ProcessingQueue.MaxConcurrency;
                    queue.MaxQueueSize = s.ProcessingQueue.MaxQueueSize;
                    queue.BackgroundEnabled = s.ProcessingQueue.BackgroundProcessingEnabled;
                }

                Apply(monitor.CurrentValue);
                monitor.OnChange(Apply);
            }

            return queue;
        });
        services.AddSingleton<IDemoProcessingQueue>(sp => sp.GetRequiredService<DemoProcessingQueue>());
        services.AddSingleton(sp => new FirstPartyExports(sp.GetRequiredService<HeavyJobGate>(),
            sp.GetRequiredService<IDemoProcessingQueue>(), sp.GetRequiredService<SettingsService>()));
        services.AddSingleton(sp => new FirstPartyHost(sp.GetRequiredService<SettingsService>()));

        // The demo-library indexer: the one internally-new'd store routed through the container, because
        // it now reads its folders from AppSettings.Library.Folders and writes them back via SettingsService.
        // Its tier-2 full parses run through the DemoScheduler (registered below), not the queue directly
        // ("one parse, many evaluators").
        // The unified demo-information cache. Registered
        // ahead of the indexer because the indexer dual-writes tier 2 into it.
        services.AddSingleton(_ =>
        {
            DemoCacheStore store = new(
                AppPaths.DemoCacheDir,
                action => Dispatcher.UIThread.Post(action));

            // One-shot copy of library.json + highlights.json into the unified cache. Marker-gated, so this
            // is a no-op on every launch after the first, and merge-only, so it never clobbers the fresher
            // data the indexer's dual-write may already have produced. It deliberately leaves the legacy
            // files in place. DemoLibraryService still reads library.json on construction.
            LegacyCacheMigration.Run(store, AppPaths.LibraryCacheFile, AppPaths.HighlightsCacheFile);
            return store;
        });

        services.AddSingleton(sp => new DemoLibraryService(
            settings: sp.GetRequiredService<SettingsService>(),
            demoCache: sp.GetRequiredService<DemoCacheStore>()));

        // Highlights pipeline: the library-wide cache store and the
        // scanner over it. The scanner's library universe is the indexer's current entries; the
        // background-scan opt-in is read live from settings; UI marshalling via the dispatcher.
        services.AddSingleton<IHighlightHarvester>(sp => new RulesHighlightHarvester(sp.GetRequiredService<MergedRulesBuild>()));
        // The scan chip's mapper. A container singleton so the shell (which registers the chip into
        // the status strip) and the Reels tab (which shows the same state inline) share ONE instance.
        // The Reels tab VM is a container SINGLETON: the module framework already caches one instance per
        // descriptor, and Match Overview's [ + ] must reach that same tray whether or not the tab has ever
        // been activated. Resolved lazily on both sides, so nothing constructs it at startup.
        services.AddSingleton(sp =>
        {
            MainViewModel Shell()
            {
                return sp.GetRequiredService<MainViewModel>();
            }

            return new HighlightsTabViewModel(
                sp.GetRequiredService<DemoCacheStore>(),
                sp.GetRequiredService<HighlightScanService>(),
                sp.GetService<IOptionsMonitor<AppSettings>>(),
                sp.GetRequiredService<SettingsService>(),
                // Generate hands off to the background reel service. Null on
                // Browser/tests → the primary degrades to a disabled control with a tip saying why.
                Shell().ReelJob,
                // Single-CS2 interlock: a live sync session owns the game, so a render must ask first.
                () => Shell().LiveSync?.State.IsSessionActive ?? false,
                // Platform mode: macOS can plan a reel but not capture one.
                OperatingSystem.IsMacOS(),
                null,
                // Passed rather than assigned, so the ENCODING section re-reconciles when the user
                // toggles highlights.encoding in Settings. A one-shot assignment would leave the section
                // wrong until the tab was rebuilt.
                sp.GetService<IFeatureGate>(),
                // v0.6.0 ffmpeg pre-flight (Services/Dependencies): detect up front and guide the
                // user to a self-install, instead of a raw CSVG failure after CS2 launches. Real
                // probe ONLY here. The VM's null default keeps pure-VM tests machine-independent.
                FfmpegDependency.Locate,
                // The tray is a client of the Review Queue: what it stages is queued there.
                sp.GetRequiredService<ReviewQueue>())
            {
                // The SAME instances the status-strip chips are bound to.
                JobStatus = Shell().ReelJobStatus,
                ScanStatus = sp.GetRequiredService<HighlightScanStatusViewModel>()
            };
        });

        services.AddSingleton(sp => new HighlightScanStatusViewModel(
            sp.GetRequiredService<HighlightScanService>(),
            sp.GetRequiredService<DemoCacheStore>()));
        services.AddSingleton(sp =>
        {
            DemoLibraryService library = sp.GetRequiredService<DemoLibraryService>();
            IOptionsMonitor<AppSettings>? monitor = sp.GetService<IOptionsMonitor<AppSettings>>();
            return new HighlightScanService(
                sp.GetRequiredService<DemoCacheStore>(),
                sp.GetRequiredService<IHighlightHarvester>(),
                () => [.. library.Entries.Select(e => e.FilePath)],
                () => monitor?.CurrentValue.Highlights.BackgroundScan ?? false,
                action => Dispatcher.UIThread.Post(action));
        });

        // Round Facts: the per-round, per-side record 2D Playback tints by and the Strat Book filters on. The
        // evaluator rides the demo's visit after the highlight scan (no second parse) and writes the rows onto
        // the record under the round_facts ruleset's own fingerprint; the source is the read API over them.
        // The row source and the identity share the one merged build, so rows are always stored under the
        // fingerprint of the doc that produced them. Always on: 2D Playback reads the rows for every user.
        services.AddSingleton(sp => new RulesRoundFactsRulesetIdentity(sp.GetRequiredService<MergedRulesBuild>()));
        services.AddSingleton<IRoundFactsRulesetIdentity>(sp => sp.GetRequiredService<RulesRoundFactsRulesetIdentity>());
        services.AddSingleton<IRoundFactsRowSource>(sp =>
            new EngineRoundFactsRowSource(sp.GetRequiredService<RulesRoundFactsRulesetIdentity>()));
        services.AddSingleton(sp => new RoundFactsEvaluator(
            sp.GetRequiredService<DemoCacheStore>(),
            sp.GetRequiredService<IRoundFactsRowSource>(),
            sp.GetRequiredService<IRoundFactsRulesetIdentity>(),
            action => Dispatcher.UIThread.Post(action)));
        services.AddSingleton<IRoundFactsSource>(sp => new RoundFactsSource(
            sp.GetRequiredService<DemoCacheStore>(),
            sp.GetRequiredService<RoundFactsEvaluator>()));

        // Every other stamped ruleset's tables, stored per demo as library facts by one core pass on the same
        // merged run, and the read API over them, the highlights and Round Facts that extensions get.
        services.AddSingleton(sp => new StampedFacts(sp.GetRequiredService<MergedRulesBuild>()));
        services.AddSingleton(sp => new FactsEvaluator(
            sp.GetRequiredService<DemoCacheStore>(),
            sp.GetRequiredService<StampedFacts>(),
            action => Dispatcher.UIThread.Post(action)));
        services.AddSingleton(sp => new AnalysisFacts(
            sp.GetRequiredService<DemoCacheStore>(),
            sp.GetRequiredService<StampedFacts>(),
            sp.GetRequiredService<IRoundFactsSource>()));

        // The Review Queue: every surface's clips in one ordered list, review-queue.json beside
        // teams.json. One per process, because the Reels tray, the Result Cards and the Review tab must
        // all mutate the same list. Null config root (the browser) keeps it for the session.
        services.AddSingleton(sp => new ReviewQueue(AppPaths.ConfigRoot,
            scheduleSave: QueueWork.Saves(sp.GetRequiredService<IDemoProcessingQueue>(), "Save: review queue", "review", "save:review-queue"),
            scheduleLoad: StartupLoad(sp, "Load: review queue", "review")));
        // The "one parse, many evaluators" scheduler: the single submitter that asks the registered passes
        // about a demo and submits them together as ONE visit, so the demo is read once. Library,
        // Highlights and Round Facts are core, always on the visit; a pack's passes come from a PassRegistry built over its
        // Pass contributions, ordered by declared After ids rather than a hand-written array. The registry
        // reads PackContributionSet lazily, on the scheduler's first plan, not here: resolving it during
        // this factory would run every pack's Contribute() during container build, well before anything
        // needs it. A disabled pack's pass factories are never invoked here, so those services are not
        // constructed by THIS path while the pack is off (a resident like SituationIndex or GrenadeIndex
        // may still resolve one directly at StartPacks time; each such evaluator sets its own .Scheduler in
        // its own factory, below). The whole-library re-check reads the unified cache's index, which is
        // worker-readable. The ORDER is a contract (the round index reads the round facts written in the
        // same visit) and is pinned by AppCompositionRootTests.
        services.AddSingleton(sp =>
        {
            DemoLibraryService library = sp.GetRequiredService<DemoLibraryService>();
            HighlightScanService highlights = sp.GetRequiredService<HighlightScanService>();
            DemoCacheStore cache = sp.GetRequiredService<DemoCacheStore>();
            RoundFactsEvaluator roundFacts = sp.GetRequiredService<RoundFactsEvaluator>();
            FactsEvaluator facts = sp.GetRequiredService<FactsEvaluator>();

            PassRegistry registry = new();
            registry.AddCoreEvaluator(library.Id, () => library);
            registry.AddCoreEvaluator(highlights.Id, () => highlights);
            registry.AddCoreEvaluator(roundFacts.Id, () => roundFacts, highlights.Id);
            registry.AddCoreEvaluator(facts.Id, () => facts, highlights.Id);

            // Each pack pass's extension, so a throw from any call into it counts against that extension.
            // Filled when the registry populates, before anything can fault.
            ConcurrentDictionary<string, ExtensionGuard> guardOf = new(StringComparer.Ordinal);

            DemoScheduler scheduler = new(
                registry.Resolve,
                sp.GetRequiredService<IDemoProcessingQueue>(),
                () => cache.Index.Select(e => e.Path),
                registry.Validate)
            {
                Faulted = (id, _, ex) =>
                {
                    if (guardOf.TryGetValue(id, out ExtensionGuard? guard))
                    {
                        guard.Report("pass " + id, ex);
                    }
                }
            };
            library.Scheduler = scheduler;
            highlights.Scheduler = scheduler;
            scheduler.Records = sp.GetRequiredService<RecordPassRunner>();

            registry.AddPacksLazily(() =>
            {
                IFeatureGate? features = sp.GetService<IFeatureGate>();
                foreach (PackContributions contributions in sp.GetRequiredService<PackContributionSet>().Packs)
                {
                    IExtension pack = contributions.Pack;
                    ExtensionGuard guard = contributions.Guard;
                    foreach (PassContribution contribution in contributions.Passes)
                    {
                        // .Scheduler (where the evaluator type has one) is set by the evaluator's own DI
                        // factory, not here: a wrapper assignment only runs once something has already
                        // planned, but GrenadeIndexEvaluator can also be built earlier, through GrenadeIndex
                        // at StartPacks time.
                        guardOf[contribution.Id] = guard;
                        string id = contribution.Id;
                        registry.AddPackPass(contribution.Id, contribution.Factory, contribution.After,
                            () => features?.IsEnabled(pack.FeatureId) ?? true,
                            ex => guard.Report("build pass " + id, ex));
                    }
                }
            });

            return scheduler;
        });

        // The record passes: work over what the cache holds, no demo read. Fed by the cache's own change
        // events; a pack's passes are read from its contributions on the first run, and asked only while
        // the pack is on.
        services.AddSingleton(sp =>
        {
            DemoCacheStore cache = sp.GetRequiredService<DemoCacheStore>();
            IDemoProcessingQueue queue = sp.GetRequiredService<IDemoProcessingQueue>();
            Lazy<IReadOnlyList<(IExtension Pack, Func<IRecordPass> Factory)>> contributed = new(() =>
            [
                .. sp.GetRequiredService<PackContributionSet>().Packs.SelectMany(c => c.RecordPasses.Select(r =>
                    (c.Pack, ExtensionRecordPassHost.Cached(r, () => HostLibrary.For(cache, queue, sp.GetRequiredService<AnalysisFacts>)))))
            ]);
            IFeatureGate? features = sp.GetService<IFeatureGate>();
            // An extension switched off takes its rulesets' facts off every library row; readers hear it as one change.
            if (features is not null)
            {
                features.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
                    HostLibrary.For(cache, queue, sp.GetRequiredService<AnalysisFacts>).RecheckFacts());
            }

            return new RecordPassRunner(() =>
            {
                List<IRecordPass> passes = [];
                foreach ((IExtension pack, Func<IRecordPass> factory) in contributed.Value)
                {
                    if (!(features?.IsEnabled(pack.FeatureId) ?? true))
                    {
                        continue;
                    }

                    try
                    {
                        passes.Add(factory());
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        sp.GetRequiredService<ExtensionFaults>().GuardFor(pack).Report("build record pass", ex);
                    }
                }

                return passes;
            }, cache, queue)
            {
                Faulted = (pass, _, ex) =>
                {
                    if (pass is ExtensionRecordPassHost host)
                    {
                        host.Guard.Report("record pass " + pass.Id, ex);
                    }
                }
            };
        });

        // Recently-opened-demos store. SINGLETON: it holds the live in-memory recents list
        // that both the shell (records on open) and the Library tab (binds + prunes) share. Persists
        // to the Recents section of the single consolidated config file via the shared SettingsService (no-op
        // on WASM). Factory-registered (mirrors DemoLibraryService) so there is no ambiguity over its optional
        // ctor param.
        services.AddSingleton(sp => new RecentFilesStore(sp.GetRequiredService<SettingsService>()));

        // The queue and every extension's job view read one registry.
        services.AddSingleton(_ => JobKindRegistry.Build(packs));

        // Each extension's host context, keyed by its id, and the hub that hands them the shell once built.
        services.AddSingleton<ExtensionShellHub>();
        services.AddSingleton<IFirstPartyShellState>(sp => sp.GetRequiredService<ExtensionShellHub>());
        services.AddSingleton<IFirstPartyExportChips>(sp => sp.GetRequiredService<ExtensionShellHub>());
        services.AddSingleton(sp => new NotificationCenter(static a => Dispatcher.UIThread.Post(a), sp.GetService<IFeatureGate>()));
        foreach (IExtension pack in packs)
        {
            IExtension owner = pack;
            services.AddKeyedSingleton<IExtensionContext>(owner.Id, (sp, _) => new ExtensionContext(owner, sp));
        }

        // Each pack's own registrations, unconditional: factories are lazy, and the gate decides what runs,
        // not what is registered. Each runs into a scratch copy of the container first, so a Register that
        // throws partway leaves nothing half-registered: the extension does not start this session and the
        // rest of the app does. The copy holds the host's registrations, so TryAdd sees them, and only what
        // the pack added comes back: a Replace or RemoveAll in Register never reaches the host's own.
        foreach (IExtension pack in packs)
        {
            ServiceCollection scratch = new();
            foreach (ServiceDescriptor existing in services)
            {
                ((IServiceCollection)scratch).Add(existing);
            }

            HashSet<ServiceDescriptor> before = new(scratch, ReferenceEqualityComparer.Instance);
            try
            {
                pack.Register(scratch);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                faults.FailStartup(faults.GuardFor(pack).Scope, "register", ex);
                continue;
            }

            foreach (ServiceDescriptor descriptor in scratch)
            {
                if (!before.Contains(descriptor))
                {
                    ((IServiceCollection)services).Add(descriptor);
                }
            }
        }

        // Every pack's Contribute, run once on first resolve: the module registry reads the modules, the
        // merged rules build the ruleset claims.
        services.AddSingleton(sp => new PackContributionSet(packs, sp));

        // The live toggle: started by StartPacks, driven by the gate's Changed after that. The re-check after
        // a switch-on asks the scheduler to plan every demo again, run as a queue item. The browser never
        // shows the wizard (NeedsFirstRun is always true there), so it never waits for it.
        services.AddSingleton(sp => new PackSwitch(packs,
            sp.GetRequiredService<IFeatureGate>(),
            pack => sp.GetKeyedService<IExtensionLifecycle>(pack.Id),
            sp.GetRequiredService<IDemoProcessingQueue>(),
            () => sp.GetRequiredService<DemoScheduler>().RecheckAll(),
            () => !OperatingSystem.IsBrowser() && sp.GetRequiredService<SettingsService>().NeedsFirstRun,
            sp.GetRequiredService<ExtensionFaults>()));

        // The first-party module registry, built ONCE by BuildRegistry and held by the container (the
        // reconciliation), injected into the shell so there is no stray second construction. The provider is
        // passed so BuildRegistry can DI-resolve module deps (the Highlights cache/scanner) + defer the
        // shell-bound delegates (never eagerly resolving MainViewModel here, which would recurse through
        // ModuleRegistry).
        services.AddSingleton(sp => BuildRegistry(sp, packs));

        // The shell, constructed by an explicit factory so DI does not auto-fill every optional ctor param:
        // only the deps it needs are supplied; the rest default. The feature gate is
        // handed in so the shell FILTERS the workspace tab strip per user category (and reconciles live on
        // IFeatureGate.Changed). A null gate (the designer / unit-test path) fails open: no tab filtering.
        services.AddSingleton(BuildShell);
        return services;
    }

    /// <summary>
    ///     Constructs the singleton shell, refusing to do it re-entrantly.
    ///     <para>
    ///         A DI singleton is not cached until its factory RETURNS, so anything that resolves
    ///         <see cref="MainViewModel" /> while its constructor is still running gets a BRAND-NEW shell
    ///         rather than the one being built, and that shell repeats the same work, forever. There is no
    ///         <c>StackOverflowException</c> to stop it either: StackGuard keeps hopping to fresh threads,
    ///         so the process just pegs a core and grows the heap without bound while the UI thread never
    ///         returns to show a window. That shipped once (v0.5.0): the shell ctor ran
    ///         <c>RestoreSession</c>, which activated the persisted tab, whose activation resolved the
    ///         shell. Anyone who quit on the Highlights tab had an app that could never start again.
    ///     </para>
    ///     <para>
    ///         The structural fix is that the ctor no longer activates tabs. <c>RestoreSession</c> is
    ///         driven by the composition root AFTER this factory returns (see
    ///         <see cref="OnFrameworkInitializationCompleted" />). This guard is the tripwire that keeps it
    ///         that way: it turns a silent, unkillable hang into an immediate, readable error naming the
    ///         cause.
    ///     </para>
    /// </summary>
    private static MainViewModel BuildShell(IServiceProvider sp)
    {
        if (_shellUnderConstruction)
        {
            throw new InvalidOperationException(
                "Re-entrant MainViewModel resolution: the shell was resolved from the service provider "
                + "while its own constructor was still running, which would build shells without bound. "
                + "Something the ctor triggers is reaching for the shell — most likely a tab activation "
                + "(the ctor must activate NO tabs; session restore runs after construction) or a module "
                + "view-model invoking a shell-bound delegate instead of storing it for OnActivated.");
        }

        _shellUnderConstruction = true;
        try
        {
            MainViewModel shell = new(
                sp.GetRequiredService<IWindowService>(),
                sp.GetRequiredService<ModuleRegistry>(),
                sp.GetRequiredService<DemoLibraryService>(),
                sp.GetService<IOptionsMonitor<AppSettings>>(),
                sp.GetService<IFeatureGate>(),
                sp.GetRequiredService<RecentFilesStore>(),
                // The consolidated-config serializer owns the UI session-restore section (session.json is
                // folded into settings.json). Threaded in so SaveSession/RestoreSession use the single file.
                sp.GetRequiredService<SettingsService>(),
                // The heavy-parse gate (interactive load coordination)
                // and the highlight scanner (piggyback + open-demo harvest + start trigger wiring).
                sp.GetRequiredService<HeavyJobGate>(),
                sp.GetRequiredService<HighlightScanService>(),
                // The interactive open is submitted as the highest-priority
                // awaitable foreground request on the global queue.
                sp.GetRequiredService<IDemoProcessingQueue>(),
                // The open runs the demo's visit on its own parse, so an un-indexed library demo fills its
                // card from THAT parse, not a second one.
                sp.GetRequiredService<DemoScheduler>(),
                // Bundled tour sample (assets/tour): the Library hero's "Try a sample match" CTA and the
                // walkthrough gateway's empty-library target. Resolves null on WASM (no filesystem to walk).
                TourDemoLocator.FindSampleDemo,
                // The unified demo cache: what a Library single-click renders on Match Overview without
                // parsing anything.
                sp.GetRequiredService<DemoCacheStore>(),
                // The Library's filter/badge contributions (the Team filter, the provenance chip).
                sp.GetRequiredService<PackContributionSet>().LibraryContributions,
                // The hub tabs the packs contribute (the Strat Book hub); the shell builds its strip from them.
                sp.GetRequiredService<PackContributionSet>().HubTabs);

            // GetService<T> falls back to this container for a pack's by-type registrations. Wired here,
            // not in OnFrameworkInitializationCompleted, so a caller that never runs that path still gets it.
            if (shell.ModuleContext is ModuleContext moduleContext)
            {
                moduleContext.SetServices(sp);
                moduleContext.SetFaults(sp.GetService<ExtensionFaults>());
            }

            // The status chips the packs contribute. A post-construction call, not a ctor parameter: the
            // ctor parameter list is the next thing to edit.
            shell.AttachStatusChips(sp.GetRequiredService<PackContributionSet>().StatusChips);
            shell.AttachNotifications(sp.GetRequiredService<NotificationCenter>());

            sp.GetRequiredService<ExtensionShellHub>().Attach(shell);
            shell.AttachExtensionFaults(sp.GetRequiredService<ExtensionFaults>());

            // The extensions' Match Overview actions, re-asked whenever a feature switch moves.
            IFeatureGate? actionGate = sp.GetService<IFeatureGate>();
            shell.MatchOverviewTab.AttachDemoActions(sp.GetRequiredService<PackContributionSet>().DemoActions,
                id => actionGate?.IsEnabled(id) ?? true);
            if (actionGate is not null)
            {
                actionGate.Changed += (_, _) => shell.MatchOverviewTab.RefreshDemoActions();
            }

            return shell;
        }
        finally
        {
            _shellUnderConstruction = false;
        }
    }

    // The first-party module registry. BuiltInTabsModule is auto-registered by the
    // shell (it needs the shell + Diagnostics VM as DataContexts), so the composition root only adds
    // additional modules here. DELIBERATE: the production shell stays built-ins-only. PlaceholderModule
    // is NOT registered here (it would show an empty "Sandbox" tab to users; it exists to prove the
    // framework end-to-end and is registered by the test that exercises it). This path registers the
    // real 2D pilot, then asks each pack for its modules. Both hosts use this path (only first-party
    // modules on WASM). This is now invoked exactly once, by the DI factory that HOLDS the resulting
    // registry (see BuildServices).
    private static ModuleRegistry BuildRegistry(IServiceProvider sp, IReadOnlyList<IExtension> packs)
    {
        IOptionsMonitor<AppSettings>? settings = sp.GetService<IOptionsMonitor<AppSettings>>();
        ModuleRegistry registry = new();
        // The 2D Playback pilot. First-party, granted Playback.Control. Both desktop and browser hosts
        // use this path. The packs' playback contributions (band menus, panes) attach to each tab
        // view-model through the host, gated live by the pack's umbrella id.
        registry.Register(new Playback2DModule(() => PlaybackContributionHost.From(
            sp.GetRequiredService<PackContributionSet>(), sp.GetService<IFeatureGate>()), sp.GetRequiredService<IRoundFactsSource>));
        // The Rulesets v2 authoring Workbench.
        // Registered on both hosts; desktop-only features (editor save, FileSystemWatcher, code --goto)
        // gate at runtime via OperatingSystem.IsBrowser() as they land, so the WASM build compiles
        // and gets the read-only surface. The live-settings monitor threads through so the
        // Workbench's DeveloperMode gate is a live read of AppSettings.Features.DeveloperMode.
        // The extensions' rulesets show beside the files, read-only, so an author can read and override one.
        IFeatureGate? workbenchGate = sp.GetService<IFeatureGate>();
        registry.Register(new RuleWorkbenchModule(settings, () => sp.GetRequiredService<PackContributionSet>().Rulesets,
            c => workbenchGate?.IsEnabled(c.FeatureId) ?? true));

        // The Highlights browser. Registered on both hosts (WASM degrades:
        // the cache/scan are absent). The VM is delegate-injected (Library precedent): the
        // cache store / scanner / settings are DI singletons resolved now (no MainViewModel dependency, so
        // no ModuleRegistry recursion); the shell-bound behaviours (open-in-workspace + Live Sync verify)
        // are LAZY closures over the provider, invoked only at tab-activation, long after the shell exists.
        DemoCacheStore hlStore =
            sp.GetRequiredService<DemoCacheStore>();
        HighlightScanService hlScanner =
            sp.GetRequiredService<HighlightScanService>();
        SettingsService hlSettingsService = sp.GetRequiredService<SettingsService>();
        // The FOURTH StatusChip consumer. Resolved (not new'd) so the tab and the status strip
        // share one mapper; the chip can therefore appear while a background scan runs even if the user has
        // never opened the Reels tab, since module tab VMs are lazy. The shell owns its lifetime.
        HighlightScanStatusViewModel scanStatus = sp.GetRequiredService<HighlightScanStatusViewModel>();

        // The tab VM is a container singleton (see BuildServices) so Match Overview's [ + ] and the tab
        // itself share ONE tray. Still resolved lazily. The module only invokes this on first activation.
        registry.Register(new HighlightsModule(sp.GetRequiredService<HighlightsTabViewModel>));

        // Each pack's modules, in pack order, after the core modules. The pack owns which modules it
        // contributes and their order.
        foreach (PackContributions contributions in sp.GetRequiredService<PackContributionSet>().Packs)
        {
            IExtension pack = contributions.Pack;
            // Passes are consumed by the PassRegistry the DemoScheduler factory builds; job kinds by
            // JobKindRegistry.Build(packs), DI-free like CommandRegistry.Build.
            // CommandRegistry.Default reads IExtension.Commands directly (no DI, so a bare-constructed
            // view model resolves pack chords in a headless test too). This is the consumer for the
            // IExtensionContributions.Commands(...) call: not a second registration, a check that the two
            // channels agree (CommandRegistry.CommandsMatch) so they cannot drift apart.
            // A mismatch, or a Commands getter that throws, keeps the extension off for the session: its
            // keymap rows and its modules would disagree.
            ExtensionGuard guard = contributions.Guard;
            bool match = guard.Run("commands", () => CommandRegistry.CommandsMatch(contributions.ContributedCommands, [.. pack.Commands]),
                false);
            if (!match)
            {
                if (!guard.Faults.StartupFailed(pack.Id))
                {
                    guard.Faults.FailStartup(guard.Scope, "commands", new InvalidOperationException(
                        $"Pack '{pack.Id}' contributed different commands through Contribute than its Commands property declares."));
                }

                continue;
            }

            foreach (IWorkspaceModule module in contributions.Modules)
            {
                registry.Register(module);
            }
        }

        return registry;
    }

    // v0.6.0: applies a persisted geometry snapshot to the still-unshown MainWindow. Width/Height
    // are DIPs and clamp to the window minimums; Position is PHYSICAL pixels and is reused only
    // when a connected screen still contains it (a +40/+20 inset keeps the title bar reachable);
    // Maximized re-applies as state so un-maximizing lands on the restored Normal bounds.
    private static void ApplyWindowBounds(Window window, WindowBoundsState bounds)
    {
        window.Width = Math.Max(window.MinWidth, bounds.Width);
        window.Height = Math.Max(window.MinHeight, bounds.Height);

        if (bounds is { X: { } x, Y: { } y })
        {
            try
            {
                if (window.Screens.All.Any(s => s.Bounds.Contains(new PixelPoint(x + 40, y + 20))))
                {
                    window.Position = new PixelPoint(x, y);
                }
            }
            catch
            {
                // Screens enumeration is platform-dependent and can fail pre-show on exotic
                // backends; geometry restore is cosmetic and must never break a launch.
            }
        }

        if (bounds.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }
}
