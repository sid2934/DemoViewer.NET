#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Cameras;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>Element/session builders shared by the annotation suites.</summary>
internal static class AnnotationFakes
{
    /// <summary>A three-sample world-anchored stroke with a fresh id.</summary>
    /// <param name="space">The anchor; a level at Z 0 by default.</param>
    /// <param name="time">The envelope; always-visible by default.</param>
    /// <param name="x">World X of the first sample.</param>
    /// <param name="y">World Y of the first sample.</param>
    /// <param name="style">The paint; <see cref="AnnotationStyle.Default" /> when null.</param>
    public static AnnotationElement Stroke(SpaceRef? space = null, TimeEnvelope time = default,
        float x = 0, float y = 0, AnnotationStyle? style = null) =>
        new(
            Guid.NewGuid(),
            AnnotationKind.Freehand,
            style ?? AnnotationStyle.Default,
            space ?? new SpaceRef.World(0),
            time,
            [
                new InkPoint(x, y, 0.5f),
                new InkPoint(x + 40, y + 10, 0.5f),
                new InkPoint(x + 80, y, 0.5f)
            ],
            null);

    /// <summary>A frame carrying the given markers and nothing else.</summary>
    /// <param name="markers">The markers.</param>
    public static Scene2DFrame Frame(params PlayerMarker[] markers) =>
        new()
        {
            Markers = markers
        };

    /// <summary>A marker with a SteamId, alive unless told otherwise.</summary>
    /// <param name="steamId">The player's SteamId.</param>
    /// <param name="x">World X.</param>
    /// <param name="y">World Y.</param>
    /// <param name="z">World Z.</param>
    /// <param name="alive">Whether the player is alive.</param>
    /// <param name="slot">Roster slot.</param>
    public static PlayerMarker Marker(ulong steamId, float x, float y, float z = 0, bool alive = true,
        int slot = 0) =>
        new(slot, 2, x, y, z, 0, RingState.Team, 1, "p" + slot, alive, 0, 0, steamId);

    /// <summary>A stacked pane set over the given bands, arranged on a host surface.</summary>
    /// <param name="host">Host surface size.</param>
    /// <param name="bands">The floor bands, lowest first.</param>
    public static (MapSpace Space, PaneSet Panes) Panes(SKSize host, params FloorSlice[] bands)
    {
        MapSpace space = new();
        space.Rebuild(bands);
        PaneSet panes = new(new StackedLayout());
        panes.Reconcile(space, LevelDisplayMode.Stacked, host, new WorldBounds(-1000, -1000, 1000, 1000));
        return (space, panes);
    }

    /// <summary>A pane framing a world rectangle, with no level set behind it.</summary>
    /// <param name="width">Pane width.</param>
    /// <param name="height">Pane height.</param>
    /// <param name="zMin">Band lower Z.</param>
    /// <param name="zMax">Band upper Z.</param>
    public static LevelPane Pane(float width, float height, double zMin = 0, double zMax = 64)
    {
        MapLevel level = new()
        {
            Id = MapSpace.IdForZMin(zMin),
            Name = "floor 0",
            ZMin = zMin,
            ZMax = zMax
        };

        return new LevelPane(level,
            new SliceCamera(ViewportTransform.Fit(width, height, -1000, -1000, 1000, 1000)),
            ManualRig.Instance)
        {
            ViewportRect = SKRect.Create(width, height)
        };
    }

    /// <summary>A pointer sample over a pane at a world position.</summary>
    /// <param name="pane">The pane.</param>
    /// <param name="world">World position.</param>
    /// <param name="screen">Host position; derived from the pane's camera when null.</param>
    /// <param name="pressure">Stylus pressure.</param>
    /// <param name="intermediate">Coalesced world samples.</param>
    public static ToolPointerEvent Press(LevelPane pane, SKPoint world, SKPoint? screen = null,
        float pressure = 0.5f, ReadOnlySpan<InkPoint> intermediate = default)
    {
        (double sx, double sy) = pane.Camera.Current.WorldToScreen(world.X, world.Y);
        SKPoint host = screen ?? new SKPoint((float)sx, (float)sy);

        return new ToolPointerEvent
        {
            Pane = pane,
            Screen = host,
            PaneLocal = new SKPoint(host.X - pane.ViewportRect.Left, host.Y - pane.ViewportRect.Top),
            World = world,
            Pressure = pressure,
            Button = ToolPointerButton.Left,
            Modifiers = ToolModifiers.None,
            Intermediate = intermediate
        };
    }
}
