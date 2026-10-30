#region

using Avalonia.Controls;
// SetTextAsync moved onto ClipboardExtensions in Avalonia 12; IClipboard no longer declares it.
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using DemoViewer.NET.ViewModels.MatchOverview;

#endregion

namespace DemoViewer.NET.Views.MatchOverview;

public partial class MatchOverviewTabView : UserControl
{
    public MatchOverviewTabView()
    {
        InitializeComponent();
        // The one/two-column breakpoint needs a measured width, which only the view has. It routes STRAIGHT
        // into a view-model bool that the body Grid's Column/Row/ColumnSpan bindings read, the layout itself
        // stays fully reactive, and the code-behind never touches a control (the same split the Highlights
        // master-detail collapse uses).
        SizeChanged += (_, e) =>
        {
            if (DataContext is MatchOverviewTabViewModel vm)
            {
                vm.SetViewportWidth(e.NewSize.Width);
            }
        };
    }

    // The clipboard needs the visual tree, so the copy lives here rather than on the view-model.
    private async void OnCopyFilePathClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MatchOverviewTabViewModel { FilePath.Length: > 0 } vm
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(vm.FilePath);
        }
        catch (Exception)
        {
            // Clipboard writes are permission- or gesture-gated on some hosts.
        }
    }
}
