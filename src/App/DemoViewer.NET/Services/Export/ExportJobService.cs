#region

using Avalonia.Threading;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Pipeline.Export;

#endregion

namespace DemoViewer.NET.Services.Export;

/// <summary>
///     The 2D export background job. Single-flight, cancel-safe, and refusing rather than queueing.
///     <para>
///         <b>Order inside the job matters</b> and is: LiveSync pre-flight refusal → enter the export
///         session on <see cref="HeavyJobGate" /> → <i>re-check</i> LiveSync (a session could have started
///         during the entry) → run → release the gate → publish the terminal status. Publishing last is
///         the <c>ReelJobService</c> pattern: <c>IsRunning</c> stays true until the machine is actually
///         free, so an interlock reading this status during wind-down cannot see it as available early.
///     </para>
///     <para>
///         <b>Refusal is start-time only</b> (plan D11, matching design §5.7's wording). A LiveSync
///         session that starts <i>mid</i>-export does not abort it: the export never touches the shared
///         clock, so it cannot corrupt sync, and killing several minutes of finished render to enforce a
///         rule that was about starting would be worse than the overlap.
///     </para>
///     <para>
///         The whole job runs on <see cref="Task.Run(Func{Task})" />. The <b>only</b> UI-thread work is
///         status marshalling, so nothing here can block a frame.
///     </para>
/// </summary>
public sealed class ExportJobService : IExportJobService, IDisposable
{
    private readonly HeavyJobGate? _gate;
    private readonly Func<bool>? _isLiveSyncBusy;
    private readonly Func<bool>? _isReelRunning;
    private readonly object _lifecycle = new();
    private readonly Action<string>? _log;
    private readonly IExportRunner _runner;

    private CancellationTokenSource? _cts;
    private Task? _job;

    /// <summary>
    ///     Single-flight, held from inside <see cref="Start" /> rather than read off
    ///     <see cref="Status" />.
    ///     <para>
    ///         The job body runs on the thread pool, so <c>Status</c> is still <c>Idle</c> for a moment
    ///         after <c>Start</c> returns. Guarding on the published status alone would let a
    ///         double-clicked Start button run two exports at the same output path, with the first job's
    ///         token source overwritten and its task unreachable by <see cref="CancelAsync" />.
    ///     </para>
    /// </summary>
    private bool _running;

    /// <summary>Creates the service.</summary>
    /// <param name="runner">Does the actual rendering. The seam every test replaces.</param>
    /// <param name="gate">The machine-wide heavy-job gate, or null in a headless harness.</param>
    /// <param name="isLiveSyncBusy">
    ///     True when a LiveSync session is active OR still owns its resources. Injected as a predicate
    ///     because <c>LiveSyncService.OwnsSessionResources</c> is internal to the desktop-only LiveSync
    ///     project, which this assembly does not reference.
    /// </param>
    /// <param name="isReelRunning">True while a reel job is running.</param>
    /// <param name="log">Optional line sink.</param>
    public ExportJobService(IExportRunner runner, HeavyJobGate? gate = null,
        Func<bool>? isLiveSyncBusy = null, Func<bool>? isReelRunning = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _gate = gate;
        _isLiveSyncBusy = isLiveSyncBusy;
        _isReelRunning = isReelRunning;
        _log = log;
    }

    /// <summary>The refusal copy shown when LiveSync holds the machine.</summary>
    public const string LiveSyncRefusal =
        "A Live Sync session is running. Video export needs the CPU that CS2 is using — " +
        "disable Live Sync and try again.";

    /// <summary>The refusal copy shown when a reel job holds the machine.</summary>
    public const string ReelRefusal =
        "A highlight reel is being generated — try again when it finishes.";

    /// <inheritdoc />
    public ExportJobStatus Status { get; private set; } = ExportJobStatus.Idle;

    /// <inheritdoc />
    public event EventHandler<ExportJobStatus>? StatusChanged;

