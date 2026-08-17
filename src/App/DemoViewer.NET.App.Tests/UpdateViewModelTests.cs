#region

using DemoViewer.NET.Services.Update;
using DemoViewer.NET.ViewModels.Update;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The in-app updater's decision logic. The Velopack layer itself is not exercised here — that
///     needs a real install — so these pin the behaviour the UI depends on: when the banner appears,
///     that <b>nothing downloads without consent</b>, and that every failure mode leaves the app
///     usable rather than stuck.
/// </summary>
public class UpdateViewModelTests
{
    /// <summary>Startup check finds a newer release → the banner appears with that version.</summary>
    [Test]
    public async Task StartupCheck_WithUpdate_RaisesBanner()
    {
        FakeUpdateService svc = new() { CheckResult = UpdateCheckResult.UpdateAvailable("0.5.2") };
        UpdateViewModel vm = new(svc);

        await vm.CheckOnStartupAsync();

        await Assert.That(vm.IsUpdateAvailable).IsTrue();
        await Assert.That(vm.AvailableVersion).IsEqualTo("0.5.2");
        // The whole point of prompting: a check must not have spent the user's bandwidth.
        await Assert.That(svc.DownloadCalls).IsEqualTo(0);
    }

    /// <summary>Already current → no banner, and nothing said. Silence is the correct startup UX.</summary>
    [Test]
    public async Task StartupCheck_UpToDate_StaysSilent()
    {
        FakeUpdateService svc = new() { CheckResult = UpdateCheckResult.UpToDate() };
        UpdateViewModel vm = new(svc);

        await vm.CheckOnStartupAsync();

        await Assert.That(vm.IsUpdateAvailable).IsFalse();
        await Assert.That(vm.StatusMessage).IsEmpty();
    }

    /// <summary>
    ///     Offline at launch must be invisible. A user who opens the app on a plane gets no banner and
    ///     no error — the check simply found nothing it could report.
    /// </summary>
    [Test]
    public async Task StartupCheck_Failure_IsSilent()
    {
        FakeUpdateService svc = new() { CheckResult = UpdateCheckResult.Failed("no such host is known") };
        UpdateViewModel vm = new(svc);

        await vm.CheckOnStartupAsync();

        await Assert.That(vm.IsUpdateAvailable).IsFalse();
        await Assert.That(vm.StatusMessage).IsEmpty();
    }

    /// <summary>
    ///     A manual check is the opposite: the user asked, so every outcome gets a sentence. Silence
    ///     on a pressed button reads as broken.
    /// </summary>
    [Test]
    public async Task ManualCheck_ReportsEveryOutcome()
    {
        UpdateViewModel upToDate = new(new FakeUpdateService { CheckResult = UpdateCheckResult.UpToDate() });
        await upToDate.CheckNowCommand.ExecuteAsync(null);
        await Assert.That(upToDate.StatusMessage).IsEqualTo("You're up to date.");

        UpdateViewModel failed = new(new FakeUpdateService { CheckResult = UpdateCheckResult.Failed("rate limited") });
        await failed.CheckNowCommand.ExecuteAsync(null);
        await Assert.That(failed.StatusMessage).Contains("rate limited");

        UpdateViewModel unpackaged = new(new FakeUpdateService { CheckResult = UpdateCheckResult.NotSupported() });
        await unpackaged.CheckNowCommand.ExecuteAsync(null);
        await Assert.That(unpackaged.StatusMessage).Contains("installed builds only");

        UpdateViewModel avail = new(new FakeUpdateService { CheckResult = UpdateCheckResult.UpdateAvailable("0.6.0") });
        await avail.CheckNowCommand.ExecuteAsync(null);
        await Assert.That(avail.StatusMessage).Contains("0.6.0");
        // A Settings check must also raise the shell banner — they share one VM for exactly this.
        await Assert.That(avail.IsUpdateAvailable).IsTrue();
    }

    /// <summary>Consent starts the download; the banner yields to the progress row.</summary>
    [Test]
    public async Task UpdateAndRestart_DownloadsAndSwapsToProgress()
    {
        FakeUpdateService svc = new()
        {
            CheckResult = UpdateCheckResult.UpdateAvailable("0.5.2"),
            DownloadSucceeds = true
        };
        UpdateViewModel vm = new(svc);
        await vm.CheckOnStartupAsync();

        await vm.UpdateAndRestartCommand.ExecuteAsync(null);

        await Assert.That(svc.DownloadCalls).IsEqualTo(1);
        // Banner cleared so it can't coexist with the progress row.
        await Assert.That(vm.IsUpdateAvailable).IsFalse();
    }

    /// <summary>
    ///     A failed download must restore the offer, not strand the user in a state with no update
    ///     and no way to retry. Velopack stages into a packages dir and only swaps on apply, so the
    ///     running install is untouched.
    /// </summary>
    [Test]
    public async Task UpdateAndRestart_FailedDownload_RestoresBannerAndExplains()
    {
        FakeUpdateService svc = new()
        {
            CheckResult = UpdateCheckResult.UpdateAvailable("0.5.2"),
            DownloadSucceeds = false
        };
        UpdateViewModel vm = new(svc);
        await vm.CheckOnStartupAsync();

        await vm.UpdateAndRestartCommand.ExecuteAsync(null);

        await Assert.That(vm.IsUpdateAvailable).IsTrue();
        await Assert.That(vm.IsDownloading).IsFalse();
        await Assert.That(vm.StatusMessage).Contains("failed");
    }

    /// <summary>Later hides the banner for this run without touching anything else.</summary>
    [Test]
    public async Task Dismiss_HidesBannerOnly()
    {
        FakeUpdateService svc = new() { CheckResult = UpdateCheckResult.UpdateAvailable("0.5.2") };
        UpdateViewModel vm = new(svc);
        await vm.CheckOnStartupAsync();

        vm.DismissCommand.Execute(null);

        await Assert.That(vm.IsUpdateAvailable).IsFalse();
        // Still resolved — a later Settings check shouldn't have to re-discover it.
        await Assert.That(vm.AvailableVersion).IsEqualTo("0.5.2");
        await Assert.That(svc.DownloadCalls).IsEqualTo(0);
    }

    /// <summary>
    ///     No host service (Browser, tests, designer, dev run) is a supported state, not a crash: no
    ///     banner, no network, and Settings reports it instead of offering a dead button.
    /// </summary>
    [Test]
    public async Task NullService_IsInertNotBroken()
    {
        UpdateViewModel vm = new(null);

        await vm.CheckOnStartupAsync();
        await vm.CheckNowCommand.ExecuteAsync(null);
        await vm.UpdateAndRestartCommand.ExecuteAsync(null);

        await Assert.That(vm.IsSupported).IsFalse();
        await Assert.That(vm.IsUpdateAvailable).IsFalse();
        await Assert.That(vm.CurrentVersionDisplay).IsEqualTo("not a packaged build");
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        public UpdateCheckResult CheckResult { get; init; } = UpdateCheckResult.UpToDate();
        public bool DownloadSucceeds { get; init; }
        public int DownloadCalls { get; private set; }

        public string? CurrentVersion => "0.5.1";

        public Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default) => Task.FromResult(CheckResult);

        public Task<bool> DownloadAndApplyAsync(IProgress<int>? progress = null, CancellationToken ct = default)
        {
            DownloadCalls++;
            progress?.Report(100);
            return Task.FromResult(DownloadSucceeds);
        }
    }
}
