namespace DemoViewer.NET.Playback2D.Core.Keyframes;

/// <summary>
///     One slot's position over the strat clock: keyframes sorted by tick, a hold and an interpolation per
///     keyframe (step-authoring.md §3.3). Immutable; an edit builds a new track and hands it to
///     <see cref="TokenTrackSet.Replace" />.
///     <para>
///         <b>Sampling is pure in the tick.</b> Scrubbing backwards equals scrubbing forwards and an export
///         at 20 fps agrees with one at 50 fps, the property <c>TimeEnvelope.OpacityAt</c> was built for.
///         For a tick <c>t</c> between keyframe <c>k</c> (tick <c>a</c>, hold <c>h</c>) and <c>k+1</c>
///         (tick <c>b</c>):
///     </para>
///     <list type="bullet">
///         <item>before the first keyframe there is no sample, so the token is not drawn;</item>
///         <item>at or after the last keyframe the last keyframe holds forever;</item>
///         <item>inside the hold, or on a <see cref="TokenInterpolation.Hold" /> segment, the token stays at <c>k</c>;</item>
///         <item>
///             otherwise <c>u = (t - a - h) / (b - a - h)</c> lerps X and Y, turns the yaw along the shorter
///             arc, and takes the level from <c>k</c> while <c>u &lt; 0.5</c> and from <c>k+1</c> after;
///         </item>
///         <item>a hold of <c>b - a</c> or more degenerates to a jump at <c>b</c>.</item>
///     </list>
///     <para>
///         <b>The level snaps at the segment midpoint</b> (decision 8) rather than following the lerp: a
///         lerped Z would spend half the move on a floor pane whose radar the token is not on. The snap
///         is what <c>MarkerSmoother</c>'s level-crossing snap expects, so no dot streaks across the wrong
///         floor. An author who wants the stairs adds a step at the stairs.
///     </para>
///     <para>
///         <b>A slot with no entry at a step is stationary through it</b> (decision 1). That is
///         <see cref="TokenTrackBuilder" />'s rule, not this type's: this type interpolates between
///         whatever keyframes it is given, and the builder gives it ones that hold still across a gap.
///     </para>
/// </summary>
public sealed class TokenTrack
{
    private readonly int[] _holds;
    private readonly TokenKeyframe[] _keyframes;
    private readonly TokenInterpolation[] _segments;

    /// <summary>Creates a track.</summary>
    /// <param name="slot">One of <see cref="TokenSlots.All" />.</param>
    /// <param name="keyframes">Sorted by tick, ticks distinct. May be empty: a track that never draws.</param>
    /// <param name="holdTicks">Per keyframe, how long it holds before moving; null is all zero.</param>
    /// <param name="segments">Per keyframe, the motion toward the next; null is all linear.</param>
    /// <exception cref="ArgumentException">
    ///     An unknown slot, unsorted or repeated ticks, a negative hold, or a list whose length is not the
    ///     keyframe count. Two steps that share a tick and both place the slot are the builder's to fold:
    ///     the later one wins, in authoring order.
    /// </exception>
    public TokenTrack(string slot, IReadOnlyList<TokenKeyframe> keyframes,
        IReadOnlyList<int>? holdTicks = null, IReadOnlyList<TokenInterpolation>? segments = null)
    {
        ArgumentNullException.ThrowIfNull(keyframes);

        if (!TokenSlots.IsKnown(slot))
        {
            throw new ArgumentException($"'{slot}' is not a token slot (A to E, O1 to O5).", nameof(slot));
        }

        int count = keyframes.Count;
        if (holdTicks is not null && holdTicks.Count != count)
        {
            throw new ArgumentException("One hold per keyframe.", nameof(holdTicks));
        }

        if (segments is not null && segments.Count != count)
        {
            throw new ArgumentException("One segment per keyframe.", nameof(segments));
        }

        _keyframes = new TokenKeyframe[count];
        _holds = new int[count];
        _segments = new TokenInterpolation[count];

        for (int i = 0; i < count; i++)
        {
            TokenKeyframe keyframe = keyframes[i];
            if (i > 0 && keyframe.Tick <= _keyframes[i - 1].Tick)
            {
                throw new ArgumentException(
                    $"Keyframe {i} at tick {keyframe.Tick} does not come after tick {_keyframes[i - 1].Tick}.",
                    nameof(keyframes));
            }

            int hold = holdTicks?[i] ?? 0;
            if (hold < 0)
            {
                throw new ArgumentException($"Hold {i} is negative.", nameof(holdTicks));
            }

            _keyframes[i] = keyframe;
            _holds[i] = hold;
            _segments[i] = segments?[i] ?? TokenInterpolation.Linear;
        }

        Slot = slot;
    }

