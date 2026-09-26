#region

using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using DemoViewer.NET.ViewModels.UtilityBook;

#endregion

namespace DemoViewer.NET.Views.UtilityBook;

/// <summary>
///     The Utility Book tab view. Bindings only, except the Lineup Card's Copy button: the clipboard
///     needs the visual tree (<c>TopLevel.GetTopLevel</c>), so it lives here rather than on the VM,
///     the Diagnostics tab's precedent. The card's console line is a read-only <c>TextBox</c> too, so
///     a clipboard write that a host rejects still leaves the line selectable by hand.
/// </summary>
public partial class UtilityBookTabView : UserControl
{
    /// <summary>Builds the view.</summary>
    public UtilityBookTabView() => InitializeComponent();

    private async void OnCopyConsoleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: GrenadeLineupRow row }
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(row.ConsoleText);
        }
        catch (Exception)
        {
            // Clipboard writes are permission/gesture-gated on some hosts; the line is already shown
            // selectable in the read-only TextBox as the manual fallback.
        }
    }
}
