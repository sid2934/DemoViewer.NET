#region

using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The ink tools decline work on a host that provides <see cref="IToolServices" /> but not
///     <see cref="IInkToolServices" />, instead of throwing or half-drawing.
///     <para>
///         Every other fake in this project implements the ink interface, so without this file the
///         fifteen guard sites added when the interface was split have no test at all and a host that
///         stopped supplying ink would lose it silently. HostedMapView is exactly such a host today.
///     </para>
/// </summary>
public class InkRefusalTests
{
    /// <summary>A host with the seven-member contract and nothing more, which is HostedMapView's shape.</summary>
    private sealed class InklessServices : IToolServices
    {
        public int CurrentTick => 0;

        public ITokenEditor? Tokens => null;

        public int RenderRequests { get; private set; }

        public LevelPane? PaneAt(SKPoint screen) => null;

        public SKPoint ScreenToWorld(LevelPane pane, SKPoint screen) => screen;

        public SKPoint WorldToScreen(LevelPane pane, SKPoint world) => world;

        public double WorldUnitsPerPixel(LevelPane pane) => 1.0;

        public void RequestRender() => RenderRequests++;
    }

    /// <param name="tool">The ink tool under test.</param>
    [Test]
    [Arguments("draw")]
    [Arguments("erase")]
    [Arguments("shape")]
    [Arguments("text")]
    public async Task AnInkTool_OnAnInklessHost_DeclinesInsteadOfThrowing(string tool)
    {
        InklessServices services = new();
        IPointerTool subject = tool switch
        {
            "draw" => new DrawTool(),
            "erase" => new EraseTool(),
            "shape" => new ShapeTool(ToolKind.Rect),
            _ => new TextTool()
        };

        // ToolPointerEvent is a ref struct, so the whole gesture runs in one synchronous call and
        // only its results cross the await.
        (bool Pressed, int Renders) result = Drive(subject, services);

        // False is what tells the router the gesture was not taken, so the next tool still sees it.
        await Assert.That(result.Pressed).IsFalse();
        await Assert.That(result.Renders).IsEqualTo(0)
            .Because("a tool that declined the gesture has nothing to repaint");
    }

    /// <summary>Runs a whole press/move/release/cancel gesture and reports what the host saw.</summary>
    /// <param name="tool">The tool to drive.</param>
    /// <param name="services">The ink-less host.</param>
    private static (bool Pressed, int Renders) Drive(IPointerTool tool, InklessServices services)
    {
        ToolPointerEvent e = new()
        {
            Screen = new SKPoint(10, 10), World = new SKPoint(10, 10), Pane = null, Pressure = 1f
        };

        bool pressed = tool.OnPressed(in e, services);

        // The void members must be no-ops rather than throws: the router calls them unconditionally
        // once a gesture is live.
        tool.OnMoved(in e, services);
        tool.OnReleased(in e, services);
        tool.OnCancelled(services);
        return (pressed, services.RenderRequests);
    }
}
