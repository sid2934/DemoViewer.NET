#region

using Avalonia.Controls;
// SetTextAsync moved onto ClipboardExtensions in Avalonia 12; IClipboard no longer declares it.
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using DemoViewer.NET.ViewModels.PlayerOverview.Sections;

#endregion

namespace DemoViewer.NET.Views.PlayerOverview.Sections;

public partial class HeaderSectionView : UserControl
{
    public HeaderSectionView()
    {
        InitializeComponent();
    }

    // The clipboard needs the visual tree, so the copy lives here; the id stays selectable as the fallback.
    private async void OnCopySteamIdClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HeaderSectionViewModel { SteamId64.Length: > 0 } vm
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(vm.SteamId64);
        }
        catch (Exception)
        {
            // Clipboard writes are permission- or gesture-gated on some hosts.
        }
    }
}
