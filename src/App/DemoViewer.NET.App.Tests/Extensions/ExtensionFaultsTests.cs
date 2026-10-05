#region

using DemoViewer.NET.Extensions;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The fault tracker on its own: when an extension is switched off, that the switch-off waits for the UI
///     thread, that a burst from a per-frame site counts once, that faults raised while the extension is
///     being switched off do not count, and that the log stays readable when a site throws on every call.
/// </summary>
[NotInParallel]
public class ExtensionFaultsTests
{
    [Test]
    public async Task ThreeFaultsInAMinute_SwitchTheExtensionOff_OnlyOnceThePostRuns()
    {
        FaultRig rig = new();
        rig.Throw();
        rig.Throw();
        await Assert.That(rig.Faults.StateOf("pack.fake").Suspended).IsFalse();

        rig.Throw();
        await Assert.That(rig.Switch.Suspensions).IsEmpty().Because("the switch-off is posted, never applied inline");

        rig.Posts.Drain();
        ExtensionFaultState state = rig.Faults.StateOf("pack.fake");
        using (Assert.Multiple())
        {
            await Assert.That(rig.Switch.Suspensions).IsEquivalentTo(["pack.fake"]);
            await Assert.That(state.Suspended).IsTrue();
            await Assert.That(state.Count).IsEqualTo(3);
            await Assert.That(state.LastSite).IsEqualTo("site");
        }
    }

    [Test]
    public async Task FaultsSpreadOverTime_TripOnlyAtTheSessionLimit()
    {
        FaultRig rig = new();
        for (int i = 0; i < ExtensionFaults.SessionLimit - 1; i++)
        {
            rig.Throw();
            rig.Clock.Advance(TimeSpan.FromSeconds(31));
        }

        rig.Posts.Drain();
        await Assert.That(rig.Switch.Suspensions).IsEmpty().Because("never more than two inside any minute");

        rig.Throw();
        rig.Posts.Drain();
        await Assert.That(rig.Switch.Suspensions).IsEquivalentTo(["pack.fake"]);
    }

    [Test]
    public async Task ARecurringSite_CountsOncePerMinute_PerExceptionType()
    {
        FaultRig rig = new();
        for (int i = 0; i < 50; i++)
        {
            rig.Throw("playhead handler", FaultKind.Recurring);
        }

        await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(1);

        Action other = () => throw new ArgumentException("other type");
        rig.Faults.Run(rig.Scope, "playhead handler", other, FaultKind.Recurring);
        await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(2);

        rig.Clock.Advance(TimeSpan.FromSeconds(61));
        rig.Throw("playhead handler", FaultKind.Recurring);
        await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(3);
    }

    [Test]
    public async Task ALogOnlyFault_IsNeverCounted()
    {
        FaultRig rig = new();
        for (int i = 0; i < 20; i++)
        {
            rig.Throw("binding", FaultKind.LogOnly);
        }

        rig.Posts.Drain();
        await Assert.That(rig.Faults.StateOf("pack.fake")).IsEqualTo(ExtensionFaultState.None);
    }

    [Test]
    public async Task FaultsRaisedWhileTheExtensionIsSwitchedOff_AreNotCounted()
    {
        FaultRig rig = new();
        int raisedDuringSwitchOff = 0;
        rig.Switch.OnSuspend = () =>
        {
            for (int i = 0; i < 5; i++)
            {
                raisedDuringSwitchOff++;
                rig.Throw("detach");
            }
        };

        rig.Throw();
        rig.Throw();
        rig.Throw();
        rig.Posts.Drain();
        rig.Posts.Drain();

        using (Assert.Multiple())
        {
            await Assert.That(raisedDuringSwitchOff).IsEqualTo(5);
            await Assert.That(rig.Switch.Suspensions.Count).IsEqualTo(1);
            await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(3);
        }
    }

    [Test]
    public async Task Resume_LiftsTheSuspension_AndResetsTheCount()
    {
        FaultRig rig = new();
        rig.Throw();
        rig.Throw();
        rig.Throw();
        rig.Posts.Drain();

        rig.Faults.Resume("pack.fake");
        await Assert.That(rig.Switch.Resumptions).IsEquivalentTo(["pack.fake"]);
        await Assert.That(rig.Faults.StateOf("pack.fake")).IsEqualTo(ExtensionFaultState.None);

        rig.Throw();
        rig.Throw();
        rig.Posts.Drain();
        await Assert.That(rig.Switch.Suspensions.Count).IsEqualTo(1).Because("the count started over");
    }

