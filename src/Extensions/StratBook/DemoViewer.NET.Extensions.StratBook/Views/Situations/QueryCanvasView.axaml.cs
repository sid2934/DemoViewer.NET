#region

using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Views.Situations;

/// <summary>The Query Canvas view. Bindings only; the host control and the VM carry every behaviour.</summary>
public partial class QueryCanvasView : UserControl
{
    /// <summary>Builds the view.</summary>
    public QueryCanvasView() => InitializeComponent();
}
