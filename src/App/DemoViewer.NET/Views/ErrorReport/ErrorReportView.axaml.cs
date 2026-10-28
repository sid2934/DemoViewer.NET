#region

using Avalonia.Controls;
// SetTextAsync moved onto ClipboardExtensions in Avalonia 12; IClipboard no longer declares it.
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using DemoViewer.NET.ViewModels.ErrorReport;

#endregion

namespace DemoViewer.NET.Views.ErrorReport;

/// <summary>The Report Analysis Errors window body. Hosted by ErrorReportWindow, as SettingsView is.</summary>
public partial class ErrorReportView : UserControl
{
    /// <summary>Initializes a new <see cref="ErrorReportView" /> instance.</summary>
    public ErrorReportView()
    {
        InitializeComponent();
    }

    // The clipboard needs the visual tree, so the copy lives here rather than on the view-model.
    private async void OnCopyDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ErrorReportViewModel { CopyDiagnosticsText.Length: > 0 } vm
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(vm.CopyDiagnosticsText);
        }
        catch (Exception)
        {
            // Clipboard writes are permission- or gesture-gated on some hosts.
        }
    }
}
