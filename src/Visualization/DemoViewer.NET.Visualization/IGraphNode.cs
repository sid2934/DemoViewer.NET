namespace DemoViewer.NET.Visualization;

/// <summary>
///     Represents a node in the directed graph. Consumers implement this interface
///     on their domain objects. The library reads these properties during layout and
///     rendering; it never writes to them.
/// </summary>
public interface IGraphNode
{
    /// <summary>
    ///     Display string for the node's current value, or null for boolean nodes.
    ///     When null, the renderer shows "ACTIVE" / "inactive" based on <see cref="IsActive" />.
    /// </summary>
    string? DisplayValue { get; }

    /// <summary>
    ///     Whether a debugger breakpoint is armed on this node. When <c>true</c> the renderer draws a
    ///     breakpoint marker in the node's corner. Defaults to <c>false</c>; mirrors the additive
    ///     <see cref="IGraphEdge.IsVisible" /> default-member pattern so existing implementors need no
    ///     change. The library only reads it — toggling never triggers a relayout.
    /// </summary>
    bool HasBreakpoint => false;

    /// <summary>
    ///     Whether the armed breakpoint carries a condition (vs the default "stop when active"). Drives
    ///     a distinct marker (a hollow centre) so conditional and unconditional breakpoints are
    ///     visually separable. Only meaningful when <see cref="HasBreakpoint" /> is <c>true</c>.
    /// </summary>
    bool HasConditionalBreakpoint => false;

    /// <summary>Whether this node is currently in its "active" state.</summary>
    bool IsActive { get; }

    /// <summary>True for the root/entry-point node — rendered with a distinct style.</summary>
    bool IsRoot { get; }

    /// <summary>Unique display name rendered inside the node box.</summary>
    string Name { get; }

    /// <summary>
    ///     Per-node style override. Return <c>null</c> to use the global theme.
    ///     Individual properties within the record can also be null to inherit selectively.
    /// </summary>
    NodeStyle? Style => null;

    /// <summary>Optional secondary label below the name (e.g. player name).</summary>
    string? Subtitle { get; }
}
