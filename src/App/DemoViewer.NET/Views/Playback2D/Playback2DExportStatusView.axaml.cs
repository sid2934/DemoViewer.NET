#region

using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using DemoViewer.NET.ViewModels.Playback2D;

#endregion

namespace DemoViewer.NET.Views.Playback2D;

/// <summary>
///     The 2D export chip's flyout body, resolved by the app <c>ViewLocator</c> for a
///     <see cref="Playback2DExportStatusViewModel" /> and hosted inside the
///     <see cref="Controls.StatusChip" />'s <c>card-flyout</c>.
///     <para>
///         The only code-behind is the "Copy error" handler: clipboard access needs the visual tree
///         (<c>TopLevel.Clipboard</c>), which a view-model command cannot reach. The error and the log are
///         both <c>SelectableTextBlock</c>s, so a rejected clipboard write still leaves a manual path.
///     </para>
/// </summary>
public partial class Playback2DExportStatusView : UserControl
{
    /// <summary>Initializes a new <see cref="Playback2DExportStatusView" /> instance.</summary>
    public Playback2DExportStatusView() => InitializeComponent();

    private async void OnCopyErrorClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not Playback2DExportStatusViewModel vm)
        {
            return;
        }

        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return; // no clipboard host (designer / degraded): the selectable text is the fallback
        }

        try
        {
            await clipboard.SetTextAsync(vm.CopyDiagnosticsText);
        }
        catch (Exception)
        {
            // Clipboard writes can be rejected (permission/gesture-gated hosts); surfacing a second
            // failure over the first would only obscure the diagnostics the user was trying to copy.
        }
    }
}