    [Test]
    public async Task AStartupFailure_SuspendsAtOnce_AndCannotBeResumedInSession()
    {
        ManualClock clock = new();
        PostQueue posts = new();
        ExtensionScope scope = new("pack.fake", "pack.fake", "Fake", typeof(FaultRig).Assembly);
        ExtensionFaults faults = new([scope], posts.Post, clock);

        // Before the gate exists: the composition root registers services first.
        faults.FailStartup(scope, "register", new InvalidOperationException("no"));
        RecordingSwitch gate = new();
        faults.AttachSwitch(gate);
        await Assert.That(gate.Suspensions).IsEquivalentTo(["pack.fake"]).Because("attaching applies it before anything reads");

        faults.Resume("pack.fake");
        using (Assert.Multiple())
        {
            await Assert.That(gate.Resumptions).IsEmpty();
            await Assert.That(faults.StateOf("pack.fake").StartupFailed).IsTrue();
            await Assert.That(faults.StartupFailed("pack.fake")).IsTrue();
        }
    }

    [Test]
    public async Task ASiteThatThrowsEveryCall_IsLoggedAFewTimes_ThenOncePerMinute()
    {
        using CapturedLogs logs = CapturedLogs.Install();
        FaultRig rig = new();
        for (int i = 0; i < 40; i++)
        {
            rig.Throw("key handler", FaultKind.Recurring);
        }

        await Assert.That(logs.WithEvent(22).Count()).IsEqualTo(ExtensionFaults.LoggedPerSite);

        rig.Clock.Advance(TimeSpan.FromSeconds(61));
        rig.Throw("key handler", FaultKind.Recurring);
        rig.Throw("key handler", FaultKind.Recurring);
        await Assert.That(logs.WithEvent(22).Count()).IsEqualTo(ExtensionFaults.LoggedPerSite + 1);
    }

    [Test]
    public async Task TheSwitchOff_IsLoggedWithTheCountAndTheLastSite()
    {
        using CapturedLogs logs = CapturedLogs.Install();
        FaultRig rig = new();
        rig.Throw("library filter");
        rig.Throw("library filter");
        rig.Throw("settings page");
        rig.Posts.Drain();

        string line = logs.WithEvent(23).Single();
        await Assert.That(line).IsEqualTo("Extension Fake turned off for this session after 3 errors (last: settings page)");
    }

    [Test]
    public async Task ACancelledAsyncCall_CountsOnlyWhenItsOwnTokenWasNotCancelled()
    {
        FaultRig rig = new();
        using CancellationTokenSource own = new();
        await own.CancelAsync();
        await rig.Guard.RunAsync("enable", () => Task.FromCanceled(own.Token), own.Token);
        await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(0);

        using CancellationTokenSource other = new();
        await other.CancelAsync();
        await rig.Guard.RunAsync("enable", () => Task.FromCanceled(other.Token));
        await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(1);
    }

    [Test]
    public async Task AMulticast_RunsEveryHandler_PastAThrowingExtensionHandler_AndStillThrowsForTheHost()
    {
        // The rig's scope owns this test assembly, so a lambda here is "extension" code; a handler compiled
        // into the app assembly is host code.
        FaultRig rig = new();
        MulticastGuard<Action<int>> guard = new("advanced", FaultKind.Recurring);
        List<int> seen = [];
        Action<int> handlers = _ => throw new InvalidOperationException("extension");
        handlers += seen.Add;

        guard.Invoke(rig.Faults, handlers, 7, static (h, v) => h(v));
        await Assert.That(seen).IsEquivalentTo([7]);
        await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(1);

        // List<int>.RemoveAt lives in the runtime, not in any extension: a host handler.
        Action<int> hostThrows = new List<int>().RemoveAt;
        hostThrows += seen.Add;
        Assert.Throws<ArgumentOutOfRangeException>(() => guard.Invoke(rig.Faults, hostThrows, 1, static (h, v) => h(v)));
    }
}
