#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Services;
using DemoViewer.NET.Theming;
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
    // registered in the container so the provider disposes it.
    private static (SettingsViewModel Vm, SettingsService Svc, IFeatureGate Gate, ServiceProvider Sp) NewVm(string dir)
    {
        SettingsService svc = new(dir);
        ServiceCollection services = new();
        services.Configure<AppSettings>(svc.Configuration);
        services.AddSingleton<IFeatureGate>(s =>
            new FeatureGate(s.GetRequiredService<IOptionsMonitor<AppSettings>>(), false));
        ServiceProvider sp = services.BuildServiceProvider();
        IOptionsMonitor<AppSettings> monitor = sp.GetRequiredService<IOptionsMonitor<AppSettings>>();
        IFeatureGate gate = sp.GetRequiredService<IFeatureGate>();
        SettingsViewModel vm = new(svc, monitor, gate, new ThemeRegistry());
        return (vm, svc, gate, sp);
    }

    // Find a feature row by its catalog id across both grouped collections.
    private static FeatureToggleRow Row(SettingsViewModel vm, string featureId) =>
        vm.TabFeatureRows.Concat(vm.ChromeFeatureRows).First(r => r.FeatureId == featureId);

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
                    frame!.Save(outPath);
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
