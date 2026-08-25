#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Modules.Playback2D.Annotations;

/// <summary>
///     <see cref="IToolServices" /> over <see cref="Scene2DHost" /> — the one place the pointer tools
///     touch Avalonia-adjacent state, and the reason the tools themselves are testable with no window.
///     <para>
///         <see cref="Session" /> is settable because the host outlives the view-model it is bound to: a
///         tab activation swaps the session in, and rebuilding the router each time would drop a gesture
///         that was in flight.
///     </para>
/// </summary>
internal sealed class SceneHostToolServices(Scene2DHost host, AnnotationSession session) : IToolServices
{
    /// <summary>The session the tools mutate. Re-pointed when the host binds a new view-model.</summary>
    public AnnotationSession Session { get; set; } = session;

    /// <summary>
    ///     The playhead in DV frame-clock ticks — the tick of the frame the host is currently showing,
    ///     which is the same number the ink layer's envelopes are evaluated against. Never a live CS2
    ///     engine tick.
    /// </summary>
    public int CurrentTick => host.CurrentSceneFrame.Time.Tick;

    /// <inheritdoc />
    public LevelPane? PaneAt(SKPoint screen) => host.PaneAtHostPoint(screen.X, screen.Y);

    /// <inheritdoc />
    public SKPoint ScreenToWorld(LevelPane pane, SKPoint screen)
    {
        ArgumentNullException.ThrowIfNull(pane);
        (double x, double y) = pane.Camera.Current.ScreenToWorld(
            screen.X - pane.ViewportRect.Left, screen.Y - pane.ViewportRect.Top);
        return new SKPoint((float)x, (float)y);
    }

    /// <inheritdoc />
    public SKPoint WorldToScreen(LevelPane pane, SKPoint world)
    {
        ArgumentNullException.ThrowIfNull(pane);
        (double x, double y) = pane.Camera.Current.WorldToScreen(world.X, world.Y);
        return new SKPoint((float)x + pane.ViewportRect.Left, (float)y + pane.ViewportRect.Top);
    }

    /// <inheritdoc />
    public double WorldUnitsPerPixel(LevelPane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        double scale = pane.Camera.Current.EffectiveScale;
        return scale > 0 ? 1 / scale : 1;
    }

    /// <summary>
    ///     The nearest LIVING marker within the capture radius, filtered to the pane's own level — a
    ///     stroke drawn on the upper floor must not silently anchor to a player standing below it.
    /// </summary>
    /// <param name="pane">The pane the press landed in.</param>
    /// <param name="world">The pressed world point.</param>
    /// <param name="worldRadius">Capture radius in world units.</param>
    /// <param name="steamId">The anchored player's SteamId.</param>
    /// <param name="dx">World X offset from the player to <paramref name="world" />.</param>
    /// <param name="dy">World Y offset from the player to <paramref name="world" />.</param>
    public bool TryResolveEntityAnchor(LevelPane pane, SKPoint world, float worldRadius,
        out ulong steamId, out float dx, out float dy)
    {
        ArgumentNullException.ThrowIfNull(pane);

        steamId = 0;
        dx = 0;
        dy = 0;

        IReadOnlyList<PlayerMarker> markers = host.CurrentSceneFrame.Markers;
        MapSpace? space = pane.Space;
        float best = worldRadius * worldRadius;
        bool found = false;

        for (int i = 0; i < markers.Count; i++)
        {
            PlayerMarker marker = markers[i];
            if (!marker.IsAlive || marker.SteamId == 0)
            {
                continue;
            }

            if (space is not null && space.Levels.Count > 1
                                  && space.LevelIndexFor(marker.WorldZ) != pane.LevelIndex)
            {
                continue;
            }

            float ddx = world.X - marker.WorldX;
            float ddy = world.Y - marker.WorldY;
            float distance = (ddx * ddx) + (ddy * ddy);
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

    /// <inheritdoc />
    public void RequestRender() => host.RequestToolRender();
}
