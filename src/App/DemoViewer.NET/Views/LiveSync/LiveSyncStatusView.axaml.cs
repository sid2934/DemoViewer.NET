#region

using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Views.LiveSync;

/// <summary>
///     The Live Sync flyout body, resolved by the app
///     <c>ViewLocator</c> for a <see cref="ViewModels.LiveSync.LiveSyncStatusViewModel" /> and hosted inside
///     the <see cref="Controls.StatusChip" />'s <c>card-flyout</c>. Purely declarative: no code-behind.
/// </summary>
public partial class LiveSyncStatusView : UserControl
{
    /// <summary>Initializes a new <see cref="LiveSyncStatusView" /> instance.</summary>
    public LiveSyncStatusView() => InitializeComponent();
}
