#region

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
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
///     Thrown internally when a dispatch faulted BEFORE the test body was entered — i.e. the fault
///     came from Avalonia's per-dispatch isolated-application setup, not from the test. Never
///     escapes <see cref="HeadlessSession.RunOnUi(Func{Task})" />.
/// </summary>
internal sealed class HeadlessSetupFaultException(Exception inner)
    : Exception("Isolated-application setup faulted before the test body ran.", inner);

/// <summary>
///     Assembly warm-up. Forces the headless session and one full isolated-application setup up
///     before any test runs, so a cold-start setup fault is attributed to the warm-up instead of to
///     whichever test happened to go first (and cannot race a <c>[NotInParallel]</c> body that
///     touches Avalonia statics before any application exists).
/// </summary>
public static class HeadlessWarmUp
{
    [Before(HookType.Assembly)]
    public static async Task ForceSessionUp() => await HeadlessSession.WarmUp();
}

/// <summary>
///     Single shared headless session for the assembly (Avalonia requires one UI thread). Test bodies
///     run their UI work via <see cref="RunOnUi(Func{Task})" /> so they execute on the headless dispatcher thread.
/// </summary>
public static class HeadlessSession
{
    // Session construction is retried rather than memoised through Lazy<T>: the default
    // LazyThreadSafetyMode.ExecutionAndPublication caches the EXCEPTION permanently, so one
    // transient StartNew failure would rethrow the same stale error for every later test with no
    // chance of recovery. StartNew is cheap (it only builds an AppBuilder on the session thread),
    // so retrying it costs nothing.
    private static readonly Lock _sessionGate = new();
    private static HeadlessUnitTestSession? _session;

    // Set (with the culprit's description) when a dispatched body never completed: the session
    // thread is then wedged forever — Avalonia's DispatchCore does a BLOCKING
    // task.GetAwaiter().GetResult() after its DispatcherFrame exits, so neither cancellation
    // nor timeout can free it, and every later dispatch would queue behind it eternally.
    // Poisoning turns that cascade into immediate, attributed failures.
    private static string? _wedgedBy;

    // Set when isolated-application setup failed twice in a row. Avalonia builds a FRESH
    // application per dispatch (StartNew defaults to AvaloniaTestIsolationLevel.PerTest, so
    // EnsureIsolatedApplication runs on EVERY Dispatch, not once at session start), and the
    // observed cold-start signature — TypeInitializationException on Avalonia.StyledElement — is
    // a poisoned type initializer, which .NET caches for the life of the process. Nothing
    // recovers from that, so the first surviving setup fault is recorded here with its fully
    // unwrapped cause and every later RunOnUi fails fast against it. That is the whole point:
    // one attributed root cause plus fast collateral, instead of ~130 six-second mystery
    // failures that teach everyone to distrust the suite.
    private static string? _poisonedBy;
    private static string? _poisonDetail;

    private static bool _warmUpBuiltAnApplication;

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

    private static HeadlessUnitTestSession Session
    {
        get
        {
            lock (_sessionGate)
            {
                return _session ??= HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
            }
        }
    }

    /// <summary>
    ///     True once the assembly warm-up has driven a full isolated-application setup to
    ///     completion. Lets a test assert that the hook actually ran, rather than inferring it
    ///     from state that the test's own dispatch would have produced anyway.
    /// </summary>
    internal static bool WarmUpBuiltAnApplication => Volatile.Read(ref _warmUpBuiltAnApplication);

