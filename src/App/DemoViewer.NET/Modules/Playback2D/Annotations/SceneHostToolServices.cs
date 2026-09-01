#region

using System.Diagnostics;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Modules.Playback2D.Annotations;

/// <summary>
///     <see cref="IToolServices" /> over <see cref="Scene2DHost" />: the one place the pointer tools
///     touch Avalonia-adjacent state, and the reason the tools themselves are testable with no window.
///     <para>
///         <see cref="Session" /> is settable because the host outlives the view-model it is bound to: a
///         tab activation swaps the session in, and rebuilding the router each time would drop a gesture
///         that was in flight.
///     </para>
/// </summary>
internal sealed class SceneHostToolServices(Scene2DHost host, AnnotationSession session) : IToolServices
{
    // Stopwatch, not DateTime: its timestamp counts from an arbitrary origin on a monotonic counter, so
    // an NTP correction or a DST step landing mid-stroke cannot walk it backwards and hand the cadence
    // accumulator a negative offset. Banned in Core, which is why the clock is an IToolServices member
    // implemented out here in the app rather than read where it is used.
    private readonly long _origin = Stopwatch.GetTimestamp();

    /// <summary>The session the tools mutate. Re-pointed when the host binds a new view-model.</summary>
    public AnnotationSession Session { get; set; } = session;

    /// <summary>
    ///     The playhead in DV frame-clock ticks: the tick of the frame the host is currently showing, the
    ///     same number the ink layer's envelopes are evaluated against. Never a live CS2 engine tick.
    /// </summary>
    public int CurrentTick => host.CurrentSceneFrame.Time.Tick;

    /// <summary>
    ///     Milliseconds since this services instance was built. The origin is arbitrary and unshared on
    ///     purpose: every consumer re-bases it at the press, so only the differences ever matter.
    /// </summary>
    public long NowMilliseconds => (long)Stopwatch.GetElapsedTime(_origin).TotalMilliseconds;

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
    ///     The nearest LIVING marker within the capture radius, filtered to the pane's own level: a
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

    /// <summary>
    ///     Mirrors <c>AnnotationLayer</c>'s per-frame anchor resolution, so what the eraser can touch is
    ///     exactly what the pane draws: a world anchor belongs to one level, and an entity anchor rides
    ///     its player's live marker (hidden while absent or dead).
    /// </summary>
    /// <param name="pane">The pane the pointer is in.</param>
    /// <param name="element">The element being tested.</param>
    /// <param name="offsetX">World X offset applied to the element's samples when drawn here.</param>
    /// <param name="offsetY">World Y offset applied to the element's samples when drawn here.</param>
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
            {
                // Through the SPACE, exactly as AnnotationLayer resolves it. IdForZMin is the MINTING
                // rule, and a floor lost and re-found is minted past the colliding key, so deriving the
                // id from Z here leaves the eraser unable to touch ink the pane is visibly drawing.
                MapSpace? space = pane.Space;
                return space is not { Levels.Count: > 1 }
                       || space.IdForAnchor(world.LevelMinZ) == pane.LevelId;
            }

            case SpaceRef.Entity entity:
            {
                IReadOnlyList<PlayerMarker> markers = host.CurrentSceneFrame.Markers;
                for (int i = 0; i < markers.Count; i++)
                {
                    PlayerMarker marker = markers[i];
                    if (marker.SteamId != entity.SteamId || entity.SteamId == 0)
                    {
                        continue;
                    }

                    if (!marker.IsAlive)
                    {
                        return false;
                    }

                    if (pane.Space is { Levels.Count: > 1 } space
                        && space.LevelIndexFor(marker.WorldZ) != pane.LevelIndex)
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

    /// <inheritdoc />
    public void RequestRender() => host.RequestToolRender();
}
