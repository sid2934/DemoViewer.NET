#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.LiveSync;
using TUnit.Core.Exceptions;
using TimeoutException = System.TimeoutException;

#endregion

namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     Dry-run gate (the macOS path): the reel job walks a real clip plan against the
///     real mock — load (grouped per demo) → spectate → range playback with tick-rate-derived
///     timeouts — reporting per-clip progress and failures without capture. Also covers the
///     retry-remaining request shape and the HeavyJobGate reel session (interactive loads
///     refused while the job runs).
/// </summary>
[Category("Integration")]
[NotInParallel("csvg-port-50051")]
public class ReelJobServiceMockTests
{
    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 120_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(50);
        }
    }

    [Test]
    [Timeout(180_000)]
    public async Task DryRun_WalksPlan_ReportsPerClipOutcomes_AndHoldsReelSession(
        CancellationToken cancellationToken)
    {
        // Skip-if-port-busy (the csproj policy every host-starting class follows): the reel job
        // starts its own CsvgWebHost internally and converts a busy port into a terminal Failed
        // status, which would fail this test RED on clip counts with a misleading message —
        // probe the port up front and skip like the sibling classes instead.
        try
        {
            CsvgWebHost probe = await CsvgWebHost.StartAsync(
                new LiveSyncSettings
                {
                    MockMode = true
                }, null, cancellationToken);
            await probe.DisposeAsync();
        }
        catch (LiveSyncPortInUseException ex)
        {
            throw new SkipTestException($"port {CsvgWebHost.GrpcPort} is owned by another process: {ex.Message}");
        }

        string demoPath = Path.Combine(Path.GetTempPath(), $"reel-dryrun-{Guid.NewGuid():N}.dem");
        await File.WriteAllBytesAsync(demoPath, [0x50, 0x42, 0x44, 0x45, 0x4D, 0x53, 0x32, 0x00],
            cancellationToken);
        string missingDemo = Path.Combine(Path.GetTempPath(), $"reel-missing-{Guid.NewGuid():N}.dem");

        using HeavyJobGate gate = new();
        List<string> logLines = [];
        ReelJobService reel = new(null, gate, null,
            line =>
            {
                lock (logLines)
                {
                    logLines.Add(line);
                }
            });
        List<ReelJobStatus> statuses = [];
        reel.StatusChanged += (_, status) =>
        {
            lock (statuses)
            {
                statuses.Add(status);
            }
        };

        try
        {
            ReelRequest request = new(
                [
                    new ReelClip(demoPath, "sha-a", 76561198000000001, "s1mple", 500, 900, 64, "double kill"),
                    new ReelClip(demoPath, "sha-a", 76561198000000002, "ZywOo", 1500, 1900, 64, "clutch"),
                    new ReelClip(missingDemo, "sha-b", 76561198000000003, "NiKo", 100, 400, 64, "ace")
                ],
                Path.GetTempPath(), "reel-test", "mp4",
                60, true, true, 20, null,
                false, true);

            reel.Start(request);

            // While the job runs, the gate's reel session refuses interactive loads.
            await WaitForAsync(() => reel.Status.IsRunning, "job running", 15_000);
            await Assert.That(gate.IsReelActive).IsTrue();
            await Assert.ThrowsAsync<ReelInProgressException>(async () =>
                await gate.AcquireInteractiveAsync(cancellationToken));

            // Sampled while waiting, because the EVENT path cannot be relied on here.
            // ReelJobService raises StatusChanged inline only when the caller already owns the UI
            // thread and posts it to the dispatcher otherwise. The job runs on a pool thread, and
            // in a full-suite run some earlier class has already bound Dispatcher.UIThread to a
            // thread nothing in this assembly pumps, so the per-clip statuses are queued and never
            // delivered. reel.Status is a plain property read with no marshalling, and the loop
            // below already ticks every 50 ms while clip capture takes seconds, so it sees the
            // same progress the app sees through the event.
            List<ReelJobStatus> polled = [];
            await WaitForAsync(() =>
            {
                polled.Add(reel.Status);
                return !reel.Status.IsRunning;
            }, "job finished");
            ReelJobStatus final = reel.Status;

            // Two real clips walk the mock successfully; the missing-demo clip fails (v1.1
            // exists-check) and lands in the retryable set.
            await Assert.That(final.Phase).IsEqualTo(ReelJobPhase.Failed);
            await Assert.That(final.ClipsCompleted).IsEqualTo(2);
            await Assert.That(final.FailedClipIndices).IsEquivalentTo([2]);
            await Assert.That(final.HasRetryableClips).IsTrue();
            await Assert.That(gate.IsReelActive).IsFalse().Because("the reel session releases on completion");

            // Progress surfaced per clip while capturing. Both sources count: whichever of the two
            // the host makes available, a subscriber sees the first clip go through Capturing.
            List<ReelJobStatus> snapshot;
            lock (statuses)
            {
                snapshot = [.. statuses, .. polled];
            }

            await Assert.That(snapshot.Any(s =>
                    s.Phase == ReelJobPhase.Capturing && s.CurrentClipLabel == "double kill")).IsTrue()
                .Because("per-clip progress reaches subscribers");
        }
        finally
        {
            File.Delete(demoPath);
        }
    }
}
