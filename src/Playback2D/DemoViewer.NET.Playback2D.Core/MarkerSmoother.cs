namespace DemoViewer.NET.Playback2D.Core;

/// <summary>Reads a slot's smoothed draw position. Implemented by <see cref="MarkerSmoother" />.</summary>
public interface ISmoothedPositionSource
{
    /// <summary>The smoothed draw position for a slot, or false when the slot is not tracked.</summary>
    /// <param name="slot">Roster slot.</param>
    /// <param name="x">World X of the smoothed position.</param>
    /// <param name="y">World Y of the smoothed position.</param>
    bool TryGetSmoothed(int slot, out float x, out float y);
}

/// <summary>
///     Per-slot smoothed draw positions, chased toward the latest sampled marker position once per
///     rendered frame so markers glide between discrete pushes instead of stepping. Port of
///     <c>Playback2DViewport.AdvanceMarkers</c> (lines 648-699), verbatim, plus the discontinuity snap.
///     <para>
///         <b>Camera targeting stays on the RAW positions</b>, and so does level assignment — only the
///         drawn dot is smoothed (parity invariant 3). Extracted from the marker layer because the
///         vision solver needs the same positions for its cone apexes, and two copies of this state
///         would drift apart within a frame.
///     </para>
/// </summary>
public sealed class MarkerSmoother : ISmoothedPositionSource
{
    /// <summary>Exponential-decay rate; snappier than the camera's, so a dot never trails its ring.</summary>
    public const double LerpResponse = 16.0;

    /// <summary>Beyond this squared distance a move is a teleport, not motion — snap, never glide.</summary>
    public const float SnapDistanceSq = 250f * 250f;

    /// <summary>Within this squared distance the glide is over; snap and stop asking for frames.</summary>
    public const float SettleEpsilonSq = 0.5f * 0.5f;

    private readonly HashSet<int> _liveSlots = new(16);
    private readonly List<int> _pruneScratch = new(8);
    private readonly Dictionary<int, (float X, float Y)> _smoothed = new(16);
    private Scene2DFrame? _lastFrame;
    private SceneTime _lastTime;
    private bool _seenAny;

    /// <summary>How many slots are currently tracked.</summary>
    public int Count => _smoothed.Count;

    /// <inheritdoc />
    public bool TryGetSmoothed(int slot, out float x, out float y)
    {
        if (_smoothed.TryGetValue(slot, out (float X, float Y) p))
        {
            x = p.X;
            y = p.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    /// <summary>The smoothed draw position for a slot, or null when the slot is not tracked.</summary>
    /// <param name="slot">Roster slot.</param>
    public (float X, float Y)? Position(int slot) =>
        _smoothed.TryGetValue(slot, out (float X, float Y) p) ? p : null;

    /// <summary>
    ///     Advances the smoothing for one frame, <b>at most once per advance cycle</b>.
    ///     <para>
    ///         Two layers depend on these positions — the markers that draw them and the vision solver
    ///         whose cone apexes sit on them — and the compositor advances layers in draw order, which
    ///         puts vision (30) before markers (40). Rather than invent an advance-order concept for one
    ///         case, whichever layer runs first drives the smoothing and the second call is a no-op.
    ///     </para>
    ///     <para>
    ///         The de-duplication key is the frame <i>reference</i> plus the whole <see cref="SceneTime" />
    ///         value, so a re-render of the same frame at a new <c>DeltaSeconds</c> — which is exactly what
    ///         a glide is — still advances. Two consecutive frames with a bit-identical
    ///         <c>DeltaSeconds</c> would be skipped; that would cost one frame of glide and cannot
    ///         happen from a real animation-frame timestamp.
    ///     </para>
    /// </summary>
    /// <param name="time">The injected clock.</param>
    /// <param name="frame">The frame being advanced to.</param>
    /// <returns>True while any marker is still gliding.</returns>
    public bool AdvanceOnce(in SceneTime time, Scene2DFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_seenAny && ReferenceEquals(_lastFrame, frame) && _lastTime.Equals(time))
        {
            return AnyMoving;
        }

        _lastFrame = frame;
        _lastTime = time;
        _seenAny = true;
        AnyMoving = Advance(frame.Markers, time.DeltaSeconds, time.IsDiscontinuity);
        return AnyMoving;
    }

    /// <summary>Whether the last advance left anything still gliding.</summary>
    public bool AnyMoving { get; private set; }

    /// <summary>
    ///     The smoothing step itself. Public and un-deduplicated so a test can drive it with a known
    ///     <paramref name="dt" /> — the pre-v2 <c>AdvanceMarkers</c> was <c>internal</c> for the same
    ///     reason, and <c>Playback2DInterpolationTests</c> is ported onto this signature.
    /// </summary>
    /// <param name="markers">The frame's markers.</param>
    /// <param name="dt">Seconds since the previous rendered frame.</param>
    /// <param name="isDiscontinuity">True after a seek — snap everything rather than glide across the map.</param>
    /// <returns>True while any marker is still gliding.</returns>
    public bool Advance(IReadOnlyList<PlayerMarker> markers, double dt, bool isDiscontinuity = false)
    {
        ArgumentNullException.ThrowIfNull(markers);

        float t = (float)(1 - Math.Exp(-LerpResponse * dt));
        bool anyMoving = false;
        _liveSlots.Clear();

        for (int i = 0; i < markers.Count; i++)
        {
            PlayerMarker m = markers[i];
            _liveSlots.Add(m.Slot);
            float tx = m.WorldX, ty = m.WorldY;

            if (isDiscontinuity)
            {
                // A seek: every dot teleports. This is a superset of the distance rule below — a short
                // seek can move a player less than the teleport threshold, and gliding across that gap
                // draws motion that never happened.
                _smoothed[m.Slot] = (tx, ty);
                continue;
            }

            if (!_smoothed.TryGetValue(m.Slot, out (float X, float Y) cur))
            {
                _smoothed[m.Slot] = (tx, ty); // first appearance — start ON the player, never glide from 0,0
                continue;
            }

            float dx = tx - cur.X, dy = ty - cur.Y;
            float distSq = dx * dx + dy * dy;

            if (distSq >= SnapDistanceSq || distSq <= SettleEpsilonSq)
            {
                _smoothed[m.Slot] = (tx, ty);
                continue;
            }

            _smoothed[m.Slot] = (cur.X + dx * t, cur.Y + dy * t);
            anyMoving = true;
        }

        // Prune slots that left (disconnect / never re-emitted) so a re-join does not glide from a
        // stale spot. Guarded on the count so the common frame never enumerates the keys.
        if (_smoothed.Count != _liveSlots.Count)
        {
            _pruneScratch.Clear();
            foreach (int slot in _smoothed.Keys)
            {
                if (!_liveSlots.Contains(slot))
                {
                    _pruneScratch.Add(slot);
                }
            }

            for (int i = 0; i < _pruneScratch.Count; i++)
            {
                _smoothed.Remove(_pruneScratch[i]);
            }
        }

        return anyMoving;
    }

    /// <summary>Forgets every tracked slot. For a demo swap or a detach.</summary>
    public void Clear()
    {
        _smoothed.Clear();
        _seenAny = false;
        _lastFrame = null;
        AnyMoving = false;
    }
}
