#region

using DemoViewer.NET.Debugging;
using Cs2DemoKit.Parser;
using DemoViewer.NET.ViewModels;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     "Verify in CS2" (the UI half; docs/csvg-integration/ux-design.md). Pins the
///     testable core of the Analysis-tab affordance — two-level gating, the trigger's firing-tick
///     resolution (an edge's own fire / a node's incoming-trigger fire / playhead fallback, always
///     frame clock AS-IS, no <c>−ServerStartTick</c>), raw spectate-name resolution, busy/re-entry,
///     and inline failure surfacing — without an MSAGL graph (the surface itself is a pointer-release
///     context menu that does not settle geometry headlessly) and without parsing a demo. Pure VM — no
///     headless UI session — so it runs in parallel.
/// </summary>
public class AnalysisVerifyInCs2Tests
{
    private static DemoFrame FrameAtTick(int serverTick) => new()
    {
        Command = "packet",
        FrameNumber = 0,
        HeaderLength = 0,
        IsCompressed = false,
        RawLength = 0,
        RawStart = 0,
        ServerTick = serverTick
    };

    // A synthetic message list whose frame i has ServerTick i*100 (index 0 → 0, 1 → 100, …).
    private static (DemoFrame Frame, NetMessage Message)[] MessagesWithTicks(int count)
    {
        (DemoFrame, NetMessage)[] messages = new (DemoFrame, NetMessage)[count];
        for (int i = 0; i < count; i++)
        {
            messages[i] = (FrameAtTick(i * 100), null!);
        }

        return messages;
    }

    private static ConditionTarget NodeTarget(string name) =>
        ConditionTarget.ForNode(new GraphNodeViewModel(name));

    // ── Pure tick resolution (frame clock AS-IS) ───────────────────────────────────────────────

    [Test]
    public async Task ResolveFrameClockTick_ValidIndex_ReturnsFrameServerTick()
    {
        (DemoFrame, NetMessage)[] messages =
            [(FrameAtTick(1000), null!), (FrameAtTick(54321), null!)];

        await Assert.That(AnalysisViewModel.ResolveFrameClockTick(messages, 1)).IsEqualTo(54321)
            .Because("the frame's ServerTick is the frame clock, passed to VerifyMomentAsync unmodified");
        await Assert.That(AnalysisViewModel.ResolveFrameClockTick(messages, 0)).IsEqualTo(1000);
    }

    [Test]
    public async Task ResolveFrameClockTick_UnpositionedOrOutOfRange_ReturnsNull()
    {
        (DemoFrame, NetMessage)[] messages = [(FrameAtTick(1000), null!)];

        await Assert.That(AnalysisViewModel.ResolveFrameClockTick(messages, -1)).IsNull();
        await Assert.That(AnalysisViewModel.ResolveFrameClockTick(messages, 5)).IsNull();
        await Assert.That(AnalysisViewModel.ResolveFrameClockTick(null, 0)).IsNull();
    }

    // ── Pure fire selection (the trigger fire nearest at-or-before the playhead, else the first) ─

    [Test]
    public async Task NearestFireMessageIndex_PicksLatestAtOrBeforePlayhead()
    {
        int[] fires = [5, 20, 40];

        await Assert.That(AnalysisViewModel.NearestFireMessageIndex(fires, 25)).IsEqualTo(20)
            .Because("the fire the user has most recently passed");
        await Assert.That(AnalysisViewModel.NearestFireMessageIndex(fires, 20)).IsEqualTo(20)
            .Because("sitting exactly on a fire verifies that fire");
    }

    [Test]
    public async Task NearestFireMessageIndex_BeforeAllFires_PicksFirst()
    {
        await Assert.That(AnalysisViewModel.NearestFireMessageIndex([20, 40], 5)).IsEqualTo(20)
            .Because("before any fire, the first (next) fire is the trigger to verify");
    }

    [Test]
    public async Task NearestFireMessageIndex_IsOrderIndependent_AndEmptyIsNull()
    {
        await Assert.That(AnalysisViewModel.NearestFireMessageIndex([40, 5, 20], 25)).IsEqualTo(20);
        await Assert.That(AnalysisViewModel.NearestFireMessageIndex([], 25)).IsNull();
    }

    // ── Pure spectate-name resolution (raw in-demo name; optional) ─────────────────────────────

    [Test]
    public async Task ResolveSpectateName_RealSlot_ReturnsRawName()
    {
        await Assert.That(AnalysisViewModel.ResolveSpectateName(new PlayerFilterOption(3, "s1mple")))
            .IsEqualTo("s1mple");
    }

