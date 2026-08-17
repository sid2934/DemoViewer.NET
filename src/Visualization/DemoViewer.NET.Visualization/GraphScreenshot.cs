#region

using Avalonia;
using Avalonia.Media.Imaging;
using DemoViewer.NET.Visualization.Internal;

#endregion

namespace DemoViewer.NET.Visualization;

/// <summary>
///     Headless PNG export for automated testing and CI pipelines.
/// </summary>
public static class GraphScreenshot
{
    /// <summary>
    ///     Renders the graph at the specified scale factor and saves to a PNG file.
    ///     Creates its own control instance — can be called from any thread after layout completes.
    /// </summary>
    public static void ExportToPng(GraphViewModel viewModel, string outputPath,
        int width = 2400, int height = 1600)
    {
        LayoutResult? layout = viewModel.CurrentLayout;
        if (layout is null)
        {
            return;
        }

        GraphView ctrl = new()
        {
            ViewModel = viewModel
        };
        Size size = new(width, height);
        ctrl.Measure(size);
        ctrl.Arrange(new Rect(size));

        RenderTargetBitmap bmp = new(new PixelSize(width, height), new Vector(96, 96));
        bmp.Render(ctrl);
        bmp.Save(outputPath);
    }
}
