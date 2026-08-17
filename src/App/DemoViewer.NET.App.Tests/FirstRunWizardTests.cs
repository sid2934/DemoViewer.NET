#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Setup;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views.Setup;

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
    // no folders — the basis-preserving write on a genuine first run.
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

    // (d) Step navigation clamps at both ends; the footer flags track the step.
    [Test]
    public async Task StepNavigation_ClampsAtBounds_AndTracksFooter()
    {
        string dir = NewTempDir();
        try
        {
            FirstRunWizardViewModel vm = new(new SettingsService(dir));

            await Assert.That(vm.CurrentStep).IsEqualTo(0);
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
    // Mirrors the Settings overlay test — this is the relaunch path on the browser host.
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
                frame!.Save(outPath);
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
                frame!.Save(outPath);
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

    // Render the FOLDERS step when auto-detection found nothing — the not-found notice listing searched libs.
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
                frame!.Save(outPath);
                Console.WriteLine($"[wizard-notfound] {outPath} nonBg={ScanNonBackground(frame)}");

                await Assert.That(vm.ShowNotFoundNotice).IsTrue();
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
