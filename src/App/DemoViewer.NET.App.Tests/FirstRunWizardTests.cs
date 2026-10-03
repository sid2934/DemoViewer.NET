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
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Features;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Setup;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Covers the P2b first-run setup wizard over a temp-dir <see cref="SettingsService" /> (never the real
///     user config): <see cref="SettingsService.NeedsFirstRun" /> is true with no <c>settings.json</c> and
///     FALSE after either Finish or Skip persists one; Finish lands the chosen category + folders; the VM
///     default-selects PowerUser and seeds from current settings; step navigation clamps at both ends; and
///     the view renders non-blank headlessly. <see cref="NotInParallelAttribute" /> because the render case
///     shares the single headless UI session.
/// </summary>
[NotInParallel]
[Category("Render")]
public class FirstRunWizardTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvwizard_" + Guid.NewGuid().ToString("N"));
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

    // (a) NeedsFirstRun is true with no settings.json; Finish writes one (category + folders land in it) and
    // NeedsFirstRun flips to false.
    [Test]
    public async Task NeedsFirstRun_TrueUntilFinish_ThenFalse_WithChoicesPersisted()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            await Assert.That(svc.NeedsFirstRun).IsTrue().Because("no settings.json exists yet");

            FirstRunWizardViewModel vm = new(svc);
            vm.SelectCategoryCommand.Execute(UserCategory.Developer);
            vm.Folders.Add("/demos/aim");
            vm.Folders.Add("/demos/retake");
            vm.FinishCommand.Execute(null);

            await Assert.That(svc.NeedsFirstRun).IsFalse().Because("Finish created settings.json");
            await Assert.That(svc.Current.UserCategory).IsEqualTo(UserCategory.Developer);
            await Assert.That(svc.Current.Library.Folders.Contains("/demos/aim")).IsTrue();
            await Assert.That(svc.Current.Library.Folders.Contains("/demos/retake")).IsTrue();

            string json = await File.ReadAllTextAsync(Path.Combine(dir, "settings.json"));
            await Assert.That(json).Contains("Developer");
            await Assert.That(json).Contains("/demos/aim");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (b) Skip also results in NeedsFirstRun false (settings.json exists) with the default PowerUser tier and
    // no folders: the basis-preserving write on a genuine first run.
    [Test]
    public async Task Skip_CreatesSettings_WithDefaultPowerUser()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            FirstRunWizardViewModel vm = new(svc);

            vm.SkipCommand.Execute(null);

            await Assert.That(svc.NeedsFirstRun).IsFalse().Because("Skip still materialises settings.json");
            await Assert.That(File.Exists(Path.Combine(dir, "settings.json"))).IsTrue();
            await Assert.That(svc.Current.UserCategory).IsEqualTo(UserCategory.PowerUser)
                .Because("a skipped first run keeps the PowerUser default");
            await Assert.That(svc.Current.Library.Folders.Length).IsEqualTo(0);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Skip on a RE-RUN must not clobber an existing configuration (the basis is preserved).
    [Test]
    public async Task Skip_OnRerun_PreservesExistingConfig()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            // A prior run persisted real choices.
            svc.Write(s =>
            {
                s.UserCategory = UserCategory.Developer;
                s.Library.Folders = ["/demos/keep"];
            });

            FirstRunWizardViewModel vm = new(svc);
            vm.SkipCommand.Execute(null);

            await Assert.That(svc.Current.UserCategory).IsEqualTo(UserCategory.Developer)
                .Because("Skip preserves the persisted basis on a re-run");
            await Assert.That(svc.Current.Library.Folders.Contains("/demos/keep")).IsTrue();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (c) The wizard sets UserCategory + Library.Folders correctly; and default-selects PowerUser / seeds
    // folders from the current settings.
    [Test]
    public async Task DefaultSelectsPowerUser_AndSeedsFromCurrent()
    {
        string dir = NewTempDir();
        try
        {
            // Fresh (no file): the default tier is PowerUser and there are no seeded folders.
            SettingsService freshSvc = new(dir);
            FirstRunWizardViewModel fresh = new(freshSvc);
            await Assert.That(fresh.SelectedCategory).IsEqualTo(UserCategory.PowerUser)
                .Because("a first run pre-selects PowerUser");
            await Assert.That(fresh.Folders.Count).IsEqualTo(0);

            // Re-run: the VM seeds from the persisted choices.
            freshSvc.Write(s =>
            {
                s.UserCategory = UserCategory.Consumer;
                s.Library.Folders = ["/demos/seeded"];
            });
            FirstRunWizardViewModel rerun = new(freshSvc);
            await Assert.That(rerun.SelectedCategory).IsEqualTo(UserCategory.Consumer);
            await Assert.That(rerun.Folders.Contains("/demos/seeded")).IsTrue();

            // Changing the selection + folders then Finishing writes exactly those.
            rerun.SelectCategoryCommand.Execute(UserCategory.Developer);
            rerun.RemoveFolderCommand.Execute("/demos/seeded");
            rerun.Folders.Add("/demos/new");
            rerun.FinishCommand.Execute(null);

            await Assert.That(freshSvc.Current.UserCategory).IsEqualTo(UserCategory.Developer);
            await Assert.That(freshSvc.Current.Library.Folders.Contains("/demos/new")).IsTrue();
            await Assert.That(freshSvc.Current.Library.Folders.Contains("/demos/seeded")).IsFalse();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (d) Step navigation clamps at both ends; the footer flags track the step. An EXISTING install (first
    // run already completed) has no Extensions step, so these are the original four: Welcome/Category/
    // Folders/Done at 0..3, same indices this test has always pinned.
    [Test]
    public async Task StepNavigation_ClampsAtBounds_AndTracksFooter()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService existing = new(dir);
            existing.Write(s => s.FirstRunCompleted = true);
            FirstRunWizardViewModel vm = new(existing);

            await Assert.That(vm.CurrentStep).IsEqualTo(0);
            await Assert.That(vm.IsExtensionsStep).IsFalse()
                .Because("an existing install never gets the Extensions step");
            await Assert.That(vm.CanGoBack).IsFalse();
            await Assert.That(vm.ShowNext).IsTrue();
            await Assert.That(vm.ShowFinish).IsFalse();

            vm.BackCommand.Execute(null); // clamp at 0
            await Assert.That(vm.CurrentStep).IsEqualTo(0);

            vm.NextCommand.Execute(null); // 1
            vm.NextCommand.Execute(null); // 2
            vm.NextCommand.Execute(null); // 3
            await Assert.That(vm.CurrentStep).IsEqualTo(3);
            await Assert.That(vm.IsDoneStep).IsTrue();
            await Assert.That(vm.ShowNext).IsFalse();
            await Assert.That(vm.ShowFinish).IsTrue();
            await Assert.That(vm.ShowSkip).IsFalse();

            vm.NextCommand.Execute(null); // clamp at 3
            await Assert.That(vm.CurrentStep).IsEqualTo(3);

            vm.BackCommand.Execute(null); // 2
            await Assert.That(vm.CurrentStep).IsEqualTo(2);
            await Assert.That(vm.IsFoldersStep).IsTrue();
            await Assert.That(vm.CanGoBack).IsTrue();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // A FRESH install (no settings.json yet) inserts Extensions right before Done, one card per
    // FeatureScope.Pack catalog row: five steps, 0..4. Folders stays at index 2 either way.
    [Test]
    public async Task StepNavigation_FreshInstall_InsertsExtensionsStep_BeforeDone()
    {
        string dir = NewTempDir();
        try
        {
            FirstRunWizardViewModel vm = new(new SettingsService(dir));
            await Assert.That(vm.PackOptions.Count).IsGreaterThan(0)
                .Because("the Strat Book pack row is in the live catalog");

            vm.CurrentStep = 2;
            await Assert.That(vm.IsFoldersStep).IsTrue();

            vm.NextCommand.Execute(null); // 3: Extensions
            await Assert.That(vm.CurrentStep).IsEqualTo(3);
            await Assert.That(vm.IsExtensionsStep).IsTrue();
            await Assert.That(vm.ShowNext).IsTrue();
            await Assert.That(vm.ShowFinish).IsFalse();

            vm.NextCommand.Execute(null); // 4: Done
            await Assert.That(vm.CurrentStep).IsEqualTo(4);
            await Assert.That(vm.IsDoneStep).IsTrue();
            await Assert.That(vm.ShowNext).IsFalse();
            await Assert.That(vm.ShowFinish).IsTrue();

            vm.NextCommand.Execute(null); // clamp at 4
            await Assert.That(vm.CurrentStep).IsEqualTo(4);

            vm.BackCommand.Execute(null); // back to 3: Extensions
            await Assert.That(vm.IsExtensionsStep).IsTrue();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Completed fires on both Finish and Skip (the host's cue to close the window / clear the overlay).
    [Test]
    public async Task Completed_Fires_OnFinishAndSkip()
    {
        string dir = NewTempDir();
        try
        {
            FirstRunWizardViewModel finishVm = new(new SettingsService(dir));
            bool finished = false;
            finishVm.Completed += (_, _) => finished = true;
            finishVm.FinishCommand.Execute(null);
            await Assert.That(finished).IsTrue();

            string dir2 = NewTempDir();
            try
            {
                FirstRunWizardViewModel skipVm = new(new SettingsService(dir2));
                bool skipped = false;
                skipVm.Completed += (_, _) => skipped = true;
                skipVm.SkipCommand.Execute(null);
                await Assert.That(skipped).IsTrue();
            }
            finally
            {
                Cleanup(dir2);
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // WASM plumbing: OpenFirstRunWizard on the browser service routes through the wired shell callback to
    // set MainViewModel.FirstRunOverlay; the wizard's Completed (Finish / Skip) then clears it back to null.
    // Mirrors the Settings overlay test. This is the relaunch path on the browser host.
    [Test]
    public async Task BrowserOverlay_OpenThenComplete_SetsAndClearsOverlay()
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                FirstRunWizardViewModel vm = new(new SettingsService(dir));
                using MainViewModel shell = new(library: TestLibraries.Empty());
                BrowserWindowService windowService = new()
                {
                    OnShowFirstRun = shell.ShowFirstRunOverlay
                };

                windowService.ShowFirstRunWizard(vm);
                await Assert.That(ReferenceEquals(shell.FirstRunOverlay, vm)).IsTrue()
                    .Because("ShowFirstRunWizard surfaces the VM as the shell's in-app overlay on WASM");

                vm.SkipCommand.Execute(null); // Completed → clears + detaches the overlay
                await Assert.That(shell.FirstRunOverlay).IsNull()
                    .Because("the wizard's Completed clears the overlay");
            });
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Render smoke: the real FirstRunWizardView bound to a real VM (on the category step) draws far more than
    // an empty background.
    [Test]
    public async Task FirstRunWizardView_Renders_NonBlank()
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                FirstRunWizardViewModel vm = new(new SettingsService(dir))
                {
                    CurrentStep = 1
                };
                FirstRunWizardView view = new()
                {
                    DataContext = vm
                };
                Window window = new()
                {
                    Width = 640,
                    Height = 560,
                    Content = view
                };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                WriteableBitmap? frame = window.CaptureRenderedFrame();
                await Assert.That(frame).IsNotNull();

                string outPath = Path.Combine(HeadlessSession.ArtifactDir, "first-run-wizard.png");
                frame!.Save(outPath, new PngBitmapEncoderOptions());
                int nonBg = ScanNonBackground(frame);
                Console.WriteLine($"[wizard] {outPath} nonBg={nonBg}");

                await Assert.That(nonBg).IsGreaterThan(200);
                await Assert.That(File.Exists(outPath)).IsTrue();
            });
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Render the FOLDERS step with a detected CS2 demos folder so the new suggestion card is exercised.
    [Test]
    public async Task FirstRunWizardView_FoldersStep_WithDetectedFolder_Renders()
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                const string demos =
                    @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\replays";
                FirstRunWizardViewModel vm = new(
                    new SettingsService(dir), () => new Cs2DemosLookup(demos, []))
                {
                    CurrentStep = 2 // folders step
                };
                FirstRunWizardView view = new()
                {
                    DataContext = vm
                };
                Window window = new()
                {
                    Width = 640,
                    Height = 560,
                    Content = view
                };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                WriteableBitmap? frame = window.CaptureRenderedFrame();
                await Assert.That(frame).IsNotNull();

                string outPath = Path.Combine(HeadlessSession.ArtifactDir, "first-run-wizard-folders.png");
                frame!.Save(outPath, new PngBitmapEncoderOptions());
                int nonBg = ScanNonBackground(frame);
                Console.WriteLine($"[wizard-folders] {outPath} nonBg={nonBg}");

                await Assert.That(vm.HasDetectedDemosFolder).IsTrue();
                await Assert.That(nonBg).IsGreaterThan(200);
            });
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Render the FOLDERS step when auto-detection found nothing: the not-found notice listing searched libs.
    [Test]
    public async Task FirstRunWizardView_FoldersStep_NotFoundNotice_Renders()
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                Cs2DemosLookup lookup = new(null, [@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"]);
                FirstRunWizardViewModel vm = new(new SettingsService(dir), () => lookup)
                {
                    CurrentStep = 2
                };
                FirstRunWizardView view = new()
                {
                    DataContext = vm
                };
                Window window = new()
                {
                    Width = 640,
                    Height = 560,
                    Content = view
                };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                WriteableBitmap? frame = window.CaptureRenderedFrame();
                await Assert.That(frame).IsNotNull();

                string outPath = Path.Combine(HeadlessSession.ArtifactDir, "first-run-wizard-notfound.png");
                frame!.Save(outPath, new PngBitmapEncoderOptions());
                Console.WriteLine($"[wizard-notfound] {outPath} nonBg={ScanNonBackground(frame)}");

                await Assert.That(vm.ShowNotFoundNotice).IsTrue();
            });
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ── Item 6: the Extensions step (one card per FeatureScope.Pack catalog row) ───────────────────

    // Builds a live FeatureGate over svc's configuration (the FeatureGateTests wiring) so a test can
    // check what the gate resolves after the wizard writes, not just what landed in the override dict.
    private static async Task WithGate(SettingsService svc, Func<FeatureGate, Task> body)
    {
        ServiceCollection services = new();
        services.Configure<AppSettings>(svc.Configuration);
        using ServiceProvider sp = services.BuildServiceProvider();
        IOptionsMonitor<AppSettings> monitor = sp.GetRequiredService<IOptionsMonitor<AppSettings>>();
        using FeatureGate gate = new(monitor, false);
        await body(gate);
    }

    // Item 33: the step asks only about packs in the catalog, and the catalog is composed from the
    // compatible packs, so an extension that failed the compatibility check is absent here (Settings is
    // where its reason shows). Proven on the live catalog against FeaturePacks.Compatible, and on a fake
    // pair where one pack fails: the catalog built from the compatible subset drives a wizard with one card.
    [Test]
    public async Task ExtensionsStep_AsksOnlyAboutCompatibleExtensions()
    {
        string dir = NewTempDir();
        try
        {
            FirstRunWizardViewModel live = new(new SettingsService(dir));
            await Assert.That(live.PackOptions.Select(o => o.FeatureId))
                .IsEquivalentTo(FeaturePacks.Compatible.Select(p => p.FeatureId));

            ExtensionHostInfo host = new(SemVersion.Parse("1.0.0"), null, SemVersion.Parse("0.13.0-beta0001"));
            IFeaturePack incompatible = new CatalogPack("pack.future", FakeManifests.For("net.demoviewer.pack.future", "Future", "2.0.0", "^2.0", "*"));
            IFeaturePack fine = new CatalogPack("pack.fine", FakeManifests.For("net.demoviewer.pack.fine", "Fine"));
            IReadOnlyList<PackStatus> statuses = PackStatus.Evaluate([incompatible, fine], host);
            FeatureDescriptor[] catalog = FeatureCatalog.Build([.. statuses.Where(s => s.IsCompatible).Select(s => s.Pack)]);

            FirstRunWizardViewModel vm = new(new SettingsService(dir), packs: catalog.Where(d => d.Scope == FeatureScope.Pack));
            using (Assert.Multiple())
            {
                await Assert.That(statuses[0].IsCompatible).IsFalse();
                await Assert.That(vm.PackOptions.Select(o => o.FeatureId)).IsEquivalentTo(["pack.fine"]);
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // A pack with one catalog row, so FeatureCatalog.Build accepts it.
    private sealed class CatalogPack(string featureId, ExtensionManifest manifest) : IFeaturePack
    {
        public string Id => manifest.Id;
        public string FeatureId => featureId;
        public ExtensionManifest Manifest => manifest;

        public IEnumerable<FeatureDescriptor> Features =>
            [new(featureId, FeatureScope.Pack, manifest.Name, "d", null, null, false, new Dictionary<UserCategory, bool> { [UserCategory.PowerUser] = true })];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IPackContributions contributions, IServiceProvider sp)
        {
        }
    }

    // The step appears on a fresh config dir (CurrentStep==3 after three Nexts) and not on an existing
    // one (the same three Nexts land on Done instead).
    [Test]
    public async Task ExtensionsStep_OnFreshDir_NotOnExistingDir()
    {
        string freshDir = NewTempDir();
        string existingDir = NewTempDir();
        try
        {
            FirstRunWizardViewModel fresh = new(new SettingsService(freshDir));
            fresh.NextCommand.Execute(null); // 1
            fresh.NextCommand.Execute(null); // 2
            fresh.NextCommand.Execute(null); // 3
            await Assert.That(fresh.IsExtensionsStep).IsTrue();

            SettingsService existingSvc = new(existingDir);
            existingSvc.Write(s => s.FirstRunCompleted = true);
            FirstRunWizardViewModel existing = new(existingSvc);
            existing.NextCommand.Execute(null); // 1
            existing.NextCommand.Execute(null); // 2
            existing.NextCommand.Execute(null); // 3
            await Assert.That(existing.IsDoneStep).IsTrue()
                .Because("an existing install has no Extensions step to land on");
        }
        finally
        {
            Cleanup(freshDir);
            Cleanup(existingDir);
        }
    }

    // Declining writes an explicit "off" override and the gate cascades every tab the pack owns off
    // with it, the same as StratBookPackTests pins for a hand-written override.
    [Test]
    public async Task ExtensionsStep_Decline_WritesOverrideOff_AndGateResolvesOff()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            FirstRunWizardViewModel vm = new(svc);
            PackOptionViewModel stratbook = vm.PackOptions.Single(p => p.FeatureId == "pack.stratbook");
            await Assert.That(stratbook.Enabled).IsTrue().Because("decision 2: default selection is on");

            stratbook.Enabled = false;
            vm.FinishCommand.Execute(null);

            await Assert.That(svc.Current.Features.Overrides["pack.stratbook"]).IsFalse();
            await WithGate(svc, async gate =>
            {
                await Assert.That(gate.IsEnabled("pack.stratbook")).IsFalse();
                await Assert.That(gate.IsEnabled("tab.stratbook")).IsFalse()
                    .Because("every tab the pack owns cascades off with it");
            });
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Regression: a category change must never undo a decline. pack.stratbook defaults on for every
    // category, so a Reseed that ignored the "already answered" guard would flip this back to true.
    [Test]
    public async Task ExtensionsStep_Decline_ThenChangeCategory_StaysDeclined()
    {
        string dir = NewTempDir();
        try
        {
            FirstRunWizardViewModel vm = new(new SettingsService(dir));
            PackOptionViewModel stratbook = vm.PackOptions.Single(p => p.FeatureId == "pack.stratbook");

            stratbook.Enabled = false;
            vm.SelectCategoryCommand.Execute(UserCategory.Developer);

            await Assert.That(stratbook.Enabled).IsFalse()
                .Because("the question is already answered; a later category change must not reseed it");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Accepting (the default, unchanged) writes an explicit "on" override and the gate resolves on.
    [Test]
    public async Task ExtensionsStep_Accept_ResolvesOn()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            FirstRunWizardViewModel vm = new(svc);
            vm.FinishCommand.Execute(null); // every PackOptions.Enabled left at its default-on seed

            await Assert.That(svc.Current.Features.Overrides["pack.stratbook"]).IsTrue()
                .Because("Finish writes the answer explicitly, on or off, like Category and Folders");
            await WithGate(svc, async gate =>
                await Assert.That(gate.IsEnabled("pack.stratbook")).IsTrue());
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Skip never answers the pack question (basis-preserving, like Category and Folders): no override
    // is written, so the pack resolves on purely from the catalog default.
    [Test]
    public async Task Skip_NeverWritesPackOverride_AndGateResolvesOn()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            FirstRunWizardViewModel vm = new(svc);
            vm.SkipCommand.Execute(null);

            await Assert.That(svc.Current.Features.Overrides.ContainsKey("pack.stratbook")).IsFalse();
            await WithGate(svc, async gate =>
                await Assert.That(gate.IsEnabled("pack.stratbook")).IsTrue());
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // The upgrade case (decision 2): a settings.json that predates pack.stratbook (FirstRunCompleted
    // true, no override for the key at all) never shows the Extensions step, and the gate still
    // resolves the pack on purely from the catalog default.
    [Test]
    public async Task SettingsWithNoPackKey_NeverShowsExtensionsStep_AndGateResolvesOn()
    {
        string dir = NewTempDir();
        try
        {
            string json = """
                {
                  "FirstRunCompleted": true,
                  "UserCategory": "PowerUser",
                  "Features": { "Overrides": { "chrome.output": true } }
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), json);

            SettingsService svc = new(dir);
            await Assert.That(svc.NeedsFirstRun).IsFalse().Because("FirstRunCompleted is already true");

            FirstRunWizardViewModel vm = new(svc);
            vm.NextCommand.Execute(null); // 1
            vm.NextCommand.Execute(null); // 2
            vm.NextCommand.Execute(null); // 3
            await Assert.That(vm.IsDoneStep).IsTrue().Because("no Extensions step for this upgrade");

            await WithGate(svc, async gate =>
                await Assert.That(gate.IsEnabled("pack.stratbook")).IsTrue()
                    .Because("no override means the catalog default, which is on for every category"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Generic over FeatureScope.Pack rows: two synthetic packs (never registered in the real catalog)
    // each get their own card, seeded from their own Defaults, with no new code in the VM or view.
    [Test]
    public async Task ExtensionsStep_IsGeneric_TwoPacks_EachGetsItsOwnQuestion()
    {
        string dir = NewTempDir();
        try
        {
            FeatureDescriptor[] packs =
            [
                new(
                    "pack.testone", FeatureScope.Pack, "Test Pack One", "First synthetic pack.",
                    null, null, false, new Dictionary<UserCategory, bool>
                    {
                        [UserCategory.Consumer] = true,
                        [UserCategory.PowerUser] = true,
                        [UserCategory.Developer] = true
                    }),
                new(
                    "pack.testtwo", FeatureScope.Pack, "Test Pack Two", "Second synthetic pack.",
                    null, null, false, new Dictionary<UserCategory, bool>
                    {
                        [UserCategory.Consumer] = false,
                        [UserCategory.PowerUser] = false,
                        [UserCategory.Developer] = true
                    })
            ];

            SettingsService svc = new(dir);
            FirstRunWizardViewModel vm = new(svc, packs: packs);

            await Assert.That(vm.PackOptions.Count).IsEqualTo(2);
            await Assert.That(vm.PackOptions[0].FeatureId).IsEqualTo("pack.testone");
            await Assert.That(vm.PackOptions[0].Copy).IsEqualTo("First synthetic pack.")
                .Because("an id with no bespoke copy entry falls back to the descriptor's own Description");
            await Assert.That(vm.PackOptions[0].Enabled).IsTrue();

            await Assert.That(vm.PackOptions[1].FeatureId).IsEqualTo("pack.testtwo");
            await Assert.That(vm.PackOptions[1].Enabled).IsFalse()
                .Because("PowerUser (the first-run default category) has no default-on entry for this pack");

            // Category (step 1) is reached before Extensions (step 3): picking Developer reseeds the
            // untouched, unoverridden pack.testtwo to ITS default for that category.
            vm.SelectCategoryCommand.Execute(UserCategory.Developer);
            await Assert.That(vm.PackOptions[1].Enabled).IsTrue()
                .Because("Developer has a default-on entry for this pack, and the question is not yet answered");

            vm.FinishCommand.Execute(null);
            await Assert.That(svc.Current.Features.Overrides["pack.testone"]).IsTrue();
            await Assert.That(svc.Current.Features.Overrides["pack.testtwo"]).IsTrue();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Render the EXTENSIONS step on a fresh dir (CurrentStep 3): the pack card(s) draw.
    [Test]
    public async Task FirstRunWizardView_ExtensionsStep_Renders()
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                FirstRunWizardViewModel vm = new(new SettingsService(dir))
                {
                    CurrentStep = 3
                };
                FirstRunWizardView view = new()
                {
                    DataContext = vm
                };
                Window window = new()
                {
                    Width = 640,
                    Height = 560,
                    Content = view
                };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                WriteableBitmap? frame = window.CaptureRenderedFrame();
                await Assert.That(frame).IsNotNull();

                string outPath = Path.Combine(HeadlessSession.ArtifactDir, "first-run-wizard-extensions.png");
                frame!.Save(outPath, new PngBitmapEncoderOptions());
                int nonBg = ScanNonBackground(frame);
                Console.WriteLine($"[wizard-extensions] {outPath} nonBg={nonBg}");

                await Assert.That(vm.IsExtensionsStep).IsTrue();
                await Assert.That(nonBg).IsGreaterThan(200);
            });
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
