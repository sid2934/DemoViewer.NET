#region

using DemoViewer.NET.Services.Tutorial;
using DemoViewer.NET.ViewModels.Tutorial;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Unit coverage of the <see cref="TutorialController" /> state machine, independent of any shell or real
///     demo: the two-segment sequencing (first-run plays immediately; at the WaitsForDemo gateway the tour
///     stays visible and WAITS when nothing is open, then resumes on
///     <see cref="TutorialController.NotifyDemoLoaded" />), the whole-script step indicator ("N of 8"), the
///     per-step tab-switch mapping, the CTA label overrides, and the Back / Skip / Finish transitions. Pure
///     logic (no visual tree), so this class runs in parallel; the over-the-real-shell resumption is covered
///     separately by <see cref="TutorialDeferredSegmentTests" />.
/// </summary>
public class TutorialControllerTests
{
    // Builds a controller over the real authored script, capturing the tab-switch requests and "seen" marks so
    // the sequencing can be asserted without a shell. `demoLoaded` backs the is-demo-open predicate the engine
    // consults at the first-run→demo-segment gateway (false = the gateway waits; true = it is Next-able).
    private static (TutorialController Controller, List<string?> Tabs, Func<int> SeenCount) NewController(
        Func<bool>? demoLoaded = null, Func<bool>? demoAvailable = null, Func<bool>? sampleCta = null)
    {
        List<string?> tabs = [];
        int seen = 0;
        TutorialController controller = new(
            TutorialSteps.Default,
            tabId => tabs.Add(tabId),
            demoLoaded ?? (() => false),
            () => seen++,
            demoAvailable ?? (() => false),
            sampleCta ?? (() => false));
        return (controller, tabs, () => seen);
    }

    [Test]
    public async Task Start_BeginsFirstRun_AtWelcome_WithGetStartedCta()
    {
        (TutorialController controller, List<string?> tabs, _) = NewController();

        controller.Start();

        TutorialViewModel vm = controller.ViewModel;
        using (Assert.Multiple())
        {
            await Assert.That(controller.IsActive).IsTrue();
            await Assert.That(vm.IsActive).IsTrue();
            await Assert.That(vm.CurrentStep!.Title).IsEqualTo("Welcome to DemoViewer");
            await Assert.That(vm.CurrentStep!.Segment).IsEqualTo(TutorialSegment.FirstRun);
            await Assert.That(vm.StepNumber).IsEqualTo(1);
            await Assert.That(vm.StepCount).IsEqualTo(8);
            await Assert.That(vm.StepIndicator).IsEqualTo("1 of 8");
            await Assert.That(vm.IsWaiting).IsFalse();
            await Assert.That(vm.CanGoBack).IsFalse().Because("welcome is the first step of the first run");
            await Assert.That(vm.NextLabel).IsEqualTo("Get started")
                .Because("the welcome step's NextLabelOverride is the CTA");
            await Assert.That(tabs[0]).IsNull().Because("the centred welcome card switches to no tab");
        }
    }

    [Test]
    public async Task Advance_ThroughFirstRun_MapsTabs_ThenWaitsAtGateway()
    {
        (TutorialController controller, List<string?> tabs, _) = NewController();
        TutorialViewModel vm = controller.ViewModel;

        controller.Start(); // welcome (1/8), tab=null
        vm.NextCommand.Execute(null); // tab-nav (2/8)
        await Assert.That(vm.CurrentStep!.Target).IsEqualTo(TutorialTarget.TabNav);
        await Assert.That(vm.StepNumber).IsEqualTo(2);
        await Assert.That(vm.CanGoBack).IsTrue();
        await Assert.That(tabs[^1]).IsEqualTo("builtin.library")
            .Because("the tab-strip step lands on the Library as its backdrop tab");

        vm.NextCommand.Execute(null); // library (3/8)
        await Assert.That(vm.CurrentStep!.Target).IsEqualTo(TutorialTarget.LibraryTab);
        await Assert.That(vm.StepNumber).IsEqualTo(3);
        await Assert.That(tabs[^1]).IsEqualTo("builtin.library");

        vm.NextCommand.Execute(null); // open-demo gateway (4/8)
        await Assert.That(vm.CurrentStep!.Target).IsEqualTo(TutorialTarget.OpenDemo);
        await Assert.That(vm.StepNumber).IsEqualTo(4);
        await Assert.That(tabs[^1]).IsEqualTo("builtin.library")
            .Because("the Open-Demo gateway still uses the Library as its backdrop tab");

        // At the gateway with no demo open, the tour stays VISIBLE and waits (it does not hide/defer), disables
        // manual advance, and shows the authored waiting hint. The auto-advance is driven by NotifyDemoLoaded.
        using (Assert.Multiple())
        {
            await Assert.That(controller.IsActive).IsTrue()
                .Because("the gateway waits in place rather than tearing the overlay down");
            await Assert.That(vm.IsWaiting).IsTrue();
            await Assert.That(vm.CanGoNext).IsFalse().Because("advance is disabled until a demo is opened");
            await Assert.That(vm.NextCommand.CanExecute(null)).IsFalse();
            await Assert.That(vm.WaitingHint).IsNotEmpty();
            await Assert.That(vm.CanGoBack).IsTrue().Because("the user can still step back through the first run");
        }
    }

