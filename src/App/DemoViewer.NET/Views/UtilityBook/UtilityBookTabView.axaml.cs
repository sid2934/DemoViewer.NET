#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using DemoViewer.NET.ViewModels.UtilityBook;

#endregion

namespace DemoViewer.NET.Views.UtilityBook;

/// <summary>
///     The Utility Book view. Bindings only, except the clipboard: it needs the visual tree
///     (<c>TopLevel.GetTopLevel</c>), so the view hands the view model a writer while it is attached.
/// </summary>
public partial class UtilityBookTabView : UserControl
{
    /// <summary>Builds the view.</summary>
    public UtilityBookTabView() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Wire();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Wire();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (DataContext is UtilityBookTabViewModel vm)
        {
            vm.Clipboard = null;
        }
    }

    private void Wire()
    {
        if (DataContext is UtilityBookTabViewModel vm)
        {
            vm.Clipboard = async text =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(text);
                }
            };
        }
    }
}
