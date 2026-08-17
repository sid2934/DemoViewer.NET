#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Avalonia entry point for headless UI tests. Uses the REAL <see cref="DemoViewer.NET.App" /> so
///     its styles, brushes, converters, and card/hex DataTemplates are loaded — and the Skia backend
///     (UseHeadlessDrawing = false) so rendered frames can be captured to PNG for inspection.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false
            })
            .WithInterFont();
}

/// <summary>
///     Single shared headless session for the assembly (Avalonia requires one UI thread). Test bodies
///     run their UI work via <see cref="RunOnUi" /> so they execute on the headless dispatcher thread.
/// </summary>
public static class HeadlessSession
{
    private static readonly Lazy<HeadlessUnitTestSession> _session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder)));

    // Set (with the culprit's description) when a dispatched body never completed: the session
    // thread is then wedged forever — Avalonia's DispatchCore does a BLOCKING
    // task.GetAwaiter().GetResult() after its DispatcherFrame exits, so neither cancellation
    // nor timeout can free it, and every later dispatch would queue behind it eternally.
    // Poisoning turns that cascade into immediate, attributed failures.
    private static string? _wedgedBy;

    // Auto-close registry: the headless session installs NO application lifetime (so there is
    // no IClassicDesktopStyleApplicationLifetime.Windows), but Window raises public routed
    // Opened/Closed events — the same mechanism the desktop lifetime uses for its own list.
    // 23 of the suite's 24 window constructions leaked (holding up to ~8 GB of ParsedDemo
    // graphs), and a leaked window whose content keeps animating is the compositor-wedge
    // class; closing after every body fixes both without per-test churn.
    private static readonly HashSet<Window> _openWindows = [];
    private static bool _windowTrackingInstalled;

    /// <summary>Directory where render-capture PNGs are written for inspection.</summary>
    public static string ArtifactDir
    {
        get
        {
            string dir = Path.Combine(Path.GetTempPath(), "demoviewer-uitests");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static void EnsureWindowTracking()
    {
        if (_windowTrackingInstalled)
        {
            return;
        }

        _windowTrackingInstalled = true;
        Window.WindowOpenedEvent.AddClassHandler(
            typeof(Window),
            (sender, _) => _openWindows.Add((Window)sender!));
        Window.WindowClosedEvent.AddClassHandler(
            typeof(Window),
            (sender, _) => _openWindows.Remove((Window)sender!));
    }

    private static void CloseLeakedWindows()
    {
        if (_openWindows.Count == 0)
        {
            return;
        }

        foreach (Window window in _openWindows.ToArray())
        {
            try
            {
                window.Close();
            }
            catch (Exception closeEx)
            {
                Console.WriteLine(
                    $"[runonui-autoclose] leaked window Close() fault (the leaker's mess): {closeEx.Message}");
            }
        }

        _openWindows.Clear();
    }

    /// <summary>
    ///     Runs an async body on the shared headless UI thread and — unlike the naive
    ///     <c>Dispatch(work)</c> form — actually awaits the body.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The wrapper MUST hand Dispatch a <c>Func&lt;Task&lt;T&gt;&gt;</c>: a bare
    ///         <c>Func&lt;Task&gt;</c> binds to the generic <c>Func&lt;TResult&gt;</c> overload
    ///         with <c>TResult = Task</c>, which awaits only the dispatch — the body's task was
    ///         never observed, so an async body's failure (or hang) after its first yield
    ///         silently PASSED the test (found by work item 0.2's canary; the "headless
    ///         swallows async-load exceptions" lore was this bug).
    ///     </para>
    ///     <para>
    ///         Honest awaiting surfaces bodies that never complete, so the timeout race turns
    ///         them into loud failures instead of eternal suite hangs. Budget: the longest
    ///         legitimate UI-thread body is a ~30s real-demo render; the multi-minute suite
    ///         members are pure compute and never dispatch. A timed-out body has wedged the
    ///         session thread permanently (see <see cref="_wedgedBy" />) — subsequent tests
    ///         fail fast with the culprit's name rather than hanging behind it.
    ///     </para>
    /// </remarks>
    public static async Task RunOnUi(Func<Task> work)
    {
        if (Volatile.Read(ref _wedgedBy) is { } culprit)
        {
            throw new InvalidOperationException(
                $"Headless UI session is wedged by an earlier hung test body ({culprit}) — "
                + "this failure is collateral; fix the culprit.");
        }

        Task<bool> dispatched = _session.Value.Dispatch(async () =>
        {
            EnsureWindowTracking();
            await work();

            // Close whatever windows the body left open BEFORE the compositor flush below, so
            // the flush drains detach work instead of re-rendering leaked animating content.
            CloseLeakedWindows();

            // Post-body composition flush. A body that leaves a Window open whose content keeps
            // requesting animation frames (e.g. the 2D viewport's smooth camera lerp) saturates
            // the compositor's in-flight batch queue — the headless render timer never ticks on
            // its own (manual ForceRenderTimerTick platform), so the NEXT test's `new Window()`
            // then parks forever inside a nested dispatcher frame waiting for a commit slot
            // (root-caused via Playback2DCameraModeTests: AllModes leaked an animating window
            // and ManualPanZoom wedged at Window construction; closing the window fixed it).
            // Ticking here drains the backlog to at most the single re-requested frame, keeping
            // leaked-but-animating windows from wedging their successors. The flush renders
            // OTHER tests' leaked windows in whatever mid-teardown state they're in — a render
            // exception there is the leaker's mess, not this body's failure, so log instead of
            // faulting the innocent current test.
            try
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }
            catch (Exception flushEx)
            {
                Console.WriteLine($"[runonui-flush] leaked-window render fault (not this test's failure): {flushEx.Message}");
            }

            return true;
        }, CancellationToken.None);

        // 8 × 30s = the 4-minute budget, with a thread-pool trend line while waiting: a wedge
        // with "dispatcher responsive: True" means the body awaits a NON-dispatcher completion,
        // and the classic culprit is pool starvation (queued blocking items eating every
        // injected worker). The trend distinguishes that from a single never-completing await.
        // WaitAsync (not WhenAny + Task.Delay) so the interval timer dies with the fast path
        // instead of lingering ~30s per call.
        bool completed = false;
        for (int interval = 1; interval <= 8 && !completed; interval++)
        {
            try
            {
                await dispatched.WaitAsync(TimeSpan.FromSeconds(30));
                completed = true;
            }
            catch (TimeoutException) when (!dispatched.IsCompleted)
            {
                if (interval >= 2)
                {
                    Console.WriteLine(
                        $"[runonui-watch] +{interval * 30}s threads={ThreadPool.ThreadCount} "
                        + $"pending={ThreadPool.PendingWorkItemCount} "
                        + $"completed={ThreadPool.CompletedWorkItemCount} "
                        + $"mem={GC.GetTotalMemory(false) / 1048576}MB");
                }
            }
        }

        if (!completed)
        {
            // First-writer-wins: the session queue is FIFO, so the first timeout is the actual
            // wedger; a parallel caller whose body queued behind it also times out but is
            // collateral — attribute it as such rather than letting the last writer steal blame.
            string me = TestContext.Current?.TestDetails.TestName ?? "unknown test";
            string? prior = Interlocked.CompareExchange(ref _wedgedBy, me, null);
            if (prior is not null)
            {
                throw new TimeoutException(
                    $"RunOnUi body never started — queued behind the earlier wedge by {prior}; "
                    + "this failure is collateral, fix the culprit.");
            }

            // Forensic hint: DispatchCore pumps a DispatcherFrame while awaiting the body, so a
            // wedged body usually leaves the dispatcher itself responsive — "alive" means the
            // body awaits a completion that never arrives (not a jammed dispatcher).
            bool dispatcherAlive = false;
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => true)
                    .GetTask().WaitAsync(TimeSpan.FromSeconds(10));
                dispatcherAlive = true;
            }
            catch
            {
                // Probe timeout/failure = dispatcher not pumping.
            }

            // Pool probe: can a trivial new work item run? False = starvation (queued blocking
            // items absorb every injected worker) rather than one stuck await.
            bool poolAlive = false;
            try
            {
                poolAlive = await Task.Run(() => true).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Probe timeout = pool starved.
            }

            throw new TimeoutException(
                "RunOnUi body did not complete within 4 minutes — the test is hung (deadlocked "
                + "await, nested RunOnUi, or an await whose completion source never fires) and "
                + "has permanently wedged the shared UI session; later UI tests will fail fast. "
                + $"Dispatcher responsive during the hang: {dispatcherAlive}; thread pool "
                + $"responsive: {poolAlive} (threads={ThreadPool.ThreadCount}, "
                + $"pending={ThreadPool.PendingWorkItemCount}). "
                + "Before the vacuous-pass fix this test would have silently passed.");
        }

        await dispatched;
    }
}
