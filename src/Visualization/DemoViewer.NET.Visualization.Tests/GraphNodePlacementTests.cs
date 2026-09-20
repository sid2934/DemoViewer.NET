#region

using DemoViewer.NET.Visualization.Sample.SampleGraphs;

#endregion

namespace DemoViewer.NET.Visualization.Tests;

/// <summary>
///     <see cref="GraphViewModel.NodePlacements" />, the one part of the layout result that is
///     public. It exists so a second renderer can put its nodes where MSAGL decided they go
///     (docs/rule-graph/design.md §6.4), and it is the whole of what that costs: routes, label
///     rectangles, group bounds and table placements stay internal.
/// </summary>
public class GraphNodePlacementTests
{
    [Test]
    public async Task BeforeLayout_ThereAreNoPlacements()
    {
        GraphViewModel graph = new();

        await Assert.That(graph.NodePlacements).IsEmpty();
        await Assert.That(graph.IsLayoutComplete).IsFalse();
    }

    /// <summary>Every node the layout placed is reported, and nothing else is.</summary>
    [Test]
    public async Task AfterLayout_EveryNodeHasAPlacement()
    {
        (IReadOnlyList<IGraphNode> nodes, IReadOnlyList<IGraphEdge> edges, _) =
            StressTestGraphs.BuildDiamond();
        GraphViewModel graph = new();

        await graph.SetGraphAsync(nodes, edges);

        await Assert.That(graph.IsLayoutComplete).IsTrue();
        await Assert.That(graph.NodePlacements.Count).IsEqualTo(nodes.Count);
        foreach (IGraphNode node in nodes)
        {
            await Assert.That(graph.NodePlacements.ContainsKey(node)).IsTrue()
                .Because($"{node.Name} was laid out and should have a placement");
        }
    }

    /// <summary>
    ///     A placement is the node's CENTRE, and it agrees with what the renderer draws. Asserted by
    ///     reconstructing the node rect from it and checking the nodes do not overlap, which is the
    ///     same property the layout gate holds: if these were top-left coordinates every box would
    ///     be half a node out and the corners would touch.
    /// </summary>
    [Test]
    public async Task APlacementIsTheNodeCentre()
    {
        (IReadOnlyList<IGraphNode> nodes, IReadOnlyList<IGraphEdge> edges, _) =
            StressTestGraphs.BuildDiamond();
        GraphViewModel graph = new();
        await graph.SetGraphAsync(nodes, edges);

        NodeStyleConfig style = graph.Style.Node;
        List<(double L, double T, double R, double B)> boxes = [];
        foreach (IGraphNode node in nodes)
        {
            GraphNodePlacement at = graph.NodePlacements[node];
            double w = node.Style?.Width ?? style.Width;
            double h = node.Style?.Height ?? style.Height;
            boxes.Add((at.X - w / 2, at.Y - h / 2, at.X + w / 2, at.Y + h / 2));
        }

        for (int i = 0; i < boxes.Count; i++)
        {
            for (int j = i + 1; j < boxes.Count; j++)
            {
                bool overlap = boxes[i].L < boxes[j].R && boxes[i].R > boxes[j].L
                                                       && boxes[i].T < boxes[j].B
                                                       && boxes[i].B > boxes[j].T;
                await Assert.That(overlap).IsFalse()
                    .Because("boxes reconstructed from placements should not overlap, as the gate asserts");
            }
        }
    }

    /// <summary>A second layout replaces the placements rather than merging into them.</summary>
    [Test]
    public async Task RelayingOut_ReplacesThePlacements()
    {
        (IReadOnlyList<IGraphNode> big, IReadOnlyList<IGraphEdge> bigEdges, _) =
            StressTestGraphs.BuildFanOut();
        (IReadOnlyList<IGraphNode> small, IReadOnlyList<IGraphEdge> smallEdges, _) =
            StressTestGraphs.BuildDiamond();

        GraphViewModel graph = new();
        await graph.SetGraphAsync(big, bigEdges);
        await graph.SetGraphAsync(small, smallEdges);

        await Assert.That(graph.NodePlacements.Count).IsEqualTo(small.Count);
        foreach (IGraphNode node in big)
        {
            await Assert.That(graph.NodePlacements.ContainsKey(node)).IsFalse()
                .Because("a node from the previous graph should not still be placed");
        }
    }
}
