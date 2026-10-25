#region

using DemoViewer.NET.Visualization.Tests.SampleGraphs;

#endregion

namespace DemoViewer.NET.Visualization.Tests;

/// <summary>
///     The canonical fixture set <see cref="LayoutMetricsHardGateTests" /> drives the
///     <see cref="LayoutMetrics" /> analyzer over. No GUI and no UI thread: the roster is built here
///     so one list feeds every gate case.
/// </summary>
public static class BaselineRunner
{
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
        (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges, IReadOnlyList<INodeGroup> Groups) shipped = ShippedScaleGraph.Build();

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
            new Fixture("BigStandard", big.Nodes, big.Edges, big.Groups, big.Tables),
            new Fixture("ShippedScale", shipped.Nodes, shipped.Edges, shipped.Groups, null)
        ];
    }

    internal sealed record Fixture(
        string Name,
        IReadOnlyList<IGraphNode> Nodes,
        IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup>? Groups,
        IReadOnlyList<INodeTable>? Tables);
}
