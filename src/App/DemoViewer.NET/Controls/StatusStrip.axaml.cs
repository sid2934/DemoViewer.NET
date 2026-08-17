#region

using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>
///     Bottom-of-shell status bar with three regions: left status
///     text (optionally clickable via <see cref="StatusCommand" />), middle perf
///     ticker, right info text. Inputs are <see cref="StyledProperty{T}" /> so
///     smoke-test consumers can wire it without a shell view-model.
///     Spec: F3.4.
/// </summary>
public partial class StatusStrip : UserControl
{
    /// <summary>Perf text property.</summary>
    public static readonly StyledProperty<string> PerfTextProperty =
        AvaloniaProperty.Register<StatusStrip, string>(nameof(PerfText), "");

    /// <summary>Right text property.</summary>
    public static readonly StyledProperty<string> RightTextProperty =
        AvaloniaProperty.Register<StatusStrip, string>(nameof(RightText), "");

    /// <summary>Status brush property.</summary>
    public static readonly StyledProperty<IBrush?> StatusBrushProperty =
        AvaloniaProperty.Register<StatusStrip, IBrush?>(nameof(StatusBrush));

    /// <summary>Status command property.</summary>
    public static readonly StyledProperty<ICommand?> StatusCommandProperty =
        AvaloniaProperty.Register<StatusStrip, ICommand?>(nameof(StatusCommand));

    /// <summary>Status text property.</summary>
    public static readonly StyledProperty<string> StatusTextProperty =
        AvaloniaProperty.Register<StatusStrip, string>(nameof(StatusText),
            "Ready.");

    /// <summary>Hidden-features note property.</summary>
    public static readonly StyledProperty<string> HiddenNoteProperty =
        AvaloniaProperty.Register<StatusStrip, string>(nameof(HiddenNote), "");

    /// <summary>Hidden-note click command property (v0.6.0 — mirrors <see cref="StatusCommandProperty" />).</summary>
    public static readonly StyledProperty<ICommand?> HiddenNoteCommandProperty =
        AvaloniaProperty.Register<StatusStrip, ICommand?>(nameof(HiddenNoteCommand));

    /// <summary>Status-chip region source — a sequence of StatusChipViewModels.</summary>
    public static readonly StyledProperty<IEnumerable?> ChipsProperty =
        AvaloniaProperty.Register<StatusStrip, IEnumerable?>(nameof(Chips));

    /// <summary>Initializes a new <see cref="StatusStrip" /> instance.</summary>
    public StatusStrip() => InitializeComponent();

    /// <summary>Perf text.</summary>

    public string PerfText
    {
        get => GetValue(PerfTextProperty);
        set => SetValue(PerfTextProperty, value);
    }

    /// <summary>Right text.</summary>

    public string RightText
    {
        get => GetValue(RightTextProperty);
        set => SetValue(RightTextProperty, value);
    }

    /// <summary>Status brush.</summary>

    public IBrush? StatusBrush
    {
        get => GetValue(StatusBrushProperty);
        set => SetValue(StatusBrushProperty, value);
    }

    /// <summary>Status command.</summary>

    public ICommand? StatusCommand
    {
        get => GetValue(StatusCommandProperty);
        set => SetValue(StatusCommandProperty, value);
    }

    /// <summary>Status text.</summary>

    public string StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    /// <summary>
    ///     Informational "N features hidden" note. Empty string hides the affordance; a non-empty value
    ///     surfaces it at the right of the strip. Clicking invokes <see cref="HiddenNoteCommand" />
    ///     (v0.6.0 — the shell opens Settings at the user-category section; the old "no-op for now"
    ///     dead end is gone).
    /// </summary>
    public string HiddenNote
    {
        get => GetValue(HiddenNoteProperty);
        set => SetValue(HiddenNoteProperty, value);
    }

    /// <summary>Command invoked when the hidden-features note is clicked. Null → inert text.</summary>
    public ICommand? HiddenNoteCommand
    {
        get => GetValue(HiddenNoteCommandProperty);
        set => SetValue(HiddenNoteCommandProperty, value);
    }

    /// <summary>
    ///     The status-chip region source — a sequence of
    ///     <see cref="ViewModels.StatusChipViewModel" />, rendered right-aligned between the perf ticker and
    ///     <see cref="RightText" />. Empty / null → no chips, so the strip reads
    ///     exactly as it did before the chip region existed.
    /// </summary>
    public IEnumerable? Chips
    {
        get => GetValue(ChipsProperty);
        set => SetValue(ChipsProperty, value);
    }
}
