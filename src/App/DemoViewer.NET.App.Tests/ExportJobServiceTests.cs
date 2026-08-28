#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.Export;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The export job's own responsibilities: who is allowed to start one, what happens when something
///     else owns the machine, and in what order the gate and the status move. The rendering itself is
///     behind <see cref="IExportRunner" />, so none of this needs a demo, a compositor or an ffmpeg.
/// </summary>
public class ExportJobServiceTests
{
    [Test]
    public async Task ItRefusesWhileLiveSyncHoldsTheMachine()
    {
        FakeRunner runner = new();
        ExportJobService service = new(runner, isLiveSyncBusy: () => true);

        ExportRefusedException? refusal = Capture(() => service.Start(Request()));

        // Start-time refusal with a message the user can act on, rather than a silent queue behind a
        // CS2 instance that may be up for another twenty minutes.
        await Assert.That(refusal).IsNotNull();
        await Assert.That(refusal!.Message).IsEqualTo(ExportJobService.LiveSyncRefusal);
        await Assert.That(runner.Started).IsEqualTo(0);
    }

    [Test]
    public async Task ItRefusesWhileAReelIsRendering()
    {
        FakeRunner runner = new();
        ExportJobService service = new(runner, isReelRunning: () => true);

        ExportRefusedException? refusal = Capture(() => service.Start(Request()));

        await Assert.That(refusal).IsNotNull();
        await Assert.That(refusal!.Message).IsEqualTo(ExportJobService.ReelRefusal);
    }

    [Test]
    public async Task ItRefusesWhileTheGateSaysAReelIsActive()
    {
        using HeavyJobGate gate = new();
        using IDisposable reel = await gate.EnterReelSessionAsync();

        FakeRunner runner = new();
        ExportJobService service = new(runner, gate);

        await Assert.That(Capture(() => service.Start(Request()))).IsNotNull();
    }

    [Test]
    public async Task AnInvalidRequest_IsRefusedBeforeAnythingStarts()
    {
        FakeRunner runner = new();
        ExportJobService service = new(runner);

        // Odd height on a yuv420p format. Validated through the SAME validator the CLI uses, so the two
        // front ends cannot disagree about what is exportable.
        ExportRequest core = Request().Core with
        {
            Size = new SKSizeI(1280, 721)
        };

        ExportValidationException? refusal = null;
        try
        {
            service.Start(Request() with
            {
                Core = core
            });
        }
        catch (ExportValidationException ex)
        {
            refusal = ex;
        }

        await Assert.That(refusal).IsNotNull();
        await Assert.That(runner.Started).IsEqualTo(0);
    }

    [Test]
    public async Task ASecondStart_WhileOneIsRunning_IsRefused()
    {
        FakeRunner runner = new()
        {
            Block = new TaskCompletionSource()
        };
        ExportJobService service = new(runner);

        service.Start(Request());
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        InvalidOperationException? refusal = null;
        try
        {
            service.Start(Request());
        }
        catch (InvalidOperationException ex)
        {
            refusal = ex;
        }

        await Assert.That(refusal).IsNotNull();

        runner.Block.SetResult();
        await service.CancelAsync();
    }

    /// <summary>
    ///     Single-flight has to hold from the instant <c>Start</c> returns, not from the instant the
    ///     job's first status lands.
    ///     <para>
    ///         The job body runs on the thread pool, so between <c>Start</c> returning and
    ///         <c>RunAsync</c> publishing <see cref="ExportPhase.Preparing" /> there is a window in which
    ///         <c>Status.IsRunning</c> is still false. A guard that reads only the published status
    ///         therefore misses a double-click on the dialog's Start button — and two exports would then
    ///         race for the same output path, with the first one's cancellation source overwritten and
    ///         its task no longer reachable by <c>CancelAsync</c>.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ASecondStart_InTheWindowBeforeTheFirstJobPublishesAnything_IsStillRefused()
    {
        FakeRunner runner = new()
        {
            Block = new TaskCompletionSource()
        };
        ExportJobService service = new(runner);

        // Deliberately NO await between the two calls: this is the double-click, not the second click a
        // second later that ASecondStart_WhileOneIsRunning_IsRefused already covers.
        service.Start(Request());

        InvalidOperationException? refusal = null;
        try
        {
            service.Start(Request());
        }
        catch (InvalidOperationException ex)
        {
            refusal = ex;
        }

        await Assert.That(refusal).IsNotNull();

        runner.Block.SetResult();
        await service.CancelAsync();
        await Assert.That(runner.Started).IsEqualTo(1);
    }

