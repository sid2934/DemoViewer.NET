#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     A direct-execution stand-in for the host's tool services: real panes and real cameras, no
///     Avalonia. This is the whole point of the <see cref="IToolServices" /> seam — the draw and erase
///     tools are exercised with no window, no dispatcher and no platform (design §11).
/// </summary>
internal sealed class FakeToolServices : IToolServices
{
    private readonly PaneSet? _panes;
    private readonly LevelPane? _single;

    public FakeToolServices(AnnotationSession session, LevelPane pane)
    {
        Session = session;
        _single = pane;
    }

    public FakeToolServices(AnnotationSession session, PaneSet panes)
    {
        Session = session;
        _panes = panes;
    }

    public AnnotationSession Session { get; }

    public int CurrentTick { get; set; }

    /// <summary>Markers <see cref="TryResolveEntityAnchor" /> searches.</summary>
    public List<PlayerMarker> Markers { get; } = [];

    /// <summary>How many repaints the tools asked for.</summary>
    public int RenderRequests { get; private set; }

    public LevelPane? PaneAt(SKPoint screen) =>
        _panes is not null ? _panes.PaneAt(screen.X, screen.Y) : _single;

    public SKPoint ScreenToWorld(LevelPane pane, SKPoint screen)
    {
        ArgumentNullException.ThrowIfNull(pane);
        (double x, double y) = pane.Camera.Current.ScreenToWorld(
            screen.X - pane.ViewportRect.Left, screen.Y - pane.ViewportRect.Top);
        return new SKPoint((float)x, (float)y);
    }

    public SKPoint WorldToScreen(LevelPane pane, SKPoint world)
    {
        ArgumentNullException.ThrowIfNull(pane);
        (double x, double y) = pane.Camera.Current.WorldToScreen(world.X, world.Y);
        return new SKPoint((float)x + pane.ViewportRect.Left, (float)y + pane.ViewportRect.Top);
    }

    public double WorldUnitsPerPixel(LevelPane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        double scale = pane.Camera.Current.EffectiveScale;
        return scale > 0 ? 1 / scale : 1;
    }

    public bool TryResolveEntityAnchor(LevelPane pane, SKPoint world, float worldRadius,
        out ulong steamId, out float dx, out float dy)
    {
        steamId = 0;
        dx = 0;
        dy = 0;

        float best = worldRadius * worldRadius;
        bool found = false;
        for (int i = 0; i < Markers.Count; i++)
        {
            PlayerMarker marker = Markers[i];
            if (!marker.IsAlive || marker.SteamId == 0)
            {
                continue;
            }

            float ddx = world.X - marker.WorldX;
            float ddy = world.Y - marker.WorldY;
            float distance = ddx * ddx + ddy * ddy;
            if (distance > best)
            {
                continue;
            }

            best = distance;
            steamId = marker.SteamId;
            dx = ddx;
            dy = ddy;
            found = true;
        }

        return found;
    }

    public bool TryResolveDrawOffset(LevelPane pane, AnnotationElement element,
        out float offsetX, out float offsetY)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(element);

        offsetX = 0;
        offsetY = 0;

        switch (element.Space)
        {
            case SpaceRef.World world:
                return pane.Space is not { Levels.Count: > 1 }
                       || MapSpace.IdForZMin(world.LevelMinZ) == pane.LevelId;

            case SpaceRef.Entity entity:
            {
                for (int i = 0; i < Markers.Count; i++)
                {
                    PlayerMarker marker = Markers[i];
                    if (marker.SteamId != entity.SteamId || entity.SteamId == 0)
                    {
                        continue;
                    }

                    if (!marker.IsAlive)
                    {
                        return false;
                    }

                    InkPoint origin = element.Points.Count > 0 ? element.Points[0] : default;
                    offsetX = marker.WorldX + entity.Dx - origin.X;
                    offsetY = marker.WorldY + entity.Dy - origin.Y;
                    return true;
                }

                return false;
            }

            default:
                return false;
        }
    }

    public void RequestRender() => RenderRequests++;
}
