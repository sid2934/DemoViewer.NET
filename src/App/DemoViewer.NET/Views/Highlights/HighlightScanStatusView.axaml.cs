#region

using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Views.Highlights;

/// <summary>
///     Flyout body for the library-wide highlight-scan chip — the fourth <c>StatusChip</c> consumer
///     (row 2). Resolved by the app <c>ViewLocator</c> from
///     <see cref="DemoViewer.NET.ViewModels.Highlights.HighlightScanStatusViewModel" />; no code-behind.
/// </summary>
public partial class HighlightScanStatusView : UserControl
{
    /// <summary>Builds the view.</summary>
    public HighlightScanStatusView() => InitializeComponent();
}
