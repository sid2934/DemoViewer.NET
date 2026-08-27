#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Models;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.RuleWorkbench;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Dependencies;
using DemoViewer.NET.Services.Diagnostics;
using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.Theming;
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
    /// <summary>
    ///     The application's composition-root service provider, set once by <see cref="BuildServices" />
    ///     during framework init. A deliberate service-locator seam so later Settings / first-run-wizard
    ///     commands can resolve the long-lived <see cref="SettingsService" /> /
    ///     <c>IOptionsMonitor&lt;AppSettings&gt;</c> without threading them through every view-model.
    ///     <c>null</c> only before init (e.g. the XAML designer). See the design notes in git history
    ///     (SUPERSEDED — the app now uses a bare Microsoft.Extensions DI container as the single
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
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            MainWindow window = new();
            // Owner-lookup defers to the live MainWindow so the parse-chain window can be
            // parented (and centred) without the service holding a hard window reference.
            DesktopWindowService windowService = new(() => window);
            // The DI container is the single composition root: it constructs + HOLDS the
            // ModuleRegistry and resolves the shell.
            ServiceProvider services = BuildServices(windowService);
            WireTheme(services); // L0c — apply persisted theme + keep it live
            MainViewModel viewModel = services.GetRequiredService<MainViewModel>();
            WireDiagnosticsLogging(services, viewModel); // internal ILogger pillar -> Diagnostics tab + file
            // Careful: host services MUST attach BEFORE RestoreSession. RestoreSession activates the persisted tab,
            // and a restored-active Reels tab builds HighlightsTabViewModel (→ HighlightReelDialogViewModel),
            // which captures Shell().ReelJob / Shell().ReelJobStatus ONCE in its constructor. Attaching after
            // restore left that capture null for the whole session whenever Reels was the last-active tab —
            // Generate became a silent no-op (CS2 never launched) and the inline reel chip stayed dead. These
            // factories depend only on the shell singleton (line above) and App.Services (set in BuildServices),
            // never on restored state, so hoisting them ahead of restore is safe.

            // CSVG live sync — the engine impl lives in the desktop-only
            // DemoViewer.NET.LiveSync project (CSVG + ASP.NET Core = WASM poison), which this
            // project cannot reference; the Desktop entry point injects a factory via the
            // AppHostHooks static seam before the lifetime starts. Unset on Browser/tests.
            ILiveSyncService? liveSync = null;
            if (AppHostHooks.LiveSyncFactory is { } liveSyncFactory)
            {
                liveSync = liveSyncFactory(viewModel);
                viewModel.AttachLiveSync(liveSync);
            }

            // CSVG reel generation — same static-seam pattern as
            // Live Sync. The job service takes the (possibly null) live-sync engine so the F1↔F3b single-CS2
            // interlock can suspend an active session. Unset on Browser/tests → reel generation absent.
            IReelJobService? reelJob = null;
            if (AppHostHooks.ReelJobFactory is { } f)
            {
                reelJob = f(viewModel, liveSync);
                viewModel.AttachReelJob(reelJob);
            }

            // 2D video export (docs/playback2d-v2/export.md). Everything reusable is in Core/Pipeline and
            // the 2D tab composes the job itself; what it cannot see through IModuleContext — the frame
            // list, the heavy-job gate, and whether Live Sync or a reel already owns the machine — is
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
                    // project. The narrower predicate still refuses every case a user can create — a
                    // faulted session holding the gRPC host for retry is the gap, and the gate's own reel
                    // check covers the overlap that actually costs CPU.
                    () => liveSync?.State.IsSessionActive == true,
                    () => reelJob?.Status.IsRunning == true,
                    () => settings.Current,
                    settings.Write,

                    // The 2D tab is lazy and the shell is not, so the chip cannot be attached here the
                    // way the reel's is. The shell hands over the mount point and the tab calls it on the
                    // first Export — see Playback2DExportHost.MountStatusChip.
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
            // activation may resolve the shell — which only works once the singleton above is cached (and now
            // once the host services above are attached). Still before the DataContext is set, so the UI binds
            // to already-restored state (see RestoreSession).
            viewModel.RestoreSession();

            // v0.6.0 — apply persisted window geometry before the window ever shows. Sizes are DIPs,
            // Position is PHYSICAL pixels (two unit systems — never mixed); a saved position is reused
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
            // semantics differ). Start it here, after the shell exists — the WASM branch never calls this.
            viewModel.StartIdleMonitoring();

            // Launch update check (desktop-only — the Browser head has no installed build to update).
            // Fire-and-forget on purpose: this is one HTTPS request to the GitHub release feed, and
            // the window must never wait on it. No-op unless Desktop supplied the service.
            viewModel.StartUpdateCheck();

            // Post-update "What's new" gate (v0.6.0) — desktop-only like the update check. The gate
            // itself is cheap (one settings read + at most one write) and the notes fetch happens
            // lazily when the window opens, so it never delays the shell — but it must NOT run here.
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
            // (bounded — shutdown must never hang on a stuck CS2 kill), and shutdown is
            // re-triggered. Repeat requests (a user hammering Cmd+Q on an apparent hang) keep
            // being cancelled and JOIN the in-flight teardown — never a second teardown, never an
            // exit while CS2 kill / install restore is still mid-flight.
            bool csvgTornDown = false;
            bool csvgTeardownStarted = false;
            desktop.ShutdownRequested += (_, e) =>
            {
                // Geometry snapshot first (idempotent — this handler can re-fire after a cancelled
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
                // like a finished export — the reel path had this teardown from day one and the export,
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
                        // Best effort — a failed restore is CSVG's `csvg restore` / doctor territory;
                        // the app must still exit.
                    }

                    try
                    {
                        // Then the 2D export: its CancelAsync awaits the job's own finally, which
                        // disposes the sink — that is what kills ffmpeg and deletes the partial file.
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

            // P2b — first-run setup wizard. DESKTOP ONLY: on WASM there is no persisted file, so
            // NeedsFirstRun is always true and auto-showing would loop every page load (see the browser
            // branch — the wizard is reachable there only via Settings). NeedsFirstRun is now driven by the
            // AppSettings.FirstRunCompleted flag (only the wizard's Finish/Skip sets it), NOT by whether
            // settings.json exists — so the library-folder migration creating the file during BuildServices
            // no longer suppresses the wizard for an UPGRADING install (that user has still never picked a
            // category and should see it; the wizard's folder step is pre-seeded with their migrated folders).
            // The wizard is shown MODAL and owned by the main window, so it must wait for the window to
            // actually open — hence the one-shot Opened hook (posted to avoid re-entrancy during the owner's
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
            WireTheme(services); // L0c — apply persisted theme + keep it live
            MainViewModel viewModel = services.GetRequiredService<MainViewModel>();
            WireDiagnosticsLogging(services, viewModel); // internal ILogger pillar -> Diagnostics tab (file no-ops on WASM)
            // Same ordering contract as the desktop root above — after the singleton is cached, never in
            // the ctor. No-ops on WASM (fileless settings persist no session), but the call site stays so
            // the two roots do not drift.
            viewModel.RestoreSession();
            // WASM has no OS windows: the window service surfaces the Settings screen as an in-app overlay
            // on the shell (P2a-i). Wired here, after the shell VM exists.
            windowService.OnOpenSettings = viewModel.ShowSettingsOverlay;
            // P2b — the first-run wizard also surfaces as an in-app overlay. It is NOT auto-triggered on
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
    ///     Publishes the first-party diagnostics logging pillar: builds an <see cref="ILoggerFactory" />
    ///     around the <see cref="HubLoggerProvider" /> (feeding the shell's unified telemetry hub and,
    ///     off WASM, the rolling file) and assigns it to the ambient <see cref="DiagnosticsLog" /> seam
    ///     so the Analysis assembly's coarse logs surface live in the Diagnostics tab. Always wired (even
    ///     when the master switch is currently off) so a live toggle takes effect with no restart — the
    ///     provider's own <c>IsEnabled</c> is the live gate. No-op when settings are unavailable (designer).
    /// </summary>
    private static void WireDiagnosticsLogging(ServiceProvider services, MainViewModel viewModel)
    {
        IOptionsMonitor<AppSettings>? monitor = services.GetService<IOptionsMonitor<AppSettings>>();
        if (monitor is null)
        {
            return; // designer / degraded host — ambient factory stays NullLogger
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
            // is the SOLE gate — otherwise LoggerFactory's default Information cap would pre-drop Debug.
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(new HubLoggerProvider(
                viewModel.Telemetry, file,
                () => ToLogLevel(monitor.CurrentValue.Diagnostics.MinimumLogLevel),
                () => monitor.CurrentValue.Diagnostics.EnableInternalLogging));
        });

        DiagnosticsLog.LoggerFactory = factory;
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
    ///     Central theme system — installs the theme registry and applies the
    ///     persisted theme at startup, keeping it live. Order matters: the registry's custom-variant override
    ///     dictionaries (built-in High-Contrast / E-Girl + any user drop-in) are merged into
    ///     <c>Application.Resources</c> by <c>Install</c> BEFORE <c>ApplyTheme</c> sets the variant, so a
    ///     persisted CUSTOM theme resolves its tokens at launch instead of falling through to its base palette.
    ///     <c>ApplyTheme</c> maps <see cref="AppSettings.Theme" /> (a theme <b>id</b>) via
    ///     <see cref="ThemeRegistry.VariantFor" /> onto <c>RequestedThemeVariant</c> (case-insensitive; an
    ///     unknown id → <c>Default</c> = follow the OS). The palette (DynamicResource over the
    ///     ThemeDictionaries) and the FluentTheme both re-resolve on the change, and the code-held surfaces
    ///     (2D playback viewport, Analysis graph, syntax highlighter) repaint on their
    ///     <c>ActualThemeVariantChanged</c> hooks — so switching themes in Settings re-themes the running app
    ///     with no restart.
    /// </summary>
    // internal so AppCompositionRootTests can drive the real startup path (Reload → Install → ApplyTheme +
    // the Reloaded → repaint subscription) — otherwise the launch-time theme wiring is untested.
    internal static void WireTheme(IServiceProvider services)
    {
        ThemeRegistry registry = services.GetRequiredService<ThemeRegistry>();
        // T3 — ensure the drop-in folder exists (so users have somewhere to add themes), then scan it BEFORE
        // Install merges the override dictionaries, so a persisted drop-in theme resolves at launch. (No
        // subscribers yet, so this startup reload paints nothing extra — ApplyTheme below does the initial paint.)
        AppPaths.EnsureThemesDirectory();
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
    ///     variant, so an edit to the active variant would otherwise stay stale — <c>ClearCache</c> drops it.
    ///     Then it BOUNCES the active variant (→ <c>Default</c> → back): a same-variant re-apply short-circuits,
    ///     but the bounce forces every <c>{DynamicResource}</c> and the code-held surfaces (2D viewport, Analysis
    ///     graph, syntax highlighter — all of which repaint on <c>ActualThemeVariantChanged</c>) to re-resolve
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
    ///     Microsoft.Extensions.Hosting). It owns the long-lived <see cref="SettingsService" /> — a live
    ///     <c>reloadOnChange</c> ConfigurationRoot / file watcher, hence a SINGLETON — binds
    ///     <see cref="AppSettings" /> to its <c>IConfiguration</c> so <c>IOptionsMonitor&lt;AppSettings&gt;</c>
    ///     is injectable AND reflects <c>Write()→Reload()→OnChange</c> live, and constructs + HOLDS the
    ///     <see cref="ModuleRegistry" /> as a singleton. Both hosts
    ///     (desktop + WASM) call this with their host-specific <see cref="IWindowService" />.
    ///     <para>
    ///         <c>internal</c> so <c>AppCompositionRootTests</c> can build the real container and prove it
    ///         resolves — a bad/missing registration otherwise crashes only at first launch. Always invoked
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
        // because `Configure<AppSettings>(IConfiguration)` binds to its live Configuration below — the
        // registration that installs the change-token source that makes IOptionsMonitor.OnChange fire on a
        // Write()→Reload(). WASM degrades to an in-memory provider (no filesystem) inside the ctor.
        SettingsService settings = new();
        services.AddSingleton(settings);
        services.Configure<AppSettings>(settings.Configuration);

        // The feature gate resolves per-category show/hide from FeatureCatalog + the live
        // AppSettings overrides. SINGLETON because it holds the IOptionsMonitor.OnChange subscription;
        // registered AFTER Configure<AppSettings> so IOptionsMonitor<AppSettings> is available to its ctor.
        // Type-based (not a factory lambda) so ValidateOnBuild covers its constructor call site — a broken
        // resolution then fails loudly here rather than at first use in the UI enforcement.
        services.AddSingleton<IFeatureGate, FeatureGate>();

        // The central theme registry — the single source of truth for the
        // available themes: native dark / light / system plus the built-in custom variants (High-Contrast,
        // E-Girl) and any user drop-in from <config>/themes/. SINGLETON because it OWNS the one merged
        // custom-variant override dictionary installed into Application.Resources (App.WireTheme), and both
        // the Settings picker and WireTheme must resolve themes from that same instance.
        services.AddSingleton<ThemeRegistry>();

        // The host window service instance (desktop real / browser no-op), resolved by MainViewModel.
        services.AddSingleton(windowService);

        // Settings screen VM (P2a-i) — registered as a MANUAL-new FACTORY, not AddTransient. A transient
        // IDisposable resolved from the ROOT provider is captured by the root and only released at app exit;
        // this factory instead hands ownership to whoever opens Settings (the window service disposes the VM
        // on window-close / overlay-clear), so a fresh VM per open leaks nothing. Its live deps come from the
        // container so a self-write and an external edit both flow through the one SettingsService.
        services.AddSingleton<Func<SettingsViewModel>>(sp => () => new SettingsViewModel(
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<IOptionsMonitor<AppSettings>>(),
            sp.GetRequiredService<IFeatureGate>(),
            sp.GetRequiredService<ThemeRegistry>(),
            // Replay-walkthrough starter — resolves the singleton shell lazily (never at ctor time, which
            // would recurse through the shell factory). Null-safe if the shell isn't built yet.
            replayWalkthrough: () => Services?.GetService<MainViewModel>()?.StartWalkthrough()));

        // First-run wizard VM (P2b) — a manual-new FACTORY (same rationale as the Settings factory): a fresh
        // VM per open, owned by whoever shows it. It only needs the live SettingsService (it seeds from and
        // writes through the one singleton). Used by BOTH the launch trigger and Settings' relaunch command.
        services.AddSingleton<Func<FirstRunWizardViewModel>>(sp => () =>
            new FirstRunWizardViewModel(sp.GetRequiredService<SettingsService>()));

        // The machine-wide ONE-heavy-parse gate:
        // the concurrency BACKSTOP the queue's workers and the shell's interactive load coordinate
        // through (interactive preempts background; reel sessions exclude both). MaxConcurrency default 1.
        services.AddSingleton<HeavyJobGate>();

        // The global demo-processing queue (demo-processing-queue.md) — the single source all background
        // demo parse/analyse work is pulled from, plus the awaitable highest-priority foreground open.
        // SINGLETON: it owns the worker loops and the observable item set the UI binds to. Its three
        // persisted settings (max concurrency / max queue size / background-enable) are applied from the
        // live AppSettings and re-applied on change (self-writes fire OnChange inline; external edits on a
        // threadpool thread — the queue setters are all lock-guarded, so either is safe). The OnChange
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

        // The demo-library indexer — the one internally-new'd store routed through the container, because
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
            // files in place — DemoLibraryService still reads library.json on construction.
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
            MainViewModel Shell() => sp.GetRequiredService<MainViewModel>();
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
                // Platform mode — macOS can plan a reel but not capture one.
                OperatingSystem.IsMacOS(),
                fileExists: null,
                // Passed rather than assigned, so the ENCODING section re-reconciles when the user
                // toggles highlights.encoding in Settings. A one-shot assignment would leave the section
                // wrong until the tab was rebuilt.
                featureGate: sp.GetService<IFeatureGate>(),
                // v0.6.0 ffmpeg pre-flight (Services/Dependencies): detect up front and guide the
                // user to a self-install, instead of a raw CSVG failure after CS2 launches. Real
                // probe ONLY here — the VM's null default keeps pure-VM tests machine-independent.
                ffmpegLocator: FfmpegDependency.Locate)
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

        // The "one parse, many evaluators" coordinator: the single submitter
        // that polls the registered IDemoEvaluators (Library + Highlights) for a demo and coalesces their
        // queue submissions onto ONE parse. The candidate universe re-polled on CapacityAvailable is the
        // UNION of each evaluator's worker-readable pending snapshot (never the UI-bound Entries collection).
        // Setting .Coordinator on each flips it off its inline/feeder path onto the coordinator; the
        // construction side-effect runs under ValidateOnBuild (+ the explicit force-resolve below).
        services.AddSingleton(sp =>
        {
            DemoLibraryService library = sp.GetRequiredService<DemoLibraryService>();
            HighlightScanService highlights =
                sp.GetRequiredService<HighlightScanService>();
            DemoEvaluationCoordinator coordinator = new(
                [library, highlights],
                sp.GetRequiredService<IDemoProcessingQueue>(),
                () => library.Tier2Backlog()
                    .Concat(highlights.PendingPaths())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());
            library.Coordinator = coordinator;
            highlights.Coordinator = coordinator;
            return coordinator;
        });

        // Recently-opened-demos store. SINGLETON: it holds the live in-memory recents list
        // that both the shell (records on open) and the Library tab (binds + prunes) share. Persists
        // to the Recents section of the single consolidated config file via the shared SettingsService (no-op
        // on WASM). Factory-registered (mirrors DemoLibraryService) so there is no ambiguity over its optional
        // ctor param.
        services.AddSingleton(sp => new RecentFilesStore(sp.GetRequiredService<SettingsService>()));

        // The first-party module registry, built ONCE by BuildRegistry and held by the container (the
        // reconciliation) — injected into the shell so there is no stray second construction. The provider is
        // passed so BuildRegistry can DI-resolve module deps (the Highlights cache/scanner) + defer the
        // shell-bound delegates (never eagerly resolving MainViewModel here — that would recurse through
        // ModuleRegistry).
        services.AddSingleton(sp => BuildRegistry(sp));

        // The shell, constructed by an explicit factory so DI does not auto-fill every optional ctor param
        // — only the deps it needs are supplied; the rest default. The feature gate is
        // handed in so the shell FILTERS the workspace tab strip per user category (and reconciles live on
        // IFeatureGate.Changed). A null gate (the designer / unit-test path) fails open: no tab filtering.
        services.AddSingleton(BuildShell);

        // ValidateOnBuild: a missing/broken registration fails HERE (a loud construction error on the UI
        // thread at framework-init) instead of silently at the first GetRequiredService<MainViewModel>().
        // It eagerly constructs the singletons — all of which the shell resolves immediately anyway — so
        // there is no extra side-effect beyond building them a few lines earlier, on the same UI thread.
        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true
        });
        // Force-construct the coordinator so its wiring side-effect (library.Coordinator = it) runs before
        // any rescan — independent of ValidateOnBuild's eager-construction behavior.
        provider.GetRequiredService<DemoEvaluationCoordinator>();
        Services = provider;
        return provider;
    }

    // Re-entrancy tripwire for BuildShell. Deliberately NOT [ThreadStatic]: the recursion it guards
    // against HOPS THREADS (ServiceProvider's StackGuard.RunOnEmptyStack moves to a fresh thread as the
    // stack deepens), so a per-thread flag would never see it. The shell is resolved on the UI thread, so
    // a plain static is not a cross-thread hazard here.
    private static bool _shellUnderConstruction;

    /// <summary>
    ///     Constructs the singleton shell, refusing to do it re-entrantly.
    ///     <para>
    ///         A DI singleton is not cached until its factory RETURNS, so anything that resolves
    ///         <see cref="MainViewModel" /> while its constructor is still running gets a BRAND-NEW shell
    ///         rather than the one being built — and that shell repeats the same work, forever. There is no
    ///         <c>StackOverflowException</c> to stop it either: StackGuard keeps hopping to fresh threads,
    ///         so the process just pegs a core and grows the heap without bound while the UI thread never
    ///         returns to show a window. That shipped once (v0.5.0): the shell ctor ran
    ///         <c>RestoreSession</c>, which activated the persisted tab, whose activation resolved the
    ///         shell. Anyone who quit on the Highlights tab had an app that could never start again.
    ///     </para>
    ///     <para>
    ///         The structural fix is that the ctor no longer activates tabs — <c>RestoreSession</c> is
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
                // Bundled tour sample (assets/tour) — the Library hero's "Try a sample match" CTA and the
                // walkthrough gateway's empty-library target. Resolves null on WASM (no filesystem to walk).
                TourDemoLocator.FindSampleDemo,
                // The unified demo cache — what a Library single-click renders on Match Overview without
                // parsing anything.
                sp.GetRequiredService<DemoCacheStore>());
        }
        finally
        {
            _shellUnderConstruction = false;
        }
    }

    // The first-party module registry. BuiltInTabsModule is auto-registered by the
    // shell (it needs the shell + Diagnostics VM as DataContexts), so the composition root only adds
    // additional modules here. DELIBERATE: the production shell stays built-ins-only — PlaceholderModule
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
        // are LAZY closures over the provider — invoked only at tab-activation, long after the shell exists.
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
        // itself share ONE tray. Still resolved lazily — the module only invokes this on first activation.
        registry.Register(new HighlightsModule(sp.GetRequiredService<HighlightsTabViewModel>));
        return registry;
    }

    // v0.6.0 — applies a persisted geometry snapshot to the still-unshown MainWindow. Width/Height
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

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        DataAnnotationsValidationPlugin[] dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (DataAnnotationsValidationPlugin plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
