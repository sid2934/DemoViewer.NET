#region

using Avalonia.Controls;
using DemoViewer.NET.ViewModels.MatchOverview;

#endregion

namespace DemoViewer.NET.Views.MatchOverview;

public partial class MatchOverviewTabView : UserControl
{
    public MatchOverviewTabView()
    {
        InitializeComponent();
        // The one/two-column breakpoint needs a measured width, which only the view has. It routes STRAIGHT
        // into a view-model bool that the body Grid's Column/Row/ColumnSpan bindings read — the layout itself
        // stays fully reactive, and the code-behind never touches a control (the same split the Highlights
        // master-detail collapse uses).
        SizeChanged += (_, e) =>
        {
            if (DataContext is MatchOverviewTabViewModel vm)
            {
                vm.SetViewportWidth(e.NewSize.Width);
            }
        };
    }
}
