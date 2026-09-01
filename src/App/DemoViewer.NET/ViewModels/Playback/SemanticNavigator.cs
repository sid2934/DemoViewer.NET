#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace DemoViewer.NET.ViewModels.Playback;

/// <summary>
///     The shell-owned semantic-navigation service: the "boundary movement" counterpart to
///     <see cref="PlaybackController" />'s "position movement". Where the
///     controller knows where the clock is, this service knows where the <em>boundaries</em> are:
///     rounds, game-events (by name), and distinct ticks, and moves the controller to them.
///     <para>
///         Following the same move already made for the clock, the
///         boundary indices are precomputed <b>once</b> after parse (via <see cref="Build" />, drained
///         in the same place as <c>MainViewModel.BuildUnknownMessageCensus</c>) and every
///         <c>Next*</c>/<c>Prev*</c> is a binary search over the precomputed array from
///         <see cref="PlaybackController.CurrentFrameIndex" />, followed by
///         <see cref="PlaybackController.SeekToFrame" />. This replaces the per-press re-scans the
///         legacy <c>*Frame*</c> / <c>*Tick*</c> methods did across two view-models.
///     </para>
///     <para>
///         All boundary values are <b>frame indices</b> (positions in the parsed frame list), so the
///         movement contract matches the controller exactly ("frame index for
///         movement, tick shown as a read-only label"). The arrays are built by scanning each frame's
///         <see cref="DemoFrame.InnerMessages" /> for <see cref="GameEventMessage" />, the same source
///         the legacy navigation and the demo-derived <c>GameEventFilters</c> use, so the precompute
///         and the filter share one source of truth.
///     </para>
/// </summary>
public sealed class SemanticNavigator
{
    private readonly PlaybackController _controller;

    // Empty until Build() runs. All three are sorted ascending and may contain duplicates removed.
    private int[] _roundBoundaryFrames = [];
    private int[] _tickBoundaryFrames = [];

    /// <summary>Initializes a new <see cref="SemanticNavigator" /> bound to the shared clock.</summary>
    public SemanticNavigator(PlaybackController controller) => _controller = controller;

    /// <summary>
    ///     Frame indices whose frame contains a <c>round_*</c> game event. Sorted ascending,
    ///     de-duplicated. Empty until <see cref="Build" /> runs.
    /// </summary>
    public IReadOnlyList<int> RoundBoundaryFrames => _roundBoundaryFrames;

    /// <summary>
    ///     For each game-event name present in the demo, the sorted (de-duplicated) frame indices where
    ///     it occurs. This is the demo-derived event set: the same data <c>GameEventFilters</c> is
    ///     populated from. Empty until <see cref="Build" /> runs.
    /// </summary>
    public IReadOnlyDictionary<string, int[]> EventBoundaryFramesByName { get; private set; } = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     First frame index of each distinct <see cref="DemoFrame.ServerTick" />. Sorted ascending.
    ///     Empty until <see cref="Build" /> runs. Computed by the parser-tier
    ///     <see cref="TickBoundaries.FrameIndices" />: the same precompute <c>TickMapper</c> consumes.
    /// </summary>
    public IReadOnlyList<int> TickBoundaryFrames => _tickBoundaryFrames;

