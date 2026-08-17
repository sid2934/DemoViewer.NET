#region

using System.Globalization;
using System.Text;
using DemoViewer.NET.Visualization.Sample.SampleGraphs;

#endregion

namespace DemoViewer.NET.Visualization.Sample;

/// <summary>
///     Headless driver that runs the <see cref="LayoutMetrics" /> analyzer over the
///     full canonical fixture set through the layout pipeline and
///     prints a markdown metrics table. No GUI / UI thread required. Invoke via
///     <c>dotnet run -- --baseline</c>.
/// </summary>
public static class BaselineRunner
{
    /// <summary>Prints the layout-metrics table for the full fixture set.</summary>
    public static string Run() => RunTable(new GraphStyle());

    internal static IReadOnlyList<Fixture> BuildAll()
    {
        (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges, IReadOnlyList<INodeGroup> Groups, INodeTable Table) demo = DemoStateGraphSample.Build();
        (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges, IReadOnlyList<INodeGroup> Groups) fanOut = StressTestGraphs.BuildFanOut();
        (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges, IReadOnlyList<INodeGroup> Groups) fanIn = StressTestGraphs.BuildFanIn();
        (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges, IReadOnlyList<INodeGroup> Groups) conv = StressTestGraphs.BuildConvergence();
        (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges, IReadOnlyList<INodeGroup> Groups) longChain = StressTestGraphs.BuildLongChain();
        (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges, IReadOnlyList<INodeGroup> Groups) skipChain = StressTestGraphs.BuildSkipChainNoSubtitle();
        (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges, IReadOnlyList<INodeGroup> Groups) diamond = StressTestGraphs.BuildDiamond();
        (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges, IReadOnlyList<INodeGroup> Groups) dense = StressTestGraphs.BuildDenseCluster();
        (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges, IReadOnlyList<INodeGroup> Groups) selfLoop = StressTestGraphs.BuildSelfLoopHeavy();
        (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges, IReadOnlyList<INodeGroup> Groups) disconnected = StressTestGraphs.BuildDisconnectedComponents();
        (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges, IReadOnlyList<INodeGroup> Groups, IReadOnlyList<INodeTable> Tables) multiTable = StressTestGraphs.BuildMultiTableStack();
        (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges, IReadOnlyList<INodeGroup> Groups) hub = StressTestGraphs.BuildHighDegreeHub();
        (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges, IReadOnlyList<INodeGroup> Groups, IReadOnlyList<INodeTable> Tables) big = StressTestGraphs.BuildBigStandard();

        return
        [
            new Fixture("DemoState", demo.Nodes, demo.Edges, demo.Groups, [demo.Table]),
            new Fixture("FanOut", fanOut.Nodes, fanOut.Edges, fanOut.Groups, null),
            new Fixture("FanIn", fanIn.Nodes, fanIn.Edges, fanIn.Groups, null),
            new Fixture("Convergence", conv.Nodes, conv.Edges, conv.Groups, null),
            new Fixture("LongChain", longChain.Nodes, longChain.Edges, longChain.Groups, null),
            new Fixture("SkipChainNoSubtitle", skipChain.Nodes, skipChain.Edges, skipChain.Groups, null),
            new Fixture("Diamond", diamond.Nodes, diamond.Edges, diamond.Groups, null),
            new Fixture("DenseCluster", dense.Nodes, dense.Edges, dense.Groups, null),
            new Fixture("SelfLoopHeavy", selfLoop.Nodes, selfLoop.Edges, selfLoop.Groups, null),
            new Fixture("DisconnectedComponents", disconnected.Nodes, disconnected.Edges, disconnected.Groups, null),
            new Fixture("MultiTableStack", multiTable.Nodes, multiTable.Edges, multiTable.Groups, multiTable.Tables),
            new Fixture("HighDegreeHub", hub.Nodes, hub.Edges, hub.Groups, null),
            new Fixture("BigStandard", big.Nodes, big.Edges, big.Groups, big.Tables)
        ];
    }

    private static string RunTable(GraphStyle style)
    {
        StringBuilder sb = new();
        CultureInfo ci = CultureInfo.InvariantCulture;

        sb.AppendLine("| Fixture | NodeOverlap | EdgeNodeX | EdgeCross | EdgeLen | SharedPorts | LabelOverlap | OOB | SelfLoopOX | Aspect | LayoutMs |");
        sb.AppendLine("|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|");

        foreach (Fixture f in BuildAll())
        {
            // Run layout twice; report the faster (warm) layout time to discount JIT.
            LayoutMetrics m = LayoutMetrics.Compute(f.Nodes, f.Edges, f.Groups, f.Tables, style);
            LayoutMetrics m2 = LayoutMetrics.Compute(f.Nodes, f.Edges, f.Groups, f.Tables, style);
            double ms = Math.Min(m.LayoutMilliseconds, m2.LayoutMilliseconds);

            sb.AppendLine(string.Format(ci,
                "| {0} | {1} | {2} | {3} | {4:F0} | {5} | {6} | {7} | {8} | {9:F2} | {10:F1} |",
                f.Name, m.NodeNodeOverlaps, m.EdgeNodeIntersections, m.EdgeCrossings,
                m.TotalEdgeLength, m.SharedPortEndpoints, m.LabelOverlaps,
                m.OutOfBoundsPrimitives, m.SelfLoopOverlaps, m.AspectRatio, ms));
        }

        return sb.ToString();
    }

    internal sealed record Fixture(
        string Name,
        IReadOnlyList<IGraphNode> Nodes,
        IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup>? Groups,
        IReadOnlyList<INodeTable>? Tables);
}
