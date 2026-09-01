#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Reactive;
using Avalonia.Styling;
using Avalonia.Threading;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services;
using DemoViewer.NET.Theming;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Builds the app's REAL composition root (<see cref="DemoViewer.NET.App.BuildServices" />) and proves
///     it resolves. The launch-time container is otherwise untested, so a bad/missing registration would
///     surface only as a first-launch crash that the green suite never catches. Each case pins
///     <see cref="AppPaths.ConfigDirEnvVar" /> to its own temp dir (so <c>new SettingsService()</c> and the
///     eager-singleton stores stay out of the real user config) and builds on the headless UI thread
///     because <c>ValidateOnBuild</c> constructs the singleton <see cref="MainViewModel" /> (which starts a
///     <c>DispatcherTimer</c>) at build time. <see cref="NotInParallelAttribute" /> because it mutates the
///     process-global <c>DEMOVIEWER_CONFIG_DIR</c>.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class AppCompositionRootTests
{
    // Builds a real container against a throwaway config dir, runs the assertion body on the UI thread, and
    // disposes the provider (and thus MainViewModel: detaches the static parser event, stops its timer).
    private static async Task WithProvider(IWindowService windowService, Func<ServiceProvider, Task> body,
        string? seedSettingsJson = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvcomproot_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Seeded BEFORE the container is built: BuildServices reads the config, and ValidateOnBuild
        // eagerly constructs the shell, so a persisted session has to already be on disk to influence it.
        if (seedSettingsJson is not null)
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), seedSettingsJson);
        }

        string? prev = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                ServiceProvider provider = App.BuildServices(windowService);
                try
                {
                    await body(provider);
                }
                finally
                {
                    provider.Dispose();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, prev);
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                /* best-effort cleanup */
            }
        }
    }

    // (a) The shell resolves non-null for BOTH host window services, the two production entry points.
    [Test]
    public async Task BuildServices_ResolvesMainViewModel_ForDesktopHost()
    {
        await WithProvider(new DesktopWindowService(() => null), async provider =>
        {
            MainViewModel vm = provider.GetRequiredService<MainViewModel>();
            await Assert.That(vm).IsNotNull().Because("the desktop composition root must resolve the shell");
        });
    }

    [Test]
    public async Task BuildServices_ResolvesMainViewModel_ForBrowserHost()
    {
        await WithProvider(new BrowserWindowService(), async provider =>
        {
            MainViewModel vm = provider.GetRequiredService<MainViewModel>();
            await Assert.That(vm).IsNotNull().Because("the WASM composition root must resolve the shell too");
        });
    }

    // (a2) REGRESSION (v0.5.0 launch hang): restoring a session whose active tab reaches for the shell
    // during activation must not recurse. The shell ctor used to call RestoreSession, which selected the
    // persisted tab, whose activation resolved MainViewModel from the container, but a DI singleton is not
    // cached until its factory RETURNS, so that built a second shell, which restored again, forever. It
    // never even threw StackOverflow (ServiceProvider's StackGuard hops to a fresh thread as the stack
    // deepens): the process just pegged a core and grew past 3 GB while the UI thread never returned to
    // show a window. Anyone who quit on the Highlights tab had an app that could not start again.
    //
    // The Highlights tab is the concrete instance (its OnActivated reads a shell-bound delegate), so this
    // seeds it as the persisted active tab and drives the REAL container the way the composition root does:
    // resolve the shell, THEN restore. tab.highlights defaults visible to every user category, so it is
    // genuinely present in Tabs here: the SelectedTab assertion below fails loudly if that ever stops
    // being true, because a gated-out id silently falls back to Tabs[0] and would make this test vacuous.
    [Test]
    public async Task RestoringASessionWhoseActiveTabResolvesTheShell_DoesNotRecurse()
    {
        // Deliberately an OLD-shaped payload: "ActiveTabIndex" no longer exists on SessionPayload (tab
        // restore is name-based only), so this doubles as the forward-compat check that a session.json
        // written by an earlier build still deserializes: STJ ignores the unknown property.
        const string seed = """
                            {
                              "Session": {
                                "ActiveTabIndex": 3,
                                "ActiveTabId": "highlights.browser"
                              }
                            }
                            """;

        await WithProvider(new DesktopWindowService(() => null), async provider =>
        {
            MainViewModel vm = provider.GetRequiredService<MainViewModel>();

            // The ctor must NOT have activated anything. That is the structural fix.
            await Assert.That(vm.SelectedTab?.TabId)
                .IsNotEqualTo("highlights.browser")
                .Because("the shell constructor must activate no tab; session restore is the host's job");

            // Now the composition root's post-construction step. This activates the Highlights tab, whose
            // OnActivated resolves MainViewModel, which must hand back the CACHED singleton.
            vm.RestoreSession();

            await Assert.That(vm.SelectedTab?.TabId)
                .IsEqualTo("highlights.browser")
                .Because("the persisted tab must actually be restored — otherwise this test proves nothing");
            await Assert.That(ReferenceEquals(vm, provider.GetRequiredService<MainViewModel>()))
                .IsTrue()
                .Because("tab activation must resolve the SAME shell; a second instance is the recursion");
        }, seed);
    }

    // (b) ModuleRegistry is a singleton: BuildRegistry runs exactly once, so both resolves are the SAME
    // instance (a second construction would show a duplicate/empty registry to the shell).
    [Test]
    public async Task ModuleRegistry_IsSingleton_SameInstanceAcrossResolves()
    {
        await WithProvider(new DesktopWindowService(() => null), async provider =>
        {
            ModuleRegistry first = provider.GetRequiredService<ModuleRegistry>();
            ModuleRegistry second = provider.GetRequiredService<ModuleRegistry>();
            await Assert.That(ReferenceEquals(first, second)).IsTrue()
                .Because("the registry is built once and HELD by the container");
        });
    }

    // (c) SettingsService is a singleton (it owns a live reloadOnChange root) AND the DemoLibraryService got
    // that very instance, so a folder Add/Remove writes through the same live config every other consumer
    // reads.
    [Test]
    public async Task SettingsService_IsSingleton_AndInjectedIntoLibraryService()
    {
        await WithProvider(new DesktopWindowService(() => null), async provider =>
        {
            SettingsService s1 = provider.GetRequiredService<SettingsService>();
            SettingsService s2 = provider.GetRequiredService<SettingsService>();
            await Assert.That(ReferenceEquals(s1, s2)).IsTrue().Because("SettingsService is a singleton");

            DemoLibraryService library = provider.GetRequiredService<DemoLibraryService>();
            await Assert.That(ReferenceEquals(library.SettingsBacking, s1)).IsTrue()
                .Because("the library indexer must be folder-backed by the SAME settings singleton");
        });
    }

    // (c2) IFeatureGate is a resolvable SINGLETON: the type-based registration means a broken
    // resolution would have already failed ValidateOnBuild, and this confirms the container hands out one
    // shared instance (it holds an IOptionsMonitor subscription that provider.Dispose then releases).
    [Test]
    public async Task FeatureGate_IsSingleton_ResolvesFromContainer()
    {
        await WithProvider(new DesktopWindowService(() => null), async provider =>
        {
            IFeatureGate g1 = provider.GetRequiredService<IFeatureGate>();
            IFeatureGate g2 = provider.GetRequiredService<IFeatureGate>();
            await Assert.That(ReferenceEquals(g1, g2)).IsTrue().Because("the feature gate is a singleton");
            // Library is Required → always enabled; a smoke check that resolution runs end-to-end.
            await Assert.That(g1.IsEnabled("tab.library")).IsTrue();
        });
    }

    // (c3) P2b precondition: on a genuinely fresh config dir (no settings.json, no library.json), building
    // the REAL container leaves NeedsFirstRun TRUE, i.e. NO eagerly-constructed singleton (the library
    // folder migration, the registered modules, the feature gate) writes settings.json during
    // ValidateOnBuild. This is the load-bearing precondition of the desktop first-run trigger; the lifetime
    // branch that consumes it is untestable headlessly, so lock the precondition here against regression.
    [Test]
    public async Task BuildServices_OnFreshDir_LeavesNeedsFirstRunTrue()
    {
        await WithProvider(new DesktopWindowService(() => null), async provider =>
        {
            await Assert.That(provider.GetRequiredService<SettingsService>().NeedsFirstRun).IsTrue()
                .Because("a fresh install must still trigger the first-run wizard after the container builds");
        });
    }

    // (d) IOptionsMonitor<AppSettings> is injectable and live: a SettingsService.Write reloads the bound
    // configuration synchronously, so OnChange fires. This is the wiring the whole live-settings design
    // depends on (Workbench DeveloperMode gate, library folders).
    [Test]
    public async Task OptionsMonitor_IsResolvable_AndFiresOnChange_AfterWrite()
    {
        await WithProvider(new DesktopWindowService(() => null), async provider =>
        {
            IOptionsMonitor<AppSettings> monitor = provider.GetRequiredService<IOptionsMonitor<AppSettings>>();
            await Assert.That(monitor).IsNotNull();

            bool fired = false;
            using IDisposable? sub = monitor.OnChange((_, _) => fired = true);

            provider.GetRequiredService<SettingsService>().Write(s => s.Theme = "Light");
            await Assert.That(fired).IsTrue()
                .Because("a self-write reloads the bound config synchronously, raising OnChange");
        });
    }

    // (e) Theme integration, the REAL App.WireTheme startup path, which no other test executes: a persisted
    // DROP-IN theme resolves AT LAUNCH (proving the Reload → Install → ApplyTheme order: a custom theme would
    // otherwise fall through to its base), and editing that theme + Reload repaints the running app (proving
    // the Reloaded → RepaintForThemeReload subscription WireTheme installs). All UI work is synchronous (an
    // awaited assertion resumes off the dispatcher thread); values are captured, then asserted.
    [Test]
    public async Task WireTheme_AppliesPersistedDropIn_AtLaunch_AndRepaintsOnReload()
    {
        await WithProvider(new DesktopWindowService(() => null), provider =>
        {
            Application app = Application.Current!;
            ThemeVariant? original = app.RequestedThemeVariant;
            ThemeRegistry registry = provider.GetRequiredService<ThemeRegistry>();
            SettingsService settings = provider.GetRequiredService<SettingsService>();

            Color? atLaunch = null;
            Color? afterReload = null;
            try
            {
                // A drop-in persisted as the active theme, present before WireTheme runs.
                string themesDir = AppPaths.ThemesDirectory!;
                Directory.CreateDirectory(themesDir);
                string file = Path.Combine(themesDir, "startup.json");
                File.WriteAllText(file,
                    """{ "id":"startup", "name":"Startup", "base":"dark", "tokens":{ "ShellBg":"#123456" } }""");
                settings.Write(s => s.Theme = "startup");

                App.WireTheme(provider);

                object? captured = null;
                using IDisposable sub = app.GetResourceObservable("ShellBg")
                    .Subscribe(new AnonymousObserver<object?>(o => captured = o));
                Dispatcher.UIThread.RunJobs();
                atLaunch = (captured as ISolidColorBrush)?.Color; // #123456 → the drop-in resolved at launch

                // Edit the active theme's file + reload: the WireTheme subscription must repaint the app.
                File.WriteAllText(file,
                    """{ "id":"startup", "name":"Startup", "base":"dark", "tokens":{ "ShellBg":"#654321" } }""");
                registry.Reload();
                Dispatcher.UIThread.RunJobs();
                afterReload = (captured as ISolidColorBrush)?.Color; // #654321 → repaint re-resolved it
            }
            finally
            {
                app.RequestedThemeVariant = original;
                registry.Uninstall(app);
            }

            return AssertBoth();

            async Task AssertBoth()
            {
                await Assert.That(atLaunch).IsEqualTo(Color.Parse("#123456"))
                    .Because("a persisted drop-in theme must resolve at launch (Reload before Install before Apply)");
                await Assert.That(afterReload).IsEqualTo(Color.Parse("#654321"))
                    .Because("editing the active theme + Reload must repaint via the WireTheme subscription");
            }
        });
    }
}