    [Test]
    public async Task ResolveSpectateName_AllPlayersOrNone_ReturnsNull()
    {
        await Assert.That(AnalysisViewModel.ResolveSpectateName(
            new PlayerFilterOption(GraphFilterViewModel.AllPlayersSlot, "All players"))).IsNull();
        await Assert.That(AnalysisViewModel.ResolveSpectateName(null)).IsNull();
    }

    // ── Pure gate truth table (enabled condition; re-entry block) ───────────────────────────────

    [Test]
    public async Task CanVerify_TruthTable()
    {
        await Assert.That(AnalysisViewModel.CanVerify(false, true, 54321))
            .IsTrue().Because("not busy + a live Synced session + a positioned moment");
        await Assert.That(AnalysisViewModel.CanVerify(true, true, 54321))
            .IsFalse().Because("a verification already in flight blocks re-entry");
        await Assert.That(AnalysisViewModel.CanVerify(false, false, 54321))
            .IsFalse().Because("no live Synced session ⇒ disabled + prompt");
        await Assert.That(AnalysisViewModel.CanVerify(false, true, null))
            .IsFalse().Because("no moment positioned ⇒ nothing to verify");
    }

    // ── Target → firing tick ("the trigger's firing tick") ─────────────────────────────────────

    [Test]
    public async Task ResolveVerifyTick_EdgeTarget_UsesTheEdgesFire_NotThePlayhead()
    {
        using AnalysisViewModel vm = new();
        vm.SetVerifyPositionForTests(MessagesWithTicks(6), 2); // playhead frame tick = 200
        vm.SetVerifyEdgeFiresForTests(("A", "X", "player_death", null), [1, 4]);
        ConditionTarget edge = ConditionTarget.ForEdge(
            new GraphEdgeViewModel(new GraphNodeViewModel("A"), new GraphNodeViewModel("X"), "player_death", default));

        // Nearest fire at-or-before playhead (2) is message 1 ⇒ tick 100 — NOT the playhead's 200.
        await Assert.That(vm.ResolveVerifyTick(edge)).IsEqualTo(100)
            .Because("an edge is the trigger itself — its own recorded fire is verified");
    }

    [Test]
    public async Task ResolveVerifyTick_NodeTarget_UnionsIncomingTriggerFires()
    {
        using AnalysisViewModel vm = new();
        vm.SetVerifyPositionForTests(MessagesWithTicks(8), 6); // playhead frame tick = 600
        vm.SetVerifyEdgeFiresForTests(("A", "X", "e1", null), [2]);
        vm.SetVerifyEdgeFiresForTests(("B", "X", "e2", null), [5]);

        // Union {2,5}; nearest at-or-before playhead (6) is 5 ⇒ tick 500 — NOT the playhead's 600.
        await Assert.That(vm.ResolveVerifyTick(NodeTarget("X"))).IsEqualTo(500)
            .Because("a node verifies the nearest fire of the triggers that activate it");
    }

    [Test]
    public async Task ResolveVerifyTick_FallsBackToPlayhead_ForContextNodeOrNoTarget()
    {
        using AnalysisViewModel vm = new();
        vm.SetVerifyPositionForTests(MessagesWithTicks(4), 3); // playhead frame tick = 300

        await Assert.That(vm.ResolveVerifyTick(NodeTarget("no_incoming_trigger"))).IsEqualTo(300)
            .Because("a node with no incoming game-scoped trigger fire falls back to the current position");
        await Assert.That(vm.ResolveVerifyTick(null)).IsEqualTo(300);
    }

    // ── VM wiring: Filter selection → spectate name ────────────────────────────────────────────

    [Test]
    public async Task VerifySpectateName_TracksGraphFilterSelectedPlayer()
    {
        using AnalysisViewModel vm = new();

        await Assert.That(vm.VerifySpectateName).IsNull().Because("no player selected ⇒ null (spectate optional)");

        vm.Filter.SelectedPlayer = new PlayerFilterOption(3, "s1mple");
        await Assert.That(vm.VerifySpectateName).IsEqualTo("s1mple");

        vm.Filter.SelectedPlayer = new PlayerFilterOption(GraphFilterViewModel.AllPlayersSlot, "All players");
        await Assert.That(vm.VerifySpectateName).IsNull();
    }

    // ── Command gating (level-2): present but not verifiable ⇒ disabled ─────────────────────────

