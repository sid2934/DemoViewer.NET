#region

using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>
///     A status-strip chip: a dot + neutral label bound to a <see cref="ViewModels.StatusChipViewModel" />
///     that opens a <c>card-flyout</c> for detail and actions (the design notes in git history;
///     contract in docs/ui/design-system.md). The dot colour resolves from a DarkPalette token via a bound
///     state→class selector (never a code-held brush), so it re-themes live; the label is always the neutral
///     <c>TextMid</c> token and is the accessible carrier of state. Click or Enter opens the flyout.
/// </summary>
public partial class StatusChip : UserControl
{
    /// <summary>Initializes a new <see cref="StatusChip" /> instance.</summary>
    public StatusChip() => InitializeComponent();
}
