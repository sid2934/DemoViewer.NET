#region

using System.Diagnostics;
using System.Globalization;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.MatchMeta;
using DemoViewer.NET.Services.Players;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The player directory projects the cache's index: an account's names across demos and slots, one entry per
///     demo however many copies there are, the departed players only the facet has, no orphaned demo, and a
///     projection rebuilt only when the index moves. Every store is in memory and no demo file is touched.
/// </summary>
public class PlayerDirectoryTests
{
    private const ulong Grim = 76561190000000003;
    private const ulong Jeorge = 76561190000000012;
    private const ulong Gone = 76561190000000020;
    private static readonly long Day = TimeSpan.TicksPerDay;

    private static string Id(ulong steamId) => steamId.ToString(CultureInfo.InvariantCulture);

    private static CachedPlayerInfo Player(int slot, string name, ulong steamId, int team, bool bot = false, bool coach = false) =>
        new() { Slot = slot, Name = name, SteamId64 = Id(steamId), Team = team, IsBot = bot, IsCoach = coach };

    private static DemoCacheRecord Demo(string path, long time, string? sha = null, string map = "de_nuke", params CachedPlayerInfo[] players)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 100,
            ModifiedTicks = 1,
            Sha256 = sha,
            Map = map,
            Players = [.. players],
            MatchMeta = new DemoMatchMeta { MatchTimeUtcTicks = time, MatchTimeSource = MatchTimeSource.ValveMatchInfo }
        };
        DemoCacheStore.StampHeader(record);
        DemoCacheStore.StampParse(record);
        return record;
    }

    private static FacetPlayer Facet(ulong steamId, int[] slots, params string[] names) =>
        new() { SteamId64 = Id(steamId), Names = [.. names], Slots = [.. slots] };

    [Test]
    public async Task Aliases_SpanDemosAndSlots_WithFirstAndLastSeen()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Demo("/d/old.dem", 10 * Day, players: Player(3, "Officer", Grim, 3)));
        DemoCacheRecord reconnect = Demo("/d/new.dem", 20 * Day, players: [Player(3, "Officer", Grim, 0), Player(4, "grim", Grim, 3)]);
        reconnect.FacetPlayers = [Facet(Grim, [3, 4], "Officer", "grim")];
        store.Upsert(reconnect);

        PlayerEntry entry = new PlayerDirectory(store).Get(Grim)!;
        PlayerAlias officer = entry.Aliases.Single(a => a.Name == "Officer");
        PlayerAlias grim = entry.Aliases.Single(a => a.Name == "grim");
        using (Assert.Multiple())
        {
            await Assert.That(entry.DemoCount).IsEqualTo(2);
            await Assert.That(entry.Aliases.Count).IsEqualTo(2);
            await Assert.That((officer.FirstSeenTicks, officer.LastSeenTicks, officer.Demos)).IsEqualTo((10 * Day, 20 * Day, 2))
                .Because("the reconnect's first slot still carries the old name");
            await Assert.That((grim.FirstSeenTicks, grim.LastSeenTicks, grim.Demos)).IsEqualTo((20 * Day, 20 * Day, 1));
            await Assert.That((entry.FirstSeenTicks, entry.LastSeenTicks)).IsEqualTo((10 * Day, 20 * Day));
        }
    }

    [Test]
    public async Task AReconnect_IsOneMatch_WithEverySlot()
    {
        DemoCacheStore store = new(null);
        DemoCacheRecord record = Demo("/d/a.dem", Day, players: [Player(3, "grim", Grim, 3), Player(4, "grim", Grim, 3)]);
        record.FacetPlayers = [Facet(Grim, [2, 3, 4], "grim")];
        store.Upsert(record);

        PlayerMatch match = new PlayerDirectory(store).Matches(Grim).Single();
        using (Assert.Multiple())
        {
            await Assert.That(match.Slots).IsEquivalentTo([2, 3, 4], CollectionOrdering.Matching).Because("the facet adds the ghost slot");
            await Assert.That(match.Side).IsEqualTo(3);
            await Assert.That(match.LeftEarly).IsFalse();
            await Assert.That(match.Names).IsEquivalentTo(["grim"]);
        }
    }

    [Test]
    public async Task BotsCoachesSpectatorsAndNoId_AreNotPlayers()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Demo("/d/a.dem", Day, players:
        [
            Player(1, "grim", Grim, 3),
            Player(2, "BOT Kirk", 76561190000000002, 2, bot: true),
            Player(3, "coach", 76561190000000009, 3, coach: true),
            Player(4, "caster", 76561190000000004, 1),
            new CachedPlayerInfo { Slot = 5, Name = "no id", SteamId64 = "0", Team = 2 },
            new CachedPlayerInfo { Slot = 6, Name = "not a number", SteamId64 = "STEAM_1:0:1", Team = 2 }
        ]));

        PlayerDirectory directory = new(store);
        await Assert.That(directory.Players.Select(p => p.SteamId64)).IsEquivalentTo([Grim]);
    }

    [Test]
    public async Task TheFacet_LeavesOutAnAccountTheRosterHasOnlyAsACoach()
    {
        FactRow Row(long slot, string steamId) => new(
            new Dictionary<string, FactValue> { ["player_slot"] = FactValue.Of(slot), ["steam_id"] = FactValue.Of(steamId) },
            new Dictionary<string, FactValue>());
        CachedPlayerInfo[] roster = [Player(1, "grim", Grim, 3), Player(3, "coach", 76561190000000009, 3, coach: true)];

        List<FacetPlayer> players = PlayerMatchFacet.Players([Row(1, Id(Grim)), Row(3, "76561190000000009")], roster);

        await Assert.That(players.Select(p => p.SteamId64)).IsEquivalentTo([Id(Grim)]);
    }

    [Test]
    public async Task APlayerOnlyTheFacetHas_LeftEarly()
    {
        DemoCacheStore store = new(null);
        DemoCacheRecord record = Demo("/d/a.dem", Day, players: [Player(1, "grim", Grim, 3), Player(7, "quitter", Gone, 1)]);
        record.FacetPlayers = [Facet(Grim, [1], "grim"), Facet(Gone, [7], "quitter")];
        store.Upsert(record);

        PlayerDirectory directory = new(store);
        PlayerMatch match = directory.Matches(Gone).Single();
        using (Assert.Multiple())
        {
            await Assert.That(match.LeftEarly).IsTrue();
            await Assert.That(match.Side).IsNull();
            await Assert.That(directory.Matches(Grim).Single().LeftEarly).IsFalse();
            await Assert.That(directory.Find("QUIT").Select(p => p.SteamId64)).IsEquivalentTo([Gone]);
            await Assert.That(directory.Matches(Gone, new PlayerFilter { Side = 3 })).IsEmpty().Because("a departed player has no side");
        }
    }

    [Test]
    public async Task ADemoWithTwoCopies_CountsOnce()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Demo("/d/a.dem", Day, sha: "aa11", players: Player(1, "grim", Grim, 3)));
        store.Upsert(Demo("/e/copy.dem", Day, sha: "aa11", players: Player(1, "grim", Grim, 3)));

        PlayerDirectory directory = new(store);
        using (Assert.Multiple())
        {
            await Assert.That(store.Index.Count).IsEqualTo(2);
            await Assert.That(directory.Get(Grim)!.DemoCount).IsEqualTo(1);
            await Assert.That(directory.Matches(Grim).Single().Row.Locations.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task AnOrphanedDemo_IsAbsent_UntilItsPathComesBack()
    {
        DemoCacheStore store = new(null);
        DemoCacheRecord record = Demo("/d/a.dem", Day, sha: "aa11", players: Player(1, "grim", Grim, 3));
        store.Upsert(record);
        PlayerDirectory directory = new(store);
        await Assert.That(directory.Get(Grim)).IsNotNull();

        store.Detach(record.Path);
        await Assert.That(store.IsOrphaned("aa11")).IsTrue();
        await Assert.That(directory.Get(Grim)).IsNull();

        await Assert.That(store.Reattach(record.Path, record.Size, record.ModifiedTicks)).IsTrue();
        await Assert.That(directory.Get(Grim)!.DemoCount).IsEqualTo(1);
    }

    [Test]
    public async Task OpenPath_IsTheFirstPathOnDisk()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Demo("/d/a.dem", Day, sha: "aa11", players: Player(1, "grim", Grim, 3)));
        store.Upsert(Demo("/e/copy.dem", Day, sha: "aa11", players: Player(1, "grim", Grim, 3)));
        HashSet<string> onDisk = new(StringComparer.OrdinalIgnoreCase) { "/d/a.dem", "/e/copy.dem" };
        PlayerDirectory directory = new(store, onDisk.Contains);
        PlayerMatch match = directory.Matches(Grim).Single();

        await Assert.That(directory.OpenPath(match)).IsEqualTo("/d/a.dem");
        onDisk.Remove("/d/a.dem");
        await Assert.That(directory.OpenPath(match)).IsEqualTo("/e/copy.dem").Because("the primary is missing");
        onDisk.Clear();
        await Assert.That(directory.OpenPath(match)).IsNull().Because("the file is missing");
    }

    [Test]
    public async Task TheProjection_IsRebuiltOnlyWhenTheIndexMoves()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Demo("/d/a.dem", Day, players: Player(1, "grim", Grim, 3)));
        PlayerDirectory directory = new(store);

        IReadOnlyList<PlayerEntry> first = directory.Players;
        await Assert.That(directory.Players).IsSameReferenceAs(first);

        store.Upsert(Demo("/d/b.dem", 2 * Day, players: Player(1, "jeorge", Jeorge, 2)));
        IReadOnlyList<PlayerEntry> second = directory.Players;
        using (Assert.Multiple())
        {
            await Assert.That(second).IsNotSameReferenceAs(first);
            await Assert.That(second.Select(p => p.SteamId64)).IsEquivalentTo([Jeorge, Grim], CollectionOrdering.Matching)
                .Because("equal demo counts put the most recently seen first");
            await Assert.That(directory.Players).IsSameReferenceAs(second);
        }
    }

    [Test]
    public async Task Changed_FollowsTheStore()
    {
        DemoCacheStore store = new(null);
        PlayerDirectory directory = new(store);
        int raised = 0;
        directory.Changed += () => raised++;

        store.Upsert(Demo("/d/a.dem", Day, players: Player(1, "grim", Grim, 3)));
        await Assert.That(raised).IsEqualTo(1);
        await Assert.That(directory.Get(Grim)).IsNotNull().Because("a handler reading on the event sees the write");
    }

    [Test]
    public async Task Matches_AreNewestFirst_AndFiltered()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Demo("/d/1.dem", 1 * Day, map: "de_nuke", players: Player(1, "grim", Grim, 3)));
        store.Upsert(Demo("/d/3.dem", 3 * Day, map: "de_mirage", players: Player(1, "grim", Grim, 2)));
        store.Upsert(Demo("/d/2.dem", 2 * Day, map: "de_nuke", players: Player(1, "grim", Grim, 2)));
        PlayerDirectory directory = new(store);

        List<string> Paths(PlayerFilter? filter) => [.. directory.Matches(Grim, filter).Select(m => m.Row.Path)];
        using (Assert.Multiple())
        {
            await Assert.That(Paths(null)).IsEquivalentTo(["/d/3.dem", "/d/2.dem", "/d/1.dem"], CollectionOrdering.Matching);
            await Assert.That(Paths(new PlayerFilter { Map = "DE_NUKE" })).IsEquivalentTo(["/d/2.dem", "/d/1.dem"], CollectionOrdering.Matching);
            await Assert.That(Paths(new PlayerFilter { Side = 2, Last = 1 })).IsEquivalentTo(["/d/3.dem"]);
            await Assert.That(Paths(new PlayerFilter { FromTicks = 2 * Day, BeforeTicks = 3 * Day })).IsEquivalentTo(["/d/2.dem"]);
            await Assert.That(directory.Matches(Jeorge)).IsEmpty();
        }
    }

    [Test]
    public async Task Find_TakesASteamIdOrAPartOfAName()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Demo("/d/a.dem", Day, players: [Player(1, "grim", Grim, 3), Player(2, "jeorge", Jeorge, 2)]));
        store.Upsert(Demo("/d/b.dem", 2 * Day, players: Player(1, "GRIMACE", Jeorge, 2)));
        PlayerDirectory directory = new(store);

        using (Assert.Multiple())
        {
            await Assert.That(directory.Find(" " + Id(Grim) + " ").Select(p => p.SteamId64)).IsEquivalentTo([Grim]);
            await Assert.That(directory.Find("grim").Select(p => p.SteamId64)).IsEquivalentTo([Jeorge, Grim], CollectionOrdering.Matching)
                .Because("every alias is searched, the account in more demos first");
            await Assert.That(directory.Find("76561190000000099")).IsEmpty();
            await Assert.That(directory.Find("  ")).IsEmpty();
        }
    }

    [Test]
    public async Task EveryStampedSidePlayer_IsFoundByTheLibraryQuery()
    {
        DemoCacheStore store = new(null);
        HostLibrary library = new(store, null, () => false);
        string stamp = StampedFacts.StampId(PlayerMatchFacet.RulesetId);
        for (int i = 0; i < 4; i++)
        {
            DemoCacheRecord record = Demo($"/d/{i}.dem", i * Day, sha: $"sha{i}",
                players: [Player(1, "a", Grim + (ulong)i, 3), Player(2, "b", Jeorge, 2), Player(3, "c", Gone + (ulong)i, 1)]);
            record.FacetPlayers = [Facet(Grim + (ulong)i, [1], "a"), Facet(Jeorge, [2], "b"), Facet(Gone + (ulong)i, [3], "c")];
            record.PackStamps.Add(new PackStamp(stamp, 1, "fp"));
            store.Upsert(record);
        }

        PlayerDirectory directory = new(store);
        List<(ulong Id, string Path)> stamped = [.. directory.Players
            .SelectMany(p => directory.Matches(p.SteamId64).Where(m => !m.LeftEarly && m.Row.Stamp(stamp) is not null)
                .Select(m => (p.SteamId64, m.Row.Path)))];
        await Assert.That(stamped.Count).IsEqualTo(12 - 4).Because("the spectators left early");
        foreach ((ulong id, string path) in stamped)
        {
            await Assert.That(library.Query(new LibraryQuery { SteamId64 = id }).Demos.Select(d => d.FilePath)).Contains(path);
        }
    }

    [Test]
    [Category("Budget")]
    public async Task TenThousandRows_ProjectUnder100Ms()
    {
        Random random = new(7);
        List<DemoCacheIndexEntry> rows = [.. Enumerable.Range(0, 10_000).Select(i => Row(i, random))];
        PlayerDirectory.Project(rows);

        // The median of five: a collection landing inside one run is the test host's heap, not the projection.
        List<double> runs = [];
        int accounts = 0;
        for (int run = 0; run < 5; run++)
        {
            Stopwatch watch = Stopwatch.StartNew();
            accounts = PlayerDirectory.Project(rows).Players.Count;
            runs.Add(watch.Elapsed.TotalMilliseconds);
        }

        double median = runs.Order().ElementAt(2);
        Console.WriteLine($"10k rows, {accounts} accounts: median {median:F1} ms of [{string.Join(", ", runs.Select(r => r.ToString("F1", CultureInfo.InvariantCulture)))}]");
        await Assert.That(median).IsLessThan(100);
    }

    private static DemoCacheIndexEntry Row(int i, Random random)
    {
        DemoCacheRecord record = new() { Path = $"/lib/{i}.dem", Size = 100, ModifiedTicks = i, Sha256 = $"sha{i}", Map = "de_nuke" };
        for (int slot = 0; slot < 10; slot++)
        {
            int account = random.Next(2000);
            string name = random.Next(10) == 0 ? $"alt{account}-{random.Next(3)}" : $"name{account}";
            record.Players.Add(Player(slot, name, 76561190000000000UL + (ulong)account, slot < 5 ? 2 : 3));
        }

        record.FacetPlayers = [.. record.Players.Select(p => Facet(ulong.Parse(p.SteamId64, CultureInfo.InvariantCulture), [p.Slot], p.Name))];
        record.MatchMeta = new DemoMatchMeta { MatchTimeUtcTicks = random.NextInt64(1, 1000) * Day };
        DemoCacheStore.StampParse(record);
        return record.ToIndexEntry();
    }
}
