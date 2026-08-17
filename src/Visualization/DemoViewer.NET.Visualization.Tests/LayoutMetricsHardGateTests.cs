#region

using DemoViewer.NET.Visualization.Sample;

#endregion

namespace DemoViewer.NET.Visualization.Tests;

/// <summary>
///     Drives the same canonical fixture set the headless <c>--baseline</c> CLI
///     emits, but asserted as CI gates. Geometry columns are deterministic so any
///     regression in a layout pass surfaces here immediately. See
///     <see cref="BaselineRunner.BuildAll" /> for the fixture roster and
///     <c>visualization-graph-review.md</c> (historical doc) for the gate rationale.
/// </summary>
public class LayoutMetricsHardGateTests
{
    /// <summary>Returns one fixture factory per canonical sample graph for the metrics gate tests.</summary>
    public static IEnumerable<Func<(string Name, LayoutMetrics Metrics)>> Fixtures()
    {
        GraphStyle style = new();
        foreach (BaselineRunner.Fixture f in BaselineRunner.BuildAll())
        {
            // Capture in a local so each test invocation reproduces independently.
            string name = f.Name;
            IReadOnlyList<IGraphNode> nodes = f.Nodes;
            IReadOnlyList<IGraphEdge> edges = f.Edges;
            IReadOnlyList<INodeGroup>? groups = f.Groups;
            IReadOnlyList<INodeTable>? tables = f.Tables;
            yield return () => (name, LayoutMetrics.Compute(nodes, edges, groups, tables, style));
        }
    }

    /// <summary>Hard gates_pass on every fixture.</summary>
    [Test]
    [MethodDataSource(nameof(Fixtures))]
    public async Task HardGates_PassOnEveryFixture((string Name, LayoutMetrics Metrics) fixture)
    {
        (string name, LayoutMetrics m) = fixture;

        await Assert.That(m.NodeNodeOverlaps)
            .IsEqualTo(0).Because($"{name}: NodeOverlap gate");
        await Assert.That(m.EdgeNodeIntersections)
            .IsEqualTo(0).Because($"{name}: EdgeNodeX gate");
        await Assert.That(m.OutOfBoundsPrimitives)
            .IsEqualTo(0).Because($"{name}: OOB gate");
        await Assert.That(m.SharedPortEndpoints)
            .IsEqualTo(0).Because($"{name}: SharedPorts gate");
        await Assert.That(m.SelfLoopOverlaps)
            .IsEqualTo(0).Because($"{name}: SelfLoopOverlap gate");
        await Assert.That(m.LabelOverlaps)
            .IsEqualTo(0).Because($"{name}: LabelOverlap gate");
    }
}
