namespace DemoViewer.NET.Playback2D.Input;

/// <summary>
///     The id each first-party tool is registered under. The router keys on
///     <see cref="IPointerTool.Id" />, not on <see cref="ToolKind" />, so selecting a first-party tool by
///     kind goes through here.
///     <para>
///         A contributed tool's id is its own and the host files it under its extension, so none of these
///         is reachable from an extension and two extensions picking the same local id still get two tools.
///     </para>
/// </summary>
public static class ToolIds
{
    private const string PanZoom = "panzoom";
    private const string Draw = "draw";
    private const string Erase = "erase";
    private const string Map = "map";
    private const string Line = "line";
    private const string Arrow = "arrow";
    private const string Rect = "rect";
    private const string Ellipse = "ellipse";
    private const string Text = "text";
    private const string Token = "token";

    /// <summary>
    ///     The id the first-party tool of this kind registers under. A value outside the enum reads as
    ///     pan/zoom's, which is the fallback an unregistered selection lands on anyway.
    /// </summary>
    /// <param name="kind">The tool's kind.</param>
    public static string Of(ToolKind kind) => kind switch
    {
        ToolKind.Draw => Draw,
        ToolKind.Erase => Erase,
        ToolKind.Map => Map,
        ToolKind.Line => Line,
        ToolKind.Arrow => Arrow,
        ToolKind.Rect => Rect,
        ToolKind.Ellipse => Ellipse,
        ToolKind.Text => Text,
        ToolKind.Token => Token,
        _ => PanZoom
    };
}
