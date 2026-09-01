#region

using Avalonia.Controls;
using Avalonia.Interactivity;
using DemoViewer.NET.ViewModels;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>
///     Reusable inspector card (F3.1): the single, adopted message-card surface.
///     Renders an accent strip, category badge, click-to-select header, column
///     header row, and a collapsible payload <c>TreeView</c> with per-row select.
///     <para>
///         Backed by <see cref="HarvestCardViewModel" /> + <c>HarvestPropertyViewModel</c>
///         via DataContext. Replaces the former <c>HarvestCardControl</c>; the VMs are
///         unchanged.
///     </para>
/// </summary>
public partial class InspectorCard : UserControl
{
    /// <summary>Initializes a new <see cref="InspectorCard" /> instance.</summary>
    public InspectorCard() => InitializeComponent();

    // Also fire SelectCommand when the header is clicked so the card becomes selected
    // (ToggleExpandCommand is already wired as the Button.Command in XAML).
    private void OnHeaderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HarvestCardViewModel vm)
        {
            vm.SelectCommand?.Execute(null);
        }
    }
}
