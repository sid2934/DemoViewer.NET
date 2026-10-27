#region

using DemoViewer.NET.Playback2D.Input;
using DemoViewer.NET.Playback2D.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Tools;

/// <summary>
///     Runs an <see cref="IMapTool" /> as one of the scene's pointer tools, under
///     <see cref="ToolKind.Map" />. The tool sees the sample and the map, never the annotation session or
///     the token editor.
///     <para>
///         This is the bridge between the two tool contracts. <c>MapView</c> and
///         <c>IPlaybackSurface.AddTool</c> take an <see cref="IMapTool" /> and key it by instance;
///         <c>SceneView.AddTool</c> takes an <see cref="IPointerTool" /> and keys it by
///         <see cref="IPointerTool.Id" />. Wrap the tool in this to put it on a scene.
///     </para>
///     <para>
///         <b>One per view.</b> <see cref="Id" /> is the first-party map tool's id, because
///         <see cref="IMapTool" /> carries none of its own, and a host files a contributed tool under its
///         extension. So one extension gets one bridged map tool on a given view: a second is refused as a
///         duplicate id. Implement <see cref="IPointerTool" /> directly for more than one.
///     </para>
/// </summary>
public sealed class MapToolAdapter : IPointerTool
{
    private ToolContext? _context;

    /// <summary>Wraps a tool.</summary>
    /// <param name="tool">The tool.</param>
    public MapToolAdapter(IMapTool tool) => Tool = tool ?? throw new ArgumentNullException(nameof(tool));

    /// <summary>The wrapped tool.</summary>
    public IMapTool Tool { get; }

    /// <inheritdoc />
    public string Id => ToolIds.Of(ToolKind.Map);

    /// <inheritdoc />
    public ToolKind Kind => ToolKind.Map;

    /// <inheritdoc />
    public bool OnPressed(in ToolPointerEvent e, IToolServices s) => Tool.OnPressed(Convert(in e), ContextFor(s));

    /// <inheritdoc />
    public void OnMoved(in ToolPointerEvent e, IToolServices s) => Tool.OnMoved(Convert(in e), ContextFor(s));

    /// <inheritdoc />
    public void OnReleased(in ToolPointerEvent e, IToolServices s) => Tool.OnReleased(Convert(in e), ContextFor(s));

    /// <inheritdoc />
    public void OnCancelled(IToolServices s) => Tool.OnCancelled(ContextFor(s));

    /// <summary>A router sample as the published contract describes it.</summary>
    /// <param name="e">The sample.</param>
    public static MapToolEvent Convert(in ToolPointerEvent e) => new()
    {
        Pane = e.Pane,
        Screen = e.Screen,
        PaneLocal = e.PaneLocal,
        World = e.World,
        Button = e.Button switch
        {
            ToolPointerButton.Left => MapToolButton.Left,
            ToolPointerButton.Right => MapToolButton.Right,
            ToolPointerButton.Middle => MapToolButton.Middle,
            _ => MapToolButton.None
        },
        Modifiers = Convert(e.Modifiers)
    };

    /// <summary>Router modifiers as the published contract names them.</summary>
    /// <param name="modifiers">The router's modifiers.</param>
    public static MapToolModifiers Convert(ToolModifiers modifiers)
    {
        MapToolModifiers result = MapToolModifiers.None;
        if ((modifiers & ToolModifiers.Shift) != 0)
        {
            result |= MapToolModifiers.Shift;
        }

        if ((modifiers & ToolModifiers.Control) != 0)
        {
            result |= MapToolModifiers.Control;
        }

        if ((modifiers & ToolModifiers.Alt) != 0)
        {
            result |= MapToolModifiers.Alt;
        }

        if ((modifiers & ToolModifiers.Space) != 0)
        {
            result |= MapToolModifiers.Space;
        }

        return result;
    }

    /// <summary>The published context over a router's services.</summary>
    /// <param name="services">The router's services.</param>
    public static IMapToolContext ContextOver(IToolServices services) => new ToolContext(services);

    private ToolContext ContextFor(IToolServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (_context is null || !ReferenceEquals(_context.Services, services))
        {
            _context = new ToolContext(services);
        }

        return _context;
    }

    private sealed class ToolContext(IToolServices services) : IMapToolContext
    {
        public IToolServices Services => services;

        public LevelPane? PaneAt(SKPoint screen) => services.PaneAt(screen);

        public SKPoint WorldToScreen(LevelPane pane, SKPoint world) => services.WorldToScreen(pane, world);

        public double WorldUnitsPerPixel(LevelPane pane) => services.WorldUnitsPerPixel(pane);

        public void RequestRender() => services.RequestRender();
    }
}
