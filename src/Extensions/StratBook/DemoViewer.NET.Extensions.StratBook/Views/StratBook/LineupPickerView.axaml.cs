#region

using Avalonia.Controls;
using Avalonia.Threading;
using DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Views.StratBook;

/// <summary>
///     The lineup picker. Bindings only, except focus: a picker that opens takes the focus, so Escape cancels at
///     once and nothing behind it keeps the keyboard.
/// </summary>
public partial class LineupPickerView : UserControl
{
    /// <summary>Builds the view.</summary>
    public LineupPickerView() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is LineupPickerViewModel)
        {
            // Posted: the overlay turns visible in the same pass, and a hidden control cannot take focus.
            Dispatcher.UIThread.Post(() => PickerKind.Focus());
        }
    }
}