    /// <summary>
    ///     Precomputes all boundary indices from the parsed frame list. Called once after parse
    ///     (drained alongside <c>BuildUnknownMessageCensus</c>). A single linear pass over the frames
    ///     fills all three structures.
    /// </summary>
    public void Build(IReadOnlyList<DemoFrame> frames)
    {
        List<int> roundFrames = new();
        Dictionary<string, List<int>> eventFrames = new(StringComparer.OrdinalIgnoreCase);

        // Tick boundaries are a parser-tier fact (TickMapper consumes the same array): one shared
        // implementation, so a live-sync seek and a NextTick press can never disagree about where a
        // tick starts. Its extra linear pass is noise next to the inner-message scan below.
        int[] tickFrames = TickBoundaries.FrameIndices(frames);

        for (int i = 0; i < frames.Count; i++)
        {
            DemoFrame frame = frames[i];

            // Scan inner messages for game events: same source as the legacy navigation and the
            // demo-derived GameEventFilters. A frame can be both a round boundary and an event
            // boundary (and an event can appear multiple times in one frame, de-duped per name below).
            bool roundThisFrame = false;
            foreach (NetMessage msg in frame.InnerMessages)
            {
                if (msg is not GameEventMessage gem)
                {
                    continue;
                }

                string name = gem.DecodedEvent.Name;

                if (!eventFrames.TryGetValue(name, out List<int>? list))
                {
                    eventFrames[name] = list = new List<int>();
                }

                // De-dupe within a frame: only append when this frame index isn't already the tail.
                if (list.Count == 0 || list[^1] != i)
                {
                    list.Add(i);
                }

                if (!roundThisFrame && name.StartsWith("round_", StringComparison.OrdinalIgnoreCase))
                {
                    roundFrames.Add(i);
                    roundThisFrame = true;
                }
            }
        }

        _roundBoundaryFrames = roundFrames.ToArray();
        _tickBoundaryFrames = tickFrames;
        EventBoundaryFramesByName = eventFrames.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Clears all precomputed boundaries (demo unload / reparse).</summary>
    public void Reset()
    {
        _roundBoundaryFrames = [];
        _tickBoundaryFrames = [];
        EventBoundaryFramesByName =
            new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
    }

    // ── Round ─────────────────────────────────────────────────────────────────

    /// <summary>Seeks to the next frame (strictly after the current one) that contains a round event.</summary>
    public void NextRound() => SeekNext(_roundBoundaryFrames);

    /// <summary>Seeks to the previous frame (strictly before the current one) that contains a round event.</summary>
    public void PrevRound() => SeekPrev(_roundBoundaryFrames);

    // ── Tick ──────────────────────────────────────────────────────────────────

    /// <summary>Seeks to the first frame of the next distinct tick.</summary>
    public void NextTick() => SeekNext(_tickBoundaryFrames);

    /// <summary>
    ///     Seeks to the first frame of the PREVIOUS distinct tick. Unlike round/event boundaries (which
    ///     are discrete target frames the cursor is either exactly on or not), tick boundaries are
    ///     <em>group starts</em> and the current frame can sit in the middle of a group. So we take the
    ///     floor boundary (the current group's own start) and step to the boundary before it. This
    ///     moves to a strictly earlier tick from anywhere inside the current group, matching the legacy
    ///     <c>PreviousFrameByTick</c> "first frame with a different ServerTick, going backwards" intent.
    ///     (Deviation noted: lands on the previous group's START frame, not its last frame as legacy
    ///     did. The symmetric, strip-friendly choice.)
    /// </summary>
    public void PrevTick()
    {
        // Floor index: largest boundary <= current (the current group's start). UpperBound gives the
        // first boundary strictly > current, so floor = that - 1.
        int floor = UpperBound(_tickBoundaryFrames, _controller.CurrentFrameIndex) - 1;
        int target = floor - 1;
        if (target >= 0)
        {
            _controller.SeekToFrame(_tickBoundaryFrames[target]);
        }
    }

    // ── Game event (filter-aware) ──────────────────────────────────────────────

    /// <summary>
    ///     Seeks to the next frame containing a game event whose name is in <paramref name="filter" />.
    ///     When <paramref name="filter" /> is null or empty, matches ANY game event (the "deselect-all
    ///     = match any" convenience preserved from the legacy navigation). The union of the selected
    ///     event names' precomputed index arrays is searched for the first boundary strictly after
    ///     the current frame.
    /// </summary>
    public void NextEvent(IReadOnlyCollection<string>? filter)
    {
        int from = _controller.CurrentFrameIndex;
        int best = int.MaxValue;
        foreach (int[] arr in SelectArrays(filter))
        {
            int idx = UpperBound(arr, from);
            if (idx < arr.Length && arr[idx] < best)
            {
                best = arr[idx];
            }
        }

        if (best != int.MaxValue)
        {
            _controller.SeekToFrame(best);
        }
    }

    /// <summary>
    ///     Seeks to the previous frame containing a game event whose name is in
    ///     <paramref name="filter" /> (null/empty = match any). The union of the selected event names'
    ///     index arrays is searched for the last boundary strictly before the current frame.
    /// </summary>
    public void PrevEvent(IReadOnlyCollection<string>? filter)
    {
        int from = _controller.CurrentFrameIndex;
        int best = int.MinValue;
        foreach (int[] arr in SelectArrays(filter))
        {
            int idx = LowerBound(arr, from) - 1;
            if (idx >= 0 && arr[idx] > best)
            {
                best = arr[idx];
            }
        }

        if (best != int.MinValue)
        {
            _controller.SeekToFrame(best);
        }
    }

    // ── Internals ───────────────────────────────────────────────────────────────

    // Selects the per-name index arrays the filter covers. Null/empty filter = ALL arrays ("match any").
    private IEnumerable<int[]> SelectArrays(IReadOnlyCollection<string>? filter)
    {
        if (filter is null || filter.Count == 0)
        {
            return EventBoundaryFramesByName.Values;
        }

        List<int[]> result = new(filter.Count);
        foreach (string name in filter)
        {
            if (EventBoundaryFramesByName.TryGetValue(name, out int[]? arr))
            {
                result.Add(arr);
            }
        }

        return result;
    }

    private void SeekNext(int[] boundaries)
    {
        int idx = UpperBound(boundaries, _controller.CurrentFrameIndex);
        if (idx < boundaries.Length)
        {
            _controller.SeekToFrame(boundaries[idx]);
        }
    }

    private void SeekPrev(int[] boundaries)
    {
        int idx = LowerBound(boundaries, _controller.CurrentFrameIndex) - 1;
        if (idx >= 0)
        {
            _controller.SeekToFrame(boundaries[idx]);
        }
    }

    // First index in the sorted array whose value is strictly greater than `value` (std upper_bound).
    private static int UpperBound(int[] sorted, int value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo >> 1);
            if (sorted[mid] <= value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    // First index in the sorted array whose value is >= `value` (std lower_bound).
    private static int LowerBound(int[] sorted, int value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo >> 1);
            if (sorted[mid] < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
