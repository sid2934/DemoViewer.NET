#region

using CS2DemoKit.Analysis.Abstractions;
using DemoViewer.NET.Modules.RuleWorkbench;
using DemoViewer.NET.ViewModels;
using DemoViewer.NET.Visualization;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="RulesetGraphProjection" />, which turns a laid-out graph into the two lists the
///     node editor binds to. Pure, so it needs no window and stays in every tier; the controls it
///     feeds are exercised separately in <see cref="RulesetNodeGraphViewTests" />.
/// </summary>
public class RulesetGraphProjectionTests
{
    /// <summary>
    ///     The projection is pure, so it is checked on its own first: positions come from MSAGL,
    ///     and a node box is placed by its top-left while the layout reports centres.
    /// </summary>
    [Test]
    public async Task Projection_PlacesNodesByTopLeftFromTheLayoutCentre()
    {
        GraphViewModel graph = new();
        GraphNodeViewModel a = new("root", true);
        GraphNodeViewModel b = new("child", false, "subtitle") { DisplayValue = "7" };
        GraphEdgeViewModel edge = new(a, b, "player_death", EdgeEffect.SetValue, "event.X == 1");

        await graph.SetGraphAsync([a, b], [edge]);

        (IReadOnlyList<RulesetGraphNode> nodes, IReadOnlyList<RulesetGraphConnection> connections) =
            RulesetGraphProjection.Project(graph, [a, b], [edge]);

        await Assert.That(nodes.Count).IsEqualTo(2);
        await Assert.That(connections.Count).IsEqualTo(1);

        NodeStyleConfig style = graph.Style.Node;
        foreach (RulesetGraphNode node in nodes)
        {
            GraphNodeViewModel source = node.Title == "root" ? a : b;
            GraphNodePlacement centre = graph.NodePlacements[source];
            await Assert.That(node.Location.X).IsEqualTo(centre.X - style.Width / 2).Within(0.001);
            await Assert.That(node.Location.Y).IsEqualTo(centre.Y - style.Height / 2).Within(0.001);
        }

        await Assert.That(nodes.Single(n => n.Title == "child").Subtitle).IsEqualTo("subtitle");
        await Assert.That(nodes.Single(n => n.Title == "child").Value).IsEqualTo("7");
        await Assert.That(nodes.Single(n => n.Title == "root").IsRoot).IsTrue();
        await Assert.That(connections[0].Label).IsEqualTo("player_death");
    }

    /// <summary>A graph that has not been laid out yet projects to nothing, rather than to a pile at the origin.</summary>
    [Test]
    public async Task Projection_BeforeLayout_IsEmpty()
    {
        GraphViewModel graph = new();
        GraphNodeViewModel a = new("root", true);

        (IReadOnlyList<RulesetGraphNode> nodes, IReadOnlyList<RulesetGraphConnection> connections) =
            RulesetGraphProjection.Project(graph, [a], []);

        await Assert.That(nodes).IsEmpty();
        await Assert.That(connections).IsEmpty();
    }
}