    /// <inheritdoc />
    public void Start(Scene2DExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Validate BEFORE anything is started, through the same validator the CLI uses: a refused
        // request must never reach the gate, let alone spawn a tracker replay.
        SceneExportSession.Validate(request.Core);

        RefuseIfBusy();

        lock (_lifecycle)
        {
            if (_running || Status.IsRunning)
            {
                throw new InvalidOperationException("A 2D video export is already running.");
            }

            _running = true;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _job = Task.Run(() => RunAsync(request, token), CancellationToken.None);
        }
    }

    /// <inheritdoc />
    public async Task CancelAsync()
    {
        Task? job;
        lock (_lifecycle)
        {
            _cts?.Cancel();
            job = _job;
        }

        if (job is null)
        {
            return;
        }

        try
        {
            await job.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The job's own terminal status carries the outcome; an awaiting caller does not need it
            // twice, and a cancellation is not an error here.
            _log?.Invoke($"export cancel: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lifecycle)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void RefuseIfBusy()
    {
        if (_isLiveSyncBusy?.Invoke() == true)
        {
            throw new ExportRefusedException(LiveSyncRefusal);
        }

        if (_isReelRunning?.Invoke() == true || _gate?.IsReelActive == true)
        {
            throw new ExportRefusedException(ReelRefusal);
        }
    }

    private async Task RunAsync(Scene2DExportRequest request, CancellationToken ct)
    {
        IDisposable? slot = null;
        ExportJobStatus? terminal = null;

        SetStatus(new ExportJobStatus(ExportPhase.Preparing, 0, request.Core.FrameCount, 0, TimeSpan.Zero,
            request.OutputPath, null, null));

        try
        {
            if (_gate is not null)
            {
                slot = await _gate.EnterExportSessionAsync(ct).ConfigureAwait(false);
            }

            // Re-check: entering the gate is not instantaneous, and a session that came up in between is
            // exactly the case the start-time rule is about.
            RefuseIfBusy();

            await _runner.RunAsync(request, new DirectProgress(OnProgress), ct).ConfigureAwait(false);

            terminal = Status with
            {
                Phase = ExportPhase.Completed, FramesDone = Status.FramesTotal, Error = null
            };
        }
        catch (OperationCanceledException)
        {
            terminal = Status with { Phase = ExportPhase.Cancelled, Error = null };
        }
        catch (Exception ex)
        {
            _log?.Invoke($"export failed: {ex}");
            terminal = Status with { Phase = ExportPhase.Failed, Error = ex.Message };
        }
        finally
        {
            // Release the gate BEFORE publishing the terminal status, so anything that reacts to
            // "finished" by starting its own heavy work finds a free machine.
            slot?.Dispose();

            if (terminal is { } final)
            {
                SetStatus(final);
            }

            // Last, so there is never an instant where the single-flight latch is open while Status
            // still says the export is running.
            lock (_lifecycle)
            {
                _running = false;
            }
        }
    }

    private void OnProgress(ExportProgress progress) =>
        SetStatus(new ExportJobStatus(progress.Phase, progress.FramesDone, progress.FramesTotal,
            progress.FramesPerSecond, progress.Elapsed, Status.OutputPath,
            progress.Phase is ExportPhase.Failed ? progress.Detail : null, progress.Eta));

    private void SetStatus(ExportJobStatus status)
    {
        Status = status;

        // Dispatcher.UIThread is null-safe here only when a platform exists; a headless test harness has
        // no dispatcher, and raising inline is what makes ExportJobServiceTests a direct-execution suite.
        if (Dispatcher.UIThread.CheckAccess())
        {
            StatusChanged?.Invoke(this, status);
            return;
        }

        Dispatcher.UIThread.Post(() => StatusChanged?.Invoke(this, status));
    }

    /// <summary>
    ///     Calls straight through instead of posting.
    ///     <para>
    ///         <see cref="Progress{T}" /> captures the <see cref="SynchronizationContext" /> of whoever
    ///         constructed it, and this job is constructed on a thread-pool thread — so it would post to
    ///         the pool, and a report queued mid-render could arrive <b>after</b> the terminal status and
    ///         overwrite "Completed" with "Rendering". The status would then say the export is still going
    ///         forever. Marshalling to the UI thread is <see cref="SetStatus" />'s job and happens once,
    ///         at the end of this chain, where the ordering is already fixed.
    ///     </para>
    /// </summary>
    private sealed class DirectProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }
}
