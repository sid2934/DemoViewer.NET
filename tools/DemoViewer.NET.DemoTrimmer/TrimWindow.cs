#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace DemoViewer.NET.DemoTrimmer;

/// <summary>
///     The contiguous frame range a trim retains, plus the round boundaries it spans.
/// </summary>
/// <param name="EntryIndex">
///     First frame of the retained stream. For a checkpoint-entry variant this is a
///     <c>DEM_FullPacket</c>; for a contiguous variant it is 0.
/// </param>
/// <param name="EndIndex">Last retained frame index, inclusive.</param>
/// <param name="StartTick">Frame tick at <see cref="EntryIndex" />.</param>
/// <param name="EndTick">Frame tick at <see cref="EndIndex" />.</param>
/// <param name="RoundsKept">Number of complete rounds in the window.</param>
/// <param name="BoundaryTicks">Tick of each retained round boundary, plus the cut boundary last.</param>
/// <param name="EnteredAtCheckpoint">True when <see cref="EntryIndex" /> is a <c>DEM_FullPacket</c>.</param>
internal sealed record TrimWindow(
    int EntryIndex,
    int EndIndex,
    int StartTick,
    int EndTick,
    int RoundsKept,
    IReadOnlyList<int> BoundaryTicks,
    bool EnteredAtCheckpoint);

/// <summary>Chooses the retained window from a parsed demo.</summary>
internal static class WindowSelector
{
    /// <summary>
    ///     Default round boundary. Matches the original feasibility measurement
    ///     ("cutting at the 4th
    ///     <c>round_freeze_end</c>"), so the size ladder stays directly comparable.
    /// </summary>
    public const string DefaultBoundaryEvent = "round_freeze_end";

    /// <summary>
    ///     Selects the window covering <paramref name="rounds" /> complete rounds.
    ///     <para>
    ///         The window runs from boundary[<paramref name="skipBoundaries" />] to the frame just
    ///         before boundary[<paramref name="skipBoundaries" /> + <paramref name="rounds" />].
    ///         <paramref name="skipBoundaries" /> exists because matchmaking demos fire the same
    ///         boundary event during warmup — a trim whose retained rounds are warmup is useless for
    ///         the tour (no kills, no economy) even if it parses perfectly.
    ///     </para>
    /// </summary>
    public static TrimWindow Select(
        ParsedDemo demo, int rounds, bool enterAtCheckpoint,
        string boundaryEvent = DefaultBoundaryEvent, int skipBoundaries = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rounds, 1);

        List<GameEvent> boundaries = demo.AllGameEvents
            .Where(e => string.Equals(e.Name, boundaryEvent, StringComparison.Ordinal))
            .ToList();

        int firstIdx = skipBoundaries;
        int cutIdx = skipBoundaries + rounds;
        if (boundaries.Count <= cutIdx)
        {
            throw new InvalidOperationException(
                $"Demo has {boundaries.Count} '{boundaryEvent}' events; need at least {cutIdx + 1} " +
                $"to keep {rounds} round(s) after skipping {skipBoundaries}.");
        }

        // Cut at the frame immediately before the next round's boundary event.
        int endIndex = Math.Max(0, boundaries[cutIdx].FrameNumber - 1);

        int entryIndex = enterAtCheckpoint
            ? LastFullPacketAtOrBefore(demo.Frames, boundaries[firstIdx].FrameNumber)
            : 0;

        if (entryIndex < 0)
        {
            throw new InvalidOperationException(
                "No DEM_FullPacket checkpoint at or before the first retained round boundary — " +
                "checkpoint entry is impossible for this demo; use the contiguous variant.");
        }

        List<int> boundaryTicks = [];
        for (int i = firstIdx; i <= cutIdx; i++)
        {
            boundaryTicks.Add(boundaries[i].GameTick);
        }

        return new TrimWindow(
            entryIndex, endIndex,
            demo.Frames[entryIndex].ServerTick, demo.Frames[endIndex].ServerTick,
            rounds, boundaryTicks, enterAtCheckpoint);
    }

    /// <summary>
    ///     Index of the last <c>DEM_FullPacket</c> at or before <paramref name="frameIndex" />, or -1.
    ///     Entity state is delta-encoded, so this checkpoint (a complete state snapshot) is the only
    ///     legal mid-stream entry point.
    /// </summary>
    public static int LastFullPacketAtOrBefore(IReadOnlyList<DemoFrame> frames, int frameIndex)
    {
        for (int i = Math.Min(frameIndex, frames.Count - 1); i >= 0; i--)
        {
            if (string.Equals(frames[i].Command, "DEM_FullPacket", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
