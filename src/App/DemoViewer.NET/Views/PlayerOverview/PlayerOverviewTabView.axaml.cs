#region

using Avalonia.Controls;
using DemoViewer.NET.ViewModels.PlayerOverview;

#endregion

namespace DemoViewer.NET.Views.PlayerOverview;

public partial class PlayerOverviewTabView : UserControl
{
    public PlayerOverviewTabView()
    {
        InitializeComponent();
        // The one/two-column breakpoint needs a measured width, which only the view has; it goes straight into
        // the view model ints the section grid binds (the Match Overview split).
        SizeChanged += (_, e) =>
        {
            if (DataContext is PlayerOverviewTabViewModel vm)
            {
                vm.SetViewportWidth(e.NewSize.Width);
            }
        };
    }
}