    [Test]
    public async Task Gateway_TargetsFirstLibraryCard_WhenAvailable_ElseOpenDemoButton()
    {
        // With a demo in the library the gateway points the spotlight at the first library card (double-click
        // loads it, no dialog); the hint reflects that.
        (TutorialController withDemo, _, _) = NewController(demoAvailable: () => true);
        TutorialViewModel a = withDemo.ViewModel;
        withDemo.Start();
        a.NextCommand.Execute(null); // tab-nav
        a.NextCommand.Execute(null); // library
        a.NextCommand.Execute(null); // gateway → waiting

        using (Assert.Multiple())
        {
            await Assert.That(a.IsWaiting).IsTrue();
            await Assert.That(a.ActiveTarget).IsEqualTo(TutorialTarget.FirstLibraryCard);
            await Assert.That(a.WaitingHint).Contains("Double-click");
        }

        // With an empty library it falls back to the Open-Demo button (picker); the hint reflects that.
        (TutorialController noDemo, _, _) = NewController(demoAvailable: () => false);
        TutorialViewModel b = noDemo.ViewModel;
        noDemo.Start();
        b.NextCommand.Execute(null); // tab-nav
        b.NextCommand.Execute(null); // library
        b.NextCommand.Execute(null); // gateway → waiting

        using (Assert.Multiple())
        {
            await Assert.That(b.IsWaiting).IsTrue();
            await Assert.That(b.ActiveTarget).IsEqualTo(TutorialTarget.OpenDemo);
            await Assert.That(b.WaitingHint).Contains("Open Demo button");
        }
    }

    // The gateway's full preference ladder with the bundled sample in play: an empty library with the
    // hero's "Try a sample match" CTA on screen spotlights the CTA (one click, no dialog); a library
    // demo still outranks the sample (the user's own content first).
    [Test]
    public async Task Gateway_TargetsSampleCta_WhenLibraryEmpty_ButLibraryCardStillWins()
    {
        (TutorialController sampleOnly, _, _) = NewController(sampleCta: () => true);
        TutorialViewModel a = sampleOnly.ViewModel;
        sampleOnly.Start();
        a.NextCommand.Execute(null); // tab-nav
        a.NextCommand.Execute(null); // library
        a.NextCommand.Execute(null); // gateway → waiting

        using (Assert.Multiple())
        {
            await Assert.That(a.IsWaiting).IsTrue();
            await Assert.That(a.ActiveTarget).IsEqualTo(TutorialTarget.SampleDemo);
            await Assert.That(a.WaitingHint).Contains("sample match");
        }

        (TutorialController both, _, _) = NewController(demoAvailable: () => true, sampleCta: () => true);
        TutorialViewModel b = both.ViewModel;
        both.Start();
        b.NextCommand.Execute(null); // tab-nav
        b.NextCommand.Execute(null); // library
        b.NextCommand.Execute(null); // gateway → waiting

        await Assert.That(b.ActiveTarget).IsEqualTo(TutorialTarget.FirstLibraryCard)
            .Because("a demo already in the user's library outranks the bundled sample");
    }

