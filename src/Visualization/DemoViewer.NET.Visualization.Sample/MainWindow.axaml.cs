#region

using Avalonia.Controls;
using Avalonia.Threading;
using DemoViewer.NET.Visualization.Sample.SampleGraphs;

#endregion

namespace DemoViewer.NET.Visualization.Sample;

/// <summary>Main window.</summary>
public partial class MainWindow : Window
{
    private readonly GraphViewModel _vm = new();

    /// <summary>Initializes the sample window and triggers the on-startup screenshot capture.</summary>
    public MainWindow()
    {
        InitializeComponent();
        GraphViewControl.ViewModel = _vm;
        Loaded += async (_, _) =>
        {
            // Capture all samples on startup for AI review
            await CaptureAllSamples();
            await LoadSample(0);
        };
    }

    // Captures every canonical fixture to /tmp/viz-{name}.png for visual review
    // (requires a real display — the render backend is unavailable headless).
    private async Task CaptureAllSamples()
    {
        foreach (BaselineRunner.Fixture f in BaselineRunner.BuildAll())
        {
            await _vm.SetGraphAsync(f.Nodes, f.Edges, f.Groups, f.Tables);
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    GraphScreenshot.ExportToPng(_vm, $"/tmp/viz-{f.Name}.png"));
            }
            catch
            {
                /* best-effort */
            }
        }
    }

    private async Task LoadSample(int index)
    {
        await LoadSampleData(index);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                GraphScreenshot.ExportToPng(_vm, "/tmp/visualization-sample.png"));
        }
        catch
        {
            /* best-effort */
        }
    }

    private async Task LoadSampleData(int index)
    {
        switch (index)
        {
            case 0:
            {
                (IReadOnlyList<IGraphNode> nodes, IReadOnlyList<IGraphEdge> edges, IReadOnlyList<INodeGroup> groups, INodeTable? table) = DemoStateGraphSample.Build();
                await _vm.SetGraphAsync(nodes, edges, groups, table is not null ? [table] : null);
                break;
            }
            case 1:
            {
                (IReadOnlyList<IGraphNode> nodes, IReadOnlyList<IGraphEdge> edges, IReadOnlyList<INodeGroup> groups) = StressTestGraphs.BuildFanOut();
                await _vm.SetGraphAsync(nodes, edges, groups);
                break;
            }
            case 2:
            {
                (IReadOnlyList<IGraphNode> nodes, IReadOnlyList<IGraphEdge> edges, IReadOnlyList<INodeGroup> groups) = StressTestGraphs.BuildConvergence();
                await _vm.SetGraphAsync(nodes, edges, groups);
                break;
            }
            case 3:
            {
                (IReadOnlyList<IGraphNode> nodes, IReadOnlyList<IGraphEdge> edges, IReadOnlyList<INodeGroup> groups) = StressTestGraphs.BuildLongChain();
                await _vm.SetGraphAsync(nodes, edges, groups);
                break;
            }
            case 4:
            {
                (IReadOnlyList<IGraphNode> nodes, IReadOnlyList<IGraphEdge> edges, IReadOnlyList<INodeGroup> groups) = StressTestGraphs.BuildDiamond();
                await _vm.SetGraphAsync(nodes, edges, groups);
                break;
            }
        }
    }

    private async void OnSampleChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SampleSelector.SelectedIndex >= 0)
        {
            await LoadSample(SampleSelector.SelectedIndex);
        }
    }
}
