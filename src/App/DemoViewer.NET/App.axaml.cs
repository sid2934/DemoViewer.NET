#region

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
using DemoViewer.NET.Features;
using DemoViewer.NET.Models;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Review;
using DemoViewer.NET.Modules.RoundTagger;
using DemoViewer.NET.Modules.RuleWorkbench;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Modules.StratBook;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Modules.Teams;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Dependencies;
using DemoViewer.NET.Services.Diagnostics;
using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.Services.Provenance;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.Services.Zones;
using DemoViewer.NET.Theming;
using DemoViewer.NET.ViewModels.Diagnostics;
using DemoViewer.NET.ViewModels.Highlights;
using DemoViewer.NET.ViewModels.Review;
using DemoViewer.NET.ViewModels.RoundTagger;
using DemoViewer.NET.ViewModels.Settings;
using DemoViewer.NET.ViewModels.Setup;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.ViewModels.Situations;
using DemoViewer.NET.ViewModels.StratBook;
using DemoViewer.NET.ViewModels.Teams;
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
    ///     The application's composition-root service provider, set once by <see cref="BuildServices" />
    ///     during framework init. A deliberate service-locator seam so later Settings / first-run-wizard
    ///     commands can resolve the long-lived <see cref="SettingsService" /> /
    ///     <c>IOptionsMonitor&lt;AppSettings&gt;</c> without threading them through every view-model.
    ///     <c>null</c> only before init (e.g. the XAML designer). See the design notes in git history
    ///     (SUPERSEDED: the app now uses a bare Microsoft.Extensions DI container as the single
    ///     composition root).
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
            WireTheme(services); // L0c: apply persisted theme + keep it live
            MainViewModel viewModel = services.GetRequiredService<MainViewModel>();
            WireDiagnosticsLogging(services, viewModel); // internal ILogger pillar -> Diagnostics tab + file
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
            // Live Sync. The job service takes the (possibly null) live-sync engine so the F1↔F3b single-CS2
            // interlock can suspend an active session. Unset on Browser/tests → reel generation absent.
            IReelJobService? reelJob = null;
            if (AppHostHooks.ReelJobFactory is { } f)
            {
                reelJob = f(viewModel, liveSync);
                viewModel.AttachReelJob(reelJob);
            }

            // 2D video export (docs/playback2d-v2/export.md). Everything reusable is in Core/Pipeline and
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

                // Strat Export (step-authoring.md §3.6): the same gate, interlocks and settings, no frame list
                // (a strat is its own scene), and a chip slot of its own so the two tabs' exports never
                // unmount each other.
                moduleContext.SetStratExportHost(new StratExportHost(
                    services.GetRequiredService<HeavyJobGate>(),
                    () => liveSync?.State.IsSessionActive == true,
                    () => reelJob?.Status.IsRunning == true,
                    () => settings.Current,
                    settings.Write,
                    viewModel.AttachStratExportStatus,
                    viewModel.OpenOutputFolder));
            }

            WireStratCapture(services, viewModel);

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

                // Shutdown is a strat commit trigger (strat-model.md §3.8), and both user-truth stores defer
                // their index to it: the Strat Book writes its own, and the Tag Store's is written here, the call
                // its design leaves to the shell. Idempotent, so a re-fired request writes nothing new.
                services.GetRequiredService<ModuleRegistry>().Modules.OfType<StratBookModule>().FirstOrDefault()?.Shutdown();
                services.GetService<TagStore>()?.SaveIndex();

                bool reelRunning = reelJob is { Status.IsRunning: true };

                // A running 2D export owns an ffmpeg subprocess and a half-written video file. Exiting
                // without cancelling orphaned the process and left the partial output on disk looking
                // like a finished export. The reel path had this teardown from day one, and the export,
                // whose Cancel had no production caller at all, had none.
                bool exportRunning = viewModel.Playback2DExportStatus is { IsRunning: true };

                if (csvgTornDown || liveSync is null && !reelRunning && !exportRunning)
                {
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
            WireTheme(services); // L0c: apply persisted theme + keep it live
            MainViewModel viewModel = services.GetRequiredService<MainViewModel>();
            WireDiagnosticsLogging(services, viewModel); // internal ILogger pillar -> Diagnostics tab (file no-ops on WASM)
            // Create Strat From Round works here too, session only (step-authoring.md §3.10): the parse is in
            // memory and so is the strat store.
            WireStratCapture(services, viewModel);

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
    ///     Hands 2D Playback Create Strat From Round's host (step-authoring.md §3.9): the open parse, the strat
    ///     store and Team Identity, and the way to the new strat, which selects the Strat Book tab and opens it
    ///     there. Both heads: nothing in it writes a file the store would not.
    /// </summary>
    private static void WireStratCapture(ServiceProvider services, MainViewModel viewModel)
    {
        if (viewModel.ModuleContext is not ModuleContext moduleContext)
        {
            return;
        }

        moduleContext.SetStratCaptureHost(new StratCaptureHost(
            () => moduleContext.CurrentDemo,
            services.GetRequiredService<StratStore>(),
            services.GetService<TeamIdentityService>(),
            id =>
            {
                // The tab first: activation refreshes its list, which the open strat is then selected in.
                viewModel.TrySelectTab(StratBookModule.BrowserTabId);
                services.GetRequiredService<StratBookTabViewModel>().OpenStrat(id);
            }));
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
        // T3: ensure the drop-in folder exists (so users have somewhere to add themes), then scan it BEFORE
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
    ///     Repaints the running app after a theme reload (T3). The syntax highlighter caches its definition per
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
    internal static ServiceProvider BuildServices(IWindowService windowService)
    {
        ServiceCollection services = new();

        // SINGLETON via a constructed instance: SettingsService holds a live reloadOnChange
        // ConfigurationRoot / file watcher and must outlive any single resolve. It is constructed eagerly
        // because `Configure<AppSettings>(IConfiguration)` binds to its live Configuration below, the
        // registration that installs the change-token source that makes IOptionsMonitor.OnChange fire on a
        // Write()→Reload(). WASM degrades to an in-memory provider (no filesystem) inside the ctor.
        SettingsService settings = new();
        services.AddSingleton(settings);
        services.Configure<AppSettings>(settings.Configuration);

        // The feature gate resolves per-category show/hide from FeatureCatalog + the live
        // AppSettings overrides. SINGLETON because it holds the IOptionsMonitor.OnChange subscription;
        // registered AFTER Configure<AppSettings> so IOptionsMonitor<AppSettings> is available to its ctor.
        // Type-based (not a factory lambda) so ValidateOnBuild covers its constructor call site. A broken
        // resolution then fails loudly here rather than at first use in the UI enforcement.
        services.AddSingleton<IFeatureGate, FeatureGate>();

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
        services.AddSingleton<Func<SettingsViewModel>>(sp => () => new SettingsViewModel(
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<IOptionsMonitor<AppSettings>>(),
            sp.GetRequiredService<IFeatureGate>(),
            sp.GetRequiredService<ThemeRegistry>(),
            // Replay-walkthrough starter: resolves the singleton shell lazily (never at ctor time, which
            // would recurse through the shell factory). Null-safe if the shell isn't built yet.
            () => Services?.GetService<MainViewModel>()?.StartWalkthrough(),
            // Suggested Tags tuning (§3.7): a fresh VM per open, reading the harness's stored report at
            // construction like the rest of this screen's sections do.
            new SuggestedTagsTuningViewModel(
                sp.GetRequiredService<SuggestedTagsTuningService>(),
                sp.GetRequiredService<ProfileStore>(),
                OperatingSystem.IsBrowser())));

        // First-run wizard VM (P2b), a manual-new FACTORY (same rationale as the Settings factory): a fresh
        // VM per open, owned by whoever shows it. It only needs the live SettingsService (it seeds from and
        // writes through the one singleton). Used by BOTH the launch trigger and Settings' relaunch command.
        services.AddSingleton<Func<FirstRunWizardViewModel>>(sp => () =>
            new FirstRunWizardViewModel(sp.GetRequiredService<SettingsService>()));

        // The machine-wide ONE-heavy-parse gate:
        // the concurrency BACKSTOP the queue's workers and the shell's interactive load coordinate
        // through (interactive preempts background; reel sessions exclude both). MaxConcurrency default 1.
        services.AddSingleton<HeavyJobGate>();

        // The global demo-processing queue (demo-processing-queue.md): the single source all background
        // demo parse/analyse work is pulled from, plus the awaitable highest-priority foreground open.
        // SINGLETON: it owns the worker loops and the observable item set the UI binds to. Its three
        // persisted settings (max concurrency / max queue size / background-enable) are applied from the
        // live AppSettings and re-applied on change (self-writes fire OnChange inline; external edits on a
        // threadpool thread, the queue setters are all lock-guarded, so either is safe). The OnChange
        // callback is rooted by the singleton IOptionsMonitor for the app's lifetime; nothing to dispose.
        services.AddSingleton(sp =>
        {
            DemoProcessingQueue queue = new(
                sp.GetRequiredService<HeavyJobGate>(),
                action => Dispatcher.UIThread.Post(action));
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

        // The demo-library indexer: the one internally-new'd store routed through the container, because
        // it now reads its folders from AppSettings.Library.Folders and writes them back via SettingsService.
        // Its tier-2 full parses run through the DemoEvaluationCoordinator (registered below), not the queue
        // directly ("one parse, many evaluators").
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
        // scanner over it. The scanner's library universe is the indexer's current entries; the D8
        // background-scan opt-in is read live from settings; UI marshalling via the dispatcher.
        services.AddSingleton<IHighlightHarvester, RulesHighlightHarvester>();
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

        // Round Facts: the per-round, per-side record every Strat Room feature filters on. An evaluator on
        // the tier-2 fan-out (no second parse) writing into the unified cache's Analysis tier under the
        // round_facts ruleset's own fingerprint, and the read API over those rows. The engine row source
        // is the parked seam: until CS2DemoKit #54 ships the surfaces the ruleset reads, the effective
        // rules carry no round_facts, the identity answers null, and the evaluator writes nothing.
        services.AddSingleton<IRoundFactsRulesetIdentity, RulesRoundFactsRulesetIdentity>();
        services.AddSingleton<IRoundFactsRowSource, EngineRoundFactsRowSource>();
        services.AddSingleton(sp => new RoundFactsEvaluator(
            sp.GetRequiredService<DemoCacheStore>(),
            sp.GetRequiredService<IRoundFactsRowSource>(),
            sp.GetRequiredService<IRoundFactsRulesetIdentity>(),
            action => Dispatcher.UIThread.Post(action)));
        services.AddSingleton<IRoundFactsSource>(sp => new RoundFactsSource(
            sp.GetRequiredService<DemoCacheStore>(),
            sp.GetRequiredService<RoundFactsEvaluator>()));

        // The Round Index: one row per (demo, live round, sampled second) with the per-side place-count
        // token, written as a .dvri.json sidecar beside the cache by an evaluator on the same tier-2
        // fan-out, one place after Round Facts so it reads the rows written in the same pass. The
        // in-memory SituationIndex is the only reader at query time; it loads once at startup off the
        // UI thread and merges each sidecar as the evaluator writes it. The zone resolver source is Zone
        // Baking's PlaceResolver over the baked zones.json plus the user overlay, one load per map; a map
        // without a zones file, and every map on the browser host, answers "no zones" and the empirical
        // graph applies.
        services.AddSingleton<IZonePlaceResolverSource>(new AssetZonePlaceResolverSource());
        services.AddSingleton(sp =>
        {
            IOptionsMonitor<AppSettings>? monitor = sp.GetService<IOptionsMonitor<AppSettings>>();
            return new RoundIndexPlaceSources(
                () => monitor?.CurrentValue.Situations.TokenSource ?? RoundIndexTokenSource.Pawn,
                sp.GetRequiredService<IZonePlaceResolverSource>());
        });
        services.AddSingleton(sp => new RoundIndexStore(
            AppPaths.DemoCacheDir,
            sp.GetRequiredService<DemoCacheStore>()));
        services.AddSingleton(sp =>
        {
            IOptionsMonitor<AppSettings>? monitor = sp.GetService<IOptionsMonitor<AppSettings>>();
            return new RoundIndexEvaluator(
                sp.GetRequiredService<DemoCacheStore>(),
                sp.GetRequiredService<RoundIndexStore>(),
                sp.GetRequiredService<RoundIndexPlaceSources>(),
                () => monitor?.CurrentValue.Situations.BackgroundIndex ?? true,
                action => Dispatcher.UIThread.Post(action));
        });
        services.AddSingleton(sp => new SituationIndex(
            sp.GetRequiredService<DemoCacheStore>(),
            sp.GetRequiredService<RoundIndexStore>(),
            sp.GetRequiredService<RoundIndexPlaceSources>(),
            sp.GetRequiredService<IRoundFactsSource>(),
            sp.GetRequiredService<IZonePlaceResolverSource>(),
            sp.GetRequiredService<RoundIndexEvaluator>(),
            action => Dispatcher.UIThread.Post(action)));
        services.AddSingleton<ISituationIndex>(sp => sp.GetRequiredService<SituationIndex>());
        // Result Cards seek playback through the shell's own funnels: the shared load core when the
        // card's demo is not the loaded one, the controller's SeekToTick, and the tab switch by id.
        // Every delegate reaches the shell at call time, never at construction.
        services.AddSingleton<ISituationPlayback>(_ => new SituationPlaybackSeek(
            () => Services?.GetService<MainViewModel>()?.LoadedDemoPath,
            async path =>
            {
                if (Services?.GetService<MainViewModel>() is not { } shell)
                {
                    return false;
                }

                await shell.LoadDemoFromPathAsync(path);
                return shell.HasFile;
            },
            tick => Services?.GetService<MainViewModel>()?.Playback.SeekToTick(tick),
            tabId => Services?.GetService<MainViewModel>()?.TrySelectTab(tabId) ?? false));
        services.AddSingleton(sp =>
        {
            IOptionsMonitor<AppSettings>? monitor = sp.GetService<IOptionsMonitor<AppSettings>>();
            return new SituationsTabViewModel(
                sp.GetRequiredService<ISituationIndex>(),
                sp.GetRequiredService<RoundIndexEvaluator>(),
                sp.GetRequiredService<DemoCacheStore>(),
                sp.GetRequiredService<RoundIndexPlaceSources>(),
                () => monitor?.CurrentValue.Situations.TokenSource ?? RoundIndexTokenSource.Pawn,
                playback: () => sp.GetService<ISituationPlayback>(),
                sidecars: sp.GetRequiredService<RoundIndexStore>(),
                // The filter rail's opponent and our-side fields join through Team Identity; its source
                // field through Demo Provenance Labels.
                teams: sp.GetRequiredService<TeamIdentityService>(),
                provenance: sp.GetRequiredService<IDemoProvenanceSource>(),
                watched: sp.GetRequiredService<WatchedSituationsService>(),
                review: sp.GetRequiredService<ReviewQueue>(),
                // The canvas shows the "us" team's callouts (Callout Aliases, strat-model.md §3.7) over
                // the stored canonical place names; no team marked falls back to the me book, same as the
                // Strat Book's own default.
                callouts: sp.GetRequiredService<CalloutResolverSource>());
        });

        // The Review Queue: every surface's clips in one ordered list, review-queue.json beside
        // teams.json. One per process, because the Reels tray, the Result Cards and the Review tab must
        // all mutate the same list. Null config root (the browser) keeps it for the session.
        services.AddSingleton(_ => new ReviewQueue(AppPaths.ConfigRoot));
        // The Review tab VM: a container singleton resolved lazily on first activation, opening clips
        // through the same seek seam the Result Cards use.
        services.AddSingleton(sp => new ReviewQueueTabViewModel(
            sp.GetRequiredService<ReviewQueue>(),
            () => sp.GetService<ISituationPlayback>()));

        // Watched Situations: the saved queries in watched-situations.json beside teams.json, re-run
        // over one demo on the index's Indexed hook and over the library at the watermark on every
        // other change. A container singleton so the module's badge and the tab's list share one
        // state; null config root (the browser) keeps the list for the session.
        services.AddSingleton(sp => new WatchedSituationsService(
            AppPaths.ConfigRoot,
            sp.GetRequiredService<ISituationIndex>(),
            sp.GetRequiredService<DemoCacheStore>(),
            sp.GetRequiredService<TeamIdentityService>(),
            sp.GetRequiredService<IDemoProvenanceSource>(),
            action => Dispatcher.UIThread.Post(action)));

        // The Round Tagger's store: per-demo tag documents keyed by content hash under <config>/tags. One
        // per process, because CheckOut's single-writer guarantee is only as wide as the instance that
        // holds it. Null root (the browser) keeps tags in memory for the session.
        services.AddSingleton(_ => new TagStore(AppPaths.TagsDir, action => Dispatcher.UIThread.Post(action)));
        // Free labels: every tag instance carries its round's facts in the parser namespace, rewritten
        // when the evaluator rewrites a demo's rows. The cache index is the path-to-hash join; the
        // refresh itself runs off the UI thread and reaches an open demo through the store's routing.
        services.AddSingleton(sp =>
        {
            DemoCacheStore cache = sp.GetRequiredService<DemoCacheStore>();
            return new TagFactsRefresher(
                sp.GetRequiredService<TagStore>(),
                sp.GetRequiredService<IRoundFactsSource>(),
                path => cache.TryGetIndex(path)?.Sha256);
        });

        // The Matrix: tag instances pivoted over the store, a container singleton resolved lazily on first
        // activation. The cache index is the hash-to-path join a cell's clips need, Team Identity the scope
        // of the multi-demo mode, and the tab switch after a send reaches the shell at call time.
        services.AddSingleton(sp =>
        {
            DemoCacheStore cache = sp.GetRequiredService<DemoCacheStore>();
            return new TagMatrixTabViewModel(
                sp.GetRequiredService<TagStore>(),
                sp.GetRequiredService<ReviewQueue>(),
                cache.TryGetIndexBySha256,
                sp.GetRequiredService<TeamIdentityService>(),
                tabId => Services?.GetService<MainViewModel>()?.TrySelectTab(tabId) ?? false,
                action => Dispatcher.UIThread.Post(action));
        });

        // Suggested Tags: the detectors as an evaluator one place after the Round Index, reading the index
        // it wrote in the same pass (overview correction 19). Proposals go to cache/suggestions/ beside
        // demos/, verdicts to the Tag Store under the tags root (correction 2); the learned site regions
        // come from <config>/suggested-tags/. The library sweep is its own opt-in, off by default
        // (correction 20); the open demo, resolved at call time, is always built. Null roots (the
        // browser) keep all of it for the session.
        services.AddSingleton(sp => new ProposalStore(AppPaths.DemoCacheDir, sp.GetRequiredService<DemoCacheStore>()));
        services.AddSingleton(_ => new SiteRegionStore(AppPaths.SuggestedTagsDirectory));
        // The parameter profile: <config>/suggested-tags/profile.json, seeded with the shipped default
        // on first read the way a theme drop-in folder is (§3.7). A singleton so the evaluator's Func
        // and the tuning view's save reach the same in-memory Current.
        services.AddSingleton(_ => new ProfileStore(AppPaths.SuggestedTagsDirectory));
        services.AddSingleton(sp =>
        {
            IOptionsMonitor<AppSettings>? monitor = sp.GetService<IOptionsMonitor<AppSettings>>();
            IFeatureGate? features = sp.GetService<IFeatureGate>();
            return new SuggestedTagsService(
                sp.GetRequiredService<DemoCacheStore>(),
                sp.GetRequiredService<ProposalStore>(),
                sp.GetRequiredService<TagStore>(),
                sp.GetRequiredService<SiteRegionStore>(),
                () => sp.GetRequiredService<ProfileStore>().Current,
                () => features?.IsEnabled(SuggestedTagsService.FeatureId) ?? true,
                () => monitor?.CurrentValue.Playback2D.SuggestedTagsBackground ?? false,
                sp.GetRequiredService<RoundIndexStore>(),
                sp.GetRequiredService<RoundIndexPlaceSources>(),
                sp.GetRequiredService<IZonePlaceResolverSource>(),
                map => sp.GetRequiredService<ISituationIndex>().Places(map),
                () => Services?.GetService<MainViewModel>()?.LoadedDemoPath,
                action => Dispatcher.UIThread.Post(action));
        });
        // The tuning view's harness: stored counts for free, an in-memory re-run over a candidate
        // profile for recall/precision (§3.7). Shares the evaluator's store and region table so a
        // preview scores exactly what the queue already built.
        services.AddSingleton(sp => new SuggestedTagsTuningService(
            sp.GetRequiredService<DemoCacheStore>(),
            sp.GetRequiredService<SuggestedTagsService>(),
            sp.GetRequiredService<TagStore>(),
            sp.GetRequiredService<SiteRegionStore>()));

        // The Tag Palette's vocabularies: the built-in palette plus <config>/palettes drop-ins, scanned on
        // first resolve (the 2D tab's construction) the way themes are scanned at startup. The browser has
        // no directory and offers the built-in alone.
        services.AddSingleton(_ =>
        {
            TagPaletteStore palettes = new(AppPaths.EnsurePalettesDirectory());
            palettes.Reload();
            return palettes;
        });

        // Team Identity: teams as data over the cache's rosters. Two files under the config root, the
        // user's teams.json beside settings.json and the derived team-index.json under cache/; the
        // service lifts side keys off DemoCacheStore.Changed and replays clustering off the UI thread.
        // Round Facts is the join SideAtRound reads. Null config root (the browser) makes it session-only.
        services.AddSingleton(sp => new TeamIdentityService(
            AppPaths.ConfigRoot,
            sp.GetRequiredService<DemoCacheStore>(),
            sp.GetRequiredService<IRoundFactsSource>(),
            action => Dispatcher.UIThread.Post(action)));
        // Demo Provenance Labels: the override from teams.json else the heuristic over the cache row and
        // the assignment. No store of its own; it re-raises the two stores' Changed on the UI thread.
        services.AddSingleton(sp => new DemoProvenanceSource(
            sp.GetRequiredService<DemoCacheStore>(),
            sp.GetRequiredService<TeamIdentityService>(),
            action => Dispatcher.UIThread.Post(action)));
        services.AddSingleton<IDemoProvenanceSource>(sp => sp.GetRequiredService<DemoProvenanceSource>());
        // The Teams tab VM: a container singleton resolved lazily on first activation. Opening a demo
        // reaches the shell at call time, never at construction.
        services.AddSingleton(sp => new TeamsTabViewModel(
            sp.GetRequiredService<TeamIdentityService>(),
            sp.GetRequiredService<DemoCacheStore>(),
            async path =>
            {
                if (Services?.GetService<MainViewModel>() is { } shell)
                {
                    await shell.LoadDemoFromPathAsync(path);
                }
            }));

        // The Strat Book's store: one folder per book under <config>/strats. One per process, because CheckOut's
        // single-writer guarantee is only as wide as the instance that holds it. Null root (the browser) keeps
        // strats in memory for the session. The tab VM is a container singleton resolved lazily on first
        // activation; its books are Team Identity's teams plus me.
        services.AddSingleton(_ => new StratStore(AppPaths.StratsDir, action => Dispatcher.UIThread.Post(action)));
        // Callout Aliases (strat-model.md §3.7): one resolver builder over the store's tables and the map's
        // baked-plus-overlay zones, shared by the Strat Book and anything else that turns a team's word into
        // a nav place.
        services.AddSingleton(sp => new CalloutResolverSource(sp.GetRequiredService<StratStore>()));
        // Strat Record Panel (strat-model.md §3.6): the evidence rule over the Tag Store and Demo
        // Provenance Labels, one instance so the panel's live rebuild and any other future reader of a
        // strat's record agree on what "run / won / aborted" means.
        services.AddSingleton(sp => new StratEvidenceService(
            sp.GetRequiredService<TagStore>(),
            sp.GetRequiredService<IDemoProvenanceSource>()));
        services.AddSingleton(sp =>
        {
            DemoCacheStore cache = sp.GetRequiredService<DemoCacheStore>();
            return new StratBookTabViewModel(
                sp.GetRequiredService<StratStore>(),
                sp.GetRequiredService<TeamIdentityService>(),
                action => Dispatcher.UIThread.Post(action),
                calloutResolvers: sp.GetRequiredService<CalloutResolverSource>(),
                tags: sp.GetRequiredService<TagStore>(),
                evidence: sp.GetRequiredService<StratEvidenceService>(),
                review: sp.GetRequiredService<ReviewQueue>(),
                indexBySha: cache.TryGetIndexBySha256,
                selectTab: tabId => Services?.GetService<MainViewModel>()?.TrySelectTab(tabId) ?? false);
        });

        // J / K in 2D playback walk the Situations result set: the same lazy resolution as Find Rounds
        // Like This, so the set the keys walk is the set the tab shows.
        services.AddSingleton<ISituationResultWalk>(sp => new SituationResultWalk(
            sp.GetRequiredService<SituationsTabViewModel>));

        // Find Rounds Like This: the 2D tab's Ctrl+F hands its current tick through this seam. The tab
        // VM resolves lazily (the same container singleton the module activates, so the canvas the key
        // fills is the one the tab shows), and the tab switch reaches the shell at call time, the way
        // the Settings factory reaches StartWalkthrough, never at construction.
        services.AddSingleton<IFindRoundsLikeThis>(sp => new FindRoundsLikeThis(
            sp.GetRequiredService<SituationsTabViewModel>,
            sp.GetRequiredService<RoundIndexPlaceSources>(),
            tabId => Services?.GetService<MainViewModel>()?.TrySelectTab(tabId) ?? false));

        // The "one parse, many evaluators" coordinator: the single submitter
        // that polls the registered IDemoEvaluators (Library + Highlights + Round Facts) for a demo and
        // coalesces their queue submissions onto ONE parse. The candidate universe re-polled on
        // CapacityAvailable is the UNION of each evaluator's worker-readable pending snapshot (never the
        // UI-bound Entries collection). Setting .Coordinator on each flips it off its inline/feeder path
        // onto the coordinator; the construction side-effect runs under ValidateOnBuild (+ the explicit
        // force-resolve below). The ORDER is a contract (the round index, when it lands, reads the round
        // facts written in the same pass) and is pinned by AppCompositionRootTests.
        services.AddSingleton(sp =>
        {
            DemoLibraryService library = sp.GetRequiredService<DemoLibraryService>();
            HighlightScanService highlights =
                sp.GetRequiredService<HighlightScanService>();
            RoundFactsEvaluator roundFacts = sp.GetRequiredService<RoundFactsEvaluator>();
            RoundIndexEvaluator roundIndex = sp.GetRequiredService<RoundIndexEvaluator>();
            SuggestedTagsService suggestedTags = sp.GetRequiredService<SuggestedTagsService>();
            DemoEvaluationCoordinator coordinator = new(
                [library, highlights, roundFacts, roundIndex, suggestedTags],
                sp.GetRequiredService<IDemoProcessingQueue>(),
                () => library.Tier2Backlog()
                    .Concat(highlights.PendingPaths())
                    .Concat(roundFacts.PendingPaths())
                    .Concat(roundIndex.PendingPaths())
                    .Concat(suggestedTags.PendingPaths())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());
            library.Coordinator = coordinator;
            highlights.Coordinator = coordinator;
            roundIndex.Coordinator = coordinator;
            suggestedTags.Coordinator = coordinator;
            return coordinator;
        });

        // Recently-opened-demos store. SINGLETON: it holds the live in-memory recents list
        // that both the shell (records on open) and the Library tab (binds + prunes) share. Persists
        // to the Recents section of the single consolidated config file via the shared SettingsService (no-op
        // on WASM). Factory-registered (mirrors DemoLibraryService) so there is no ambiguity over its optional
        // ctor param.
        services.AddSingleton(sp => new RecentFilesStore(sp.GetRequiredService<SettingsService>()));

        // The first-party module registry, built ONCE by BuildRegistry and held by the container (the
        // reconciliation), injected into the shell so there is no stray second construction. The provider is
        // passed so BuildRegistry can DI-resolve module deps (the Highlights cache/scanner) + defer the
        // shell-bound delegates (never eagerly resolving MainViewModel here, which would recurse through
        // ModuleRegistry).
        services.AddSingleton(sp => BuildRegistry(sp));

        // The shell, constructed by an explicit factory so DI does not auto-fill every optional ctor param:
        // only the deps it needs are supplied; the rest default. The feature gate is
        // handed in so the shell FILTERS the workspace tab strip per user category (and reconciles live on
        // IFeatureGate.Changed). A null gate (the designer / unit-test path) fails open: no tab filtering.
        services.AddSingleton(BuildShell);

        // ValidateOnBuild: a missing/broken registration fails HERE (a loud construction error on the UI
        // thread at framework-init) instead of silently at the first GetRequiredService<MainViewModel>().
        // It eagerly constructs the singletons, all of which the shell resolves immediately anyway, so
        // there is no extra side-effect beyond building them a few lines earlier, on the same UI thread.
        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true
        });
        // Force-construct the coordinator so its wiring side-effect (library.Coordinator = it) runs before
        // any rescan, independent of ValidateOnBuild's eager-construction behavior.
        provider.GetRequiredService<DemoEvaluationCoordinator>();
        // The situation index's startup load: every current sidecar, off the UI thread (2 ms per demo
        // measured). Queries before it finishes answer empty with IsReady false and the strip says so.
        _ = provider.GetRequiredService<SituationIndex>().StartLoadAsync();
        // Team Identity's startup: a rebuild from the sidecars when team-index.json is missing or behind,
        // else the index-versus-cache diff. Off the UI thread; the tab reads whatever is there meanwhile.
        _ = provider.GetRequiredService<TeamIdentityService>().StartAsync();
        // Nothing resolves the facts refresher; constructing it is what subscribes it to the rows writes.
        provider.GetRequiredService<TagFactsRefresher>();
        Services = provider;
        return provider;
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
            return new MainViewModel(
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
                // The open fans its parse out to the background
                // evaluators so an un-indexed library demo fills its card from THAT parse, not a second one.
                sp.GetRequiredService<DemoEvaluationCoordinator>(),
                // Bundled tour sample (assets/tour): the Library hero's "Try a sample match" CTA and the
                // walkthrough gateway's empty-library target. Resolves null on WASM (no filesystem to walk).
                TourDemoLocator.FindSampleDemo,
                // The unified demo cache: what a Library single-click renders on Match Overview without
                // parsing anything.
                sp.GetRequiredService<DemoCacheStore>(),
                // Team Identity, for the Library's team filter.
                sp.GetRequiredService<TeamIdentityService>(),
                // Demo Provenance Labels, for the Library card's label chip.
                sp.GetRequiredService<IDemoProvenanceSource>());
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
    // real 2D pilot. Both hosts use this path (only first-party modules on WASM). This
    // is now invoked exactly once, by the DI factory that HOLDS the resulting registry (see BuildServices).
    private static ModuleRegistry BuildRegistry(IServiceProvider sp)
    {
        IOptionsMonitor<AppSettings>? settings = sp.GetService<IOptionsMonitor<AppSettings>>();
        ModuleRegistry registry = new();
        // The 2D Playback pilot. First-party,
        // granted Playback.Control. Both desktop and browser hosts use this path.
        registry.Register(new Playback2DModule());
        // The Rulesets v2 authoring Workbench.
        // Registered on both hosts; desktop-only features (editor save, FileSystemWatcher, code --goto)
        // gate at runtime via OperatingSystem.IsBrowser() as they land, so the WASM build compiles
        // and gets the read-only surface. The live-settings monitor threads through so the
        // Workbench's DeveloperMode gate is a live read of AppSettings.Features.DeveloperMode.
        registry.Register(new RuleWorkbenchModule(settings));

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

        // The Situations tab. Registered on both hosts: the browser renders the strip and says there is
        // no library index there. The VM is a container singleton resolved lazily on first activation.
        registry.Register(new SituationsModule(sp.GetRequiredService<SituationsTabViewModel>,
            sp.GetRequiredService<WatchedSituationsService>()));

        // The Teams tab. Registered on both hosts: the browser keeps teams for the session and says so.
        registry.Register(new TeamsModule(sp.GetRequiredService<TeamsTabViewModel>));

        // The Review tab. Registered on both hosts: the browser keeps the queue for the session and says
        // so. The badge reads the queue, so clips sent from another tab count before the tab is opened.
        registry.Register(new ReviewQueueModule(sp.GetRequiredService<ReviewQueueTabViewModel>,
            sp.GetRequiredService<ReviewQueue>()));

        // The Round Tagger's Matrix tab. Registered on both hosts: the browser pivots the session's
        // in-memory tag documents and says so. The VM is a container singleton resolved lazily.
        registry.Register(new RoundTaggerModule(sp.GetRequiredService<TagMatrixTabViewModel>));

        // The Strat Book tab. Registered on both hosts: the browser keeps strats for the session and says so.
        registry.Register(new StratBookModule(sp.GetRequiredService<StratBookTabViewModel>));
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
