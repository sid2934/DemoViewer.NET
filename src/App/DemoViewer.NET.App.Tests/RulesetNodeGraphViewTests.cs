#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.RuleWorkbench;
using DemoViewer.NET.ViewModels;
using CS2DemoKit.Analysis.Abstractions;
using DemoViewer.NET.Visualization;
using Nodify;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The read-only nodify view of the open ruleset (docs/rule-graph/design.md §6.4 item 3).
///     <para>
///         These RUN the controls rather than compiling them. That distinction is the whole reason
///         the fork exists: the published <c>NodifyAvalonia</c> 6.6.0 compiles perfectly well
///         against Avalonia 12 and then throws <c>TypeLoadException</c> at the first Nodify type it
///         JITs (§6.5). A test that only built the XAML would have said nothing about that, and a
///         theme that fails to merge would leave every node templateless and still "pass".
///     </para>
/// </summary>
public class RulesetNodeGraphViewTests
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

    /// <summary>
    ///     The real answer to R11, on the Avalonia this repository ships: the editor realizes a
    ///     container per node and every one gets a template, which is what a merged theme looks like.
    /// </summary>
    [Test]
    public async Task NodifyEditor_RealizesATemplatedContainerPerNode_AtItsLayoutPosition()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (IReadOnlyList<RulesetGraphNode> nodes, IReadOnlyList<RulesetGraphConnection> connections) =
                await ProbeGraph();

            NodifyEditor editor = new()
            {
                ItemsSource = nodes,
                Connections = connections
            };
            Window window = new()
            {
                Width = 1200,
                Height = 900,
                Content = editor
            };

            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                List<ItemContainer> containers =
                    [.. editor.GetVisualDescendants().OfType<ItemContainer>()];

                await Assert.That(containers.Count).IsEqualTo(nodes.Count)
                    .Because("every projected node should realize a container");
                await Assert.That(containers.All(c => c.Template is not null)).IsTrue()
                    .Because("a container with no template means the Nodify theme did not merge");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    ///     The Workbench's own XAML, with real data pushed through it: the item template produces
    ///     <see cref="Node" /> controls, each container sits where MSAGL put it, and read-only is
    ///     ENFORCED rather than merely unused. "We did not write a drag handler" is not the same
    ///     claim as "the control will not drag", and editing is §6.4 items 4 and 5, not started.
    /// </summary>
    [Test]
    public async Task TheWorkbenchGraph_DrawsNodesWhereTheLayoutPutThem_AndCannotBeDragged()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (IReadOnlyList<RulesetGraphNode> nodes, IReadOnlyList<RulesetGraphConnection> connections) =
                await ProbeGraph();

            Views.RuleWorkbench.RuleWorkbenchView view = new();
            Window window = new()
            {
                Width = 1200,
                Height = 900,
                Content = view
            };

            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                NodifyEditor? editor = view.GetVisualDescendants().OfType<NodifyEditor>()
                    .FirstOrDefault();
                await Assert.That(editor).IsNotNull()
                    .Because("the Workbench XAML should mount the node editor");

                // The view has no view model here, so the data goes in directly. The TEMPLATE and
                // the container theme under test are still the Workbench's own.
                editor!.ItemsSource = nodes;
                editor.Connections = connections;
                editor.IsVisible = true;
                Dispatcher.UIThread.RunJobs();

                List<ItemContainer> containers =
                    [.. editor.GetVisualDescendants().OfType<ItemContainer>()];

                await Assert.That(containers.Count).IsEqualTo(nodes.Count);
                await Assert.That(editor.GetVisualDescendants().OfType<Node>().Any()).IsTrue()
                    .Because("the Workbench item template should have produced Node controls");
                await Assert.That(containers.All(c => !c.IsDraggable)).IsTrue()
                    .Because("the view is read-only; nothing should be draggable");
                await Assert.That(containers.All(c => !c.IsSelectable)).IsTrue();
                await Assert.That(editor.CanSelectMultipleItems).IsFalse();
                await Assert.That(editor.EnableRealtimeSelection).IsFalse();

                foreach (ItemContainer container in containers)
                {
                    RulesetGraphNode model = (RulesetGraphNode)container.DataContext!;
                    await Assert.That(container.Location).IsEqualTo(model.Location)
                        .Because($"{model.Title} should sit where the layout put it");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>A small laid-out graph, projected, for the two realization tests.</summary>
    private static async Task<(IReadOnlyList<RulesetGraphNode> Nodes,
        IReadOnlyList<RulesetGraphConnection> Connections)> ProbeGraph()
    {
        GraphViewModel graph = new();
        List<GraphNodeViewModel> source = [];
        for (int i = 0; i < 12; i++)
        {
            source.Add(new GraphNodeViewModel($"stat_{i}", i == 0));
        }

        List<GraphEdgeViewModel> edges = [];
        for (int i = 1; i < source.Count; i++)
        {
            edges.Add(new GraphEdgeViewModel(source[i - 1], source[i], "next", EdgeEffect.SetValue));
        }

        await graph.SetGraphAsync([.. source], [.. edges]);
        return RulesetGraphProjection.Project(graph, [.. source], [.. edges]);
    }
}
