#region

using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>
///     The bomb in one round, from the Round Facts row (integrator correction 11): the site is the
///     fact's letter as the place name the detectors compare against, never re-derived from the
///     planter's place. Ticks are frame clock.
/// </summary>
/// <param name="PlantTick">The plant.</param>
/// <param name="Site"><c>BombsiteA</c> or <c>BombsiteB</c>; null when the row could not say which.</param>
/// <param name="DefuseTick">The defuse, when there was one.</param>
/// <param name="ExplodeTick">The detonation, when there was one.</param>
public sealed record RoundBomb(int PlantTick, string? Site, int? DefuseTick, int? ExplodeTick);

/// <summary>
///     The detectors' input for one round (suggested-tags.md §3.1, §3.5): per second since freeze end
///     and per side, how many alive players stood in each place, and when the per-slot form exists,
///     the place each alive player was in. Built from the Round Index token or from a walk by
///     <see cref="RoundOccupancyBuilder" />; a detector never knows which.
///     <para>
///         Second <c>s</c> is the ticks <c>[StartTick + s * TickRate, StartTick + (s + 1) * TickRate)</c>.
///         An alive player whose place is unknown counts in <see cref="AliveCount" /> and in no place,
///         so a region count never includes a player it cannot place.
///     </para>
/// </summary>
public sealed class RoundOccupancy
{
    /// <summary>The entry for an alive player with no place: the Round Index token's null place.</summary>
    public const string Unplaced = PlaceCountToken.NullPlace;

    private static readonly IReadOnlyDictionary<string, int> _empty = new Dictionary<string, int>(StringComparer.Ordinal);

    private readonly Dictionary<int, int[]> _alive;
    private readonly Dictionary<int, Dictionary<string, int>[]> _counts;
    private readonly Dictionary<int, string?[]>? _places;
    private readonly Dictionary<int, bool[]>? _slotAlive;
    private readonly Dictionary<int, int> _sideBySlot;

    private RoundOccupancy(
        int round,
        int startTick,
        int endTick,
        int tickRate,
        int seconds,
        Dictionary<int, int> sideBySlot,
        Dictionary<int, Dictionary<string, int>[]> counts,
        Dictionary<int, int[]> alive,
        Dictionary<int, string?[]>? places,
        Dictionary<int, bool[]>? slotAlive,
        RoundBomb? bomb)
    {
        Round = round;
        StartTick = startTick;
        EndTick = endTick;
        TickRate = tickRate;
        Seconds = seconds;
        _sideBySlot = sideBySlot;
        _counts = counts;
        _alive = alive;
        _places = places;
        _slotAlive = slotAlive;
        Bomb = bomb;
    }

    /// <summary>The Round Facts round number (<c>ClipRound.Number</c>, correction 12).</summary>
    public int Round { get; }

    /// <summary>Frame clock: the freeze end, second 0.</summary>
    public int StartTick { get; }

    /// <summary>Frame clock: the first tick past the round's live window.</summary>
    public int EndTick { get; }

    /// <summary>Ticks per second.</summary>
    public int TickRate { get; }

    /// <summary>How many seconds the round covers; valid seconds are <c>[0, Seconds)</c>.</summary>
    public int Seconds { get; }

    /// <summary>True when <see cref="PlaceOf" /> and <see cref="IsAlive" /> answer per slot.</summary>
    public bool HasSlots => _places is not null;

    /// <summary>The plant, defuse and detonation from the row; null when the bomb never went down.</summary>
    public RoundBomb? Bomb { get; }

    /// <summary>Frame clock tick of the start of <paramref name="second" />.</summary>
    public int TickAt(int second) => StartTick + second * TickRate;

    /// <summary>The second <paramref name="tick" /> falls in; negative before the freeze end.</summary>
    public int SecondAt(int tick) => (int)Math.Floor((tick - StartTick) / (double)TickRate);

    /// <summary>Place to alive count for one side at one second. Empty outside the round.</summary>
    /// <param name="side">2 = T, 3 = CT.</param>
    /// <param name="second">Seconds since freeze end.</param>
    public IReadOnlyDictionary<string, int> CountsAt(int side, int second) =>
        _counts.TryGetValue(side, out Dictionary<string, int>[]? bySecond) && second >= 0 && second < bySecond.Length
            ? bySecond[second]
            : _empty;

