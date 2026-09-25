namespace DemoViewer.NET.Playback2D.Core.Keyframes;

/// <summary>
///     The ten tokens' tracks, at most one per slot, kept in <see cref="TokenSlots.All" /> order so a
///     sample always lists <c>A..E</c> before <c>O1..O5</c> (step-authoring.md §3.3).
///     <para>
///         <b>Not thread-safe.</b> The canvas edits and samples it on the UI thread; an export takes its
///         own set built from the document, never this one.
///     </para>
///     <para>
///         <see cref="Replace" /> is how both an edit and an undo reach the canvas: the strat session owns
///         the one undo stack, and the set only ever hears the resulting track (correction 18).
///     </para>
/// </summary>
public sealed class TokenTrackSet
{
    private readonly List<TokenTrack> _tracks = [];

    /// <summary>Creates an empty set.</summary>
    public TokenTrackSet()
    {
    }

    /// <summary>Creates a set from tracks.</summary>
    /// <param name="tracks">At most one per slot.</param>
    /// <exception cref="ArgumentException">Two tracks for one slot.</exception>
    public TokenTrackSet(IEnumerable<TokenTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        foreach (TokenTrack track in tracks)
        {
            ArgumentNullException.ThrowIfNull(track, nameof(tracks));
            if (IndexOf(track.Slot) >= 0)
            {
                throw new ArgumentException($"Two tracks for slot {track.Slot}.", nameof(tracks));
            }

            Insert(track);
        }
    }

    /// <summary>The tracks, in slot order.</summary>
    public IReadOnlyList<TokenTrack> Tracks => _tracks;

    /// <summary>Bumped on every change, so a consumer can tell a republish is needed without diffing.</summary>
    public int Version { get; private set; }

    /// <summary>The track for a slot, or null.</summary>
    /// <param name="slot">The slot.</param>
    public TokenTrack? Get(string slot)
    {
        int index = IndexOf(slot);
        return index >= 0 ? _tracks[index] : null;
    }

    /// <summary>Puts a track in, replacing the slot's current one if it has one.</summary>
    /// <param name="track">The new track for its slot.</param>
    public void Replace(TokenTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        int index = IndexOf(track.Slot);
        if (index >= 0)
        {
            _tracks[index] = track;
        }
        else
        {
            Insert(track);
        }

        Bump();
    }

    /// <summary>Takes a slot's track out. Returns false, and changes nothing, when it had none.</summary>
    /// <param name="slot">The slot.</param>
    public bool Remove(string slot)
    {
        int index = IndexOf(slot);
        if (index < 0)
        {
            return false;
        }

        _tracks.RemoveAt(index);
        Bump();
        return true;
    }

    /// <summary>
    ///     Fills <paramref name="into" /> with one entry per track that has a sample at the tick, in slot
    ///     order. Allocation-free: the list is cleared and refilled, so the caller pools it.
    /// </summary>
    /// <param name="tick">A strat frame-clock tick.</param>
    /// <param name="into">Cleared, then filled.</param>
    /// <param name="includeOpponents">
    ///     False leaves <c>O1..O5</c> out, which is how the canvas's <c>showOpponents</c> flag hides them
    ///     without deleting them.
    /// </param>
    public void Sample(int tick, List<TokenSample> into, bool includeOpponents = true)
    {
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();
        for (int i = 0; i < _tracks.Count; i++)
        {
            TokenTrack track = _tracks[i];
            if (!includeOpponents && track.IsOpponent)
            {
                continue;
            }

            if (track.TrySample(tick, out TokenKeyframe position))
            {
                into.Add(new TokenSample(track.Slot, position));
            }
        }
    }

    private void Bump() => Version++;

    private int IndexOf(string slot)
    {
        for (int i = 0; i < _tracks.Count; i++)
        {
            if (string.Equals(_tracks[i].Slot, slot, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    // Keeps slot order on insert, so Sample never sorts.
    private void Insert(TokenTrack track)
    {
        int order = TokenSlots.OrderOf(track.Slot);
        int at = 0;
        while (at < _tracks.Count && TokenSlots.OrderOf(_tracks[at].Slot) < order)
        {
            at++;
        }

        _tracks.Insert(at, track);
    }
}