    [Test]
    public async Task VerifyCommand_PresentButNotSynced_IsDisabled()
    {
        using AnalysisViewModel vm = new();
        vm.IsVerifyInCs2Present = () => true;
        vm.CanVerifyMoment = () => false; // no live Synced session
        vm.SetVerifyPositionForTests(MessagesWithTicks(1), 0);

        await Assert.That(vm.VerifyInCs2Command.CanExecute(NodeTarget("n"))).IsFalse();
    }

    [Test]
    public async Task VerifyCommand_SyncedButNoMoment_IsDisabled()
    {
        using AnalysisViewModel vm = new();
        vm.IsVerifyInCs2Present = () => true;
        vm.CanVerifyMoment = () => true;
        // No SetVerifyPositionForTests ⇒ no tick resolvable for the node (no fires, no playhead).

        await Assert.That(vm.VerifyInCs2Command.CanExecute(NodeTarget("n"))).IsFalse()
            .Because("a Synced session with nothing positioned still has no tick to verify");
    }

    // ── Command flow: passes the resolved tick + raw name; arrival leaves status clean ──────────

    [Test]
    public async Task VerifyCommand_Invokes_WithFrameClockTickAndRawName_OnArrival()
    {
        using AnalysisViewModel vm = new();
        int? seenTick = null;
        string? seenName = "sentinel";
        vm.IsVerifyInCs2Present = () => true;
        vm.CanVerifyMoment = () => true;
        vm.VerifyMomentHandler = (tick, name, _) =>
        {
            seenTick = tick;
            seenName = name;
            return Task.FromResult(true); // deterministic paused arrival
        };
        vm.Filter.SelectedPlayer = new PlayerFilterOption(3, "s1mple");
        vm.SetVerifyPositionForTests([(FrameAtTick(1000), null!), (FrameAtTick(54321), null!)], 1);
        ConditionTarget target = NodeTarget("some_node"); // no fires ⇒ playhead frame tick
        string statusBefore = vm.StatusText;

        await Assert.That(vm.VerifyInCs2Command.CanExecute(target)).IsTrue();
        await vm.VerifyInCs2Command.ExecuteAsync(target);

        await Assert.That(seenTick).IsEqualTo(54321).Because("the positioned frame's ServerTick, passed AS-IS");
        await Assert.That(seenName).IsEqualTo("s1mple").Because("the filter-selected player's raw in-demo name");
        await Assert.That(vm.IsVerifying).IsFalse().Because("the busy flag clears after arrival");
        await Assert.That(vm.StatusText).IsEqualTo(statusBefore).Because("a successful verify surfaces no failure note");
    }

    // ── Command flow: a false return surfaces the inline failure note ───────────────────────────

    [Test]
    public async Task VerifyCommand_HandlerReturnsFalse_SurfacesInlineFailure()
    {
        using AnalysisViewModel vm = new();
        vm.IsVerifyInCs2Present = () => true;
        vm.CanVerifyMoment = () => true;
        vm.VerifyMomentHandler = (_, _, _) => Task.FromResult(false); // session dropped mid-seek
        vm.SetVerifyPositionForTests(MessagesWithTicks(1), 0);

        await vm.VerifyInCs2Command.ExecuteAsync(NodeTarget("n"));

        await Assert.That(vm.IsVerifying).IsFalse();
        await Assert.That(vm.StatusText).Contains("Couldn't verify in CS2")
            .Because("a false return is surfaced inline; the shell chip is the primary failure surface");
    }

    // ── Command flow: an in-flight verify blocks re-entry, then recovers ────────────────────────

    [Test]
    public async Task VerifyCommand_WhileInFlight_BlocksReentry_ThenRecovers()
    {
        using AnalysisViewModel vm = new();
        TaskCompletionSource<bool> gate = new();
        vm.IsVerifyInCs2Present = () => true;
        vm.CanVerifyMoment = () => true;
        vm.VerifyMomentHandler = (_, _, _) => gate.Task; // stays pending until released
        vm.SetVerifyPositionForTests(MessagesWithTicks(1), 0);
        ConditionTarget target = NodeTarget("n");

        Task running = vm.VerifyInCs2Command.ExecuteAsync(target);

        await Assert.That(vm.IsVerifying).IsTrue();
        await Assert.That(vm.VerifyInCs2Command.CanExecute(target)).IsFalse()
            .Because("a second verify must not launch while one is in flight");

        gate.SetResult(true);
        await running;

        await Assert.That(vm.IsVerifying).IsFalse();
        await Assert.That(vm.VerifyInCs2Command.CanExecute(target)).IsTrue().Because("re-enabled once the verify completes");
    }
}