    [Test]
    public async Task NotifyDemoLoaded_WhileWaiting_ResumesAtStats_AsStepFiveOfEight()
    {
        (TutorialController controller, List<string?> tabs, _) = NewController();
        TutorialViewModel vm = controller.ViewModel;

        controller.Start();
        vm.NextCommand.Execute(null); // tab-nav
        vm.NextCommand.Execute(null); // library
        vm.NextCommand.Execute(null); // gateway → waiting

        controller.NotifyDemoLoaded();

        using (Assert.Multiple())
        {
            await Assert.That(controller.IsActive).IsTrue().Because("a demo load resumes the waiting gateway");
            await Assert.That(vm.IsWaiting).IsFalse();
            await Assert.That(vm.CurrentStep!.Target).IsEqualTo(TutorialTarget.StatsContent);
            await Assert.That(vm.CurrentStep!.Segment).IsEqualTo(TutorialSegment.DemoLoaded);
            await Assert.That(vm.StepNumber).IsEqualTo(5)
                .Because("the indicator counts across the whole 8-step script, not per-run");
            await Assert.That(vm.StepIndicator).IsEqualTo("5 of 8");
            await Assert.That(vm.CanGoBack).IsFalse()
                .Because("stats is the first step of the resumed demo run");
            await Assert.That(tabs[^1]).IsEqualTo("builtin.stats");
        }
    }

    [Test]
    public async Task Advance_ThroughFirstRun_WhenDemoAlreadyOpen_IsNextableAtGateway()
    {
        // Replay-from-Settings scenario: a demo is already open when the tour reaches the gateway, so it is a
        // normal Next-able step (no waiting) and advancing plays the demo segment.
        (TutorialController controller, List<string?> tabs, _) = NewController(() => true);
        TutorialViewModel vm = controller.ViewModel;

        controller.Start(); // welcome (1/8)
        vm.NextCommand.Execute(null); // tab-nav (2/8)
        vm.NextCommand.Execute(null); // library (3/8)
        vm.NextCommand.Execute(null); // gateway (4/8), demo already open

        await Assert.That(vm.CurrentStep!.Target).IsEqualTo(TutorialTarget.OpenDemo);
        await Assert.That(vm.IsWaiting).IsFalse().Because("a demo is already open, so the gateway does not wait");
        await Assert.That(vm.CanGoNext).IsTrue();

        vm.NextCommand.Execute(null); // → demo segment plays immediately

        using (Assert.Multiple())
        {
            await Assert.That(controller.IsActive).IsTrue();
            await Assert.That(vm.CurrentStep!.Target).IsEqualTo(TutorialTarget.StatsContent);
            await Assert.That(vm.StepNumber).IsEqualTo(5);
            await Assert.That(vm.StepIndicator).IsEqualTo("5 of 8");
            await Assert.That(vm.CanGoBack).IsFalse().Because("stats is the first step of the demo run");
            await Assert.That(tabs[^1]).IsEqualTo("builtin.stats");
        }
    }

    [Test]
    public async Task NotifyDemoLoaded_WhenNotWaiting_IsNoOp()
    {
        (TutorialController controller, _, _) = NewController();

        // Before Start: nothing is waiting, so a demo-load signal must not spuriously start the tour.
        controller.NotifyDemoLoaded();
        await Assert.That(controller.IsActive).IsFalse();

        controller.Start(); // welcome, first-run segment, not a waiting gateway
        controller.NotifyDemoLoaded();
        await Assert.That(controller.ViewModel.CurrentStep!.Target).IsEqualTo(TutorialTarget.None)
            .Because("a demo load on a non-waiting step does not jump to the demo segment");
        await Assert.That(controller.ViewModel.StepNumber).IsEqualTo(1);
    }

    [Test]
    public async Task DemoSegment_AdvancesToOutro_ThenFinishTearsDown()
    {
        (TutorialController controller, List<string?> tabs, Func<int> seenCount) = NewController();
        TutorialViewModel vm = controller.ViewModel;

        controller.Start();
        vm.NextCommand.Execute(null); // tab-nav
        vm.NextCommand.Execute(null); // library
        vm.NextCommand.Execute(null); // gateway → waiting
        controller.NotifyDemoLoaded(); // stats (5/8)

        vm.NextCommand.Execute(null); // 2D playback (6/8)
        await Assert.That(vm.CurrentStep!.Target).IsEqualTo(TutorialTarget.PlaybackTab);
        await Assert.That(vm.StepNumber).IsEqualTo(6);
        await Assert.That(tabs[^1]).IsEqualTo("playback2d.viewport");

        vm.NextCommand.Execute(null); // transport (7/8)
        await Assert.That(vm.CurrentStep!.Target).IsEqualTo(TutorialTarget.PlaybackTransport);
        await Assert.That(vm.StepNumber).IsEqualTo(7);
        await Assert.That(tabs[^1]).IsEqualTo("playback2d.viewport");

        vm.NextCommand.Execute(null); // outro (8/8)
        await Assert.That(vm.CurrentStep!.Target).IsEqualTo(TutorialTarget.None);
        await Assert.That(vm.StepNumber).IsEqualTo(8);
        await Assert.That(vm.StepIndicator).IsEqualTo("8 of 8");
        await Assert.That(vm.HasSpotlight).IsFalse().Because("the outro is a centred card");
        await Assert.That(vm.NextLabel).IsEqualTo("Finish").Because("the outro's NextLabelOverride is the CTA");
        await Assert.That(seenCount()).IsEqualTo(0).Because("not marked seen until the tour actually finishes");

        vm.NextCommand.Execute(null); // Finish
        using (Assert.Multiple())
        {
            await Assert.That(controller.IsActive).IsFalse();
            await Assert.That(vm.CurrentStep).IsNull().Because("Finish tears the step down");
            await Assert.That(seenCount()).IsEqualTo(1).Because("finishing records the 'seen it' mark once");
        }
    }

