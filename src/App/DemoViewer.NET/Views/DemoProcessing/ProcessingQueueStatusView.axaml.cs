#region

using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Views.DemoProcessing;

/// <summary>
///     The demo-processing-queue flyout body, resolved by the app
///     <c>ViewLocator</c> for a <see cref="ViewModels.DemoProcessing.ProcessingQueueStatusViewModel" /> and
///     hosted inside the <see cref="Controls.StatusChip" />'s <c>card-flyout</c>. Purely declarative: no
///     code-behind.
/// </summary>
public partial class ProcessingQueueStatusView : UserControl
{
    /// <summary>Initializes a new <see cref="ProcessingQueueStatusView" /> instance.</summary>
    public ProcessingQueueStatusView() => InitializeComponent();
}
