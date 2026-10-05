#region

using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.Extensions.Sdk.Ui.Controls;

/// <summary>
///     The dot's semantic state. Each maps onto one palette token through a bound state-to-class selector in
///     <see cref="StatusChip" />, never a code-held brush, so the dot re-themes live. The word in
///     <see cref="StatusChipViewModel.Label" /> is the accessible carrier of state; the dot is a redundant
///     colour cue.
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
///     The view model behind a <see cref="StatusChip" />: a persistent background-activity indicator, a dot and
///     a neutral label, that opens a <c>card-flyout</c> for detail and actions.
///     <para>
///         It holds no brushes. It exposes the dot's semantic state (<see cref="DotState" />) and the
///         <see cref="IsPulsing" /> and <see cref="IsHollow" /> flags; the control resolves the colour from a
///         palette token, so every colour tracks the active theme. The label is always the neutral
///         <c>TextMid</c> token.
///     </para>
/// </summary>
public sealed partial class StatusChipViewModel : ObservableObject
{
    /// <summary>The dot's semantic state (drives the state→token class selector).</summary>
    [ObservableProperty]
    private StatusChipDotState _dotState = StatusChipDotState.Off;

    /// <summary>The control shown inside the chip's <c>card-flyout</c> (a bound view, not code-behind chrome).</summary>
    [ObservableProperty]
    private object? _flyoutContent;

    /// <summary>
    ///     True to render the dot as a hollow ring (stroke, transparent fill): the one "believed good but
    ///     inferred, not confirmed" treatment, meant to pair with <see cref="StatusChipDotState.Good" />.
    /// </summary>
    [ObservableProperty]
    private bool _isHollow;

    /// <summary>True while the dot runs the subtle opacity pulse (working / in-flight states).</summary>
    [ObservableProperty]
    private bool _isPulsing;

    /// <summary>The chip text: always the accessible carrier of state (e.g. "CS2 · Following").</summary>
    [ObservableProperty]
    private string _label = "";

    /// <summary>Optional primary action for the chip's owner to run.</summary>
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
