#region

using DemoViewer.NET.ViewModels.Tutorial;

#endregion

namespace DemoViewer.NET.Services.Tutorial;

/// <summary>
///     Drives the first-run Visual Walkthrough: owns the step script + current position, feeds the display-only
///     <see cref="TutorialViewModel" /> one step at a time, and switches the workspace tab so each step's target
///     region is realized before the overlay measures it. All logic is App-layer / UI-thread; it never touches
///     parser or entity state, and it performs no visual-tree work (the overlay view resolves the spotlight
///     rect from the anchor registry).
///     <para>
///         <b>Two segments, one indicator.</b> The tour is authored as one 8-step script but plays in two runs:
///         the <see cref="TutorialSegment.FirstRun" /> steps (welcome, tabs, library, open-a-demo) play right
///         after setup with no demo; the <see cref="TutorialSegment.DemoLoaded" /> steps (stats, playback,
///         controls, outro) need an open demo. The hand-off is the <see cref="TutorialStep.WaitsForDemo" />
///         gateway (the open-a-demo step): if no demo is open when the user reaches it, the tour
///         <b>
///             stays
///             visible and waits
///         </b>
///         — spotlight on the Open-Demo affordance, advance disabled, a hint shown — and
///         auto-advances into the demo run the instant a demo loads (<see cref="NotifyDemoLoaded" />). If a demo
///         is already open at the gateway (replay-from-Settings), it is a normal Next-able step. The step
///         indicator ("2 of 8") counts across the whole script so the two runs read as one tour.
///     </para>
/// </summary>
public sealed class TutorialController
{
    // Hints shown while a gateway step waits, tailored to what the spotlight points at.
    private const string CardWaitingHint =
        "Double-click a demo below to open it — the tour will pick back up automatically.";

    private const string ButtonWaitingHint =
        "Click the highlighted Open Demo button — the tour will pick back up automatically.";

    private const string SampleWaitingHint =
        "Click “Try a sample match” to open the bundled demo — the tour will pick back up automatically.";

    // Global indices (into _steps) of each run, so the indicator can show the whole-script position.
    private readonly List<int> _demoRun = [];
    private readonly List<int> _firstRun = [];

    private readonly Func<bool> _hasDemoAvailable;
    private readonly Func<bool> _hasSampleCta;
    private readonly Func<bool> _isDemoLoaded;
    private readonly Action _markSeen;
    private readonly Action<string?> _selectTab;
    private readonly IReadOnlyList<TutorialStep> _steps;

    private int _pos; // position within the current run
    private IReadOnlyList<int> _run = []; // global indices of the run currently playing

    /// <summary>
    ///     Builds the controller over the authored script.
    /// </summary>
    /// <param name="steps">The walkthrough script (defaults to <see cref="TutorialSteps.Default" />).</param>
    /// <param name="selectTab">
    ///     Switches the workspace to the tab with the given TabId (null = no switch). The controller maps each
    ///     step's target region to its host tab so the region is live before the overlay measures it.
    /// </param>
    /// <param name="isDemoLoaded">
    ///     Predicate: is a demo currently open? Consulted at the first-run→demo-segment boundary so the tour
    ///     plays the demo steps immediately when one is already loaded (e.g. replay-from-Settings) and only
    ///     defers when none is.
    /// </param>
    /// <param name="markSeen">Invoked when the tour finishes or is skipped (for a "seen it" record; may no-op).</param>
    /// <param name="hasDemoAvailable">
    ///     Predicate: does the user's library hold at least one demo card the gateway can point at? True → the
    ///     gateway spotlights the first library card (double-click loads it, no dialog); false → it tries
    ///     <paramref name="hasSampleCta" />, then falls back to the Open-Demo button (file picker).
    /// </param>
    /// <param name="hasSampleCta">
    ///     Predicate: is the Library hero's "Try a sample match" CTA on screen — a bundled sample demo
    ///     resolved AND the hero (empty-library) state showing? The gateway's second preference: one click
    ///     opens the sample and the tour continues with real match data, no file dialog. Must be false when
    ///     the hero is hidden (folders configured) — the CTA control is invisible then, so spotlighting it
    ///     would frame nothing.
    /// </param>
    public TutorialController(
        IReadOnlyList<TutorialStep> steps, Action<string?> selectTab, Func<bool> isDemoLoaded, Action markSeen,
        Func<bool> hasDemoAvailable, Func<bool> hasSampleCta)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(selectTab);
        ArgumentNullException.ThrowIfNull(isDemoLoaded);
        ArgumentNullException.ThrowIfNull(markSeen);
        ArgumentNullException.ThrowIfNull(hasDemoAvailable);
        ArgumentNullException.ThrowIfNull(hasSampleCta);
        _steps = steps;
        _selectTab = selectTab;
        _isDemoLoaded = isDemoLoaded;
        _markSeen = markSeen;
        _hasDemoAvailable = hasDemoAvailable;
        _hasSampleCta = hasSampleCta;

        for (int i = 0; i < _steps.Count; i++)
        {
            (_steps[i].Segment == TutorialSegment.FirstRun ? _firstRun : _demoRun).Add(i);
        }