    [Test]
    public async Task Skip_DuringFirstRun_FinishesImmediately()
    {
        (TutorialController controller, _, Func<int> seenCount) = NewController();
        TutorialViewModel vm = controller.ViewModel;

        controller.Start();
        vm.NextCommand.Execute(null); // tab-nav

        vm.SkipCommand.Execute(null);

        await Assert.That(controller.IsActive).IsFalse();
        await Assert.That(vm.CurrentStep).IsNull();
        await Assert.That(seenCount()).IsEqualTo(1).Because("Skip marks the tour seen, same as Finish");
    }

    [Test]
    public async Task Back_ReturnsWithinTheCurrentRun()
    {
        (TutorialController controller, _, _) = NewController();
        TutorialViewModel vm = controller.ViewModel;

        controller.Start();
        vm.NextCommand.Execute(null); // tab-nav (2/8)
        vm.NextCommand.Execute(null); // library (3/8)

        vm.BackCommand.Execute(null); // → tab-nav (2/8)
        await Assert.That(vm.StepNumber).IsEqualTo(2);
        await Assert.That(vm.CurrentStep!.Target).IsEqualTo(TutorialTarget.TabNav);

        vm.BackCommand.Execute(null); // → welcome (1/8)
        await Assert.That(vm.StepNumber).IsEqualTo(1);
        await Assert.That(vm.CanGoBack).IsFalse().Because("cannot go back past the first step of the run");
    }

    [Test]
    public async Task Back_FromWaitingGateway_ClearsTheWaitingState()
    {
        (TutorialController controller, _, _) = NewController();
        TutorialViewModel vm = controller.ViewModel;

        controller.Start();
        vm.NextCommand.Execute(null); // tab-nav
        vm.NextCommand.Execute(null); // library
        vm.NextCommand.Execute(null); // gateway → waiting
        await Assert.That(vm.IsWaiting).IsTrue();

        vm.BackCommand.Execute(null); // → library

        using (Assert.Multiple())
        {
            await Assert.That(vm.IsWaiting).IsFalse().Because("stepping off the gateway leaves the waiting state");
            await Assert.That(vm.CanGoNext).IsTrue();
            await Assert.That(vm.WaitingHint).IsEmpty();
            await Assert.That(vm.CurrentStep!.Target).IsEqualTo(TutorialTarget.LibraryTab);
        }
    }

    // The authored script is the engine's input contract: eight steps, the CTA overrides on the two centred
    // cards, the segment split (4 first-run + 4 demo-loaded), and the single WaitsForDemo gateway with a hint.
    [Test]
    public async Task DefaultScript_HasEightSteps_WithSegmentSplit_CtaOverrides_AndOneGateway()
    {
        IReadOnlyList<TutorialStep> steps = TutorialSteps.Default;
        using (Assert.Multiple())
        {
            await Assert.That(steps.Count).IsEqualTo(8);
            await Assert.That(steps.Count(s => s.Segment == TutorialSegment.FirstRun)).IsEqualTo(4);
            await Assert.That(steps.Count(s => s.Segment == TutorialSegment.DemoLoaded)).IsEqualTo(4);
            await Assert.That(steps[0].NextLabelOverride).IsEqualTo("Get started");
            await Assert.That(steps[^1].NextLabelOverride).IsEqualTo("Finish");

            TutorialStep[] gateways = steps.Where(s => s.WaitsForDemo).ToArray();
            await Assert.That(gateways.Length).IsEqualTo(1).Because("exactly one step gates on an open demo");
            await Assert.That(gateways[0].Target).IsEqualTo(TutorialTarget.OpenDemo);
            await Assert.That(gateways[0].WaitingHint).IsNotNull();
        }
    }
}
