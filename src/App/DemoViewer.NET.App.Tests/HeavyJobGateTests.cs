#region

using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The resized <see cref="HeavyJobGate" /> (demo-processing-queue.md). At
///     <c>MaxConcurrency == 1</c> every path must be behaviourally identical to the historical
///     <c>SemaphoreSlim(1,1)</c> gate: one holder at a time; background yields to a pending
///     interactive between demos; a reel session drains the in-flight holder and refuses
///     interactive. Resize is apply-forward: grow admits more, shrink drains.
///     <para>No demo parses here: pure concurrency assertions, so the class runs in parallel.</para>
/// </summary>
public class HeavyJobGateTests
{
    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(5);
        }
    }

    // Asserts a task is STILL not complete after a settle window (used to prove a waiter is blocked).
    private static async Task AssertStaysBlockedAsync(Task task, string what, int settleMs = 300)
    {
        await Task.Delay(settleMs);
        if (task.IsCompleted)
        {
            throw new InvalidOperationException($"expected {what} to stay blocked, but it completed");
        }
    }

    // TUnit's async value-delegate builder doesn't surface Throws for a Task<T>-returning lambda, so
    // assert the throw directly (catches derived types too, e.g. TaskCanceledException : OperationCanceled).
    private static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception other)
        {
            throw new InvalidOperationException(
                $"expected {typeof(TException).Name}, got {other.GetType().Name}: {other.Message}");
        }

        throw new InvalidOperationException($"expected {typeof(TException).Name}, but nothing was thrown");
    }

    [Test]
    public async Task Max1_OnlyOneHolder_SecondBackgroundWaitsUntilRelease()
    {
        using HeavyJobGate gate = new();

        IDisposable first = await gate.AcquireBackgroundAsync();
        await Assert.That(gate.InFlight).IsEqualTo(1);

        Task<IDisposable> second = gate.AcquireBackgroundAsync();
        await AssertStaysBlockedAsync(second, "the second background acquire at max=1");
        await Assert.That(gate.InFlight).IsEqualTo(1).Because("still just the one holder");

        first.Dispose();
        IDisposable secondHeld = await second;
        await Assert.That(gate.InFlight).IsEqualTo(1);
        secondHeld.Dispose();
        await Assert.That(gate.InFlight).IsEqualTo(0);
    }

    [Test]
    public async Task Max1_BackgroundYieldsToPendingInteractive_ThenInteractiveGoesFirst()
    {
        using HeavyJobGate gate = new();

        // A background parse is mid-demo.
        IDisposable bg1 = await gate.AcquireBackgroundAsync();

        // An interactive load arrives and starts waiting (pending flag up).
        Task<IDisposable> interactive = gate.AcquireInteractiveAsync();
        await WaitForAsync(() => gate.IsInteractivePending, "interactive pending flag");

        // A second background acquire must YIELD while interactive is pending.
        Task<IDisposable> bg2 = gate.AcquireBackgroundAsync();
        await AssertStaysBlockedAsync(bg2, "the second background while interactive is pending");

        // The in-flight background finishes → the interactive gets the slot BEFORE bg2.
        bg1.Dispose();
        IDisposable interactiveHeld = await interactive;
        await AssertStaysBlockedAsync(bg2, "bg2 while interactive holds at max=1");

        // Only once interactive releases does the yielded background proceed.
        interactiveHeld.Dispose();
        IDisposable bg2Held = await bg2;
        bg2Held.Dispose();
        await Assert.That(gate.InFlight).IsEqualTo(0);
    }

    [Test]
    public async Task Reel_RefusesInteractive_AndDrainsInFlightBackground()
    {
        using HeavyJobGate gate = new();

        // Background mid-demo when a reel starts: EnterReelSession must DRAIN it (wait for release).
        IDisposable bg = await gate.AcquireBackgroundAsync();
        Task<IDisposable> reel = gate.EnterReelSessionAsync();
        await WaitForAsync(() => gate.IsReelActive, "reel flag");
        await AssertStaysBlockedAsync(reel, "reel entry while a background parse is in flight");

        // Interactive is REFUSED (not queued) while the reel flag is up.
        await AssertThrowsAsync<ReelInProgressException>(async () =>
        {
            using IDisposable _ = await gate.AcquireInteractiveAsync();
        });

        // The background finishes → reel entry completes (machine drained).
        bg.Dispose();
        IDisposable reelSlot = await reel;
        await Assert.That(gate.InFlight).IsEqualTo(0);

        // While the reel owns the machine, a background acquire yields (does not proceed).
        Task<IDisposable> bgDuringReel = gate.AcquireBackgroundAsync();
        await AssertStaysBlockedAsync(bgDuringReel, "background while reel is active");

        // Reel ends → background resumes.
        reelSlot.Dispose();
        IDisposable resumed = await bgDuringReel;
        resumed.Dispose();
    }

    [Test]
    public async Task Reel_StartedWhileInteractiveWaiting_RefusesTheWaiter()
    {
        using HeavyJobGate gate = new();

        // Hold the single slot with a background so the interactive must wait.
        IDisposable bg = await gate.AcquireBackgroundAsync();
        Task<IDisposable> interactive = gate.AcquireInteractiveAsync();
        await WaitForAsync(() => gate.IsInteractivePending, "interactive pending");

        // A reel begins while the interactive is still waiting → the waiter is refused, not queued.
        Task<IDisposable> reel = gate.EnterReelSessionAsync();

        await AssertThrowsAsync<ReelInProgressException>(async () => await interactive);

        // The pending flag was cleared by the refusal, so the reel can drain the background and enter.
        bg.Dispose();
        IDisposable reelSlot = await reel;
        reelSlot.Dispose();
    }

    [Test]
    public async Task Resize_Grow_AdmitsMoreConcurrentBackground()
    {
        using HeavyJobGate gate = new();

        IDisposable a = await gate.AcquireBackgroundAsync();
        Task<IDisposable> b = gate.AcquireBackgroundAsync();
        await AssertStaysBlockedAsync(b, "second background at max=1");

        // Grow to 2 → the second background is admitted on the next poll cycle.
        gate.MaxConcurrency = 2;
        IDisposable bHeld = await b;
        await Assert.That(gate.InFlight).IsEqualTo(2).Because("both run once the cap grew to 2");

        // A third stays blocked at max=2.
        Task<IDisposable> c = gate.AcquireBackgroundAsync();
        await AssertStaysBlockedAsync(c, "third background at max=2");

        a.Dispose();
        IDisposable cHeld = await c;
        await Assert.That(gate.InFlight).IsEqualTo(2);
        bHeld.Dispose();
        cHeld.Dispose();
        await Assert.That(gate.InFlight).IsEqualTo(0);
    }

    [Test]
    public async Task Resize_ClampsToBounds()
    {
        using HeavyJobGate gate = new();
        gate.MaxConcurrency = 0;
        await Assert.That(gate.MaxConcurrency).IsEqualTo(1).Because("clamped up to the safe floor of 1");
        gate.MaxConcurrency = 999;
        await Assert.That(gate.MaxConcurrency).IsEqualTo(HeavyJobGate.HardCapConcurrency)
            .Because("clamped down to the hard cap");
        gate.MaxConcurrency = 3;
        await Assert.That(gate.MaxConcurrency).IsEqualTo(3);
    }

    [Test]
    public async Task Resize_ShrinkWhileIdle_TakesEffectImmediately()
    {
        using HeavyJobGate gate = new()
        {
            MaxConcurrency = 3
        };

        // Idle, then shrink to 1: the very next acquire pair must serialize (only one holder).
        gate.MaxConcurrency = 1;

        IDisposable a = await gate.AcquireBackgroundAsync();
        Task<IDisposable> b = gate.AcquireBackgroundAsync();
        await AssertStaysBlockedAsync(b, "second background after shrink-to-1");
        await Assert.That(gate.InFlight).IsEqualTo(1);

        a.Dispose();
        IDisposable bHeld = await b;
        bHeld.Dispose();
    }

    [Test]
    public async Task Resize_ShrinkWhileRunning_DrainsExcessBeforeAdmittingNew()
    {
        using HeavyJobGate gate = new()
        {
            MaxConcurrency = 3
        };

        IDisposable a = await gate.AcquireBackgroundAsync();
        IDisposable b = await gate.AcquireBackgroundAsync();
        await Assert.That(gate.InFlight).IsEqualTo(2);

        // Shrink to 1 while 2 are in flight: no new start until _held falls below 1.
        gate.MaxConcurrency = 1;
        Task<IDisposable> c = gate.AcquireBackgroundAsync();
        await AssertStaysBlockedAsync(c, "new background while over the shrunk cap (held=2 > 1)");

        a.Dispose(); // held → 1, still not below the new cap of 1
        await AssertStaysBlockedAsync(c, "new background while held==cap after one release");

        b.Dispose(); // held → 0 < 1
        IDisposable cHeld = await c;
        await Assert.That(gate.InFlight).IsEqualTo(1);
        cHeld.Dispose();
    }

    [Test]
    public async Task CancelledInteractiveWaiter_ClearsPending_SoBackgroundResumes()
    {
        using HeavyJobGate gate = new();

        IDisposable bg = await gate.AcquireBackgroundAsync();

        using CancellationTokenSource cts = new();
        Task<IDisposable> interactive = gate.AcquireInteractiveAsync(cts.Token);
        await WaitForAsync(() => gate.IsInteractivePending, "interactive pending");

        // A background that arrived is yielding behind the pending interactive.
        Task<IDisposable> bg2 = gate.AcquireBackgroundAsync();
        await AssertStaysBlockedAsync(bg2, "background yielding to the pending interactive");

        // Cancel the interactive: its pending flag must clear so the yielded background can proceed.
        cts.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(async () => await interactive);
        await WaitForAsync(() => !gate.IsInteractivePending, "pending cleared after cancel");

        bg.Dispose();
        IDisposable bg2Held = await bg2;
        bg2Held.Dispose();
        await Assert.That(gate.InFlight).IsEqualTo(0);
    }

    [Test]
    public async Task Max1_InteractiveExclusiveWithBackground_NeverBothInFlight()
    {
        using HeavyJobGate gate = new();

        IDisposable interactive = await gate.AcquireInteractiveAsync();
        await Assert.That(gate.InFlight).IsEqualTo(1);

        Task<IDisposable> bg = gate.AcquireBackgroundAsync();
        await AssertStaysBlockedAsync(bg, "background while interactive holds the single slot");

        interactive.Dispose();
        IDisposable bgHeld = await bg;
        bgHeld.Dispose();
        await Assert.That(gate.InFlight).IsEqualTo(0);
    }

    // ── B4: the 2D-export session ──────────────────────────────────────────────
    // A new session kind rather than an interactive or a background slot (plan D10). An export is
    // CPU-bound and holds one extra EntityTracker, not a multi-gigabyte parse: taking an interactive slot
    // would block the user's next demo open for the whole render, and taking a background one would queue
    // it behind, and then in front of, the user's own work.

    [Test]
    public async Task Export_PausesBackground_ButNeverBlocksAnInteractiveLoad()
    {
        using HeavyJobGate gate = new();
        using IDisposable export = await gate.EnterExportSessionAsync();

        await Assert.That(gate.IsExportActive).IsTrue();
        await Assert.That(gate.CanStartBackground).IsFalse();

        Task<IDisposable> background = gate.AcquireBackgroundAsync();
        await AssertStaysBlockedAsync(background, "background while an export renders");

        // The user's foreground demo load still wins: that is the whole point of not reusing the reel
        // session's semantics here.
        IDisposable interactive = await gate.AcquireInteractiveAsync();
        interactive.Dispose();

        export.Dispose();
        IDisposable held = await background;
        held.Dispose();
        await Assert.That(gate.IsExportActive).IsFalse();
    }

    [Test]
    public async Task Export_DoesNotDrainAnInFlightParse()
    {
        using HeavyJobGate gate = new();
        using IDisposable parse = await gate.AcquireInteractiveAsync();

        // Unlike a reel session, an export shares the machine with a parse that is already running; it
        // only declines to let a NEW one start. Entering must therefore not wait for the drain.
        Task<IDisposable> entering = gate.EnterExportSessionAsync();
        IDisposable export = await entering.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.That(gate.InFlight).IsEqualTo(1);
        export.Dispose();
    }

    [Test]
    public async Task AReel_IsRefusedWhileAnExportRenders()
    {
        using HeavyJobGate gate = new();
        using IDisposable export = await gate.EnterExportSessionAsync();

        ExportInProgressException? refusal = null;
        try
        {
            await gate.EnterReelSessionAsync();
        }
        catch (ExportInProgressException ex)
        {
            refusal = ex;
        }

        await Assert.That(refusal).IsNotNull();
        await Assert.That(refusal!.Message).Contains("2D video export");
    }

    [Test]
    public async Task AnExport_IsRefusedWhileAReelRenders()
    {
        using HeavyJobGate gate = new();
        using IDisposable reel = await gate.EnterReelSessionAsync();

        ReelInProgressException? refusal = null;
        try
        {
            await gate.EnterExportSessionAsync();
        }
        catch (ReelInProgressException ex)
        {
            refusal = ex;
        }

        // Symmetric with the case above: whichever heavy renderer got there first keeps the machine, and
        // the second is told so instead of quietly interleaving with it.
        await Assert.That(refusal).IsNotNull();
    }

    [Test]
    public async Task DisposingAnExportSessionTwice_IsSafe()
    {
        using HeavyJobGate gate = new();
        IDisposable export = await gate.EnterExportSessionAsync();

        export.Dispose();
        export.Dispose();

        // A double release that decremented twice would leave the counter negative and make the NEXT
        // export's refusal checks silently pass.
        await Assert.That(gate.IsExportActive).IsFalse();
        await Assert.That(gate.CanStartBackground).IsTrue();
    }
}