    [Test]
    // Asserts a cross-thread publish ordering that loses a race ~1 run in 5 under full-suite
    // parallelism and passes 10/10 in isolation (P2 report; reproduced at the tiers merge).
    // Environmental keeps it out of fast/standard; the App suite is not in CI, so no lane changes.
    [Category("Environmental")]
    public async Task TheTerminalStatus_PublishesOnlyAfterTheGateIsReleased()
    {
        using HeavyJobGate gate = new();
        FakeRunner runner = new();
        ExportJobService service = new(runner, gate);

        service.Start(Request());
        await WaitUntil(() => !service.Status.IsRunning);

        // Status is assigned before StatusChanged is raised, and the gate is released before Status is
        // assigned. So the instant a poller can SEE a terminal status, the machine must already be free:
        // that is the reel job's rule, for the same reason — anything that reacts to "finished" by
        // starting its own heavy work must not find a gate this job has not let go of yet.
        await Assert.That(gate.IsExportActive).IsFalse();
        await Assert.That(service.Status.Phase).IsEqualTo(ExportPhase.Completed);
    }

    [Test]
    public async Task ARunnerFailure_BecomesAFailedStatus_CarryingItsMessage()
    {
        FakeRunner runner = new()
        {
            Failure = new InvalidOperationException("the encoder exploded")
        };
        ExportJobService service = new(runner);

        service.Start(Request());
        await WaitUntil(() => service.Status.Phase == ExportPhase.Failed);

        await Assert.That(service.Status.Error).IsEqualTo("the encoder exploded");
    }

    [Test]
    public async Task CancellingBeforeTheFirstFrame_EndsCleanly()
    {
        FakeRunner runner = new()
        {
            Block = new TaskCompletionSource(),
            HonourCancellation = true
        };
        ExportJobService service = new(runner);

        service.Start(Request());
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.CancelAsync();

        await Assert.That(service.Status.Phase).IsEqualTo(ExportPhase.Cancelled);
        await Assert.That(service.Status.IsRunning).IsFalse();
    }

    [Test]
    public async Task ALiveSyncSessionStartingMidExport_DoesNotAbortIt()
    {
        bool liveSyncBusy = false;
        FakeRunner runner = new()
        {
            Block = new TaskCompletionSource()
        };
        ExportJobService service = new(runner, isLiveSyncBusy: () => liveSyncBusy);

        service.Start(Request());
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Refusal is start-time only. An export never touches the shared clock, so it cannot corrupt a
        // sync session — and throwing away minutes of finished render to enforce a rule that was about
        // STARTING would be worse than the overlap.
        liveSyncBusy = true;
        runner.Block.SetResult();

        await WaitUntil(() => !service.Status.IsRunning);
        await Assert.That(service.Status.Phase).IsEqualTo(ExportPhase.Completed);
    }

    private static Scene2DExportRequest Request() =>
        new(new ExportRequest(0, 9, 60, new SKSizeI(320, 240), 1.0, ExportFormats.WebM,
                new HashSet<string>(StringComparer.Ordinal),
                new CameraScript.Fixed(new Dictionary<MapLevelId, ViewportTransform>())),
            "out.webm", "demo.dem");

    private static ExportRefusedException? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (ExportRefusedException ex)
        {
            return ex;
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }
    }

    /// <summary>Stands in for the whole render pipeline: it records, blocks or fails on demand.</summary>
    private sealed class FakeRunner : IExportRunner
    {
        public TaskCompletionSource Entered { get; } = new();

        public TaskCompletionSource? Block { get; init; }

        public Exception? Failure { get; init; }

        public bool HonourCancellation { get; init; }

        public int Started { get; private set; }

        public async Task RunAsync(Scene2DExportRequest request, IProgress<ExportProgress> progress,
            CancellationToken ct)
        {
            Started++;
            Entered.TrySetResult();

            if (Block is not null)
            {
                if (HonourCancellation)
                {
                    await Block.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    await Block.Task.ConfigureAwait(false);
                }
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            ct.ThrowIfCancellationRequested();
            progress.Report(new ExportProgress(ExportPhase.Rendering, request.Core.FrameCount,
                request.Core.FrameCount, 60, TimeSpan.FromSeconds(1), null, null));
        }
    }
}
