#region

using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

#endregion

namespace DemoViewer.NET.ViewModels.Tutorial;

/// <summary>
///     The binding contract for the first-run Visual Walkthrough overlay
///     (<see cref="Views.Tutorial.TutorialView" />). It is deliberately <b>display-only</b>: it holds the
///     current step + presentation state the overlay renders, and delegates Back / Next / Skip to
///     <see cref="System.Action" />s supplied by the follow-up tour-engine phase. The engine owns all
///     advancement, tab-switching, anchor measurement, persistence and the wizard trigger — it drives this
///     VM by setting <see cref="CurrentStep" />, <see cref="SpotlightRect" /> and the step counters, and by
///     implementing the three delegated actions. The VM itself contains no navigation logic (matching the
///     <see cref="Idle.IdleViewModel" /> delegated-action pattern).
///     <para>
///         Every member below is bound by the overlay — see each summary for its meaning. The overlay never
///         reads anything else, so this is the complete contract the engine builds against.
///     </para>
/// </summary>
public sealed partial class TutorialViewModel : ViewModelBase
{
    private readonly Action _back;
    private readonly Action _next;
    private readonly Action _skip;

    /// <summary>
    ///     The region the overlay should actually spotlight right now (engine-set). Usually equals the current
    ///     step's <see cref="TutorialStep.Target" />, but the gateway overrides it: when the library has a demo
    ///     to click it points at the first library card (double-click loads it, no dialog), otherwise at the
    ///     Open-Demo button. The view measures THIS, not the step's static target.
    /// </summary>
    [ObservableProperty]
    private TutorialTarget _activeTarget;

    /// <summary>Whether a previous step exists — gates the callout's Back button (engine-set).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    private bool _canGoBack;

    /// <summary>Whether advancing is allowed — gates the callout's Next/Finish button (engine-set).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _canGoNext = true;

    /// <summary>
    ///     The step currently shown. The overlay binds <c>CurrentStep.Title</c> / <c>CurrentStep.Body</c> for
    ///     the callout copy; <see cref="HasSpotlight" /> / <see cref="Placement" /> proxy its layout fields.
    ///     The engine assigns this as it advances (it does not have to be the "next" item in any list — the
    ///     two segments fire at different times).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpotlight))]
    [NotifyPropertyChangedFor(nameof(Placement))]
    private TutorialStep? _currentStep;

    /// <summary>
    ///     Whether the tour is running. The overlay root binds its <c>IsVisible</c> to this, so setting it
    ///     false tears the tour down visually. The engine flips it on to start and off on Skip / finish.
    /// </summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>
    ///     True while the tour is parked on a <see cref="TutorialStep.WaitsForDemo" /> gateway step with no demo
    ///     open: the spotlight stays on the Open-Demo affordance and the overlay swaps its advance button for a
    ///     waiting hint (<see cref="WaitingHint" />), because the only way forward is to open a demo — which the
    ///     engine detects and auto-advances on. The overlay binds this to show the hint / hide the Next button.
    /// </summary>
    [ObservableProperty]
    private bool _isWaiting;

    /// <summary>
    ///     Label for the advance button ("Next", or "Get started" / "Finish" on the welcome / outro). The
    ///     engine typically sets this from <c>CurrentStep.NextLabelOverride</c>.
    /// </summary>
    [ObservableProperty]
    private string _nextLabel = "Next";

    /// <summary>
    ///     The spotlight target rectangle <b>in overlay (window) coordinates</b>. The engine measures the
    ///     anchored control (from <c>CurrentStep.Target</c>) and sets this live; the overlay draws the
    ///     cut-out hole here and positions the callout beside it. Ignored while <see cref="HasSpotlight" />
    ///     is false. Default <see cref="Avalonia.Rect" />.Empty is treated as "no hole yet".
    /// </summary>
    [ObservableProperty]
    private Rect _spotlightRect;

    /// <summary>Total steps in the indicator (engine-set) — the denominator of the step indicator.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepIndicator))]
    private int _stepCount = 1;

    /// <summary>1-based index of the current step (engine-set) — the numerator of the step indicator.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepIndicator))]
    private int _stepNumber = 1;

    /// <summary>
    ///     The hint shown in place of the advance button while <see cref="IsWaiting" /> is true (engine-set from
    ///     <c>CurrentStep.WaitingHint</c>). Empty on every non-waiting step.
    /// </summary>
    [ObservableProperty]
    private string _waitingHint = string.Empty;

    /// <summary>
    ///     Engine-facing constructor. The three actions are invoked by the callout's Back / Next / Skip
    ///     commands respectively; the engine implements step advancement, tab navigation and teardown behind
    ///     them. None run any logic in this class.
    /// </summary>
    public TutorialViewModel(Action back, Action next, Action skip)
    {
        ArgumentNullException.ThrowIfNull(back);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(skip);
        _back = back;
        _next = next;
        _skip = skip;
    }

    /// <summary>
    ///     Design-time / preview constructor: no-op actions + the first authored step, so the XAML previewer
    ///     and headless captures render a representative welcome card. Not used at runtime (the engine uses
    ///     the delegated constructor).
    /// </summary>
    public TutorialViewModel()
        : this(static () => { }, static () => { }, static () => { })
    {
        IsActive = true;
        StepCount = TutorialSteps.Default.Count;
        StepNumber = 1;
        CurrentStep = TutorialSteps.Default[0];
        NextLabel = CurrentStep.NextLabelOverride ?? "Next";
        CanGoBack = false;
        CanGoNext = true;
    }

    /// <summary>Human step indicator ("2 of 7"), derived from <see cref="StepNumber" /> / <see cref="StepCount" />.</summary>
    public string StepIndicator => $"{StepNumber} of {StepCount}";

    /// <summary>Convenience proxy for <c>CurrentStep.HasSpotlight</c> (false when no step) — drives the scrim hole.</summary>
    public bool HasSpotlight => CurrentStep?.HasSpotlight ?? false;

    /// <summary>Convenience proxy for <c>CurrentStep.Placement</c> — the callout placement hint.</summary>
    public CalloutPlacement Placement => CurrentStep?.Placement ?? CalloutPlacement.Center;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => _back();

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => _next();

    [RelayCommand]
    private void Skip() => _skip();
}