    /// <summary>The slot, <c>A..E</c> or <c>O1..O5</c>.</summary>
    public string Slot { get; }

    /// <summary>Whether this is an opponent token.</summary>
    public bool IsOpponent => TokenSlots.IsOpponent(Slot);

    /// <summary>The keyframes, sorted by tick with distinct ticks.</summary>
    public IReadOnlyList<TokenKeyframe> Keyframes => _keyframes;

    /// <summary>Per keyframe: how many ticks it holds before moving toward the next.</summary>
    public IReadOnlyList<int> HoldTicks => _holds;

    /// <summary>Per keyframe: the motion toward the NEXT keyframe. The last entry is never read.</summary>
    public IReadOnlyList<TokenInterpolation> Segments => _segments;

    /// <summary>
    ///     The position at a tick. Pure; false before the first keyframe, and always false on an empty
    ///     track.
    /// </summary>
    /// <param name="tick">A strat frame-clock tick.</param>
    /// <param name="sample">The position, with <c>Tick</c> set to <paramref name="tick" />.</param>
    public bool TrySample(int tick, out TokenKeyframe sample)
    {
        int count = _keyframes.Length;
        if (count == 0 || tick < _keyframes[0].Tick)
        {
            sample = default;
            return false;
        }

        int k = IndexAtOrBefore(tick);
        TokenKeyframe here = _keyframes[k];

        if (k == count - 1 || _segments[k] == TokenInterpolation.Hold)
        {
            sample = here with { Tick = tick };
            return true;
        }

        TokenKeyframe to = _keyframes[k + 1];

        // Long arithmetic: a hold near int.MaxValue has to degenerate to a jump, not wrap into a motion.
        long moveStart = (long)here.Tick + _holds[k];
        if (tick < moveStart || moveStart >= to.Tick)
        {
            sample = here with { Tick = tick };
            return true;
        }

        double u = (tick - moveStart) / (double)(to.Tick - moveStart);
        sample = new TokenKeyframe(
            tick,
            Lerp(here.X, to.X, u),
            Lerp(here.Y, to.Y, u),
            u < 0.5 ? here.LevelMinZ : to.LevelMinZ,
            LerpYaw(here.YawDegrees, to.YawDegrees, u));
        return true;
    }

    /// <summary>
    ///     A yaw part-way along the shorter arc from <paramref name="from" /> to <paramref name="to" />.
    ///     Not wrapped into a range: the result stays within 180 of <paramref name="from" />, and the
    ///     marker reads it through a sine and a cosine, where 360 and 0 are the same heading.
    /// </summary>
    /// <param name="from">Starting yaw, degrees.</param>
    /// <param name="to">Ending yaw, degrees.</param>
    /// <param name="u">0 at <paramref name="from" />, 1 at <paramref name="to" />.</param>
    internal static float LerpYaw(float from, float to, double u)
    {
        double delta = (((to - (double)from) % 360) + 540) % 360 - 180;
        return (float)(from + delta * u);
    }

    private static float Lerp(float a, float b, double u) => (float)(a + (b - (double)a) * u);

    // The last keyframe at or before the tick; the caller has already ruled out "before the first".
    private int IndexAtOrBefore(int tick)
    {
        int lo = 0, hi = _keyframes.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_keyframes[mid].Tick <= tick)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }
}
