#region

using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using DemoViewer.NET.ViewModels.Dossier;

#endregion

namespace DemoViewer.NET.Views.Dossier;

/// <summary>
///     The Opponent Dossier tab view. Bindings only, apart from "Copy markdown": the clipboard needs the
///     visual tree (<c>TopLevel.Clipboard</c>), so the handler lives here and the text comes from the VM.
/// </summary>
public partial class DossierTabView : UserControl
{
    /// <summary>Builds the view.</summary>
    public DossierTabView() => InitializeComponent();

    private async void OnCopyMarkdownClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DossierTabViewModel { Editor: { HasTeam: true } editor })
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            editor.ReportCopyFailed();
            return;
        }

        try
        {
            await clipboard.SetTextAsync(editor.MarkdownText());
            editor.ReportCopied();
        }
        catch (Exception)
        {
            // Clipboard writes are permission or gesture gated on some hosts (the browser among them).
            editor.ReportCopyFailed();
        }
    }
}
