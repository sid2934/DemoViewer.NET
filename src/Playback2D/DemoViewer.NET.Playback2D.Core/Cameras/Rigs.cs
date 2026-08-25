#region

using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Cameras;

/// <summary>
///     Holds whatever the camera already shows. Used for <c>CameraMode.Fit</c> — a one-shot fit applied
///     by <c>PaneSet.FitAll</c>, static thereafter — and as the starting rig of a freshly created pane.
///     <para>
///         The naming reads backwards against the mode vocabulary (plan decision D-3): <c>Fit</c> maps
///         to <see cref="ManualRig" /> because it fits <i>once</i>, while <c>Map</c> maps to
///         <see cref="FitMapRig" /> because it fits <i>continuously</i>. The behaviours are what matter.
///     </para>
/// </summary>
public sealed class ManualRig : ICameraRig
{
    /// <summary>The shared instance. Stateless, so one is enough.</summary>
    public static readonly ManualRig Instance = new();

    /// <inheritdoc />
    public string Id => "manual";

    /// <inheritdoc />
    public ViewportTransform? ComputeTarget(LevelPane pane, Scene2DFrame frame) => null;
}

/// <summary>
///     Frames the map. The <b>real networked</b> playable bounds (<c>m_vMinimapMins</c>/<c>Maxs</c>)
///     when the map publishes them, else the all-demo observed extent — the approximation the mode
///     selector labels "Map (approx.)". Port of <c>TryComputeTarget</c>'s <c>Map</c> arm (lines 716-728).
/// </summary>
public sealed class FitMapRig : ICameraRig
{
    /// <summary>The shared instance. Stateless.</summary>
    public static readonly FitMapRig Instance = new();

    /// <inheritdoc />
    public string Id => "fit-map";

    /// <inheritdoc />
    public ViewportTransform? ComputeTarget(LevelPane pane, Scene2DFrame frame)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(frame);

        WorldBounds bounds = frame.Map.NetworkedBounds ?? frame.Map.ObservedBounds;
        return ViewportTransform.Fit(pane.ViewportRect.Width, pane.ViewportRect.Height,
            bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
    }
}

/// <summary>
///     Frames the alive players <b>assigned to this pane's level</b>, padded. Port of
///     <c>TryFitAlive</c> (lines 743-785), including its two parity behaviours: a pane with no alive
///     players holds rather than snapping somewhere arbitrary, and a single alive player gets a fixed
///     box rather than a degenerate zero-area fit.
/// </summary>
public sealed class FitAliveRig : ICameraRig
{
    /// <summary>The shared instance with the pre-v2 constants.</summary>
    public static readonly FitAliveRig Instance = new();

    private readonly double _minHalfWorld;
    private readonly double _padding;

    /// <summary>Creates a rig.</summary>
    /// <param name="padding">Fractional margin around the alive bounds (pre-v2 <c>AlivePadding</c>).</param>
    /// <param name="minHalfWorld">Floor on that margin (pre-v2 <c>FollowHalfWorld</c>).</param>
    public FitAliveRig(double padding = 0.18, double minHalfWorld = 900)
    {
        _padding = padding;
        _minHalfWorld = minHalfWorld;
    }

    /// <inheritdoc />
    public string Id => "fit-alive";

    /// <inheritdoc />
    public ViewportTransform? ComputeTarget(LevelPane pane, Scene2DFrame frame)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(frame);

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        int count = 0;

        IReadOnlyList<PlayerMarker> markers = frame.Markers;
        for (int i = 0; i < markers.Count; i++)
        {
            PlayerMarker m = markers[i];
            if (!m.IsAlive)
            {
                continue;
            }

            // Single-band renders frame every player regardless of Z (pre-v2 `_cameras.Length > 1`).
            if (pane.PaneCount > 1 && pane.Space is { } space &&
                space.LevelIndexFor(m.WorldZ) != pane.LevelIndex)
            {
                continue;
            }

            minX = Math.Min(minX, m.WorldX);
            minY = Math.Min(minY, m.WorldY);
            maxX = Math.Max(maxX, m.WorldX);
            maxY = Math.Max(maxY, m.WorldY);
            count++;
        }

