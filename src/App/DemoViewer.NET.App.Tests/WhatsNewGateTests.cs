#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.Update;
using DemoViewer.NET.ViewModels.Settings;
using DemoViewer.NET.ViewModels.Setup;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.ViewModels.Update;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The post-update "What's new" gate (v0.6.0) and the update-notice pop-up routing. Pins the
///     launch decisions: a fresh install records the version silently (the first-run wizard is
///     enough for one launch), a version change on a set-up install shows the window exactly once
///     (the stored version advances BEFORE the window opens, so a crash can never loop it), and an
///     unchanged version stays silent. <see cref="NotInParallelAttribute" /> because the shell
///     constructions are heavy and one case mutates the <see cref="UpdateViewModel.Shared" /> static.
/// </summary>
[NotInParallel]
public class WhatsNewGateTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>An <see cref="IWindowService" /> that records instead of opening OS windows.</summary>
    private sealed class RecordingWindowService : IWindowService
    {
        public List<WhatsNewViewModel> WhatsNews { get; } = [];
        public List<UpdateNoticeViewModel> UpdateNotices { get; } = [];

        public void OpenParseChainInspector(object dataContext)
        {
        }

        public void OpenSettings(SettingsViewModel viewModel)
        {
        }

        public void ShowFirstRunWizard(FirstRunWizardViewModel viewModel)
        {
        }

        public void ShowUpdateNotice(UpdateNoticeViewModel viewModel) => UpdateNotices.Add(viewModel);

        public void ShowWhatsNew(WhatsNewViewModel viewModel) => WhatsNews.Add(viewModel);
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvwhatsnew_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static MainViewModel NewShell(RecordingWindowService windows, SettingsService settings) =>
        new(windows, null, TestLibraries.Empty(), null, null, null, settings);

    // ── The gate ──────────────────────────────────────────────────────────────

    /// <summary>Fresh install (wizard pending): record the version, show nothing.</summary>
    [Test]
    public async Task FreshInstall_RecordsVersion_ShowsNothing()
    {
        string dir = NewTempDir();
        try
        {
            RecordingWindowService windows = new();
            SettingsService settings = new(dir);
            MainViewModel vm = NewShell(windows, settings);

            vm.StartWhatsNewCheck();

            await Assert.That(windows.WhatsNews).HasCount().EqualTo(0);
            await Assert.That(settings.Current.LastSeenVersion)
                .IsEqualTo(AppVersionInfo.CurrentReleaseVersion);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    ///     Set-up install, version changed (the first post-update launch — including upgrades from
    ///     builds that predate the gate, where LastSeenVersion is null): show once, advance the
    ///     stored version, and never re-show for the same version.
    /// </summary>
    [Test]
    public async Task VersionChange_OnSetUpInstall_ShowsExactlyOnce()
    {
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                """{ "FirstRunCompleted": true, "LastSeenVersion": "0.0.1" }""");
            RecordingWindowService windows = new();
            SettingsService settings = new(dir);
            MainViewModel vm = NewShell(windows, settings);

            vm.StartWhatsNewCheck();

            await Assert.That(windows.WhatsNews).HasCount().EqualTo(1);
            await Assert.That(windows.WhatsNews[0].Version)
                .IsEqualTo(AppVersionInfo.CurrentReleaseVersion);
            // Advanced BEFORE the window opened — the crash-loop guard.
            await Assert.That(settings.Current.LastSeenVersion)
                .IsEqualTo(AppVersionInfo.CurrentReleaseVersion);

            // A second launch on the same version stays silent.
            vm.StartWhatsNewCheck();
            await Assert.That(windows.WhatsNews).HasCount().EqualTo(1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Unchanged version: nothing shows, nothing changes.</summary>
    [Test]
    public async Task SameVersion_StaysSilent()
    {
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                $$"""{ "FirstRunCompleted": true, "LastSeenVersion": "{{AppVersionInfo.CurrentReleaseVersion}}" }""");
            RecordingWindowService windows = new();
            SettingsService settings = new(dir);
            MainViewModel vm = NewShell(windows, settings);

            vm.StartWhatsNewCheck();

            await Assert.That(windows.WhatsNews).HasCount().EqualTo(0);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ── Update-notice routing ─────────────────────────────────────────────────

    /// <summary>
    ///     "Details…" builds ONE notice VM per run and re-shows the same instance — the notes fetch
    ///     must not repeat per click, and the window service re-activates rather than re-spawns.
    /// </summary>
    [Test]
    public async Task ShowUpdateDetails_ReusesOneNoticeVm()
    {
        UpdateViewModel shared = UpdateViewModel.Shared; // restore after — a process-wide static
        try
        {
            UpdateViewModel.Shared = new UpdateViewModel(null);
            RecordingWindowService windows = new();
            string dir = NewTempDir();
            try
            {
                MainViewModel vm = NewShell(windows, new SettingsService(dir));

                // No update offered → the command must no-op rather than open an empty window.
                vm.ShowUpdateDetailsCommand.Execute(null);
                await Assert.That(windows.UpdateNotices).HasCount().EqualTo(0);

                UpdateViewModel.Shared.AvailableVersion = "9.9.9";
                UpdateViewModel.Shared.IsUpdateAvailable = true;
                vm.ShowUpdateDetailsCommand.Execute(null);
                vm.ShowUpdateDetailsCommand.Execute(null);

                await Assert.That(windows.UpdateNotices).HasCount().EqualTo(2);
                await Assert.That(ReferenceEquals(windows.UpdateNotices[0], windows.UpdateNotices[1])).IsTrue();
                await Assert.That(windows.UpdateNotices[0].HeadlineText).Contains("9.9.9");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
        finally
        {
            UpdateViewModel.Shared = shared;
        }
    }

    // ── Version normalization ─────────────────────────────────────────────────

    /// <summary>Every version shape the app encounters reduces to the release tag's x.y.z.</summary>
    [Test]
    public async Task NormalizeVersion_HandlesEveryKnownShape()
    {
        await Assert.That(GitHubReleaseNotesService.NormalizeVersion("0.6.0")).IsEqualTo("0.6.0");
        await Assert.That(GitHubReleaseNotesService.NormalizeVersion("v0.6.0")).IsEqualTo("0.6.0");
        await Assert.That(GitHubReleaseNotesService.NormalizeVersion("0.6.0-alpha+g1a2b3c4")).IsEqualTo("0.6.0");
        await Assert.That(GitHubReleaseNotesService.NormalizeVersion("0.6.0.12345")).IsEqualTo("0.6.0");
        await Assert.That(GitHubReleaseNotesService.NormalizeVersion("(unknown)")).IsNull();
        await Assert.That(GitHubReleaseNotesService.NormalizeVersion(null)).IsNull();
        await Assert.That(GitHubReleaseNotesService.NormalizeVersion("")).IsNull();
    }
}