    /// <summary>Alive players of one side at one second, placed or not.</summary>
    /// <param name="side">2 = T, 3 = CT.</param>
    /// <param name="second">Seconds since freeze end.</param>
    public int AliveCount(int side, int second) =>
        _alive.TryGetValue(side, out int[]? bySecond) && second >= 0 && second < bySecond.Length ? bySecond[second] : 0;

    /// <summary>Alive players of one side standing in any of <paramref name="places" /> at one second.</summary>
    /// <param name="side">2 = T, 3 = CT.</param>
    /// <param name="second">Seconds since freeze end.</param>
    /// <param name="places">A site region or any other place set.</param>
    public int CountIn(int side, int second, IReadOnlySet<string> places)
    {
        int count = 0;
        foreach ((string place, int n) in CountsAt(side, second))
        {
            if (places.Contains(place))
            {
                count += n;
            }
        }

        return count;
    }

    /// <summary>The place an alive player was in; null when unplaced, dead, unsampled or without the per-slot form.</summary>
    /// <param name="slot">The player slot.</param>
    /// <param name="second">Seconds since freeze end.</param>
    public string? PlaceOf(int slot, int second) =>
        _places is not null && _places.TryGetValue(slot, out string?[]? bySecond) && second >= 0 && second < bySecond.Length
            ? bySecond[second]
            : null;

    /// <summary>Whether a player was sampled alive in a second; false without the per-slot form.</summary>
    /// <param name="slot">The player slot.</param>
    /// <param name="second">Seconds since freeze end.</param>
    public bool IsAlive(int slot, int second) =>
        _slotAlive is not null && _slotAlive.TryGetValue(slot, out bool[]? bySecond) && second >= 0 && second < bySecond.Length
        && bySecond[second];

    /// <summary>The slots seated on one side for this round, ascending.</summary>
    /// <param name="side">2 = T, 3 = CT.</param>
    public IReadOnlyList<int> Slots(int side) => [.. _sideBySlot.Where(s => s.Value == side).Select(s => s.Key).Order()];

    /// <summary>The side a slot was seated on this round, or 0 when it was not seated.</summary>
    /// <param name="slot">The player slot.</param>
    public int SideOf(int slot) => _sideBySlot.GetValueOrDefault(slot);

    /// <summary>
    ///     The per-slot form: one entry per second per seated slot. An entry is the place the player
    ///     stood in, <see cref="Unplaced" /> when alive with no place, and null when dead or not
    ///     sampled. The per-side counts are folded from it, so both views always agree.
    /// </summary>
    /// <param name="round">The round number.</param>
    /// <param name="startTick">Frame clock freeze end.</param>
    /// <param name="endTick">Frame clock end of the live window.</param>
    /// <param name="tickRate">Ticks per second.</param>
    /// <param name="sideBySlot">Every seated slot and its side (2 or 3).</param>
    /// <param name="placesBySlot">Per slot, one entry per second; shorter arrays are padded with null.</param>
    /// <param name="bomb">The bomb, or null.</param>
    public static RoundOccupancy FromSlots(
        int round,
        int startTick,
        int endTick,
        int tickRate,
        IReadOnlyDictionary<int, int> sideBySlot,
        IReadOnlyDictionary<int, string?[]> placesBySlot,
        RoundBomb? bomb = null)
    {
        ArgumentNullException.ThrowIfNull(sideBySlot);
        ArgumentNullException.ThrowIfNull(placesBySlot);
        int seconds = SecondsIn(startTick, endTick, tickRate);

        Dictionary<int, string?[]> places = [];
        Dictionary<int, bool[]> slotAlive = [];
        Dictionary<int, Dictionary<string, int>[]> counts = NewCounts(seconds);
        Dictionary<int, int[]> alive = new()
        {
            [2] = new int[seconds],
            [3] = new int[seconds]
        };

        foreach ((int slot, int side) in sideBySlot)
        {
            string?[] fold = new string?[seconds];
            bool[] living = new bool[seconds];
            placesBySlot.TryGetValue(slot, out string?[]? given);
            for (int s = 0; s < seconds && given is not null && s < given.Length; s++)
            {
                string? entry = given[s];
                if (entry is null)
                {
                    continue;
                }

                living[s] = true;
                if (!counts.TryGetValue(side, out Dictionary<string, int>[]? bySecond))
                {
                    continue; // a side that is neither T nor CT seats nobody
                }

                alive[side][s]++;
                if (entry.Length == 0 || entry == Unplaced)
                {
                    continue;
                }

                fold[s] = entry;
                bySecond[s][entry] = bySecond[s].GetValueOrDefault(entry) + 1;
            }

            places[slot] = fold;
            slotAlive[slot] = living;
        }

        return new RoundOccupancy(round, startTick, endTick, tickRate, seconds, new Dictionary<int, int>(sideBySlot),
            counts, alive, places, slotAlive, bomb);
    }

