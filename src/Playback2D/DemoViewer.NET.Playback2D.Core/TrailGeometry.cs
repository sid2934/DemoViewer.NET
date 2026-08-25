#region

using DemoViewer.NET.Playback2D.Core.Compositing;

#endregion

namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     Splits a grenade's flight path into the contiguous point runs that belong on one level.
///     Port of <c>Playback2DViewport.FloorSegmentRuns</c> (lines 1298-1333), verbatim.
///     <para>
///         <b>A segment belongs to a level if EITHER endpoint maps to it</b> (parity invariant 4). That
///         is deliberate over-draw: the one segment that crosses between floors is drawn on both bands,
///         so a Nuke upper→lower throw reads as a continuous arc rather than two lines that stop short
///         of each other.
///     </para>
/// </summary>
public static class TrailGeometry
{
    /// <summary>
    ///     Fills <paramref name="into" /> with the runs belonging to this pane. <b>Allocation-free</b>
    ///     once the list has grown — the pre-v2 version allocated a <c>List</c> per trail per pane per
    ///     frame plus a closure for the floor lookup (plan §4 T15 items 4 and 5).
    /// </summary>
    /// <param name="points">The flight path, oldest first.</param>
    /// <param name="ctx">The pane being drawn; supplies the level test.</param>
    /// <param name="into">Destination, cleared first. Each entry is an inclusive index range.</param>
    public static void FloorSegmentRuns(IReadOnlyList<GrenadeTrailPoint> points,
        in SceneRenderContext ctx, List<(int Start, int End)> into)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();
        if (points.Count < 2)
        {
            return;
        }

        bool single = ctx.IsSingleLevel;
        int runStart = -1;

        for (int i = 1; i < points.Count; i++)
        {
            bool onLevel = single
                           || ctx.BelongsHere(points[i - 1].Z)
                           || ctx.BelongsHere(points[i].Z);
            if (onLevel)
            {
                if (runStart < 0)
                {
                    runStart = i - 1;
                }
            }
            else if (runStart >= 0)
            {
                into.Add((runStart, i - 1));
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            into.Add((runStart, points.Count - 1));
        }
    }

    /// <summary>
    ///     The delegate-driven form, kept for tests that want to drive the level lookup directly. The
    ///     render path uses the <see cref="SceneRenderContext" /> overload, which needs no closure.
    /// </summary>
    /// <param name="points">The flight path, oldest first.</param>
    /// <param name="levelIndex">Target level index, or &lt; 0 for "every point" (the single-level render).</param>
    /// <param name="levelOf">Maps a world Z to a level index.</param>
    /// <param name="into">Destination, cleared first.</param>
    public static void FloorSegmentRuns(IReadOnlyList<GrenadeTrailPoint> points, int levelIndex,
        Func<double, int> levelOf, List<(int Start, int End)> into)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(levelOf);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();
        if (points.Count < 2)
        {
            return;
        }

        int runStart = -1;
        for (int i = 1; i < points.Count; i++)
        {
            bool onLevel = levelIndex < 0
                           || levelOf(points[i - 1].Z) == levelIndex
                           || levelOf(points[i].Z) == levelIndex;
            if (onLevel)
            {
                if (runStart < 0)
                {
                    runStart = i - 1;
                }
            }
            else if (runStart >= 0)
            {
                into.Add((runStart, i - 1));
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            into.Add((runStart, points.Count - 1));
        }
    }

    /// <summary>The allocating form of the delegate overload, for tests and one-shot callers.</summary>
    /// <param name="points">The flight path, oldest first.</param>
    /// <param name="levelIndex">Target level index, or &lt; 0 for "every point".</param>
    /// <param name="levelOf">Maps a world Z to a level index.</param>
    public static List<(int Start, int End)> FloorSegmentRuns(IReadOnlyList<GrenadeTrailPoint> points,
        int levelIndex, Func<double, int> levelOf)
    {
        List<(int Start, int End)> runs = [];
        FloorSegmentRuns(points, levelIndex, levelOf, runs);
        return runs;
    }
}