        ViewModel = new TutorialViewModel(Back, Next, Skip);
    }

    /// <summary>The overlay's bound view-model (the shell binds the tutorial overlay to this).</summary>
    public TutorialViewModel ViewModel { get; }

    /// <summary>True while a run is on screen (mirrors <see cref="TutorialViewModel.IsActive" />).</summary>
    public bool IsActive => ViewModel.IsActive;

    /// <summary>
    ///     Starts the tour from the top (the first-run run). Used by the post-setup trigger and the
    ///     "replay walkthrough" affordance. If the first-run run is empty, falls straight through to the
    ///     demo run (or completes).
    /// </summary>
    public void Start()
    {
        if (_firstRun.Count > 0)
        {
            BeginRun(_firstRun);
        }
        else if (_demoRun.Count > 0)
        {
            BeginRun(_demoRun);
        }
        else
        {
            Finish();
        }
    }

    /// <summary>
    ///     Signals that a demo just finished loading. If the tour is parked on the gateway step waiting for a
    ///     demo (the first run reached the open-a-demo step with nothing open), this resumes it with the
    ///     stats/playback steps. No-op otherwise, so it is safe to call on every demo load.
    /// </summary>
    public void NotifyDemoLoaded()
    {
        if (ViewModel is { IsActive: true, IsWaiting: true } && _demoRun.Count > 0)
        {
            BeginRun(_demoRun);
        }
    }

    private void BeginRun(IReadOnlyList<int> run)
    {
        _run = run;
        _pos = 0;
        Show();
    }

    private void Show()
    {
        int globalIndex = _run[_pos];
        TutorialStep step = _steps[globalIndex];

        // Gateway step reached with no demo open → park in the visible waiting state: keep the spotlight on the
        // real open affordance, disable manual advance, and show the hint. NotifyDemoLoaded auto-advances.
        bool waiting = step.WaitsForDemo && !_isDemoLoaded();

        // The gateway's preference ladder: the first library card (double-click loads it, no dialog) when the
        // library has one; else the hero's "Try a sample match" CTA (one click, bundled demo) when it's on
        // screen; else the Open-Demo button (picker). Every other step just uses its authored target.
        TutorialTarget active = step.Target;
        if (waiting)
        {
            if (_hasDemoAvailable())
            {
                active = TutorialTarget.FirstLibraryCard;
            }
            else if (_hasSampleCta())
            {
                active = TutorialTarget.SampleDemo;
            }
        }

        // Switch to the tab hosting this step's region so it is realized before the overlay measures it.
        _selectTab(TabIdForTarget(active));

        ViewModel.CurrentStep = step;
        ViewModel.ActiveTarget = active;
        ViewModel.StepNumber = globalIndex + 1;
        ViewModel.StepCount = _steps.Count;
        ViewModel.NextLabel = step.NextLabelOverride ?? "Next";
        ViewModel.CanGoBack = _pos > 0;
        ViewModel.IsWaiting = waiting;
        ViewModel.WaitingHint = waiting
            ? active switch
            {
                TutorialTarget.FirstLibraryCard => CardWaitingHint,
                TutorialTarget.SampleDemo => SampleWaitingHint,
                _ => ButtonWaitingHint
            }
            : string.Empty;
        ViewModel.CanGoNext = !waiting;
        ViewModel.SpotlightRect = default; // cleared; the view re-measures the new target on its next pass
        ViewModel.IsActive = true;
    }

    private void Next()
    {
        if (_pos < _run.Count - 1)
        {
            _pos++;
            Show();
            return;
        }

        // Last step of the current run. On the first run this is the gateway step — Next is only enabled here
        // when a demo is already open (a waiting gateway disables Next and advances via NotifyDemoLoaded), so
        // reaching this branch means we can flow straight into the demo segment.
        if (ReferenceEquals(_run, _firstRun) && _demoRun.Count > 0)
        {
            BeginRun(_demoRun);
            return;
        }

        Finish();
    }

    private void Back()
    {
        if (_pos > 0)
        {
            _pos--;
            Show();
        }
    }

    private void Skip() => Finish();

    private void Finish()
    {
        ViewModel.IsActive = false;
        ViewModel.IsWaiting = false;
        ViewModel.CurrentStep = null;
        _markSeen();
    }

    // Maps a step's target region to the tab that hosts it, so the tour switches there before the overlay
    // measures. Chrome targets (Open-Demo button, transport cluster) still pick a sensible backdrop tab.
    private static string? TabIdForTarget(TutorialTarget target) => target switch
    {
        TutorialTarget.TabNav => "builtin.library", // land on Library so the strip has a sensible backdrop
        TutorialTarget.LibraryTab => "builtin.library",
        TutorialTarget.FirstLibraryCard => "builtin.library",
        TutorialTarget.SampleDemo => "builtin.library",
        TutorialTarget.OpenDemo => "builtin.library",
        TutorialTarget.StatsContent => "builtin.stats",
        TutorialTarget.PlaybackTab => "playback2d.viewport",
        TutorialTarget.PlaybackTransport => "playback2d.viewport",
        _ => null // None (welcome / outro) — no switch; play over the current tab.
    };
}
