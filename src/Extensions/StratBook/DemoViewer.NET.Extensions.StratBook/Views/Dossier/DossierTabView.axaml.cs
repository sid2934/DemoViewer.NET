#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions.StratBook.ViewModels.Dossier;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Views.Dossier;

/// <summary>
///     The Opponent Dossier tab view. Bindings only, apart from "Copy markdown": the clipboard needs the
///     visual tree (<c>TopLevel.Clipboard</c>), so the handler lives here and the text comes from the VM.
/// </summary>
public partial class DossierTabView : UserControl
{
    // The finding being edited when its editor had focus. A row that scrolls out is recycled, which takes
    // the focus with it; the editor that shows the row again takes it back.
    private DossierFindingViewModel? _editing;

    /// <summary>Builds the view.</summary>
    public DossierTabView() => InitializeComponent();

    private void OnFindingEditGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: DossierFindingViewModel row })
        {
            _editing = row;
        }
    }

    // Only a focus the user moved elsewhere ends the claim; a recycled editor has a new row or none.
    private void OnFindingEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || _editing is not { } row)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(box.DataContext, row) && box.IsEffectivelyVisible && TopLevel.GetTopLevel(box) is not null
                || !row.IsEditing)
            {
                _editing = null;
            }
        }, DispatcherPriority.Background);
    }

    private void OnFindingEditRebound(object? sender, EventArgs e) => Reclaim(sender as TextBox);

    private void OnFindingEditAttached(object? sender, VisualTreeAttachmentEventArgs e) => Reclaim(sender as TextBox);

    private void Reclaim(TextBox? box)
    {
        if (box is { DataContext: DossierFindingViewModel { IsEditing: true } row } && ReferenceEquals(row, _editing) && !box.IsFocused)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(box.DataContext, row) && row.IsEditing)
                {
                    box.Focus();
                }
            }, DispatcherPriority.Loaded);
        }
    }

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
