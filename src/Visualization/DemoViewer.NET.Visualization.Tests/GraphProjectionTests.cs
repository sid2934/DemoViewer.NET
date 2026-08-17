#region

using DemoViewer.NET.Visualization.Sample.SampleGraphs;

#endregion

namespace DemoViewer.NET.Visualization.Tests;

/// <summary>
///     Unit tests for <see cref="GraphProjection.Induce" /> — the pure structure-layer projection
///     that produces an induced sub-graph from a node predicate. No layout, no state; just
///     references in, references out.
/// </summary>
public class GraphProjectionTests
{
    private static SampleNode Node(string name) => new(name);

    private static SampleEdge Edge(SampleNode a, SampleNode b) =>
        new(a, b, $"{a.Name}->{b.Name}", VisualEdgeEffect.Activate);

    /// <summary>Selected nodes survive_in original order; unselected dropped.</summary>
    [Test]
    public async Task Induce_SelectsNodes_PreservingOrder()
    {
        SampleNode a = Node("a"), b = Node("b"), c = Node("c");
        List<IGraphNode> nodes = [a, b, c];

        SubGraph sub = GraphProjection.Induce(nodes, [], null, n => n.Name != "b");

        await Assert.That(sub.Nodes.Count).IsEqualTo(2);
        await Assert.That(ReferenceEquals(sub.Nodes[0], a)).IsTrue();
        await Assert.That(ReferenceEquals(sub.Nodes[1], c)).IsTrue();
    }

    /// <summary>Edge survives_only when both endpoints selected (induced subgraph).</summary>
    [Test]
    public async Task Induce_KeepsEdge_OnlyWhenBothEndpointsSurvive()
    {
        SampleNode a = Node("a"), b = Node("b"), c = Node("c");
        SampleEdge ab = Edge(a, b); // both survive
        SampleEdge bc = Edge(b, c); // c dropped → boundary edge, must be removed
        SampleEdge ca = Edge(c, a); // c dropped → removed
        List<IGraphNode> nodes = [a, b, c];
        List<IGraphEdge> edges = [ab, bc, ca];

        // Keep a and b, drop c.
        SubGraph sub = GraphProjection.Induce(nodes, edges, null, n => n.Name is "a" or "b");

        await Assert.That(sub.Edges.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(sub.Edges[0], ab)).IsTrue();
    }

    /// <summary>Group restricted_to surviving members; empty group dropped.</summary>
    [Test]
    public async Task Induce_RestrictsGroups_AndDropsEmptyOnes()
    {
        SampleNode a = Node("a"), b = Node("b"), c = Node("c"), d = Node("d");
        List<IGraphNode> nodes = [a, b, c, d];

        SampleGroup mixed = new("mixed", [a, b]); // a survives, b dropped → restricted to {a}
        SampleGroup gone = new("gone", [b, d]); // both dropped → group removed entirely
        List<INodeGroup> groups = [mixed, gone];

        // Keep a and c only.
        SubGraph sub = GraphProjection.Induce(nodes, [], groups, n => n.Name is "a" or "c");

        await Assert.That(sub.Groups.Count).IsEqualTo(1);
        await Assert.That(sub.Groups[0].GroupName).IsEqualTo("mixed");
        await Assert.That(sub.Groups[0].Members.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(sub.Groups[0].Members[0], a)).IsTrue();
    }

    /// <summary>Empty predicate_yields empty sub-graph (everything dropped).</summary>
    [Test]
    public async Task Induce_EmptySelection_YieldsEmptySubGraph()
    {
        SampleNode a = Node("a"), b = Node("b");
        SampleEdge ab = Edge(a, b);

        SubGraph sub = GraphProjection.Induce([a, b], [ab], [new SampleGroup("g", [a, b])], _ => false);

        await Assert.That(sub.Nodes.Count).IsEqualTo(0);
        await Assert.That(sub.Edges.Count).IsEqualTo(0);
        await Assert.That(sub.Groups.Count).IsEqualTo(0);
    }

    /// <summary>Full selection_returns everything unchanged (identity projection).</summary>
    [Test]
    public async Task Induce_FullSelection_ReturnsEverything()
    {
        SampleNode a = Node("a"), b = Node("b"), c = Node("c");
        SampleEdge ab = Edge(a, b), bc = Edge(b, c);

        SubGraph sub = GraphProjection.Induce([a, b, c], [ab, bc], null, _ => true);

        await Assert.That(sub.Nodes.Count).IsEqualTo(3);
        await Assert.That(sub.Edges.Count).IsEqualTo(2);
    }
}
