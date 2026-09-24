#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>What one walk produced: the rounds and the sparse cloud detonations are placed against.</summary>
/// <param name="Rounds">One occupancy per live round, in round order.</param>
/// <param name="Cloud">Every 25th alive placed sample of the same walk.</param>
public sealed record OccupancyBuild(IReadOnlyList<RoundOccupancy> Rounds, DetonationCloud Cloud);

/// <summary>
///     Who is on which side and when each of them died, read from one Round Facts row: the side
///     resolver and the alive filter both sources share (integrator correction 11). The sample the
///     walk yields carries neither, and GOTV keeps producing positions for a dead player's pawn for
///     the rest of the round (166 of 169 dead slots on de_nuke, suggested-tags.md §2.1), so a slot is
///     alive until its first kill tick and counts for the side its row seats it on.
/// </summary>
public static class OccupancyRoster
{
    /// <summary>Every seated slot and its side: 3 for the CT slots, 2 for the T slots.</summary>
    /// <param name="round">One Round Facts row.</param>
    public static Dictionary<int, int> SideBySlot(RoundFacts round)
    {
        ArgumentNullException.ThrowIfNull(round);
        Dictionary<int, int> sides = [];
        foreach (int slot in round.Ct.Slots)
        {
            sides[slot] = 3;
        }

        foreach (int slot in round.T.Slots)
        {
            sides[slot] = 2;
        }

        return sides;
    }

    /// <summary>
    ///     The first kill tick per victim. A later kill of the same slot in one round cannot happen and
    ///     is ignored rather than resurrecting anyone, the Round Index's rule.
    /// </summary>
    /// <param name="round">One Round Facts row.</param>
    public static Dictionary<int, int> DeathTickBySlot(RoundFacts round)
    {
        ArgumentNullException.ThrowIfNull(round);
        Dictionary<int, int> deaths = [];
        foreach (KillStep kill in round.Kills)
        {
            if (kill.VictimSlot >= 0 && !deaths.ContainsKey(kill.VictimSlot))
            {
                deaths[kill.VictimSlot] = kill.Tick;
            }
        }

        return deaths;
    }

    /// <summary>The row's bomb with the site as a place name; null when nothing was planted.</summary>
    /// <param name="round">One Round Facts row.</param>
    public static RoundBomb? Bomb(RoundFacts round)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (round.PlantTick is not { } plant)
        {
            return null;
        }

        string? site = round.PlantSite switch
        {
            BombSite.A => SiteRegions.SiteA,
            BombSite.B => SiteRegions.SiteB,
            _ => null
        };
        return new RoundBomb(plant, site, round.DefuseTick, round.ExplodeTick);
    }
}

/// <summary>
///     Builds <see cref="RoundOccupancy" /> for every live round of a demo, from either source the
///     design names (suggested-tags.md §3.2): the Round Index document already written for the demo,
///     decoded from its alive-only per-side tokens, or a position walk of the held parse when there is
///     no index (the browser, or a demo the index has not reached). Both read windows, sides and alive
///     from the same Round Facts rows, so for one demo they produce the same per-side counts.
///     <para>
///         The walk runs at a frame stride of eight and keeps the first sample per (slot, second). A
///         stride does not save decode time (1.6 s at both 8 and 64 on the measured demos), and the
///         eighth-second samples are what the per-slot form and the detonation cloud want.
///     </para>
/// </summary>
public static class RoundOccupancyBuilder
{
    /// <summary>The <see cref="PositionSampler.Walk" /> frame stride of the fallback walk.</summary>
    public const int FrameStride = 8;

    /// <summary>
    ///     The walk over the held parse. Pure: no I/O and no cache.
    /// </summary>
    /// <param name="demo">The held parse.</param>
    /// <param name="facts">The demo's Round Facts rows: the windows, the sides, the kills and the bomb.</param>
    /// <param name="source">Which string names a sample's place; the pawn's by default, as the index does.</param>
    /// <param name="samples">The walk to fold; null walks <paramref name="demo" />. Tests hand in synthetic samples.</param>
    public static OccupancyBuild FromWalk(
        ParsedDemo demo,
        RoundFactsRows facts,
        IPlaceSource? source = null,
        IEnumerable<PositionSample>? samples = null)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(facts);
        source ??= PawnPlaceSource.Instance;
        int tickRate = demo.TickRate > 0 ? demo.TickRate : 64;
        int lastFrameTick = demo.Frames.Count > 0 ? demo.Frames[^1].ServerTick : 0;
        List<Window> windows = Windows(facts, lastFrameTick, tickRate);
        DetonationCloud cloud = new();

