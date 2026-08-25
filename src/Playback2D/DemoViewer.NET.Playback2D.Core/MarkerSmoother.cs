#region

using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

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

    /// <summary>How many slots are currently tracked.</summary>
    public int Count => _smoothed.Count;

    /// <summary>
    ///     The level-crossing source, when the owner keeps one. A slot that changed floor on this frame
    ///     <b>snaps</b> instead of gliding — the same code path as the teleport rule below, deliberately
    ///     rather than a second snap mechanism, so there is one answer to "why did that dot jump".
    ///     <para>
    ///         Null leaves the smoothing exactly as B1 shipped it, which is what every golden and the
    ///         determinism gate were captured against.
    ///     </para>
    /// </summary>
    public LevelCrossingTracker? LevelCrossings { get; set; }

    /// <summary>Whether the last <see cref="Advance" /> left anything still gliding.</summary>
    public bool AnyMoving { get; private set; }

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
    ///     The smoothing step.
    ///     <para>
    ///         <b>Exactly one owner may call this per frame</b>, and that owner is <c>MarkerLayer</c>.
    ///         Two calls in one frame step every dot twice and it glides at double speed. The vision
    ///         solver shares these positions but only <i>reads</i> them, which costs it a one-frame lag
    ///         on the cone apexes during a glide — a couple of pixels at most, and only while something
    ///         is moving. That is much cheaper than the alternative: an earlier draft de-duplicated on
    ///         <c>(frame, time)</c> so either layer could drive it, and a constant frame delta — which
    ///         is exactly what a headless render timer produces — made every call after the first a
    ///         no-op that returned a stale "still moving", pinning the self-terminating render loop
    ///         permanently on.
    ///     </para>
    ///     <para>
    ///         Public and un-deduplicated so a test can drive it with a known <paramref name="dt" />:
    ///         the pre-v2 <c>AdvanceMarkers</c> was <c>internal</c> for the same reason, and
    ///         <c>Playback2DInterpolationTests</c> is ported onto this signature.
    ///     </para>
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
        LevelCrossingTracker? crossings = LevelCrossings;
        _liveSlots.Clear();

        for (int i = 0; i < markers.Count; i++)
        {
            PlayerMarker m = markers[i];
            _liveSlots.Add(m.Slot);
            float tx = m.WorldX, ty = m.WorldY;

            if (isDiscontinuity || crossings?.Crossed(m.Slot) == true)
            {
                // A seek: every dot teleports. This is a superset of the distance rule below — a short
                // seek can move a player less than the teleport threshold, and gliding across that gap
                // draws motion that never happened.
                //
                // A level crossing is the same shape of event on one slot: the player left this floor,
                // and a dot that glides to its new position paints a streak across a map it never
                // walked (design §5.3).
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

        AnyMoving = anyMoving;
        return anyMoving;
    }

    /// <summary>Forgets every tracked slot. For a demo swap or a detach.</summary>
    public void Clear()
    {
        _smoothed.Clear();
        AnyMoving = false;
    }
}
