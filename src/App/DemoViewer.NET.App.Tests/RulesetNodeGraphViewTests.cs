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
///     The read-only nodify view of the open ruleset (docs/rule-graph/design.md §6.4 item 3), as
///     controls that are actually realized. The pure projection behind it is
///     <see cref="RulesetGraphProjectionTests" />.
///     <para>
///         These RUN the controls rather than compiling them. That distinction is the whole reason
///         the fork exists: the published <c>NodifyAvalonia</c> 6.6.0 compiles perfectly well
///         against Avalonia 12 and then throws <c>TypeLoadException</c> at the first Nodify type it
///         JITs (§6.5). A test that only built the XAML would have said nothing about that, and a
///         theme that fails to merge would leave every node templateless and still "pass".
///     </para>
/// </summary>
// Boots a window, so it is out of the fast tier: TestTierContractTests holds that contract for the
// whole suite, and it caught this class the first time it ran.
[Category("Render")]
public class RulesetNodeGraphViewTests
{
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
