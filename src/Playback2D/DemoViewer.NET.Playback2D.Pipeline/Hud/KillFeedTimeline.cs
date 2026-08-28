#region

using DemoViewer.NET.Playback2D.Core;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Hud;

/// <summary>
///     The kill-feed <b>window</b> function: which of a demo's kills are on screen at a tick.
///     <para>
///         <b>One implementation, two consumers</b> (plan D5). The XAML feed in
///         <c>Playback2DTabViewModel</c> and the exported <c>KillFeedLayer</c> both call this, so design
///         risk 8 — the two feeds drifting apart — is not a thing that can happen to the row set. Only
///         the row <i>builder</i> stayed at the event source, because it reads
///         <c>IModuleContext.GetEventTimeline</c>, an Avalonia-referencing abstraction Pipeline cannot
///         consume.
///     </para>
///     <para>
///         Deliberately not a <c>KillFeedRow</c> declaration: that record is Core's (integrator
///         correction 3), already carried on <c>Scene2DFrame.KillFeed</c>.
///     </para>
/// </summary>
public static class KillFeedTimeline
{
    /// <summary>How long a kill stays visible, in game seconds.</summary>
    public const int DefaultWindowSeconds = 8;

    /// <summary>How many rows the feed shows at once.</summary>
    public const int DefaultMaxRows = 6;

    private static readonly Comparison<KillFeedRow> ByTick = static (a, b) => a.Tick.CompareTo(b.Tick);

    /// <summary>
    ///     Fills <paramref name="into" /> with the rows whose tick is in
    ///     <c>(nowTick − windowSeconds·tickRate, nowTick]</c>, sorted by tick, keeping the most recent
    ///     <paramref name="maxRows" />.
    ///     <para>
    ///         <b>The inclusive upper bound is load-bearing</b>: a kill AHEAD of the playhead must never
    ///         appear while paused or seeking, and a kill exactly ON the playhead must. The exclusive
    ///         lower bound is the matching half — a kill at exactly <c>lowTick</c> has just expired.
    ///     </para>
    ///     <para>
    ///         Allocation-free given a warm <paramref name="into" />: a linear pass over a few-hundred
    ///         element list, then an in-place sort of the small visible window (the source order is not
    ///         guaranteed to be by tick).
    ///     </para>
    /// </summary>
    /// <param name="all">Every kill in the demo, in any order.</param>
    /// <param name="nowTick">The playhead tick.</param>
    /// <param name="tickRate">Ticks per second; values below 1 are treated as 1.</param>
    /// <param name="into">Destination, cleared first.</param>
    /// <param name="windowSeconds">How long a row stays visible.</param>
    /// <param name="maxRows">Row ceiling.</param>
    public static void Window(IReadOnlyList<KillFeedRow> all, int nowTick, int tickRate,
        List<KillFeedRow> into, int windowSeconds = DefaultWindowSeconds, int maxRows = DefaultMaxRows)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        int lowTick = nowTick - windowSeconds * Math.Max(1, tickRate);
        for (int i = 0; i < all.Count; i++)
        {
            KillFeedRow row = all[i];
            if (row.Tick > lowTick && row.Tick <= nowTick)
            {
                into.Add(row);
            }
        }

        into.Sort(ByTick);

        int excess = into.Count - Math.Max(0, maxRows);
        if (excess > 0)
        {
            into.RemoveRange(0, excess);
        }
    }
}
