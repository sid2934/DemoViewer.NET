#region

using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>
///     The dot's semantic state: maps 1:1 onto a DarkPalette token via a bound state→class selector in
///     <c>Controls/StatusChip.axaml</c> (the <c>Border.teamChip</c> pattern), NOT a code-held brush, so the
///     dot re-themes live. Off and Suspended share
///     <see cref="Off" /> (both render <c>TextDim</c>); the <em>word</em> in <see cref="StatusChipViewModel.Label" />
///     is the accessible carrier of state, the dot a redundant colour cue (WCAG 1.4.1).
/// </summary>
public enum StatusChipDotState
{
    /// <summary>Idle / suspended: <c>TextDim</c> solid dot.</summary>
    Off,

    /// <summary>Bringing a session up: <c>AccentInteractive</c>, pulsing.</summary>
    Working,

    /// <summary>Believed-good: <c>StatPositive</c>; the only state that pairs with a hollow ring (inferred).</summary>
    Good,

    /// <summary>Genuinely uncertain: <c>AccentCaution</c> solid.</summary>
    Degraded,

    /// <summary>Session lost / failed: <c>AccentError</c> solid.</summary>
    Error
}

/// <summary>
///     The reusable view-model behind a <c>Controls/StatusChip</c>: a persistent, stateful
///     background-activity indicator (a dot + neutral label) that opens a <c>card-flyout</c> for detail and
///     actions (docs/ui/design-system.md "StatusChip"). Two consumers justify the shared control: Live Sync
///     (F1, this WI) and the future Reel job (F3b).
///     <para>
///         Colour rule (theme mandate): this VM holds <b>no brushes</b>. It exposes the dot's semantic state
///         (<see cref="DotState" />) plus <see cref="IsPulsing" /> / <see cref="IsHollow" /> as
///         class-driving flags; the XAML resolves the token via a <c>{DynamicResource}</c> state→class Style,
///         so every colour tracks the active theme. The label is always the neutral <c>TextMid</c> token
///         (the only universally-AA-safe label across the four built-in themes).
///     </para>
/// </summary>
public sealed partial class StatusChipViewModel : ViewModelBase
{
    /// <summary>The dot's semantic state (drives the state→token class selector).</summary>
    [ObservableProperty]
    private StatusChipDotState _dotState = StatusChipDotState.Off;

    /// <summary>The control shown inside the chip's <c>card-flyout</c> (a bound view, not code-behind chrome).</summary>
    [ObservableProperty]
    private object? _flyoutContent;

    /// <summary>
    ///     True to render the dot as a hollow ring (stroke, transparent fill): the single "believed-good but
    ///     inferred, not engine-confirmed" treatment. Sync is outbound-only today so this is always false
    ///     today, but the rendering path is implemented against the contract's <c>IsInferred</c> flag.
    /// </summary>
    [ObservableProperty]
    private bool _isHollow;

    /// <summary>True while the dot runs the subtle opacity pulse (working / in-flight states).</summary>
    [ObservableProperty]
    private bool _isPulsing;

    /// <summary>The chip text: always the accessible carrier of state (e.g. "CS2 · Following").</summary>
    [ObservableProperty]
    private string _label = "";

    /// <summary>Optional primary action (unused by the flyout-driven Live Sync chip; part of the shared contract).</summary>
    [ObservableProperty]
    private ICommand? _primaryAction;

    /// <summary>Tooltip on the chip body (the label can truncate at the strip's scale).</summary>
    [ObservableProperty]
    private string? _tooltip;

    // ── Class-driving projections (bound to Classes.x in StatusChip.axaml; the Border.teamChip pattern) ──

    /// <summary>True when the dot is the idle/suspended <c>TextDim</c> state.</summary>
    public bool IsStateOff => DotState == StatusChipDotState.Off;

    /// <summary>True when the dot is the working <c>AccentInteractive</c> state.</summary>
    public bool IsStateWorking => DotState == StatusChipDotState.Working;

    /// <summary>True when the dot is the good <c>StatPositive</c> state.</summary>
    public bool IsStateGood => DotState == StatusChipDotState.Good;

    /// <summary>True when the dot is the degraded <c>AccentCaution</c> state.</summary>
    public bool IsStateDegraded => DotState == StatusChipDotState.Degraded;

    /// <summary>True when the dot is the error <c>AccentError</c> state.</summary>
    public bool IsStateError => DotState == StatusChipDotState.Error;

    partial void OnDotStateChanged(StatusChipDotState value)
    {
        OnPropertyChanged(nameof(IsStateOff));
        OnPropertyChanged(nameof(IsStateWorking));
        OnPropertyChanged(nameof(IsStateGood));
        OnPropertyChanged(nameof(IsStateDegraded));
        OnPropertyChanged(nameof(IsStateError));
    }
}
