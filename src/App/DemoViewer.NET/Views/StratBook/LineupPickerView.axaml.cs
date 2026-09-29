#region

using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Views.StratBook;

/// <summary>The lineup picker. Bindings only; the map host reads its DataContext itself.</summary>
public partial class LineupPickerView : UserControl
{
    /// <summary>Builds the view.</summary>
    public LineupPickerView() => InitializeComponent();
}
