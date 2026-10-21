#region

using Avalonia.Controls;
using DemoViewer.NET.ViewModels.PlayerOverview;

#endregion

namespace DemoViewer.NET.Views.PlayerOverview;

public partial class PlayerOverviewPageView : UserControl
{
    public PlayerOverviewPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PlayerOverviewPage page)
            {
                Columns.ColumnDefinitions[0].Width = new GridLength(page.PrimaryWeight, GridUnitType.Star);
                Columns.ColumnDefinitions[2].Width = new GridLength(page.RailWeight, GridUnitType.Star);
            }
        };
        PageScroll.SizeChanged += (_, e) => (DataContext as PlayerOverviewPage)?.SetHeight(e.NewSize.Height);
    }
}
