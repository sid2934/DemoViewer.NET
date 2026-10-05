#region

using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Extensions.Sdk.Ui.Controls;

/// <summary>
///     A status chip: a dot and a neutral label bound to a <see cref="StatusChipViewModel" /> that opens a
///     <c>card-flyout</c> for detail and actions. The dot colour resolves from a palette token through a bound
///     state-to-class selector, so it re-themes live; the label is always the neutral <c>TextMid</c> token and
///     carries the state in words. Click or Enter opens the flyout.
/// </summary>
public partial class StatusChip : UserControl
{
    /// <summary>Initializes a new <see cref="StatusChip" /> instance.</summary>
    public StatusChip() => InitializeComponent();
}
