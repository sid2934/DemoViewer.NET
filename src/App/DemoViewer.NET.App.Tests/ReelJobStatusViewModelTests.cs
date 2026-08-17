#region

using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.ViewModels;
using DemoViewer.NET.ViewModels.Highlights;
using DemoViewer.NET.Views.Highlights;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Covers the <see cref="ReelJobStatusViewModel" /> status→chip + status→flyout mapping
///     (docs/csvg-integration/ux-design.md) over a fake <see cref="IReelJobService" />. Pure VM. Asserts the
///     dot vocabulary (working pulse / positive done / error), the neutral "Reel · …" labels, the
///     mutually-exclusive flyout sections, the per-clip status list (done/current/queued/failed), and the
///     retry / cancel / dismiss gating.
/// </summary>
public class ReelJobStatusViewModelTests
{
    private static ReelJobStatusViewModel New(out FakeReelJob job, Action<string>? openFolder = null)
    {
        job = new FakeReelJob();
        return new ReelJobStatusViewModel(job, openFolder);
    }

    private static ReelJobStatus Status(
        ReelJobPhase phase, int completed = 0, int total = 0, string? current = null,
        string? error = null, string? output = null, int[]? failed = null) =>
        new(phase, completed, total, current, error, output, failed ?? []);

    // (a) Idle seeds a dim "Reel · idle" chip (the chip is not shown in this phase, but the map is never blank).
    [Test]
    public async Task InitialIdle_IsOffDimChip()
    {
        ReelJobStatusViewModel vm = New(out _);
        await Assert.That(vm.Chip.DotState).IsEqualTo(StatusChipDotState.Off);
        await Assert.That(vm.Chip.Label).IsEqualTo("Reel · idle");
    }

    // (b) StartingSession → a pulsing working chip; Cancel is enabled while running.
    [Test]
    public async Task StartingSession_PulsesWorking_CancelEnabled()
    {
        ReelJobStatusViewModel vm = New(out FakeReelJob job);
        job.Raise(Status(ReelJobPhase.StartingSession, 0, 3));

        await Assert.That(vm.Chip.DotState).IsEqualTo(StatusChipDotState.Working);
        await Assert.That(vm.Chip.IsPulsing).IsTrue();
        await Assert.That(vm.Chip.Label).IsEqualTo("Reel · starting…");
        await Assert.That(vm.IsRunning).IsTrue();
        await Assert.That(vm.CancelCommand.CanExecute(null)).IsTrue();
    }

    // (c) Capturing → "Reel · k of N" (k = the active clip = completed + 1); the per-clip list marks done /
    // current / queued, and the active row carries the live CurrentClipLabel.
    [Test]
    public async Task Capturing_LabelsKofN_AndBuildsPerClipList()
    {
        ReelJobStatusViewModel vm = New(out FakeReelJob job);
        job.Raise(Status(ReelJobPhase.Capturing, 1, 3, "s1mple · ace"));

        await Assert.That(vm.Chip.Label).IsEqualTo("Reel · 2 of 3");
        await Assert.That(vm.Clips.Count).IsEqualTo(3);
        await Assert.That(vm.Clips[0].State).IsEqualTo(ReelClipRowState.Done);
        await Assert.That(vm.Clips[1].State).IsEqualTo(ReelClipRowState.Current);
        await Assert.That(vm.Clips[1].Label).IsEqualTo("s1mple · ace");
        await Assert.That(vm.Clips[2].State).IsEqualTo(ReelClipRowState.Queued);
    }

    // (d) Completed → a positive "Reel · done" chip; the flyout offers Open folder (with a launcher + path).
    [Test]
    public async Task Completed_IsGood_OffersOpenFolder()
    {
        string? opened = null;
        ReelJobStatusViewModel vm = New(out FakeReelJob job, p => opened = p);
        job.Raise(Status(ReelJobPhase.Completed, 3, 3, output: "/out/reel.mp4"));

        await Assert.That(vm.Chip.DotState).IsEqualTo(StatusChipDotState.Good);
        await Assert.That(vm.Chip.Label).IsEqualTo("Reel · done");
        await Assert.That(vm.IsCompleted).IsTrue();
        await Assert.That(vm.CanOpenFolder).IsTrue();

        vm.OpenFolderCommand.Execute(null);
        await Assert.That(opened).IsEqualTo("/out/reel.mp4");
    }

