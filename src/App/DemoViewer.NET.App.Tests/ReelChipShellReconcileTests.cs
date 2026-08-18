#region

using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.ViewModels.Highlights;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Covers the shell-side Reel-chip lifecycle: the chip is present while
///     a job runs OR a finished result is not yet dismissed, Dismiss removes it, and a NEW run un-dismisses
///     and re-shows it. This is <see cref="MainViewModel.AttachReelJob" /> + the private reconcile — logic the
///     pure <c>ReelJobStatusViewModel</c> mapping tests do not exercise. Needs the shell, so it runs on the UI
///     thread over a fake job service (no real reel job is started).
/// </summary>
[NotInParallel]
public class ReelChipShellReconcileTests
{
    private static ReelJobStatus Status(ReelJobPhase phase, int completed = 0, int total = 0,
        string? output = null) =>
        new(phase, completed, total, null, null, output, []);

    [Test]
    public async Task ChipLifecycle_Running_FinishedNotDismissed_Dismiss_Rerun()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());
            FakeReelJob job = new();
            vm.AttachReelJob(job);

            await Assert.That(vm.Chips.Count).IsEqualTo(0).Because("Idle ⇒ no reel chip");

            job.Raise(Status(ReelJobPhase.StartingSession, 0, 3));
            await Assert.That(vm.Chips.Count).IsEqualTo(1).Because("running ⇒ chip present");

            job.Raise(Status(ReelJobPhase.Completed, 3, 3, "/out/reel.mp4"));
            await Assert.That(vm.Chips.Count).IsEqualTo(1).Because("finished-not-dismissed ⇒ still present");

            // The chip's FlyoutContent IS the ReelJobStatusViewModel (StatusChip pattern) — invoke Dismiss.
            ReelJobStatusViewModel reelVm = (ReelJobStatusViewModel)vm.Chips[0].FlyoutContent!;
            reelVm.DismissCommand.Execute(null);
            await Assert.That(vm.Chips.Count).IsEqualTo(0).Because("Dismiss removes the finished chip");

            job.Raise(Status(ReelJobPhase.StartingSession, 0, 2));
            await Assert.That(vm.Chips.Count).IsEqualTo(1).Because("a new run un-dismisses and re-shows the chip");
        });
    }

    [Test]
    public async Task CancelledResult_StaysUntilDismissed()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());
            FakeReelJob job = new();
            vm.AttachReelJob(job);

            job.Raise(Status(ReelJobPhase.Capturing, 0, 2));
            job.Raise(Status(ReelJobPhase.Cancelled));
            await Assert.That(vm.Chips.Count).IsEqualTo(1).Because("a cancelled result is a result — shown until dismissed");

            ReelJobStatusViewModel reelVm = (ReelJobStatusViewModel)vm.Chips[0].FlyoutContent!;
            reelVm.DismissCommand.Execute(null);
            await Assert.That(vm.Chips.Count).IsEqualTo(0);
        });
    }

    private sealed class FakeReelJob : IReelJobService
    {
        public ReelJobStatus Status { get; private set; } = ReelJobStatus.Idle;

        public event EventHandler<ReelJobStatus>? StatusChanged;

        public void Start(ReelRequest request)
        {
        }

        public Task CancelAsync() => Task.CompletedTask;

        public void RetryRemaining()
        {
        }

        public void Raise(ReelJobStatus next)
        {
            Status = next;
            StatusChanged?.Invoke(this, next);
        }
    }
}
