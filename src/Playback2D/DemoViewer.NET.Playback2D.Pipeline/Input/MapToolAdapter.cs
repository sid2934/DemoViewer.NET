#region

using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Tools;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Input;

/// <summary>
///     Runs a published <see cref="IMapTool" /> as one of the router's tools, under <see cref="ToolKind.Map" />.
///     The tool sees the sample and the map, never the annotation session or the token editor.
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

    /// <summary>Router modifiers as the published contract names them. Space has no published flag.</summary>
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