    // (e) Failed → an error chip with the per-clip failure count; the flyout marks the failed clip ✕ and
    // offers Retry remaining (fail-fast leaves later clips queued).
    [Test]
    public async Task Failed_IsError_MarksFailedClip_AndRetries()
    {
        ReelJobStatusViewModel vm = New(out FakeReelJob job);
        job.Raise(Status(ReelJobPhase.Failed, 2, 4, error: "OBS dropped the capture",
            failed: [2]));

        await Assert.That(vm.Chip.DotState).IsEqualTo(StatusChipDotState.Error);
        await Assert.That(vm.Chip.Label).IsEqualTo("Reel · failed (1)");
        await Assert.That(vm.IsFailed).IsTrue();
        await Assert.That(vm.Clips[2].State).IsEqualTo(ReelClipRowState.Failed);
        await Assert.That(vm.Clips[3].State).IsEqualTo(ReelClipRowState.Queued);
        await Assert.That(vm.CanRetry).IsTrue();

        vm.RetryCommand.Execute(null);
        await Assert.That(job.RetryCount).IsEqualTo(1);
    }

    // (e2) A failed job exposes the copyable diagnostic block (Copy button gate + clipboard payload): it
    // carries the phase, the clip tally, the failed clip number (1-based), the active clip, and the verbatim
    // engine error — a self-contained report, not a truncated sentence. A running/idle job has nothing to copy.
    [Test]
    public async Task Failed_ExposesCopyableDiagnostics_RunningDoesNot()
    {
        ReelJobStatusViewModel vm = New(out FakeReelJob job);

        job.Raise(Status(ReelJobPhase.Capturing, 1, 4));
        await Assert.That(vm.HasError).IsFalse().Because("a running job has no error to copy");

        job.Raise(Status(ReelJobPhase.Failed, 2, 4, current: "s1mple · ace",
            error: "Capture provider is configured but not available.", failed: [2]));

        await Assert.That(vm.HasError).IsTrue();
        string diag = vm.CopyDiagnosticsText;
        await Assert.That(diag).Contains("FAILED");
        await Assert.That(diag).Contains("2 of 4");
        await Assert.That(diag).Contains("Failed clip #: 3"); // 0-based index 2 → 1-based #3
        await Assert.That(diag).Contains("s1mple · ace");
        await Assert.That(diag).Contains("Capture provider is configured but not available.");
    }

    // (f) Cancelled → a dim chip; Cancel command routes to the service while running.
    [Test]
    public async Task Cancelled_IsOff_AndCancelRoutesToService()
    {
        ReelJobStatusViewModel vm = New(out FakeReelJob job);
        job.Raise(Status(ReelJobPhase.Capturing, 0, 2));
        vm.CancelCommand.Execute(null);
        await Assert.That(job.CancelCalled).IsTrue();

        job.Raise(Status(ReelJobPhase.Cancelled));
        await Assert.That(vm.Chip.DotState).IsEqualTo(StatusChipDotState.Off);
        await Assert.That(vm.Chip.Label).IsEqualTo("Reel · cancelled");
        await Assert.That(vm.IsCancelled).IsTrue();
    }

    // (g) The flyout sections are mutually exclusive — at most one visible per phase.
    [Test]
    public async Task FlyoutSections_AreMutuallyExclusive()
    {
        ReelJobStatusViewModel vm = New(out FakeReelJob job);
        foreach (ReelJobPhase phase in Enum.GetValues<ReelJobPhase>())
        {
            job.Raise(Status(phase, 1, 3, failed: phase == ReelJobPhase.Failed ? [1] : null));
            int visible = new[]
            {
                vm.IsRunning, vm.IsCompleted, vm.IsFailed, vm.IsCancelled
            }.Count(b => b);
            await Assert.That(visible).IsLessThanOrEqualTo(1).Because($"at most one section for {phase}");
        }
    }

    // (h) Dismiss raises the event the shell uses to remove the chip.
    [Test]
    public async Task Dismiss_RaisesDismissRequested()
    {
        ReelJobStatusViewModel vm = New(out FakeReelJob job);
        job.Raise(Status(ReelJobPhase.Completed, 2, 2, output: "/out"));
        bool dismissed = false;
        vm.DismissRequested += (_, _) => dismissed = true;

        vm.DismissCommand.Execute(null);
        await Assert.That(dismissed).IsTrue();
    }

    // (i) The flyout body resolves via the ViewLocator name mapping (rename guard).
    [Test]
    public async Task FlyoutView_TypeName_MatchesViewLocatorMapping()
    {
        string mapped = typeof(ReelJobStatusViewModel).FullName!
            .Replace("ViewModel", "View", StringComparison.Ordinal);
        await Assert.That(mapped).IsEqualTo(typeof(ReelJobStatusView).FullName);
    }

    private sealed class FakeReelJob : IReelJobService
    {
        public bool CancelCalled { get; private set; }
        public int RetryCount { get; private set; }
        public ReelJobStatus Status { get; private set; } = ReelJobStatus.Idle;

        public event EventHandler<ReelJobStatus>? StatusChanged;

        public void Start(ReelRequest request)
        {
        }

        public Task CancelAsync()
        {
            CancelCalled = true;
            return Task.CompletedTask;
        }

        public void RetryRemaining() => RetryCount++;

        public void Raise(ReelJobStatus next)
        {
            Status = next;
            StatusChanged?.Invoke(this, next);
        }
    }
}
