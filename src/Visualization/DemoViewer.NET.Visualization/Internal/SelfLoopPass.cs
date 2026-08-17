namespace DemoViewer.NET.Visualization.Internal;

/// <summary>
///     Pass 2 — Self-loops. Computes loop routes via <see cref="SelfLoopRenderer" />.
///     Loops are not rerouted inward; instead the ContainmentPass measures their
///     extents into the true bbox and shifts the whole layout if a loop escapes
///     negative — which is what structurally fixes the SelfLoopHeavy OOB bug.
/// </summary>
internal static class SelfLoopPass
{
    internal static void Run(LayoutContext ctx)
    {
        IEnumerable<IGraphEdge> selfLoops = ctx.Edges.Where(e => ReferenceEquals(e.Source, e.Destination));
        ctx.SelfLoopRoutes = SelfLoopRenderer.ComputeSelfLoopRoutes(
            selfLoops, ctx.NodePositions, ctx.Style);
    }
}
