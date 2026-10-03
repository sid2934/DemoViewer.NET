#region

using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Views.Situations;

/// <summary>The watched situations list. Bindings only; every behaviour lives on the VM.</summary>
public partial class WatchedSituationsView : UserControl
{
    /// <summary>Builds the view.</summary>
    public WatchedSituationsView() => InitializeComponent();
}