        samples ??= PositionSampler.Walk(demo, FrameStride);
        int current = 0;
        foreach (PositionSample sample in samples)
        {
            // Windows are in round order and the walk is in frame order, so a round left behind is
            // never revisited.
            while (current < windows.Count && sample.Tick >= windows[current].EndTick)
            {
                current++;
            }

            if (current >= windows.Count)
            {
                break;
            }

            Window window = windows[current];
            if (sample.Tick < window.StartTick
                || !window.Sides.ContainsKey(sample.PlayerSlot)
                || (window.Deaths.TryGetValue(sample.PlayerSlot, out int death) && death <= sample.Tick))
            {
                continue;
            }

            int second = (sample.Tick - window.StartTick) / tickRate;
            string?[] seconds = window.Places[sample.PlayerSlot];
            if (second >= seconds.Length || seconds[second] is not null)
            {
                continue; // the first sample of a second stands
            }

            string? place = source.PlaceFor(in sample);
            if (string.IsNullOrEmpty(place))
            {
                // The wire delivers "" where the engine doc promises null (0.18 to 0.75 percent of
                // samples); both mean the same thing here.
                seconds[second] = RoundOccupancy.Unplaced;
                continue;
            }

            seconds[second] = place;
            cloud.Offer(sample.Position, place);
        }

        List<RoundOccupancy> rounds =
        [
            .. windows.Select(w => RoundOccupancy.FromSlots(w.Number, w.StartTick, w.EndTick, tickRate, w.Sides,
                w.Places, w.Bomb))
        ];
        return new OccupancyBuild(rounds, cloud);
    }

    /// <summary>
    ///     The Round Index source: the document's runs expanded to rows and each alive-only token decoded
    ///     to place counts. No per-slot form, so <see cref="RoundOccupancy.HasSlots" /> is false and the
    ///     detectors that want it take their per-side fallback.
    /// </summary>
    /// <param name="index">The demo's index document.</param>
    /// <param name="facts">The same Round Facts rows the index was built over, for seats and the bomb.</param>
    /// <param name="tickRate">The demo's tick rate; the document's clock header when zero.</param>
    public static IReadOnlyList<RoundOccupancy> FromIndex(RoundIndexDocument index, RoundFactsRows facts, int tickRate = 0)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(facts);
        if (tickRate <= 0)
        {
            tickRate = index.Clock.TickRate > 0 ? index.Clock.TickRate : 64;
        }

        Dictionary<int, RoundFacts> rows = [];
        foreach (RoundFacts row in facts.Rounds)
        {
            rows.TryAdd(row.Number, row);
        }

        List<RoundOccupancy> rounds = [];
        foreach (RoundIndexRound round in index.Rounds.OrderBy(r => r.Number))
        {
            rows.TryGetValue(round.Number, out RoundFacts? row);
            Dictionary<int, (IReadOnlyList<(string Place, int Count)> Ct, IReadOnlyList<(string Place, int Count)> T)> bySecond = [];
            foreach (RoundIndexRow step in index.ExpandRows(round))
            {
                int second = (step.Tick - round.FreezeEndTick) / tickRate;
                bySecond.TryAdd(second, (PlaceCountToken.Decode(step.Ct), PlaceCountToken.Decode(step.T)));
            }

            rounds.Add(RoundOccupancy.FromSideCounts(round.Number, round.FreezeEndTick, round.EndTick, tickRate,
                row is null ? new Dictionary<int, int>() : OccupancyRoster.SideBySlot(row), bySecond,
                row is null ? null : OccupancyRoster.Bomb(row)));
        }

        return rounds;
    }

    // Every live round from its freeze end to its end: the row's EndTick, else the next round's
    // freeze end, else one past the last frame. The Round Index's windows, so both sources agree.
    private static List<Window> Windows(RoundFactsRows facts, int lastFrameTick, int tickRate)
    {
        List<RoundFacts> rounds = [.. facts.Rounds.OrderBy(r => r.Number)];
        List<Window> windows = [];
        for (int i = 0; i < rounds.Count; i++)
        {
            RoundFacts round = rounds[i];
            if (!round.IsLive)
            {
                continue;
            }

            int end = round.EndTick
                      ?? (i + 1 < rounds.Count ? rounds[i + 1].FreezeEndTick : lastFrameTick + 1);
            if (end <= round.FreezeEndTick)
            {
                continue;
            }

            int seconds = (end - round.FreezeEndTick + tickRate - 1) / tickRate;
            Dictionary<int, int> sides = OccupancyRoster.SideBySlot(round);
            Dictionary<int, string?[]> places = [];
            foreach (int slot in sides.Keys)
            {
                places[slot] = new string?[seconds];
            }

            windows.Add(new Window(round.Number, round.FreezeEndTick, end, sides, OccupancyRoster.DeathTickBySlot(round),
                places, OccupancyRoster.Bomb(round)));
        }

        return windows;
    }

    private sealed record Window(
        int Number,
        int StartTick,
        int EndTick,
        Dictionary<int, int> Sides,
        Dictionary<int, int> Deaths,
        Dictionary<int, string?[]> Places,
        RoundBomb? Bomb);
}
