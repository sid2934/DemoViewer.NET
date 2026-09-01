#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using DemoViewer.NET.Visualization;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>
///     Pure conversion of a <see cref="BuildResult" /> (its <c>Nodes</c> + <c>Edges</c> + group hints)
///     into the <see cref="GraphViewModel" />-ready node/edge/group view-models: the graph <em>skeleton</em>
///     (topology + pre-eval node values, <c>TrackedIndex = -1</c>), independent of any evaluation. Extracted
///     so the progressive-reveal pre-render (<c>AnalysisViewModel.RenderGraphSkeletonAsync</c>) and the
///     authoring Workbench's ruleset-structure graph share one conversion rather than duplicating it. This is
///     deliberately the <em>skeleton</em> path (not the live post-eval render, which additionally wires real
///     <c>TrackedIndex</c> + evaluated display values for seek/highlight).
/// </summary>
public static class RuleGraphSkeleton
{
    private static readonly IReadOnlySet<string> _emptyChainKeys = new HashSet<string>();

    /// <summary>
    ///     Converts the demo-less, ruleset-focused <see cref="AuthoringGraph.AuthoringGraphModel" /> (built
    ///     from the open ruleset in the Workbench) into graph view-models. Unlike <see cref="Build" />, this
    ///     path already carries the reduction + per-player flagging: it just maps the model's indexed nodes
    ///     and edges onto <see cref="GraphNodeViewModel" />/<see cref="GraphEdgeViewModel" />.
    /// </summary>
    public static Skeleton BuildAuthoring(AuthoringGraph.AuthoringGraphModel model)
    {
        List<GraphNodeViewModel> nodeVms = new(model.Nodes.Count);
        foreach (AuthoringGraph.AuthoringGraphNode n in model.Nodes)
        {
            nodeVms.Add(new GraphNodeViewModel(n.Name, n.IsRoot, n.Subtitle)
            {
                DisplayValue = n.DisplayValue,
                ChainIds = n.ChainIds,
                IsPerPlayer = n.IsPerPlayer,
                TrackedIndex = -1
            });
        }

        List<GraphEdgeViewModel> edgeVms = new(model.Edges.Count);
        foreach (AuthoringGraph.AuthoringGraphEdge e in model.Edges)
        {
            edgeVms.Add(new GraphEdgeViewModel(
                nodeVms[e.Source], nodeVms[e.Destination], e.Label, e.Effect, e.ConditionLabel));
        }

        return new Skeleton(
            nodeVms.Cast<IGraphNode>().ToList(),
            edgeVms.Cast<IGraphEdge>().ToList(),
            null);
    }

    /// <summary>
    ///     Builds the graph skeleton from <paramref name="build" />. Nodes carry their chain keys and
    ///     pre-eval display value; edges carry label/effect/condition; groups mirror the build's group hints
    ///     (null when none: the shape <see cref="GraphViewModel.SetGraphAsync" /> expects for "no groups").
    /// </summary>
    public static Skeleton Build(BuildResult build)
    {
        Dictionary<StateNode, GraphNodeViewModel> byNode = new(ReferenceEqualityComparer.Instance);
        List<GraphNodeViewModel> nodeVms = new(build.Nodes.Count);
        foreach (StateNode node in build.Nodes)
        {
            IReadOnlySet<string> chainIds =
                build.NodeChains is not null && build.NodeChains.TryGetValue(node, out IReadOnlySet<string>? keys)
                    ? keys
                    : _emptyChainKeys;

            GraphNodeViewModel vm = new(node.Name, node is RootNode, node.Subtitle)
            {
                IsActive = node.IsActive,
                DisplayValue = node.GetDisplayValue(),
                ChainIds = chainIds,
                TrackedIndex = -1
            };
            byNode[node] = vm;
            nodeVms.Add(vm);
        }

        List<GraphEdgeViewModel> edgeVms = new(build.Edges.Count);
        foreach (GraphEdgeDescriptor e in build.Edges)
        {
            if (byNode.TryGetValue(e.Source, out GraphNodeViewModel? srcVm)
                && byNode.TryGetValue(e.Destination, out GraphNodeViewModel? dstVm))
            {
                edgeVms.Add(new GraphEdgeViewModel(srcVm, dstVm, e.Label, e.Effect, e.ConditionLabel));
            }
        }

        List<INodeGroup> groups = new();
        foreach (NodeGroupHint hint in build.GroupHints)
        {
            List<IGraphNode> members = new();
            foreach (StateNode member in hint.Members)
            {
                if (byNode.TryGetValue(member, out GraphNodeViewModel? vm))
                {
                    members.Add(vm);
                }
            }

            if (members.Count > 0)
            {
                groups.Add(new AnalysisNodeGroup(hint.GroupName, members));
            }
        }

        return new Skeleton(
            nodeVms.Cast<IGraphNode>().ToList(),
            edgeVms.Cast<IGraphEdge>().ToList(),
            groups.Count > 0 ? groups : null);
    }

    /// <summary>The three view-model lists a <see cref="GraphViewModel.SetGraphAsync" /> call consumes.</summary>
    public readonly record struct Skeleton(
        IReadOnlyList<IGraphNode> Nodes,
        IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup>? Groups);
}
