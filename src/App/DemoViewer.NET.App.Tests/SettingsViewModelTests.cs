#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DemoViewer.NET.AppTests.Extensions;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Services;
using DemoViewer.NET.Theming;
using DemoViewer.NET.ViewModels;
using DemoViewer.NET.ViewModels.Settings;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Covers <see cref="SettingsViewModel" /> over a temp-dir <see cref="SettingsService" /> (never the real
///     user config): a category / theme / folder change writes through to <c>settings.json</c> and
///     <c>Current</c> reflects it; an EXTERNAL write (another surface / hand-edit) flows back into the VM's
///     bound values via the injected <c>IOptionsMonitor</c>; and the view renders non-blank headlessly.
///     <see cref="NotInParallelAttribute" /> because the render cases share the single headless UI session.
/// </summary>
[NotInParallel]
[Category("Render")]
public class SettingsViewModelTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvsettingsvm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    // Build a SettingsViewModel over a fresh SettingsService rooted at a throwaway dir, plus an
    // IOptionsMonitor<AppSettings> and an IFeatureGate bound to that same service's live config (mirrors
    // SettingsServiceTests + FeatureGateTests). The gate uses UI-thread marshaling DISABLED so its Changed
    // event, the cue that refreshes the feature rows, is observable inline in these non-UI cases; it is
    // registered in the container so the provider disposes it. reindexEstimate is the item 14 test seam
    // for the Extensions "N demos" notice; null everywhere except the tests that exercise it.
    private static (SettingsViewModel Vm, SettingsService Svc, IFeatureGate Gate, ServiceProvider Sp) NewVm(
        string dir, IPackReindexEstimate? reindexEstimate = null, IPackDataRemoval? dataRemoval = null,
        IReadOnlyList<PackStatus>? packStatuses = null)
    {
        SettingsService svc = new(dir);
        ServiceCollection services = new();
        services.Configure<AppSettings>(svc.Configuration);
        services.AddSingleton<IFeatureGate>(s =>
            new FeatureGate(s.GetRequiredService<IOptionsMonitor<AppSettings>>(), false));
        ServiceProvider sp = services.BuildServiceProvider();
        IOptionsMonitor<AppSettings> monitor = sp.GetRequiredService<IOptionsMonitor<AppSettings>>();
        IFeatureGate gate = sp.GetRequiredService<IFeatureGate>();
        SettingsViewModel vm = new(svc, monitor, gate, new ThemeRegistry(), OperatingSystem.IsBrowser,
            null, null, reindexEstimate is null ? null : [reindexEstimate], dataRemoval is null ? null : [dataRemoval],
            packStatuses);
        return (vm, svc, gate, sp);
    }

    // The pending-reindex-count test seam: a fixed pack feature id (the real StratBookPack's) with a
    // caller-supplied count function, so a test can observe "Counting…" before controlling when it lands.
    private sealed class FakeReindexEstimate(Func<Task<int>> count) : IPackReindexEstimate
    {
        public string PackFeatureId => StratBookPack.PackFeatureId;
        public Task<int> CountAsync() => count();
    }

    // The "delete extension data" test seam (item 24): canned inventory/delete results and a call count
    // for each, so a test can assert Confirm reached the remover without a real PackDataRemover, queue or
    // filesystem. No gate/PackSwitch coupling: a real IPackDataRemoval owns that, this fake does not.
    private sealed class FakePackDataRemoval(PackDataInventory inventory, PackDataRemovalResult result,
        TaskCompletionSource<PackDataRemovalResult>? deleteGate = null) : IPackDataRemoval
    {
        public int InventoryCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public string PackFeatureId => StratBookPack.PackFeatureId;

        public Task<PackDataInventory> InventoryAsync()
        {
            InventoryCalls++;
            return Task.FromResult(inventory);
        }

        // With deleteGate set, DeleteAsync counts the call and then waits for the test to release it, so a
        // test can observe state (IsBusy, a locked row, a second concurrent call) while the delete is
        // still "in flight". Without it, completes immediately with result.
        public Task<PackDataRemovalResult> DeleteAsync()
        {
            DeleteCalls++;
            return deleteGate?.Task ?? Task.FromResult(result);
        }
    }

    // Find a feature row by its catalog id across every grouped collection, Extensions included.
    private static FeatureToggleRow Row(SettingsViewModel vm, string featureId) =>
        vm.TabFeatureRows.Concat(vm.ChromeFeatureRows).Concat(vm.ExtensionsFeatureRows)
            .First(r => r.FeatureId == featureId);

    /// <summary>
    ///     The settings search filter (v0.6.x findability): matching sections stay, non-matching
    ///     hide, groups follow their members (a match auto-expands its group), and clearing the
    ///     filter restores everything the platform gates allow.
    /// </summary>
    [Test]
    public async Task SettingsFilter_HidesNonMatches_AndAutoExpandsGroups()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService _, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                // Baseline: everything visible (desktop gates), Features group collapsed by default.
                await Assert.That(vm.ShowSectionTheme).IsTrue();
                await Assert.That(vm.ShowSectionIdle).IsTrue();
                await Assert.That(vm.IsGroupFeaturesExpanded).IsFalse();

                vm.SettingsFilterText = "theme";
                await Assert.That(vm.ShowSectionTheme).IsTrue();
                await Assert.That(vm.ShowSectionIdle).IsFalse().Because("idle has no 'theme' keyword");
                await Assert.That(vm.ShowGroupGeneral).IsTrue();
                await Assert.That(vm.ShowGroupLibrary).IsFalse().Because("no member matches");

                // A hit inside the collapsed Features group auto-expands it.
                vm.SettingsFilterText = "overrides";
                await Assert.That(vm.ShowSectionFeatures).IsTrue();
                await Assert.That(vm.IsGroupFeaturesExpanded).IsTrue();

                // Clearing restores the gate-permitted world.
                vm.SettingsFilterText = "";
                await Assert.That(vm.ShowSectionIdle).IsTrue();
                await Assert.That(vm.ShowGroupLibrary).IsTrue();

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (a) Setting the category writes UserCategory to settings.json and Current reflects it.
    [Test]
    public async Task SelectingCategory_WritesUserCategory()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                vm.SelectCategoryCommand.Execute(UserCategory.Developer);

                await Assert.That(svc.Current.UserCategory).IsEqualTo(UserCategory.Developer);
                await Assert.That(vm.SelectedCategory).IsEqualTo(UserCategory.Developer);

                string json = await File.ReadAllTextAsync(Path.Combine(dir, "settings.json"));
                await Assert.That(json).Contains("Developer");

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (b) Add + Remove folder each write Library.Folders (and keep the bound collection in sync).
    [Test]
    public async Task AddAndRemoveFolder_WritesLibraryFolders()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                // AddFolders is the exact write path AddFolderCommand feeds after the OS picker.
                vm.AddFolders(["/demos/aim", "/demos/retake"]);

                await Assert.That(svc.Current.Library.Folders.Contains("/demos/aim")).IsTrue();
                await Assert.That(svc.Current.Library.Folders.Contains("/demos/retake")).IsTrue();
                await Assert.That(vm.LibraryFolders.Contains("/demos/aim")).IsTrue();

                vm.RemoveFolderCommand.Execute("/demos/aim");

                await Assert.That(svc.Current.Library.Folders.Contains("/demos/aim")).IsFalse();
                await Assert.That(svc.Current.Library.Folders.Contains("/demos/retake")).IsTrue();
                await Assert.That(vm.LibraryFolders.Contains("/demos/aim")).IsFalse();

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (c) Setting the theme persists its id (the central theme system: stores
    // the lowercase Theme.Id, not the old capitalized display value; App.WireTheme resolves it case-insensitively).
    [Test]
    public async Task SelectingTheme_WritesTheme()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                vm.SelectedTheme = vm.Themes.First(t => t.Id == "system");

                await Assert.That(svc.Current.Theme).IsEqualTo("system");

                string json = await File.ReadAllTextAsync(Path.Combine(dir, "settings.json"));
                await Assert.That(json).Contains("system");

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (c2) Light is an offered, live theme; selecting it persists its id. App.WireTheme maps that onto
    // RequestedThemeVariant at startup + on change.
    [Test]
    public async Task LightTheme_IsOffered_AndPersists()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                await Assert.That(vm.Themes.Any(t => t.Id == "light")).IsTrue();

                vm.SelectedTheme = vm.Themes.First(t => t.Id == "light");
                await Assert.That(svc.Current.Theme).IsEqualTo("light");

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (c3) "Reload themes" re-scans the drop-in folder and surfaces a newly-added theme in the picker,
    // keeping the current selection. Rooted at a temp config dir via the AppPaths override so it never touches
    // the real ~/config themes folder.
    [Test]
    public async Task ReloadThemes_PicksUpDropIn_AndKeepsSelection()
    {
        string dir = NewTempDir();
        string? prior = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);
        try
        {
            (SettingsViewModel vm, SettingsService svc, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                await Assert.That(vm.Themes.Any(t => t.Id == "midnight")).IsFalse();

                string themesDir = Path.Combine(dir, "themes");
                Directory.CreateDirectory(themesDir);
                await File.WriteAllTextAsync(Path.Combine(themesDir, "midnight.json"),
                    """{ "id": "midnight", "name": "Midnight", "base": "dark", "tokens": { "ShellBg": "#000000" } }""");

                vm.ReloadThemesCommand.Execute(null);

                await Assert.That(vm.Themes.Any(t => t.Id == "midnight")).IsTrue();
                // Selection unchanged (still the default Dark) and NOT persisted by the reload.
                await Assert.That(vm.SelectedTheme.Id).IsEqualTo("dark");
                await Assert.That(svc.Current.Theme).IsEqualTo("Dark");

                vm.Dispose();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, prior);
            Cleanup(dir);
        }
    }

    // (d) An external write (another writer on the SAME service) flows back into the VM's bound values via
    // the OnChange subscription. Runs on the UI thread so the reflect (which marshals to it) runs inline.
    [Test]
    public async Task ExternalWrite_ReflectsIntoViewModel()
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                (SettingsViewModel vm, SettingsService svc, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
                using (sp)
                {
                    // A DIFFERENT writer changes settings (simulating another surface / a hand-edited file).
                    // A legacy CAPITALIZED "System" proves the id lookup is case-insensitive (back-compat with
                    // pre-central-theme-system persisted values).
                    svc.Write(s =>
                    {
                        s.UserCategory = UserCategory.Consumer;
                        s.Theme = "System";
                        s.Library.Folders = ["/ext/demos"];
                    });

                    await Assert.That(vm.SelectedCategory).IsEqualTo(UserCategory.Consumer);
                    await Assert.That(vm.SelectedTheme.Id).IsEqualTo("system");
                    await Assert.That(vm.LibraryFolders.Contains("/ext/demos")).IsTrue();

                    vm.Dispose();
                }
            });
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Render smoke: the real SettingsView bound to a real VM draws far more than an empty background.
    [Test]
    public async Task SettingsView_Renders_NonBlank()
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                (SettingsViewModel vm, SettingsService svc, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
                using (sp)
                {
                    vm.AddFolders(["/demos/one"]);

                    SettingsView view = new()
                    {
                        DataContext = vm
                    };
                    Window window = new()
                    {
                        Width = 560,
                        Height = 720,
                        Content = view
                    };
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();

                    WriteableBitmap? frame = window.CaptureRenderedFrame();
                    await Assert.That(frame).IsNotNull();

                    string outPath = Path.Combine(HeadlessSession.ArtifactDir, "settings.png");
                    frame!.Save(outPath, new PngBitmapEncoderOptions());
                    int nonBg = ScanNonBackground(frame);
                    Console.WriteLine($"[settings] {outPath} nonBg={nonBg}");

                    await Assert.That(nonBg).IsGreaterThan(200);
                    await Assert.That(File.Exists(outPath)).IsTrue();

                    vm.Dispose();
                }
            });
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Render: the Extensions section (item 5), master switch and every nested row, fits the real settings
    // host width (520-560px, design-system.md) with no horizontal overflow. SectionsScroll disables its
    // horizontal scrollbar, so wider content would silently clip rather than error. Extent > Viewport is
    // the actual overflow signal, not a visual guess.
    [Test]
    public async Task ExtensionsSection_FitsTheRealSettingsHostWidth_NoHorizontalOverflow()
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                (SettingsViewModel vm, SettingsService _, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
                using (sp)
                {
                    vm.SettingsFilterText = "extension"; // selects and auto-expands the Extensions group

                    SettingsView view = new()
                    {
                        DataContext = vm
                    };
                    Window window = new()
                    {
                        Width = 560,
                        Height = 900,
                        Content = view
                    };
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();

                    ScrollViewer scroll = view.FindControl<ScrollViewer>("SectionsScroll")
                                           ?? throw new InvalidOperationException("SectionsScroll is gone from the view.");

                    Console.WriteLine($"[extensions-width] extent={scroll.Extent} viewport={scroll.Viewport}");
                    await Assert.That(scroll.Extent.Width).IsLessThanOrEqualTo(scroll.Viewport.Width)
                        .Because("wider content than the viewport would silently clip at the real host width");

                    vm.Dispose();
                }
            });
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Item 14: the real pack's two settings pages (Suggested Tags tuning, Grenade Index) now arrive as
    // contributions, not constructor parameters, so this goes through the REAL composition root rather
    // than NewVm's minimal container: their own VMs need SuggestedTagsTuningService/ProfileStore, which
    // only a real container wires, and AppPaths.ConfigDirEnvVar is sandboxed to a temp dir here exactly as
    // AppCompositionRootTests/StratBookPackTests do, so nothing touches the live config dir. Proves the
    // pages render with the same content as before the move AND still fit the real host width with the
    // Extensions group's default expansion (no filter needed).
    [Test]
    public async Task ContributedSettingsPages_RenderTheRealPacksContent_AtTheRealHostWidth()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvsettingspages_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string? prevConfigDir = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                ServiceProvider provider = App.BuildServices(new DesktopWindowService(() => null));
                try
                {
                    SettingsViewModel vm = provider.GetRequiredService<Func<SettingsViewModel>>()();
                    try
                    {
                        using (Assert.Multiple())
                        {
                            await Assert.That(vm.ContributedSettingsPages.Select(p => p.Header)).IsEquivalentTo(
                                ["SUGGESTED TAGS TUNING", "GRENADE INDEX"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
                            await Assert.That(vm.ContributedSettingsPages.All(p => p.IsVisible)).IsTrue()
                                .Because("the pack is on by default and the search filter is empty");
                        }

                        SettingsView view = new()
                        {
                            DataContext = vm
                        };
                        Window window = new()
                        {
                            Width = 560,
                            Height = 1400,
                            Content = view
                        };
                        window.Show();
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Dispatcher.UIThread.RunJobs();

                        ScrollViewer scroll = view.FindControl<ScrollViewer>("SectionsScroll")
                                               ?? throw new InvalidOperationException("SectionsScroll is gone from the view.");
                        Console.WriteLine($"[contributed-pages-width] extent={scroll.Extent} viewport={scroll.Viewport}");
                        await Assert.That(scroll.Extent.Width).IsLessThanOrEqualTo(scroll.Viewport.Width)
                            .Because("the two contributed pages must fit the real host width, not just the row list");
                    }
                    finally
                    {
                        vm.Dispose();
                    }
                }
                finally
                {
                    provider.Dispose();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, prevConfigDir);
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    // A trivial contributed page, independent of the real pack: a bare ViewModelBase and a bare Control,
    // so the mechanism itself (gate × keyword match) is pinned without any Strat Book machinery.
    private sealed class FakePageViewModel : ViewModelBase;

    // Item 14, the generic mechanism: a fake page contribution (gated on the real pack.stratbook id, so
    // the override write below is enough to flip it) renders under Extensions only while its gate
    // resolves on, and the search filter hides/shows it by its own Keywords, independent of any built-in
    // section's keyword row.
    [Test]
    public async Task ContributedPage_ShowsOnlyWhileItsGateIsOn_AndSearchFindsItByItsOwnKeywords()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            ServiceCollection services = new();
            services.Configure<AppSettings>(svc.Configuration);
            services.AddSingleton<IFeatureGate>(s =>
                new FeatureGate(s.GetRequiredService<IOptionsMonitor<AppSettings>>(), false));
            ServiceProvider sp = services.BuildServiceProvider();
            IOptionsMonitor<AppSettings> monitor = sp.GetRequiredService<IOptionsMonitor<AppSettings>>();
            IFeatureGate gate = sp.GetRequiredService<IFeatureGate>();

            SettingsPageContribution fake = new(
                "fake.widget", "WIDGET SETTINGS", 0, "widget gadget",
                () => new FakePageViewModel(), () => new Border(), StratBookPack.PackFeatureId);
            SettingsViewModel vm = new(svc, monitor, gate, new ThemeRegistry(), OperatingSystem.IsBrowser,
                null, [fake], null);
            using (sp)
            {
                try
                {
                    MountedSettingsPage page = vm.ContributedSettingsPages.Single();
                    await Assert.That(page.Header).IsEqualTo("WIDGET SETTINGS");
                    await Assert.That(page.IsVisible).IsTrue()
                        .Because("the pack is on by default and the filter is empty");

                    svc.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);
                    await Assert.That(page.IsVisible).IsFalse()
                        .Because("the page's own gate is the real pack's, now off");

                    svc.Write(s => s.Features.Overrides.Remove(StratBookPack.PackFeatureId));
                    await Assert.That(page.IsVisible).IsTrue().Because("the pack is back on");

                    vm.SettingsFilterText = "widget";
                    await Assert.That(page.IsVisible).IsTrue().Because("matches its own Keywords");

                    vm.SettingsFilterText = "something-nobody-typed";
                    await Assert.That(page.IsVisible).IsFalse().Because("matches none of its Keywords");
                }
                finally
                {
                    vm.Dispose();
                }
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Item 14 blocker fix: a page whose gate is off at construction must not run its factories at all, and
    // must run them exactly once, the first time the gate turns on, never again on a later toggle.
    [Test]
    public async Task ContributedPage_BuildsItsFactoriesOnlyOnce_TheFirstTimeItsGateTurnsOn()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            svc.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);
            ServiceCollection services = new();
            services.Configure<AppSettings>(svc.Configuration);
            services.AddSingleton<IFeatureGate>(s =>
                new FeatureGate(s.GetRequiredService<IOptionsMonitor<AppSettings>>(), false));
            ServiceProvider sp = services.BuildServiceProvider();
            IOptionsMonitor<AppSettings> monitor = sp.GetRequiredService<IOptionsMonitor<AppSettings>>();
            IFeatureGate gate = sp.GetRequiredService<IFeatureGate>();

            int calls = 0;
            SettingsPageContribution fake = new(
                "fake.widget", "WIDGET SETTINGS", 0, "widget",
                () =>
                {
                    calls++;
                    return new FakePageViewModel();
                },
                () => new Border(), StratBookPack.PackFeatureId);
            SettingsViewModel vm = new(svc, monitor, gate, new ThemeRegistry(), OperatingSystem.IsBrowser,
                null, [fake], null);
            using (sp)
            {
                try
                {
                    MountedSettingsPage page = vm.ContributedSettingsPages.Single();
                    await Assert.That(calls).IsEqualTo(0).Because("the gate is off at construction");
                    await Assert.That(page.IsBuilt).IsFalse();
                    await Assert.That(page.IsVisible).IsFalse();

                    svc.Write(s => s.Features.Overrides.Remove(StratBookPack.PackFeatureId));
                    await Assert.That(calls).IsEqualTo(1).Because("built once, the first time the gate turns on");
                    await Assert.That(page.IsBuilt).IsTrue();
                    await Assert.That(page.IsVisible).IsTrue();

                    svc.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);
                    svc.Write(s => s.Features.Overrides.Remove(StratBookPack.PackFeatureId));
                    await Assert.That(calls).IsEqualTo(1).Because("already built; a later toggle cycle does not rebuild");
                    await Assert.That(page.IsVisible).IsTrue();
                }
                finally
                {
                    vm.Dispose();
                }
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Plumbing: the WASM overlay open/close flow. OpenSettings on the browser service routes through the
    // wired shell callback to set MainViewModel.SettingsOverlay; the VM's Close then clears it back to null.
    [Test]
    public async Task BrowserOverlay_OpenThenClose_SetsAndClearsOverlay()
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                (SettingsViewModel vm, SettingsService _, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
                using (sp)
                {
                    using MainViewModel shell = new(library: TestLibraries.Empty());
                    BrowserWindowService windowService = new()
                    {
                        OnOpenSettings = shell.ShowSettingsOverlay
                    };

                    windowService.OpenSettings(vm);
                    await Assert.That(ReferenceEquals(shell.SettingsOverlay, vm)).IsTrue()
                        .Because("OpenSettings surfaces the VM as the shell's in-app overlay");

                    // Close from the VM (the footer Close button path) clears + disposes the overlay.
                    vm.CloseCommand.Execute(null);
                    await Assert.That(shell.SettingsOverlay).IsNull()
                        .Because("the VM's Close request clears the overlay");
                }
            });
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Plumbing: the ViewLocator resolves SettingsView for a SettingsViewModel (the ContentControl path both
    // hosts use). Locks the FullName "ViewModel"->"View" string-replace mapping against a future regression.
    [Test]
    public async Task ViewLocator_ResolvesSettingsView_ForViewModel()
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                (SettingsViewModel vm, SettingsService _, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
                using (sp)
                {
                    ViewLocator locator = new();
                    await Assert.That(locator.Match(vm)).IsTrue();

                    Control? view = locator.Build(vm);
                    await Assert.That(view).IsNotNull();
                    await Assert.That(view is SettingsView).IsTrue()
                        .Because("ViewModels.Settings.SettingsViewModel must map to Views.Settings.SettingsView");

                    vm.Dispose();
                }
            });
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ── P2a-ii: the per-feature toggle list ───────────────────────────────────────────────────────

    // (a) Toggling a row writes Overrides[id] to settings.json AND flips the gate's live decision. Uses a
    // TAB (no cascade), default-ON for the PowerUser default category, so the flip is directly observable.
    [Test]
    public async Task TogglingRow_WritesOverride_AndFlipsGate()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, IFeatureGate gate, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                FeatureToggleRow row = Row(vm, "tab.parser");
                await Assert.That(row.IsEnabled).IsTrue().Because("Parser is PowerUser-default-on");
                await Assert.That(gate.IsEnabled("tab.parser")).IsTrue();

                row.IsEnabled = false;

                await Assert.That(svc.Current.Features.Overrides.TryGetValue("tab.parser", out bool persisted)).IsTrue()
                    .Because("the toggle persisted an explicit override");
                await Assert.That(persisted).IsFalse();
                await Assert.That(gate.IsEnabled("tab.parser")).IsFalse().Because("the gate re-resolved from the override");
                await Assert.That(row.IsOverridden).IsTrue();

                string json = await File.ReadAllTextAsync(Path.Combine(dir, "settings.json"));
                await Assert.That(json).Contains("tab.parser");

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (b) A Required feature's row is IsRequired=true and its IsEnabled setter is a no-op. It stays enabled
    // and persists no override.
    [Test]
    public async Task RequiredRow_ToggleIsNoOp_StaysEnabled()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                FeatureToggleRow row = Row(vm, "tab.library");
                await Assert.That(row.IsRequired).IsTrue();
                await Assert.That(row.IsEnabled).IsTrue();

                row.IsEnabled = false; // the programmatic path (the UI toggle is disabled for Required rows)

                await Assert.That(row.IsEnabled).IsTrue().Because("a Required feature can never be disabled");
                await Assert.That(svc.Current.Features.Overrides.ContainsKey("tab.library")).IsFalse()
                    .Because("no override is persisted for a Required feature");

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (c) ResetOverrides clears every override: settings.json Overrides empties and rows revert to defaults.
    [Test]
    public async Task ResetOverrides_ClearsAll_RowsRevertToDefaults()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                Row(vm, "tab.parser").IsEnabled = false; // override a default-on tab off
                Row(vm, "tab.diagnostics").IsEnabled = true; // override a (power) default-off tab on
                await Assert.That(svc.Current.Features.Overrides.Count).IsGreaterThanOrEqualTo(2);

                vm.ResetOverridesCommand.Execute(null);

                await Assert.That(svc.Current.Features.Overrides.Count).IsEqualTo(0)
                    .Because("reset clears every per-feature override");
                await Assert.That(Row(vm, "tab.parser").IsEnabled).IsTrue().Because("reverted to the PowerUser default");
                await Assert.That(Row(vm, "tab.parser").IsOverridden).IsFalse();
                await Assert.That(Row(vm, "tab.diagnostics").IsEnabled).IsFalse().Because("reverted to the PowerUser default");

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (d) Changing category refreshes the rows to the new defaults and updates HiddenCount, WITHOUT
    // materialising any override (the critical no-corruption guarantee).
    [Test]
    public async Task CategoryChange_RefreshesRows_AndHiddenCount_WithoutOverrides()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                await Assert.That(Row(vm, "tab.parser").IsEnabled).IsTrue().Because("PowerUser default shows Parser");
                int hiddenAsPower = vm.HiddenCount;

                vm.SelectCategoryCommand.Execute(UserCategory.Consumer);

                await Assert.That(Row(vm, "tab.parser").IsEnabled).IsFalse().Because("Consumer default hides Parser");
                await Assert.That(vm.FeatureCategoryLabel).IsEqualTo("Consumer");
                await Assert.That(vm.HiddenCount).IsGreaterThan(hiddenAsPower).Because("a consumer hides more features");
                await Assert.That(svc.Current.Features.Overrides.Count).IsEqualTo(0)
                    .Because("a category change must never materialise per-feature overrides");

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (e) IsOverridden reflects whether an explicit override exists: true after a toggle, false after clear.
    [Test]
    public async Task IsOverridden_ReflectsExplicitOverride()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                FeatureToggleRow row = Row(vm, "tab.playback2d"); // default-on for all, not Required
                await Assert.That(row.IsOverridden).IsFalse();

                row.IsEnabled = false;
                await Assert.That(row.IsOverridden).IsTrue().Because("an explicit override now exists");

                row.ClearOverrideCommand.Execute(null);
                await Assert.That(row.IsOverridden).IsFalse().Because("the override was cleared");
                await Assert.That(svc.Current.Features.Overrides.ContainsKey("tab.playback2d")).IsFalse();
                await Assert.That(row.IsEnabled).IsTrue().Because("cleared → reverts to the default-on state");

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (f) A NON-leader group member is locked (follows its leader): a stray set persists NO override and
    // bounces back, and toggling the LEADER flips the whole group live (the correct control point).
    [Test]
    public async Task GroupFollowerRow_IsLocked_LeaderDrivesGroup()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, IFeatureGate gate, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                // chrome.debugger is a non-leader member of graphDebug (leader = analysis.breakpoints).
                FeatureToggleRow follower = Row(vm, "chrome.debugger");
                await Assert.That(follower.IsGroupFollower).IsTrue();
                await Assert.That(follower.IsInteractive).IsFalse().Because("a group follower is locked here");
                await Assert.That(gate.IsEnabled("chrome.debugger")).IsFalse().Because("PowerUser default");

                follower.IsEnabled = true; // stray programmatic set: must not persist an inert override

                await Assert.That(follower.IsEnabled).IsFalse().Because("a follower bounces to the leader-governed state");
                await Assert.That(follower.IsOverridden).IsFalse().Because("no phantom override for a follower");
                await Assert.That(svc.Current.Features.Overrides.ContainsKey("chrome.debugger")).IsFalse();

                // The LEADER's toggle flips the whole group live (tab.analysis is on for power → no cascade).
                Row(vm, "analysis.breakpoints").IsEnabled = true;
                await Assert.That(gate.IsEnabled("chrome.debugger")).IsTrue().Because("the group follows its leader");
                await Assert.That(Row(vm, "chrome.debugger").IsEnabled).IsTrue().Because("the follower row refreshed live");

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ── Extensions (item 5): the pack master switch + its rows ───────────────────────────────────

    // (a) The master row is present (Pack scope, indent 0), the pack's own tabs sit beneath it at
    // indent 1 with their sub-feature children one level deeper, and the sub-feature it docks in a CORE
    // tab (2D Playback's tag palette) is also present, flat. None of these appear in the generic lists.
    [Test]
    public async Task ExtensionsSection_ListsThePackWithItsChildren_BeneathTheMasterSwitch()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService _, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                FeatureToggleRow master = Row(vm, StratBookPack.PackFeatureId);
                await Assert.That(master.Scope).IsEqualTo(FeatureScope.Pack);
                await Assert.That(master.IndentLevel).IsEqualTo(0);
                await Assert.That(master.ScopeLabel).IsEqualTo("Extension");

                FeatureToggleRow stratBookTab = Row(vm, "tab.stratbook");
                await Assert.That(stratBookTab.IndentLevel).IsEqualTo(1);

                FeatureToggleRow stratBookChild = Row(vm, "stratbook.routing");
                await Assert.That(stratBookChild.IndentLevel).IsEqualTo(2)
                    .Because("nested under its own tab, which is nested under the master");

                FeatureToggleRow dockedInCoreTab = Row(vm, "playback2d.tagger");
                await Assert.That(dockedInCoreTab.IndentLevel).IsEqualTo(1)
                    .Because("no tab of its own under the pack, so it lists flat under the master");

                await Assert.That(vm.TabFeatureRows.Any(r => r.FeatureId == "tab.stratbook")).IsFalse();
                await Assert.That(vm.TabFeatureRows.Any(r => r.FeatureId == "playback2d.tagger")).IsFalse()
                    .Because("the docked sub-feature must not also appear under tab.playback2d in the generic list");
                await Assert.That(vm.ChromeFeatureRows.Any(r => r.FeatureId == StratBookPack.PackFeatureId)).IsFalse();

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (b) A pack child is interactive only while the master is on: the cascade already resolves its
    // IsEnabled off, but the row additionally locks (IsInteractive false, a lock hint) and bounces a
    // stray programmatic set rather than writing a new override. Flipping the master back on unlocks it
    // live and the master itself is never locked by its own switch.
    [Test]
    public async Task ExtensionChildRow_IsInteractiveOnlyWhileTheMasterIsOn()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                FeatureToggleRow master = Row(vm, StratBookPack.PackFeatureId);
                FeatureToggleRow child = Row(vm, "tab.situations");
                await Assert.That(child.IsInteractive).IsTrue().Because("the pack defaults on for PowerUser");

                svc.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);

                await Assert.That(child.IsEnabled).IsFalse().Because("cascaded off with the pack");
                await Assert.That(child.IsInteractive).IsFalse().Because("locked while its pack is off");
                await Assert.That(child.HasLockHint).IsTrue();
                await Assert.That(child.LockHint).Contains("extension is off");
                await Assert.That(master.IsInteractive).IsTrue()
                    .Because("the master switch is never locked by its own state");

                child.IsEnabled = true; // stray programmatic set while locked: must not persist

                await Assert.That(child.IsEnabled).IsFalse();
                await Assert.That(svc.Current.Features.Overrides.ContainsKey("tab.situations")).IsFalse()
                    .Because("no phantom override for a locked pack child");

                svc.Write(s => s.Features.Overrides.Remove(StratBookPack.PackFeatureId));

                await Assert.That(child.IsInteractive).IsTrue().Because("unlocked live once the pack is back on");
                await Assert.That(child.IsEnabled).IsTrue();

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Item 34: the master row says where the running copy came from ("(bundled)" for the compile-linked
    // pack this assembly configures), its children nothing; with no staged candidate there is no note.
    [Test]
    public async Task ExtensionMasterRow_ShowsTheSource()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService _, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                FeatureToggleRow master = Row(vm, StratBookPack.PackFeatureId);
                FeatureToggleRow child = Row(vm, "tab.situations");
                using (Assert.Multiple())
                {
                    await Assert.That(master.Source).IsEqualTo(PackSource.Bundled);
                    await Assert.That(master.HasSource).IsTrue();
                    await Assert.That(master.SourceLabel).IsEqualTo("(bundled)");
                    await Assert.That(master.HasLoadNote).IsFalse();
                    await Assert.That(child.HasSource).IsFalse();
                    await Assert.That(child.HasLoadNote).IsFalse();
                }

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Item 34: a staged copy that won shows "(installed update)"; the staged candidates the loader refused
    // show one line each under the row, and lock nothing, since the copy that is running works.
    [Test]
    public async Task ExtensionMasterRow_ShowsAnInstalledUpdate_AndWhyAHigherOneWasRefused()
    {
        string dir = NewTempDir();
        try
        {
            PackStatus real = FeaturePacks.Statuses.Single(s => s.Pack.Id == StratBookPack.PackId);
            LoadOutcome higher = new(
                "/extensions/net.demoviewer.pack.stratbook/1.1.0",
                FakeManifests.For(StratBookPack.PackId, "Strat Book", "1.1.0", "^1.0", "0.14.0"),
                LoadFailure.Incompatible,
                "Strat Book 1.1.0 needs CS2DemoKit 0.14.0; this app ships 0.13.0-beta0001");
            LoadOutcome broken = new("/extensions/net.demoviewer.pack.stratbook/1.0.9", null, LoadFailure.ManifestInvalid, "'version' is required.");
            PackStatus staged = real with
            {
                Source = new PackSource.Staged("/extensions/net.demoviewer.pack.stratbook/1.0.1"),
                Rejected = [higher, broken]
            };

            (SettingsViewModel vm, SettingsService svc, IFeatureGate _, ServiceProvider sp) = NewVm(dir, packStatuses: [staged]);
            using (sp)
            {
                FeatureToggleRow master = Row(vm, StratBookPack.PackFeatureId);
                using (Assert.Multiple())
                {
                    await Assert.That(master.SourceLabel).IsEqualTo("(installed update)");
                    await Assert.That(master.HasLoadNote).IsTrue();
                    await Assert.That(master.LoadNote).IsEqualTo(
                        "Update 1.1.0 was not loaded: Strat Book 1.1.0 needs CS2DemoKit 0.14.0; this app ships 0.13.0-beta0001"
                        + Environment.NewLine
                        + "An update in '1.0.9' was not loaded: 'version' is required.");
                    await Assert.That(master.IsIncompatible).IsFalse();
                    await Assert.That(master.IsInteractive).IsTrue().Because("a refused update locks nothing");
                    await Assert.That(master.IsEnabled).IsTrue();
                }

                master.IsEnabled = false;
                await Assert.That(svc.Current.Features.Overrides[StratBookPack.PackFeatureId]).IsFalse().Because("the switch still works");

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Item 33: the master row carries the manifest version (from FeaturePacks.Statuses by default), its
    // children none.
    [Test]
    public async Task ExtensionMasterRow_ShowsTheManifestVersion()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService _, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                FeatureToggleRow master = Row(vm, StratBookPack.PackFeatureId);
                FeatureToggleRow child = Row(vm, "tab.situations");
                using (Assert.Multiple())
                {
                    await Assert.That(master.Version).IsEqualTo(new StratBookPack().Manifest.Version.ToString());
                    await Assert.That(master.HasVersion).IsTrue();
                    await Assert.That(master.IsIncompatible).IsFalse();
                    await Assert.That(master.IsInteractive).IsTrue();
                    await Assert.That(child.HasVersion).IsFalse();
                }

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Item 33: a pack that failed the compatibility check has no catalog row, so Settings synthesizes a
    // locked master row from its status: off, not interactive, the "incompatible" lock hint, the reason in
    // user terms, searchable by name, and a stray set writes no override.
    [Test]
    public async Task IncompatibleExtension_GetsALockedRow_WithTheReason()
    {
        string dir = NewTempDir();
        try
        {
            ExtensionHostInfo host = new(SemVersion.Parse("1.0.0"), null, SemVersion.Parse("0.13.0-beta0001"));
            PackCompatibilityTests.ManifestPack pack = new("net.demoviewer.pack.future",
                FakeManifests.For("net.demoviewer.pack.future", "Future Book", "1.2.0", "^2.0", "*"));
            PackStatus status = PackStatus.Evaluate(pack, host);
            PackStatus[] statuses = [.. FeaturePacks.Statuses, status];

            (SettingsViewModel vm, SettingsService svc, IFeatureGate _, ServiceProvider sp) = NewVm(dir, packStatuses: statuses);
            using (sp)
            {
                FeatureToggleRow row = Row(vm, pack.FeatureId);
                using (Assert.Multiple())
                {
                    await Assert.That(row.Label).IsEqualTo("Future Book");
                    await Assert.That(row.Version).IsEqualTo("1.2.0");
                    await Assert.That(row.Scope).IsEqualTo(FeatureScope.Pack);
                    await Assert.That(row.IsIncompatible).IsTrue();
                    await Assert.That(row.Incompatibility).IsEqualTo("Future Book 1.2.0 needs app contract ^2.0; this app provides 1.0.0");
                    await Assert.That(row.IsEnabled).IsFalse();
                    await Assert.That(row.IsInteractive).IsFalse();
                    await Assert.That(row.HasLockHint).IsTrue();
                    await Assert.That(row.LockHint).IsEqualTo("incompatible");
                    await Assert.That(Row(vm, StratBookPack.PackFeatureId).IsInteractive).IsTrue()
                        .Because("the compatible extension is unaffected");
                }

                row.IsEnabled = true; // stray programmatic set while locked: must not persist

                using (Assert.Multiple())
                {
                    await Assert.That(row.IsEnabled).IsFalse();
                    await Assert.That(svc.Current.Features.Overrides.ContainsKey(pack.FeatureId)).IsFalse()
                        .Because("an override for an extension that composed nothing is inert");
                }

                vm.SettingsFilterText = "Future Book";
                await Assert.That(vm.ShowSectionExtensions).IsTrue().Because("the synthesized row is searchable by its name");

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (c) FeatureGate.HiddenCount excludes the pack's own row (it renders as the live master switch, not
    // a hidden feature), so turning the extension off counts exactly its tabs and sub-features, nothing
    // more. Derives the expected number from the catalog rather than hardcoding it.
    [Test]
    public async Task HiddenCount_ExcludesThePackRow_WhenTheExtensionIsOverriddenOff()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, IFeatureGate gate, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                vm.SelectCategoryCommand.Execute(UserCategory.Developer);
                svc.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);

                int expected = FeatureCatalog.All.Count(d => d.OwnerPackId == StratBookPack.PackFeatureId && !d.Required);
                await Assert.That(expected).IsGreaterThan(0);
                await Assert.That(gate.HiddenCount).IsEqualTo(expected);
                await Assert.That(vm.HiddenCount).IsEqualTo(expected);

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (d) Search finds the Extensions section by its generic keyword, by the pack's own label ("Strat
    // Book"), and by a row's label ("Situations") with no per-pack code: ExtensionsSectionMatches scans
    // the built rows. An unrelated term hides it, the sanity check against over-eager fuzzy matching.
    [Test]
    public async Task SettingsFilter_FindsExtensions_ByKeywordAndByPackRowLabels()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService _, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                vm.SettingsFilterText = "extension";
                await Assert.That(vm.ShowSectionExtensions).IsTrue();

                vm.SettingsFilterText = "Strat Book";
                await Assert.That(vm.ShowSectionExtensions).IsTrue().Because("matches the master row's own label");

                vm.SettingsFilterText = "situations";
                await Assert.That(vm.ShowSectionExtensions).IsTrue().Because("matches a built child row's label");

                vm.SettingsFilterText = "theme";
                await Assert.That(vm.ShowSectionExtensions).IsFalse();
                await Assert.That(vm.ShowSectionTheme).IsTrue();

                vm.SettingsFilterText = "idle";
                await Assert.That(vm.ShowSectionExtensions).IsFalse();

                vm.SettingsFilterText = "";
                await Assert.That(vm.ShowSectionExtensions).IsTrue();

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ── Extensions: the in-session re-index notice (architecture doc §8) ─────────────────────────

    // (e) No notice at a plain startup: it is feedback for an IN-SESSION flip, not persisted state.
    [Test]
    public async Task StratBookToggleNotice_IsNull_AtPlainStartup()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService _, IFeatureGate _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                await Assert.That(vm.StratBookToggleNotice).IsNull();
                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (f) Turning the extension ON shows "Counting…" immediately, then the demo count once the injected
    // probe lands, proven with a TaskCompletionSource so the interim state is actually observed, not
    // raced. TaskCompletionSource's continuation runs synchronously on SetResult's calling thread by
    // default, so the post-count assertion needs no dispatcher pump.
    [Test]
    public async Task TurningOnThePack_ShowsCounting_ThenTheReindexCount()
    {
        string dir = NewTempDir();
        try
        {
            TaskCompletionSource<int> tcs = new();
            (SettingsViewModel vm, SettingsService svc, IFeatureGate _, ServiceProvider sp) =
                NewVm(dir, new FakeReindexEstimate(() => tcs.Task));
            using (sp)
            {
                svc.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);
                svc.Write(s => s.Features.Overrides.Remove(StratBookPack.PackFeatureId)); // off -> on

                await Assert.That(vm.StratBookToggleNotice).IsEqualTo("Counting…");

                tcs.SetResult(7);

                await Assert.That(vm.StratBookToggleNotice).IsEqualTo("7 demos will be re-indexed in the background.");

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (g) Turning the extension OFF shows the one-line note immediately (no async probe involved).
    [Test]
    public async Task TurningOffThePack_ShowsTheStopsWorkNotice()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, IFeatureGate _, ServiceProvider sp) =
                NewVm(dir, new FakeReindexEstimate(() => Task.FromResult(0)));
            using (sp)
            {
                svc.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);

                await Assert.That(vm.StratBookToggleNotice)
                    .IsEqualTo("The Strat Book extension stops its background work. Its data stays on disk.");

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (h) A result that lands after a LATER flip is dropped: the generation guard, not a race the test
    // merely hopes doesn't happen. Flip on (probe A pending) -> flip off (probe A must never finish the
    // on-notice) -> flip on again (probe B) -> complete A -> notice still reflects B's eventual answer,
    // never A's.
    [Test]
    public async Task AStaleReindexCount_FromAnEarlierToggle_NeverOverwritesTheCurrentNotice()
    {
        string dir = NewTempDir();
        try
        {
            TaskCompletionSource<int> probeA = new();
            TaskCompletionSource<int> probeB = new();
            Queue<Func<Task<int>>> probes = new(new Func<Task<int>>[] { () => probeA.Task, () => probeB.Task });

            (SettingsViewModel vm, SettingsService svc, IFeatureGate _, ServiceProvider sp) =
                NewVm(dir, new FakeReindexEstimate(() => probes.Dequeue()()));
            using (sp)
            {
                svc.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);
                svc.Write(s => s.Features.Overrides.Remove(StratBookPack.PackFeatureId)); // on: starts probe A
                await Assert.That(vm.StratBookToggleNotice).IsEqualTo("Counting…");

                svc.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = true); // still on: no transition, no new probe
                svc.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false); // off
                svc.Write(s => s.Features.Overrides.Remove(StratBookPack.PackFeatureId)); // on again: starts probe B

                probeA.SetResult(999); // A's answer must never reach the notice now

                await Assert.That(vm.StratBookToggleNotice).IsEqualTo("Counting…")
                    .Because("probe A's stale result was dropped by generation, not applied");

                probeB.SetResult(3);

                await Assert.That(vm.StratBookToggleNotice).IsEqualTo("3 demos will be re-indexed in the background.");

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     The "delete extension data" row (item 24): available with the pack off (the master switch
    ///     override sits off throughout; the row names nothing about the gate), Arm's confirmation names
    ///     the user-work stores by label and size and leaves out the regenerable one, Cancel calls the
    ///     remover for neither InventoryAsync's result nor a delete, and Confirm calls DeleteAsync exactly
    ///     once and reports the count.
    /// </summary>
    [Test]
    public async Task DeleteExtensionData_Arm_ListsUserWorkStores_CancelTouchesNothing_ConfirmDeletes()
    {
        string dir = NewTempDir();
        try
        {
            PackDataInventory inventory = new(
            [
                new StoreInventoryItem(new StoreDescriptor("strats", "Strats", StoreRoot.Config, ["strats"], true), 3, 4096),
                new StoreInventoryItem(new StoreDescriptor("round-index", "Round Index", StoreRoot.Cache, ["round-index"], false), 10, 1_048_576)
            ]);
            PackDataRemovalResult result = new(true, inventory, 1);
            FakePackDataRemoval fake = new(inventory, result);
            (SettingsViewModel vm, SettingsService svc, _, ServiceProvider sp) = NewVm(dir, dataRemoval: fake);
            using (sp)
            {
                svc.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false); // the pack stays off throughout
                ExtensionDataActionViewModel row = vm.ExtensionDataActions.Single();

                await row.ArmCommand.ExecuteAsync(null);

                using (Assert.Multiple())
                {
                    await Assert.That(fake.InventoryCalls).IsEqualTo(1);
                    await Assert.That(row.IsConfirming).IsTrue();
                    await Assert.That(row.ConfirmationText).Contains("Strats: 4.0 KB");
                    await Assert.That(row.ConfirmationText).DoesNotContain("Round Index")
                        .Because("only the user-work stores are named; the regenerable one is just \"caches will be rebuilt\"");
                }

                row.CancelCommand.Execute(null);
                using (Assert.Multiple())
                {
                    await Assert.That(row.IsConfirming).IsFalse();
                    await Assert.That(fake.DeleteCalls).IsEqualTo(0).Because("Cancel must never reach the remover's delete");
                }

                await row.ArmCommand.ExecuteAsync(null);
                await row.ConfirmCommand.ExecuteAsync(null);

                using (Assert.Multiple())
                {
                    await Assert.That(fake.DeleteCalls).IsEqualTo(1);
                    await Assert.That(row.IsConfirming).IsFalse();
                    await Assert.That(row.StatusText).Contains("Deleted 13 files");
                }

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task DeleteExtensionData_Arm_WithNothingOnDisk_ShowsNothingToDelete_NoConfirmation()
    {
        string dir = NewTempDir();
        try
        {
            FakePackDataRemoval fake = new(PackDataInventory.Empty, PackDataRemovalResult.NotRun);
            (SettingsViewModel vm, _, _, ServiceProvider sp) = NewVm(dir, dataRemoval: fake);
            using (sp)
            {
                ExtensionDataActionViewModel row = vm.ExtensionDataActions.Single();
                await row.ArmCommand.ExecuteAsync(null);

                using (Assert.Multiple())
                {
                    await Assert.That(row.IsConfirming).IsFalse();
                    await Assert.That(row.StatusText).IsEqualTo("Nothing to delete.");
                }

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     While a delete is in flight the pack's own master switch locks (item 24 review): flipping it
    ///     mid-delete is exactly the race <c>StratBookDataRemoval</c>'s own gate re-checks guard against,
    ///     so the row must not even offer the toggle meanwhile.
    /// </summary>
    [Test]
    public async Task DeleteExtensionData_WhileBusy_LocksTheExtensionsMasterRow()
    {
        string dir = NewTempDir();
        try
        {
            TaskCompletionSource<PackDataRemovalResult> gate = new();
            FakePackDataRemoval fake = new(PackDataInventory.Empty, PackDataRemovalResult.NotRun, gate);
            (SettingsViewModel vm, _, _, ServiceProvider sp) = NewVm(dir, dataRemoval: fake);
            using (sp)
            {
                FeatureToggleRow master = Row(vm, StratBookPack.PackFeatureId);
                ExtensionDataActionViewModel row = vm.ExtensionDataActions.Single();
                await Assert.That(master.IsInteractive).IsTrue().Because("idle: the switch is usable");

                Task confirm = row.ConfirmCommand.ExecuteAsync(null);

                using (Assert.Multiple())
                {
                    await Assert.That(row.IsBusy).IsTrue();
                    await Assert.That(master.IsDeleteBusy).IsTrue();
                    await Assert.That(master.IsInteractive).IsFalse().Because("the switch must not race the delete");
                    await Assert.That(master.HasLockHint).IsTrue();
                }

                gate.SetResult(PackDataRemovalResult.NotRun);
                await confirm;

                using (Assert.Multiple())
                {
                    await Assert.That(master.IsDeleteBusy).IsFalse();
                    await Assert.That(master.IsInteractive).IsTrue().Because("back to normal once the delete finishes");
                }

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     Two ConfirmCommand executions landing before the UI has a chance to disable the button must
    ///     delete exactly once: the command's own IsBusy guard, not the XAML binding, is what makes this
    ///     safe (item 24 review).
    /// </summary>
    [Test]
    public async Task DeleteExtensionData_TwoConcurrentConfirms_DeleteExactlyOnce()
    {
        string dir = NewTempDir();
        try
        {
            TaskCompletionSource<PackDataRemovalResult> gate = new();
            FakePackDataRemoval fake = new(PackDataInventory.Empty, PackDataRemovalResult.NotRun, gate);
            (SettingsViewModel vm, _, _, ServiceProvider sp) = NewVm(dir, dataRemoval: fake);
            using (sp)
            {
                ExtensionDataActionViewModel row = vm.ExtensionDataActions.Single();

                Task first = row.ConfirmCommand.ExecuteAsync(null);
                Task second = row.ConfirmCommand.ExecuteAsync(null);

                await Assert.That(fake.DeleteCalls).IsEqualTo(1).Because("the second call sees IsBusy already true and no-ops");

                gate.SetResult(new PackDataRemovalResult(true, PackDataInventory.Empty, 0));
                await Task.WhenAll(first, second);

                await Assert.That(fake.DeleteCalls).IsEqualTo(1);

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static int ScanNonBackground(WriteableBitmap bmp)
    {
        PixelSize size = bmp.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
        }

        int nonBg = 0;
        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            byte b = buffer[i], g = buffer[i + 1], r = buffer[i + 2];
            if (r > 60 || g > 60 || b > 60)
            {
                nonBg++;
            }
        }

        return nonBg;
    }
}
