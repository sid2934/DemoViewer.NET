#region

using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using DemoViewer.NET.ViewModels.Highlights;

#endregion

namespace DemoViewer.NET.Views.Highlights;

/// <summary>
///     The Reel job flyout body, resolved by the app
///     <c>ViewLocator</c> for a <see cref="ViewModels.Highlights.ReelJobStatusViewModel" /> and hosted inside
///     the <see cref="Controls.StatusChip" />'s <c>card-flyout</c>. The only code-behind is the failed-state
///     "Copy error" handler: clipboard access needs the visual tree (<c>TopLevel.Clipboard</c>), which a VM
///     command cannot reach. The error itself is also a <c>SelectableTextBlock</c>, so keyboard-only users
///     retain a copy path if the platform clipboard write is rejected.
/// </summary>
public partial class ReelJobStatusView : UserControl
{
    /// <summary>Initializes a new <see cref="ReelJobStatusView" /> instance.</summary>
    public ReelJobStatusView() => InitializeComponent();

    private async void OnCopyErrorClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ReelJobStatusViewModel vm)
        {
            return;
        }

        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return; // no clipboard host (designer / degraded): the SelectableTextBlock is the fallback
        }

        try
        {
            await clipboard.SetTextAsync(vm.CopyDiagnosticsText);
        }
        catch (Exception)
        {
            // Clipboard writes can be rejected (permission/gesture-gated hosts); the selectable error
            // text is the manual fallback, so swallow rather than surface a second failure.
        }
    }
}