    /// <summary>
    ///     Runs one trivial dispatch so the first isolated-application setup happens here, under
    ///     the same retry-and-attribute path as a real test. Never throws: an Avalonia setup fault
    ///     must not fail the ~470 suite members that never touch the UI, so it is recorded and
    ///     left for <see cref="RunOnUi(Func{Task})" /> to report against the tests it actually affects.
    /// </summary>
    internal static async Task WarmUp()
    {
        try
        {
            await RunOnUi(() => Task.CompletedTask, "<assembly warm-up>");
            Volatile.Write(ref _warmUpBuiltAnApplication, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[headless-warmup] warm-up failed; UI tests will fail fast. {ex.Message}");
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
    ///     Walks the whole cause chain and renders it, because the interesting exception is never
    ///     the outer one: Avalonia's SetupUnsafe reaches the app through reflection, so the fault
    ///     arrives wrapped in <see cref="TargetInvocationException" />, and the cold-start
    ///     signature bottoms out in a <see cref="TypeInitializationException" /> whose
    ///     <c>TypeName</c> is the only thing that identifies which static initializer died.
    ///     Losing that chain is what left issue #6 unexplainable for a whole release cycle.
    /// </summary>
    internal static string DescribeFault(Exception fault)
    {
        StringBuilder sb = new();
        Exception? cursor = fault;
        Exception innermost = fault;

        for (int depth = 0; cursor is not null && depth < 16; depth++)
        {
            sb.Append(' ', depth * 2)
                .Append(depth == 0 ? string.Empty : "-> ")
                .Append(cursor.GetType().FullName)
                .Append(": ")
                .Append(cursor.Message);

            if (cursor is TypeInitializationException typeInit)
            {
                sb.Append("   [static initializer for ").Append(typeInit.TypeName).Append(']');
            }

            sb.AppendLine();

            if (cursor is ReflectionTypeLoadException typeLoad)
            {
                foreach (Exception? loaderEx in typeLoad.LoaderExceptions.Where(e => e is not null).Take(5))
                {
                    sb.Append(' ', (depth + 1) * 2)
                        .Append("loader: ")
                        .AppendLine(loaderEx!.Message);
                }
            }

            innermost = cursor;
            cursor = cursor is AggregateException { InnerExceptions.Count: > 0 } aggregate
                ? aggregate.InnerExceptions[0]
                : cursor.InnerException;
        }

        // The innermost frame is where the fault actually happened; the outer stack is just the
        // reflection plumbing that carried it out.
        sb.AppendLine("--- innermost stack ---")
            .AppendLine(innermost.StackTrace ?? "(none)");

        return sb.ToString();
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
    public static Task RunOnUi(Func<Task> work) =>
        RunOnUi(work, TestContext.Current?.TestDetails.TestName ?? "unknown test");

    private static async Task RunOnUi(Func<Task> work, string caller)
    {
        if (Volatile.Read(ref _wedgedBy) is { } culprit)
        {
            throw new InvalidOperationException(
                $"Headless UI session is wedged by an earlier hung test body ({culprit}) — "
                + "this failure is collateral; fix the culprit.");
        }

        if (Volatile.Read(ref _poisonedBy) is { } poisoner)
        {
            throw new InvalidOperationException(
                $"Headless isolated-application setup is broken for this process — first seen in "
                + $"{poisoner}; this failure is collateral, not a fault in {caller}. Root cause:"
                + Environment.NewLine + Volatile.Read(ref _poisonDetail));
        }

        try
        {
            await DispatchWatched(work, caller);
        }
        catch (HeadlessSetupFaultException firstFault)
        {
            // The body provably never ran (see DispatchWatched), so nothing observable happened
            // and re-dispatching is safe. This is the whole transient-flake cure: a cold-start
            // setup fault that would have failed a test now costs one retry.
            Console.WriteLine(
                $"[headless-setup] {caller}: isolated-application setup faulted before the body ran "
                + $"({firstFault.InnerException!.GetType().Name}: {firstFault.InnerException.Message}); retrying once.");

            try
            {
                await DispatchWatched(work, caller);
            }
            catch (HeadlessSetupFaultException secondFault)
            {
                string detail = DescribeFault(secondFault.InnerException!);
                RecordPoison(caller, detail);

                throw new InvalidOperationException(
                    $"Headless isolated-application setup failed twice in {caller}; the test body "
                    + "never ran, so this is a harness failure rather than a product failure. "
                    + "Later UI tests will fail fast against this cause. Root cause:"
                    + Environment.NewLine + detail,
                    secondFault.InnerException);
            }
        }
    }

    private static void RecordPoison(string caller, string detail)
    {
        // Detail is published BEFORE the gate: _poisonedBy is what readers test, so writing it
        // first would let a parallel test report "Root cause:" with nothing under it. A loser of
        // the race may overwrite the detail, which is harmless — both describe the same fault.
        Volatile.Write(ref _poisonDetail, detail);

        // First-writer-wins, matching the wedge attribution: under parallel load several tests
        // hit the broken setup at once and only the first is the real report.
        if (Interlocked.CompareExchange(ref _poisonedBy, caller, null) is not null)
        {
            return;
        }

        Console.WriteLine($"[headless-setup] FATAL — first setup failure in {caller}:{Environment.NewLine}{detail}");

        // Console output drowns in the collateral cascade, and this is exactly the forensic
        // detail issue #6 needed and never had. Keep a copy beside the render captures.
        try
        {
            File.WriteAllText(
                Path.Combine(ArtifactDir, "headless-setup-failure.txt"),
                $"first setup failure in {caller}{Environment.NewLine}{detail}");
        }
        catch (Exception writeEx)
        {
            Console.WriteLine($"[headless-setup] could not write the forensic file: {writeEx.Message}");
        }
    }

    /// <summary>
    ///     One dispatch attempt, with the wedge watchdog. Throws
    ///     <see cref="HeadlessSetupFaultException" /> when the dispatch faulted before the body was
    ///     entered; any other fault is the body's own and propagates unchanged.
    /// </summary>
    private static async Task DispatchWatched(Func<Task> work, string caller)
    {
        // Avalonia invokes the body only after EnsureIsolatedApplication has returned, so this
        // flag cleanly separates "the harness could not build an application" from "the test
        // failed". Written on the session thread, read on the caller's — hence Volatile.
        StrongBox<bool> bodyEntered = new(false);

        // Acquiring the session (StartNew) and queueing the dispatch can both fail synchronously —
        // and when they do the body has just as surely not run, so they belong on the same
        // retry-and-attribute path rather than surfacing as a bare fault from the harness.
        Task<bool> dispatched;
        try
        {
            dispatched = Session.Dispatch(async () =>
            {
                Volatile.Write(ref bodyEntered.Value, true);
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
        }
        catch (Exception queueFault)
        {
            throw new HeadlessSetupFaultException(queueFault);
        }

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
            catch (Exception dispatchFault) when (!Volatile.Read(ref bodyEntered.Value))
            {
                throw new HeadlessSetupFaultException(dispatchFault);
            }
        }

        if (!completed)
        {
            // First-writer-wins: the session queue is FIFO, so the first timeout is the actual
            // wedger; a parallel caller whose body queued behind it also times out but is
            // collateral — attribute it as such rather than letting the last writer steal blame.
            string? prior = Interlocked.CompareExchange(ref _wedgedBy, caller, null);
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

        try
        {
            await dispatched;
        }
        catch (Exception dispatchFault) when (!Volatile.Read(ref bodyEntered.Value))
        {
            throw new HeadlessSetupFaultException(dispatchFault);
        }
    }
}
