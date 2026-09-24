#region

using System.Numerics;
using DemoViewer.NET.Modules.SuggestedTags;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Hand-written occupancy for the detector tests (suggested-tags.md §7.1): five CT slots and five
///     T slots on a made-up map whose A region is <c>BombsiteA+ALong+ASmall</c> and B region is
///     <c>BombsiteB+BTunnel</c>, a place per second per slot, and detonations placed by hand. A slot
///     not given a place stands in its spawn; a null second is a dead player.
/// </summary>
internal static class SuggestedTagsTestData
{
    internal const string Map = "de_test";
    internal const int Rate = 64;
    internal const int Start = 6400;

    internal static readonly int[] CtSlots = [1, 2, 3, 4, 5];
    internal static readonly int[] TSlots = [6, 7, 8, 9, 10];

    internal static SiteRegionTable Table { get; } = new()
    {
        Map = Map,
        DemoCount = 1,
        RoundCount = 10,
        Plants = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [SiteRegions.SiteA] = 5,
            [SiteRegions.SiteB] = 5
        },
        Regions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [SiteRegions.SiteA] = [SiteRegions.SiteA, "ALong", "ASmall"],
            [SiteRegions.SiteB] = [SiteRegions.SiteB, "BTunnel"]
        },
        SpawnAdjacent = ["TSpawn", "TRamp"]
    };

    internal static SiteRegions Regions { get; } = SiteRegions.Compose(Map, null, Table, DetectorProfile.Default);

    /// <summary>One slot's seconds: every second in its spawn, then each stay from its first to its last second inclusive.</summary>
    internal static string?[] Path(int seconds, string spawn, params (int From, int To, string? Place)[] stays)
    {
        string?[] places = new string?[seconds];
        Array.Fill(places, spawn);
        foreach ((int from, int to, string? place) in stays)
        {
            for (int s = from; s <= to && s < seconds; s++)
            {
                places[s] = place;
            }
        }

        return places;
    }

    /// <summary>Dead from <paramref name="second" /> on.</summary>
    internal static string?[] DiesAt(string?[] places, int second)
    {
        for (int s = second; s < places.Length; s++)
        {
            places[s] = null;
        }

        return places;
    }

    /// <summary>
    ///     A round of <paramref name="seconds" /> seconds from <see cref="Start" />. Every T slot not in
    ///     <paramref name="t" /> stands in <c>TSpawn</c> and every CT slot not in <paramref name="ct" /> in
    ///     <c>CTSpawn</c>.
    /// </summary>
    internal static RoundOccupancy Round(
        int seconds = 90,
        Dictionary<int, string?[]>? t = null,
        Dictionary<int, string?[]>? ct = null,
        RoundBomb? bomb = null,
        int number = 1,
        int[]? tSlots = null)
    {
        tSlots ??= TSlots;
        Dictionary<int, int> sides = [];
        Dictionary<int, string?[]> places = [];
        foreach (int slot in CtSlots)
        {
            sides[slot] = 3;
            places[slot] = ct is not null && ct.TryGetValue(slot, out string?[]? given) ? given : Path(seconds, "CTSpawn");
        }

        foreach (int slot in tSlots)
        {
            sides[slot] = 2;
            places[slot] = t is not null && t.TryGetValue(slot, out string?[]? given) ? given : Path(seconds, "TSpawn");
        }

        return RoundOccupancy.FromSlots(number, Start, Start + seconds * Rate, Rate, sides, places, bomb);
    }

    /// <summary>The frame clock tick of a (possibly fractional) second of the round.</summary>
    internal static int At(double second) => Start + (int)Math.Round(second * Rate);

    /// <summary>A detonation at a second, placed by hand.</summary>
    internal static PlacedEvent Det(double second, string kind, int slot, string? place) =>
        new(At(second), kind, slot, 0, Vector3.Zero, place);

    /// <summary>A bomb planted at a second.</summary>
    internal static RoundBomb Plant(double second, string site, double? defuse = null, double? explode = null) =>
        new(At(second), site, defuse is { } d ? At(d) : null, explode is { } e ? At(e) : null);

    internal static IReadOnlyList<TagProposal> Detect(RoundOccupancy round, IReadOnlyList<PlacedEvent>? events = null,
        DetectorProfile? profile = null) =>
        ProposalDetection.DetectRound(Map, Rate, Regions, events ?? [], round, profile ?? DetectorProfile.Default);

    internal static List<TagProposal> Of(this IEnumerable<TagProposal> proposals, string detector) =>
        [.. proposals.Where(p => p.Detector == detector)];

    /// <summary>A proposal as comparable text: every field, labels and factors in key order.</summary>
    internal static string Describe(TagProposal p) =>
        FormattableString.Invariant($"{p.Id} {p.Round} {p.Side} {p.FromTick}-{p.ToTick}@{p.TriggerTick} c={p.Confidence} ") +
        string.Join(',', p.Factors.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => FormattableString.Invariant($"{f.Key}={f.Value}"))) + " " +
        string.Join(',', p.Labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}={l.Value}"));

    // ── The scenarios the parameter table moves one number in ─────────────────────────────────────

    /// <summary>
    ///     Four T in <c>ALong</c> from second 12 to 20, one of them reaching the site at 18 (six seconds
    ///     after the trigger, so the touch is late at the shipped <c>T = 4</c>), a T smoke into
    ///     <c>ASmall</c> at 10, a plant at A at 25.
    /// </summary>
    internal static (RoundOccupancy Round, List<PlacedEvent> Events) LateTouchExecute()
    {
        const int seconds = 90;
        Dictionary<int, string?[]> t = new()
        {
            [6] = Path(seconds, "TSpawn", (12, 17, "ALong"), (18, 30, SiteRegions.SiteA)),
            [7] = Path(seconds, "TSpawn", (12, 30, "ALong")),
            [8] = Path(seconds, "TSpawn", (12, 30, "ALong")),
            [9] = Path(seconds, "TSpawn", (12, 30, "ALong"))
        };
        return (Round(seconds, t, bomb: Plant(25, SiteRegions.SiteA)), [Det(10, DetonationEvents.Smoke, 6, "ASmall")]);
    }

    /// <summary>
    ///     Four T each in <c>ALong</c> for two seconds, staggered one second apart from 10, so never more
    ///     than two at once but four distinct inside any four-second window from 10; the last reaches the
    ///     site at 15.
    /// </summary>
    internal static RoundOccupancy StaggeredArrivals()
    {
        const int seconds = 90;
        Dictionary<int, string?[]> t = new()
        {
            [6] = Path(seconds, "TSpawn", (10, 11, "ALong"), (12, 89, "TRamp")),
            [7] = Path(seconds, "TSpawn", (11, 12, "ALong"), (13, 89, "TRamp")),
            [8] = Path(seconds, "TSpawn", (12, 13, "ALong"), (14, 89, "TRamp")),
            [9] = Path(seconds, "TSpawn", (13, 14, "ALong"), (15, 89, SiteRegions.SiteA))
        };
        return Round(seconds, t);
    }

    /// <summary>
    ///     T spread over four non-spawn places at 30 (<c>ALong</c>, <c>Middle</c>, <c>BTunnel</c>,
    ///     <c>Connector</c>) with one T still in spawn, T smokes into both regions before 40, no execute.
    /// </summary>
    internal static (RoundOccupancy Round, List<PlacedEvent> Events) SpreadDefault(int? executeAt = null)
    {
        const int seconds = 90;
        Dictionary<int, string?[]> t = new()
        {
            [6] = Path(seconds, "TSpawn", (20, 89, "ALong")),
            [7] = Path(seconds, "TSpawn", (20, 89, "Middle")),
            [8] = Path(seconds, "TSpawn", (20, 89, "BTunnel")),
            [9] = Path(seconds, "TSpawn", (20, 89, "Connector"))
        };
        if (executeAt is { } s)
        {
            foreach (int slot in (int[])[7, 8, 9])
            {
                t[slot] = Path(seconds, "TSpawn", (20, s - 1, slot == 8 ? "BTunnel" : "Middle"), (s, 89, "ALong"));
            }

            t[6] = Path(seconds, "TSpawn", (20, s + 1, "ALong"), (s + 2, 89, SiteRegions.SiteA));
        }

        return (Round(seconds, t),
        [
            Det(22, DetonationEvents.Smoke, 6, "ASmall"),
            Det(25, DetonationEvents.Smoke, 8, "BTunnel")
        ]);
    }

    /// <summary>
    ///     A fake at B then an execute at A at 40: <paramref name="fakers" /> T in <c>BTunnel</c> from 20
    ///     to 30, then four T in <c>ALong</c> from 40 and one on the site at 42.
    /// </summary>
    internal static RoundOccupancy FakeThenA(int fakers = 2)
    {
        const int seconds = 90;
        Dictionary<int, string?[]> t = [];
        int[] slots = [6, 7, 8, 9];
        for (int i = 0; i < slots.Length; i++)
        {
            string early = i < fakers ? "BTunnel" : "Middle";
            t[slots[i]] = i == 0
                ? Path(seconds, "TSpawn", (20, 30, early), (31, 39, "Middle"), (40, 41, "ALong"), (42, 89, SiteRegions.SiteA))
                : Path(seconds, "TSpawn", (20, 30, early), (31, 39, "Middle"), (40, 89, "ALong"));
        }

        return Round(seconds, t);
    }

    /// <summary>Three T flashes into A's region at 10.0, 10.5 and 11.5 seconds.</summary>
    internal static List<PlacedEvent> ThreeFlashes(double spread = 1.5) =>
    [
        Det(10, DetonationEvents.Flash, 6, "ALong"),
        Det(10 + spread / 3, DetonationEvents.Flash, 7, "ASmall"),
        Det(10 + spread, DetonationEvents.Flash, 8, SiteRegions.SiteA)
    ];

    /// <summary>
    ///     A plant at A at 50 and CTs entering A's region at 55, 58 and 61; <paramref name="aliveCts" />
    ///     of them alive at the plant (the others died at 45).
    /// </summary>
    internal static RoundOccupancy Retake(int aliveCts = 3, bool defused = true)
    {
        const int seconds = 90;
        Dictionary<int, string?[]> ct = new()
        {
            [1] = Path(seconds, "CTSpawn", (55, 89, "ASmall")),
            [2] = Path(seconds, "CTSpawn", (58, 89, SiteRegions.SiteA)),
            [3] = Path(seconds, "CTSpawn", (61, 89, "ALong")),
            [4] = DiesAt(Path(seconds, "CTSpawn"), 45),
            [5] = DiesAt(Path(seconds, "CTSpawn"), 45)
        };
        for (int slot = aliveCts + 1; slot <= 3; slot++)
        {
            ct[slot] = DiesAt(ct[slot], 45);
        }

        Dictionary<int, string?[]> t = new()
        {
            [6] = Path(seconds, "TSpawn", (40, 89, SiteRegions.SiteA))
        };
        return Round(seconds, t, ct, Plant(50, SiteRegions.SiteA, defused ? 70 : null, defused ? null : 90));
    }
}