        if (count == 0)
        {
            return null; // hold — this level has nobody alive this frame.
        }

        double padX = Math.Max((maxX - minX) * _padding, _minHalfWorld);
        double padY = Math.Max((maxY - minY) * _padding, _minHalfWorld);
        return ViewportTransform.Fit(pane.ViewportRect.Width, pane.ViewportRect.Height,
            minX - padX, minY - padY, maxX + padX, maxY + padY);
    }
}

/// <summary>
///     Centres on one player's marker. Port of <c>TryFollow</c> (lines 789-818) — the followed player
///     keeps a gray marker at their last-known position when dead, so following survives a death; only
///     a slot with no marker at all makes the camera hold.
///     <para>
///         <b>The one deliberate behaviour change in B1</b> (plan §4 T4): a deadzone. The committed
///         centre is held while the marker stays inside a box of half-extent
///         <see cref="DeadzoneHalfWorld" /> around it, so small strafes stop dragging the whole map.
///         <see cref="DeadzoneHalfWorld" /> = 0 reproduces the pre-v2 behaviour exactly, and that is
///         what the parity test uses.
///     </para>
/// </summary>
public sealed class FollowPlayerRig : ICameraRig
{
    private readonly double _halfWorld;
    private bool _committed;
    private double _committedX, _committedY;

    /// <summary>Creates a rig following one roster slot.</summary>
    /// <param name="slot">The roster slot to follow, or -1 for none.</param>
    /// <param name="halfWorld">Half-extent of the framed box (pre-v2 <c>FollowHalfWorld</c>).</param>
    /// <param name="deadzoneHalfWorld">Half-extent of the hold box. 0 = pre-v2 behaviour.</param>
    public FollowPlayerRig(int slot, double halfWorld = 900, double deadzoneHalfWorld = 180)
    {
        Slot = slot;
        _halfWorld = halfWorld;
        DeadzoneHalfWorld = deadzoneHalfWorld;
    }

    /// <summary>The roster slot being followed. -1 = none, which makes the rig hold.</summary>
    public int Slot { get; set; }

    /// <summary>Half-extent of the axis-aligned box the marker may move in before the camera recentres.</summary>
    public double DeadzoneHalfWorld { get; set; }

    /// <inheritdoc />
    public string Id => "follow-player";

    /// <inheritdoc />
    public ViewportTransform? ComputeTarget(LevelPane pane, Scene2DFrame frame)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(frame);

        if (Slot < 0)
        {
            return null;
        }

        IReadOnlyList<PlayerMarker> markers = frame.Markers;
        for (int i = 0; i < markers.Count; i++)
        {
            PlayerMarker m = markers[i];
            if (m.Slot != Slot)
            {
                continue;
            }

            // Only the level the followed player is on tracks them; the others hold. Note this RETURNS
            // rather than continuing — the pre-v2 code does the same, and a roster with a duplicate slot
            // must not make a second marker win.
            if (pane.PaneCount > 1 && pane.Space is { } space &&
                space.LevelIndexFor(m.WorldZ) != pane.LevelIndex)
            {
                return null;
            }

            double cx = m.WorldX, cy = m.WorldY;
            if (DeadzoneHalfWorld > 0)
            {
                if (_committed &&
                    Math.Abs(cx - _committedX) <= DeadzoneHalfWorld &&
                    Math.Abs(cy - _committedY) <= DeadzoneHalfWorld)
                {
                    cx = _committedX;
                    cy = _committedY;
                }
                else
                {
                    _committed = true;
                    _committedX = cx;
                    _committedY = cy;
                }
            }

            return ViewportTransform.Fit(pane.ViewportRect.Width, pane.ViewportRect.Height,
                cx - _halfWorld, cy - _halfWorld, cx + _halfWorld, cy + _halfWorld);
        }

        return null; // the followed slot has no marker at all → hold.
    }

    /// <summary>
    ///     Forgets the committed centre so the next frame recentres. Called on
    ///     <c>SceneTime.IsDiscontinuity</c> — after a seek the deadzone must not hold the camera at
    ///     where the player used to be.
    /// </summary>
    public void ResetDeadzone() => _committed = false;
}
