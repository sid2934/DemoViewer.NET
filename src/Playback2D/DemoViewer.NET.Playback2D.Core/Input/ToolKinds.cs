#region

using DemoViewer.NET.Playback2D.Core.Annotations;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Input;

/// <summary>
///     What each <see cref="ToolKind" /> means to the annotation document. Lives in Core, not in the
///     published scene contract: the two tables answer questions only the app's own toolbar and the ink
///     tools ask, and no extension names a shape tool.
/// </summary>
public static class ToolKinds
{
    /// <summary>
    ///     True for every tool that writes or erases annotation elements: what the keymap's tool scope
    ///     keys off, so Space holds to pan and Esc cancels under a shape tool exactly as under the pen.
    ///     Pan/zoom and the Situations tab's query token are not annotation tools.
    /// </summary>
    /// <param name="kind">The tool.</param>
    public static bool IsAnnotationTool(ToolKind kind) =>
        kind is ToolKind.Draw or ToolKind.Erase or ToolKind.Line or ToolKind.Arrow or ToolKind.Rect
            or ToolKind.Ellipse or ToolKind.Text;

    /// <summary>The element kind a two-point shape tool commits, or null for any other tool.</summary>
    /// <param name="kind">The tool.</param>
    public static AnnotationKind? ShapeKindOf(ToolKind kind) => kind switch
    {
        ToolKind.Line => AnnotationKind.Line,
        ToolKind.Arrow => AnnotationKind.Arrow,
        ToolKind.Rect => AnnotationKind.Rect,
        ToolKind.Ellipse => AnnotationKind.Ellipse,
        _ => null
    };
}