    /// <summary>
    ///     The per-side form only, as the Round Index token carries it: per side, per second, place
    ///     counts and the alive total. <see cref="HasSlots" /> is false.
    /// </summary>
    /// <param name="round">The round number.</param>
    /// <param name="startTick">Frame clock freeze end.</param>
    /// <param name="endTick">Frame clock end of the live window.</param>
    /// <param name="tickRate">Ticks per second.</param>
    /// <param name="sideBySlot">Every seated slot and its side, for <see cref="Slots" />.</param>
    /// <param name="bySecond">Per second: the CT and T pairs, <see cref="Unplaced" /> for an alive player with no place.</param>
    /// <param name="bomb">The bomb, or null.</param>
    public static RoundOccupancy FromSideCounts(
        int round,
        int startTick,
        int endTick,
        int tickRate,
        IReadOnlyDictionary<int, int> sideBySlot,
        IReadOnlyDictionary<int, (IReadOnlyList<(string Place, int Count)> Ct, IReadOnlyList<(string Place, int Count)> T)> bySecond,
        RoundBomb? bomb = null)
    {
        ArgumentNullException.ThrowIfNull(sideBySlot);
        ArgumentNullException.ThrowIfNull(bySecond);
        int seconds = SecondsIn(startTick, endTick, tickRate);

        Dictionary<int, Dictionary<string, int>[]> counts = NewCounts(seconds);
        Dictionary<int, int[]> alive = new()
        {
            [2] = new int[seconds],
            [3] = new int[seconds]
        };

        foreach ((int second, (IReadOnlyList<(string Place, int Count)> ct, IReadOnlyList<(string Place, int Count)> t)) in bySecond)
        {
            if (second < 0 || second >= seconds)
            {
                continue;
            }

            Fold(3, ct);
            Fold(2, t);

            void Fold(int side, IReadOnlyList<(string Place, int Count)> pairs)
            {
                foreach ((string place, int count) in pairs)
                {
                    alive[side][second] += count;
                    if (place.Length > 0 && place != Unplaced)
                    {
                        counts[side][second][place] = counts[side][second].GetValueOrDefault(place) + count;
                    }
                }
            }
        }

        return new RoundOccupancy(round, startTick, endTick, tickRate, seconds, new Dictionary<int, int>(sideBySlot),
            counts, alive, null, null, bomb);
    }

    private static int SecondsIn(int startTick, int endTick, int tickRate)
    {
        if (tickRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tickRate), tickRate, "a tick rate is positive");
        }

        return endTick <= startTick ? 0 : (endTick - startTick + tickRate - 1) / tickRate;
    }

    private static Dictionary<int, Dictionary<string, int>[]> NewCounts(int seconds)
    {
        Dictionary<int, Dictionary<string, int>[]> counts = [];
        foreach (int side in (int[])[2, 3])
        {
            Dictionary<string, int>[] bySecond = new Dictionary<string, int>[seconds];
            for (int s = 0; s < seconds; s++)
            {
                bySecond[s] = new Dictionary<string, int>(StringComparer.Ordinal);
            }

            counts[side] = bySecond;
        }

        return counts;
    }
}
