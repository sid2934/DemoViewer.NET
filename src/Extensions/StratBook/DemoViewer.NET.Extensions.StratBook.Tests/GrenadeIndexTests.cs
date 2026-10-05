#region

using System.Numerics;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Utility;
using DemoViewer.NET.Views.UtilityBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.ViewModels.UtilityBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Grenade Index over synthetic rows siblings in an in-memory cache: the done
///     bar ("every smoke that landed on Mirage CT in these nine demos" returns clustered origins), the
///     coarse landing grid, the origin dedup and the jump-throw split, a copied demo counted once, the stamp
///     rule, the no-zones fallback, a zones version change re-resolving, the evaluator's merge and a
///     removal; then the Utility Book module's ids, the tab VM over the same index, and its Lineup Cards
///    : the CS2UTIL field set and the setpos/setang console line's exact format.
/// </summary>
public class GrenadeIndexTests
{
    private const string Mirage = "de_mirage";

    // Mirage's zones for these tests: west of x = -1000 is CT spawn, east of x = 1000 is T spawn.
    private static readonly RoundIndexBuilderTests.FakeZoneResolver MirageZones = new("zv-mirage",
        v => v.X < -1000 ? "CTSpawn" : v.X > 1000 ? "TSpawn" : "Mid");

    private static readonly RoundIndexBuilderTests.FakeZoneResolver InfernoZones = new("zv-inferno", _ => "CTSpawn");

    private static readonly string[] CtAndT = ["CTSpawn", "TSpawn"];
    private static readonly string[] TOnly = ["TSpawn"];
    private static readonly string[] Renamed = ["CT"];

    private static string DemoPath(int n) => $"/d/mirage-{n}.dem";

    private static IReadOnlyList<string> NineDemos => [.. Enumerable.Range(1, 9).Select(DemoPath)];

    private static GrenadeRow Row(string id, GrenadeKind kind, Vector3 origin, Vector3? landing, bool jump = false,
        int team = 3, int releaseTick = 1000) => new()
    {
        Id = id,
        Kind = kind,
        ThrowerTeam = team,
        ReleaseTick = releaseTick,
        ReleasePosition = WorldPoint.From(origin),
        JumpThrow = jump,
        DetonationPosition = landing is { } l ? WorldPoint.From(l) : null,
        EndKind = landing is null ? GrenadeEndKind.Removed : GrenadeEndKind.Detonated
    };

    // Writes the rows sibling and stamps the record the way the evaluator leaves a walked demo.
    private static void Indexed(DemoCacheStore cache, string path, string map, string? sha, IEnumerable<GrenadeRow> rows,
        bool stamp = true)
    {
        DemoCacheRecord record = RoundIndexTestData.ParsedRecord(path, map, sha);
        GrenadeDocument document = new()
        {
            Demo = new GrenadeDemoHeader { Sha256 = sha, StableKey = DemoCacheStore.StableKey(path) },
            Grenades = [.. rows]
        };
        cache.Upsert(record);
        cache.WriteGrenades(path, document);
        if (!stamp)
        {
            cache.Data().Invalidate(GrenadeStore.Facet, path);
        }
    }

    // The nine Mirage demos. Every one: a smoke from A into CT spawn (a few units of jitter, the same
    // position), a smoke into T spawn, a flash into CT spawn. Demos 1 to 4 add a jump-throw smoke from B
    // into the same spot, demos 1 and 2 a standing one from B, demos 6 and 7 a smoke from C into the next
    // cell over, and demo 5 a smoke that never went off.
    private static List<GrenadeRow> MirageRows(int n)
    {
        float jitter = (n % 3 - 1) * 5f;
        List<GrenadeRow> rows =
        [
            Row("a", GrenadeKind.Smoke, new Vector3(512 + jitter, 288 - jitter, -160), new Vector3(-1400 + 4 * jitter, -1400, -170)),
            Row("t", GrenadeKind.Smoke, new Vector3(512, 288, -160), new Vector3(1400, 200, -170), team: 2),
            Row("f", GrenadeKind.Flash, new Vector3(512, 288, -160), new Vector3(-1400, -1400, -170))
        ];
        if (n <= 4)
        {
            rows.Add(Row("bj", GrenadeKind.Smoke, new Vector3(800, 800, -96), new Vector3(-1450, -1350, -170), jump: true,
                releaseTick: 2000));
        }

        if (n <= 2)
        {
            rows.Add(Row("b", GrenadeKind.Smoke, new Vector3(803, 797, -96), new Vector3(-1350, -1450, -170),
                releaseTick: 3000));
        }

        if (n is 6 or 7)
        {
            rows.Add(Row("c", GrenadeKind.Smoke, new Vector3(-200, 900, -100), new Vector3(-1100, -1100, -170)));
        }

        if (n == 5)
        {
            rows.Add(Row("dud", GrenadeKind.Smoke, new Vector3(0, 0, 0), null));
        }

        return rows;
    }

    // The library: the nine, a byte copy of demo 1 at another path, an inferno demo, and a Mirage demo whose
    // rows were written but never stamped.
    private static DemoCacheStore Library()
    {
        DemoCacheStore cache = new(null);
        for (int n = 1; n <= 9; n++)
        {
            Indexed(cache, DemoPath(n), Mirage, $"sha{n}", MirageRows(n));
        }

        Indexed(cache, "/d/zz-copy/mirage-1.dem", Mirage, "sha1", MirageRows(1));
        Indexed(cache, "/d/inferno.dem", "de_inferno", "shaI",
            [Row("i", GrenadeKind.Smoke, new Vector3(512, 288, -160), new Vector3(-1400, -1400, -170))]);
        Indexed(cache, "/d/unstamped.dem", Mirage, "shaU", MirageRows(1), stamp: false);
        return cache;
    }

    private static GrenadeIndex Loaded(DemoCacheStore cache, IZonePlaceResolverSource? zones = null)
    {
        GrenadeIndex index = new(cache.Library(),
            zones ?? new RoundIndexEvaluatorTests.MapZones((Mirage, MirageZones), ("de_inferno", InfernoZones)));
        index.Load();
        return index;
    }

    private static GrenadeQuery SmokesIntoCt(IReadOnlySet<string>? demos = null) => new(Mirage,
        new HashSet<GrenadeKind> { GrenadeKind.Smoke }, new HashSet<string> { "CTSpawn" }, DemoPaths: demos);

    [Test]
    public async Task ALineupId_SurvivesARestart_AndALaterDemoJoinsItRatherThanMintingAnother()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-lineups-{Guid.NewGuid():N}");
        try
        {
            DemoCacheStore cache = Library();
            Guid first;
            using (GrenadeIndex index = new(cache.Library(), new RoundIndexEvaluatorTests.MapZones((Mirage, MirageZones)), lineups: new GrenadeLineupStore(root)))
            {
                index.Load();
                first = index.Query(SmokesIntoCt(NineDemos.ToHashSet()))[0].Lineups[0].Id;
            }

            // A tenth demo throws A from 20 units off the old mean: inside the spot radius of the stored anchor.
            Indexed(cache, DemoPath(10), Mirage, "sha10",
                [Row("a", GrenadeKind.Smoke, new Vector3(532, 288, -160), new Vector3(-1400, -1400, -170))]);
            GrenadeLineupStore reread = new(root);
            int anchors = reread.For(Mirage).Anchors.Count;
            using GrenadeIndex again = new(cache.Library(), new RoundIndexEvaluatorTests.MapZones((Mirage, MirageZones)), lineups: reread);
            again.Load();
            GrenadeLineup a = again.Query(SmokesIntoCt())[0].Lineups[0];
            using (Assert.Multiple())
            {
                await Assert.That(a.Id).IsEqualTo(first).Because("the anchor is read back, not re-derived");
                await Assert.That(a.Throws.Select(t => t.Demo.Path)).Contains(DemoPath(10));
                await Assert.That(reread.For(Mirage).Anchors.Count).IsEqualTo(anchors).Because("the new throw joined an anchor");
                await Assert.That(File.Exists(Path.Combine(root, GrenadeLineupStore.FileName))).IsTrue();
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task EveryOldGridId_ResolvesToExactlyOneLineup()
    {
        using GrenadeIndex index = Loaded(Library());
        List<GrenadeLineup> lineups = [.. index.Query(new GrenadeQuery(Mirage)).SelectMany(c => c.Lineups)];
        List<Guid> gridIds = [.. GrenadeIndex.Cluster(index.Rows(new GrenadeQuery(Mirage))).SelectMany(c => c.Lineups).SelectMany(l => l.AliasIds.Append(l.Id)).Distinct()];
        using (Assert.Multiple())
        {
            foreach (Guid id in gridIds)
            {
                await Assert.That(lineups.Count(l => l.Answers(id))).IsEqualTo(1).Because($"grid id {id} names one lineup");
                await Assert.That(index.DescribeLineup(Mirage, id)).IsNotNull();
            }
        }
    }

    [Test]
    public async Task BeforeTheLoadFinishes_NothingIsMinted()
    {
        DemoCacheStore cache = Library();
        GrenadeLineupStore store = new(null);
        using GrenadeIndex index = new(cache.Library(), new RoundIndexEvaluatorTests.MapZones((Mirage, MirageZones)), lineups: store);
        index.Query(new GrenadeQuery(Mirage));
        await Assert.That(store.For(Mirage).Anchors).IsEmpty().Because("an index that has not loaded sees a partial library");
    }

    [Test]
    public async Task ACoveredLandingIcon_IsFoldedIntoTheBadgeOfTheOneOnTop_UnlessFocused()
    {
        // Draw order is smallest first. 1 sits inside 3's disc; 0 sits inside 1's, which is itself covered;
        // 2 is clear of everything; 4 is inside 3 but focused.
        UtilityMapLayer.Disc[] discs =
        [
            new(100, 100, 11, false),
            new(104, 100, 11, false),
            new(300, 300, 11, false),
            new(110, 100, 17, false),
            new(125, 100, 11, true)
        ];
        (bool[] hidden, int[] counts) = UtilityMapLayer.Declutter(discs);
        using (Assert.Multiple())
        {
            await Assert.That(string.Join(",", hidden)).IsEqualTo("True,True,False,False,False");
            await Assert.That(counts[3]).IsEqualTo(3).Because("the top icon stands for itself and the two under it");
            await Assert.That(counts[2]).IsEqualTo(1);
            await Assert.That(counts[4]).IsEqualTo(1).Because("the focused icon is always drawn");
        }
    }

    [Test]
    public async Task TwoLandingGroupsSeededInOneCell_BothReachTheMap()
    {
        // (20, 20) and (220, 220) share the 256-unit cell (0, 0) and are 283 units apart: two groups.
        DemoCacheStore cache = new(null);
        for (int n = 1; n <= 2; n++)
        {
            Indexed(cache, DemoPath(n), Mirage, $"sha{n}",
            [
                Row("big1", GrenadeKind.Smoke, new Vector3(1200, 0, -160), new Vector3(20, 20, -170)),
                Row("big2", GrenadeKind.Smoke, new Vector3(1200, 0, -160), new Vector3(22, 18, -170), releaseTick: 2000),
                Row("small", GrenadeKind.Smoke, new Vector3(-400, 900, -160), new Vector3(220, 220, -170), releaseTick: 3000)
            ]);
        }

        using GrenadeIndex index = Loaded(cache);
        using UtilityBookTabViewModel vm = new(index, isBrowser: false, loadMapAsset: _ => null);
        vm.SelectedMap = Mirage;
        await Assert.That(index.Query(vm.CurrentQuery()!).Select(c => c.Cell).Distinct().Count()).IsEqualTo(1);
        await Assert.That(vm.Groups.Select(g => g.ThrowCount)).IsEquivalentTo([4, 2]);
    }

    [Test]
    public async Task AHiddenUtilityBook_DoesNotReadTheIndex_UntilItIsShownAgain()
    {
        using GrenadeIndex index = Loaded(Library());
        int reads = 0;
        using UtilityBookTabViewModel vm = new(index, isBrowser: false, loadMapAsset: _ => null,
            background: work => { reads++; work(); });
        int afterOpen = reads;
        vm.OnDeactivated();
        vm.Refresh();
        vm.SelectedMap = Mirage;
        int whileHidden = reads;
        vm.OnActivated(null!);
        using (Assert.Multiple())
        {
            await Assert.That(whileHidden).IsEqualTo(afterOpen);
            await Assert.That(reads).IsEqualTo(afterOpen + 1).Because("activation catches up with one read");
            await Assert.That(vm.SelectedMap).IsEqualTo(Mirage);
            await Assert.That(vm.HasGroups).IsTrue();
        }
    }

    [Test]
    public async Task AUtilityBookRefresh_ReadsTheIndexInTheBackground_AndOnlyTheNewestResultLands()
    {
        using GrenadeIndex index = Loaded(Library());
        Queue<Action> background = new();
        Queue<Action> posted = new();
        using UtilityBookTabViewModel vm = new(index, isBrowser: false, loadMapAsset: _ => null,
            background: background.Enqueue, post: posted.Enqueue);
        vm.SelectedMap = Mirage;
        vm.ShowSingleThrows = true;
        bool nothingYet = !vm.HasGroups && vm.Maps.Count == 0;
        while (background.Count > 0)
        {
            background.Dequeue()();
        }

        // Three reads finished; the first two were overtaken and must not land.
        int results = posted.Count;
        posted.Dequeue()();
        bool staleApplied = vm.HasGroups;
        while (posted.Count > 0)
        {
            posted.Dequeue()();
        }

        using (Assert.Multiple())
        {
            await Assert.That(nothingYet).IsTrue();
            await Assert.That(results).IsEqualTo(3);
            await Assert.That(staleApplied).IsFalse();
            await Assert.That(vm.SelectedMap).IsEqualTo(Mirage);
            await Assert.That(vm.HiddenLine).IsEqualTo("").Because("the newest read had single throws shown");
            await Assert.That(vm.HasGroups).IsTrue();
        }
    }

    [Test]
    public async Task ALineupSave_LandsOnDiskOnFlush_WithEverythingTheQueryMinted()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-lineups-{Guid.NewGuid():N}");
        try
        {
            GrenadeLineupStore store = new(root);
            using GrenadeIndex index = new(Library().Library(), new RoundIndexEvaluatorTests.MapZones((Mirage, MirageZones)), lineups: store);
            index.Load();
            Guid id = index.Query(SmokesIntoCt())[0].Lineups[0].Id;
            index.FlushLineups();
            GrenadeLineupStore reread = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(store.ReadsBack()).IsTrue();
                await Assert.That(reread.For(Mirage).Anchors.Select(a => a.Id)).Contains(id);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task EverySmokeIntoMirageCt_InTheNineDemos_ReturnsClusteredOrigins()
    {
        using GrenadeIndex index = Loaded(Library());

        IReadOnlyList<GrenadeCluster> clusters = index.Query(SmokesIntoCt(NineDemos.ToHashSet()));

        await Assert.That(clusters.Count).IsEqualTo(2).Because("two landing cells: the spawn and the cell north of it");
        GrenadeCluster spawn = clusters[0];
        GrenadeCluster north = clusters[1];
        using (Assert.Multiple())
        {
            await Assert.That(spawn.Kind).IsEqualTo(GrenadeKind.Smoke);
            await Assert.That(spawn.LandingPlace).IsEqualTo("CTSpawn");
            await Assert.That(spawn.Cell).IsEqualTo((-6, -6, -2));
            await Assert.That(spawn.ThrowCount).IsEqualTo(15).Because("nine from A, four jump-throws from B, two standing from B");
            await Assert.That(spawn.Lineups.Count).IsEqualTo(2).Because("B's jump-throws and standing throws are one lineup");

            GrenadeLineup a = spawn.Lineups[0];
            await Assert.That(a.Throws.Count).IsEqualTo(9).Because("five units of jitter rounds to one position");
            await Assert.That(a.DemoCount).IsEqualTo(9);
            await Assert.That(a.JumpThrow).IsFalse();
            await Assert.That(a.Origin).IsEqualTo(new WorldPoint(512, 288, -160));

            GrenadeLineup b = spawn.Lineups[1];
            await Assert.That(b.Throws.Count).IsEqualTo(6);
            await Assert.That(b.JumpThrow).IsTrue().Because("its most thrown technique is the jump-throw");
            await Assert.That(b.Techniques.Select(t => (t.Key, t.Throws.Count)))
                .IsEquivalentTo(new[] { ("stand-jump-left", 4), ("stand-throw-left", 2) })
                .Because("the same spot thrown another way is another position of the same lineup");
            await Assert.That(GrenadeIndex.RoundedOrigin(b.Techniques[1].Origin)).IsEqualTo(GrenadeIndex.RoundedOrigin(b.Techniques[0].Origin));

            await Assert.That(north.Cell).IsEqualTo((-5, -5, -2));
            await Assert.That(north.LandingPlace).IsEqualTo("CTSpawn");
            await Assert.That(north.Lineups.Single().Throws.Select(t => t.Demo.Path))
                .IsEquivalentTo(new[] { DemoPath(6), DemoPath(7) });
        }
    }

    [Test]
    public async Task TheWholeLibrary_CountsACopyOnce_AndSkipsOtherMapsAndUnstampedRows()
    {
        using GrenadeIndex index = Loaded(Library());

        IReadOnlyList<GrenadeCluster> clusters = index.Query(SmokesIntoCt());

        using (Assert.Multiple())
        {
            await Assert.That(clusters.Sum(c => c.ThrowCount)).IsEqualTo(17)
                .Because("the copy of demo 1 shares its hash, the inferno smoke is another map, the unstamped demo never loaded");
            await Assert.That(clusters.SelectMany(c => c.Lineups).SelectMany(l => l.Throws).Select(t => t.Demo.Path))
                .DoesNotContain("/d/zz-copy/mirage-1.dem");
            await Assert.That(index.DemoCount).IsEqualTo(10).Because("the nine and inferno: the copy is the same demo");
            await Assert.That(index.GrenadeCount).IsEqualTo(9 * 3 + 4 + 2 + 2 + 1)
                .Because("rows without a landing point are not index rows; the copy is not counted");
            await Assert.That(index.Maps()).IsEquivalentTo(new[] { "de_inferno", Mirage });
            await Assert.That(index.LandingPlaces(Mirage)).IsEquivalentTo(CtAndT);
        }
    }

    [Test]
    public async Task TheFilters_KeepTheirKind_Side_AndDemoSet()
    {
        using GrenadeIndex index = Loaded(Library());

        using (Assert.Multiple())
        {
            await Assert.That(index.Rows(new GrenadeQuery(Mirage, new HashSet<GrenadeKind> { GrenadeKind.Flash })).Count)
                .IsEqualTo(9);
            await Assert.That(index.Rows(new GrenadeQuery(Mirage, ThrowerTeam: 2)).Select(r => r.LandingPlace).Distinct())
                .IsEquivalentTo(TOnly);
            await Assert.That(index.Rows(new GrenadeQuery("DE_MIRAGE", DemoPaths: new HashSet<string> { DemoPath(6) })).Count)
                .IsEqualTo(4).Because("map and path compare without case");
            await Assert.That(index.Rows(SmokesIntoCt()).All(r => r.PlaceSource == "zones:zv-mirage")).IsTrue()
                .Because("the zones version is stored beside the place");
        }
    }

    [Test]
    public async Task WithoutZones_TheGridStillClusters_AndAPlaceFilterFindsNothing()
    {
        using GrenadeIndex index = Loaded(Library(), NoZonePlaceResolverSource.Instance);

        IReadOnlyList<GrenadeCluster> clusters =
            index.Query(new GrenadeQuery(Mirage, new HashSet<GrenadeKind> { GrenadeKind.Smoke }));

        using (Assert.Multiple())
        {
            await Assert.That(index.Query(SmokesIntoCt())).IsEmpty();
            await Assert.That(clusters.Select(c => c.Cell)).Contains((-6, -6, -2));
            await Assert.That(clusters.All(c => c.LandingPlace is null)).IsTrue();
            await Assert.That(index.LandingPlaces(Mirage)).IsEmpty();
        }
    }

    [Test]
    public async Task AZonesVersionChange_ReResolvesTheMapAtQueryTime()
    {
        SwitchableZones zones = new(MirageZones);
        using GrenadeIndex index = Loaded(Library(), zones);
        await Assert.That(index.Query(SmokesIntoCt()).Count).IsEqualTo(2);

        zones.Current = new RoundIndexBuilderTests.FakeZoneResolver("zv-renamed", v => v.X < -1000 ? "CT" : null);

        using (Assert.Multiple())
        {
            await Assert.That(index.Query(SmokesIntoCt())).IsEmpty();
            await Assert.That(index.Rows(new GrenadeQuery(Mirage, LandingPlaces: new HashSet<string> { "ct" })).Count)
                .IsEqualTo(9 * 2 + 4 + 2 + 2).Because("the new name, compared without case");
            await Assert.That(index.LandingPlaces(Mirage)).IsEquivalentTo(Renamed);
        }
    }

    [Test]
    public async Task TheEvaluatorsWrite_MergesADemo_AndARemovalDropsIt()
    {
        DemoCacheStore cache = new(null);
        cache.Upsert(RoundIndexTestData.ParsedRecord("/d/new.dem", Mirage, "shaN"));
        GrenadeIndexEvaluator evaluator = new(cache.Library(), cache.Grenades(), () => true,
            walk: _ => new GrenadeWalk(MirageRows(1), 1, ReconstructedInputSource.DecoderName, 4));
        using GrenadeIndex index = new(cache.Library(), new RoundIndexEvaluatorTests.MapZones((Mirage, MirageZones)), evaluator);
        index.Load();
        int changes = 0;
        index.Changed += () => changes++;
        await Assert.That(index.DemoCount).IsEqualTo(0);

        evaluator.Evaluate("/d/new.dem", RoundIndexTestData.Demo(lastTick: 5000, map: Mirage));
        int merged = index.Rows(SmokesIntoCt()).Count;
        cache.Remove("/d/new.dem");

        using (Assert.Multiple())
        {
            await Assert.That(merged).IsEqualTo(3);
            await Assert.That(changes).IsGreaterThanOrEqualTo(2);
            await Assert.That(index.DemoCount).IsEqualTo(0);
        }
    }

    [Test]
    public async Task LineupId_IsStableAcrossAReindex_EvenWhenTheRepresentativeThrowChanges()
    {
        DemoCacheStore cache = Library();
        using GrenadeIndex before = Loaded(cache);
        GrenadeLineup lineupBefore = before.Query(SmokesIntoCt(NineDemos.ToHashSet()))
            .Single(c => c.Cell == (-6, -6, -2)).Lineups[0];
        Guid id = lineupBefore.Id;
        string representativeBefore = lineupBefore.Throws[0].Demo.Path;

        // A demo whose path sorts before every "mirage-N.dem" path, thrown from the same spot: the
        // representative throw (Cluster's own ordering, oldest by path) changes; the identity does not.
        Indexed(cache, "/d/aaa-earlier.dem", Mirage, "sha-earlier",
            [Row("a", GrenadeKind.Smoke, new Vector3(512, 288, -160), new Vector3(-1400, -1400, -170))]);
        using GrenadeIndex after = Loaded(cache);
        GrenadeLineup lineupAfter = after.Query(SmokesIntoCt()).Single(c => c.Cell == (-6, -6, -2)).Lineups[0];

        using (Assert.Multiple())
        {
            await Assert.That(lineupAfter.Id).IsEqualTo(id);
            await Assert.That(lineupAfter.Throws[0].Demo.Path).IsEqualTo("/d/aaa-earlier.dem");
            await Assert.That(lineupAfter.Throws[0].Demo.Path).IsNotEqualTo(representativeBefore);
        }
    }

    [Test]
    public async Task DescribeLineup_ResolvesTheCardAndConsoleText_NullWhenNotOnThatMap()
    {
        using GrenadeIndex index = Loaded(Library());
        GrenadeLineup lineup = index.Query(SmokesIntoCt(NineDemos.ToHashSet()))
            .Single(c => c.Cell == (-6, -6, -2)).Lineups[0];

        LineupSummary? summary = index.DescribeLineup(Mirage, lineup.Id);

        using (Assert.Multiple())
        {
            await Assert.That(summary).IsNotNull();
            await Assert.That(summary!.Id).IsEqualTo(lineup.Id);
            await Assert.That(summary.Kind).IsEqualTo(GrenadeKind.Smoke);
            await Assert.That(summary.LandingPlace).IsEqualTo("CTSpawn");
            await Assert.That(summary.Title).IsEqualTo("Smoke into CTSpawn");
            await Assert.That(summary.ThrowCount).IsEqualTo(lineup.Throws.Count);
            await Assert.That(summary.ConsoleText).IsNull().Because("these fixture rows carry no release eye angles");
        }

        await Assert.That(index.DescribeLineup(Mirage, Guid.NewGuid())).IsNull();
        await Assert.That(index.DescribeLineup("de_inferno", lineup.Id)).IsNull().Because("the id names a Mirage position");
    }

    [Test]
    public async Task Lineups_FiltersByKind_AndCoversEveryClusterOnTheMap()
    {
        using GrenadeIndex index = Loaded(Library());

        IReadOnlyList<LineupSummary> smokes = index.Lineups(Mirage, new HashSet<GrenadeKind> { GrenadeKind.Smoke });
        IReadOnlyList<LineupSummary> all = index.Lineups(Mirage);

        using (Assert.Multiple())
        {
            await Assert.That(smokes.Count).IsGreaterThan(0);
            await Assert.That(smokes.All(s => s.Kind == GrenadeKind.Smoke)).IsTrue();
            await Assert.That(all.Any(s => s.Kind == GrenadeKind.Flash)).IsTrue().Because("unfiltered covers every kind on the map");
            await Assert.That(smokes.Select(s => s.Id).Distinct().Count()).IsEqualTo(smokes.Count).Because("one id per throw position");
        }
    }

    [Test]
    public async Task TheModule_ContributesTheUtilitySection_UnderThePersistedIds()
    {
        UtilityBookModule module = new(() => throw new InvalidOperationException("never built here"));
        WorkspaceTabDescriptor tab = module.CreateTabs(null!).Single();
        FeatureDescriptor? feature = FeatureCatalog.ById(UtilityBookModule.TabFeatureId);

        using (Assert.Multiple())
        {
            await Assert.That(module.Id).IsEqualTo("net.demoviewer.utilitybook");
            await Assert.That(tab.TabId).IsEqualTo("utilitybook.browser");
            await Assert.That(tab.Header).IsEqualTo("Utility");
            await Assert.That(tab.HostId).IsEqualTo(DemoViewer.NET.ViewModels.StratBook.StratBookHubViewModel.HostId);
            await Assert.That(tab.Order).IsEqualTo(3).Because("after Tags on the rail");
            await Assert.That(tab.DataContext).IsNull();
            await Assert.That(feature).IsNotNull();
            await Assert.That(feature!.Scope).IsEqualTo(FeatureScope.Tab);
            await Assert.That(ShellModuleFeatureGate.DesktopOnlyIds).DoesNotContain(UtilityBookModule.TabFeatureId);
        }
    }

    [Test]
    public async Task TheMap_ShowsLineupsSeenTwice_FocusShowsPositions_AndTheCardOpensEachThrow()
    {
        using GrenadeIndex index = Loaded(Library());
        RecordingPlayback playback = new();
        using UtilityBookTabViewModel vm = new(index, playback, isBrowser: false, loadMapAsset: _ => null);
        vm.SelectedMap = Mirage;
        vm.SelectedPlace = "CTSpawn";

        LandingGroup top = vm.Groups[0];
        using (Assert.Multiple())
        {
            await Assert.That(vm.SelectedKind!.Value).IsEqualTo(GrenadeKind.Smoke).Because("smokes are the default");
            await Assert.That(vm.Groups.Count).IsEqualTo(2).Because("the main CT smoke spot and the two-throw spot beside it");
            await Assert.That(top.Title).IsEqualTo("Smoke into CTSpawn");
            await Assert.That(top.ThrowCount).IsEqualTo(15);
            await Assert.That(top.Lineups.All(l => l.Throws.Count >= UtilityBookTabViewModel.LineupMinThrows)).IsTrue();
            await Assert.That(vm.HiddenLine).IsEqualTo("").Because("every CTSpawn position here was thrown from twice or more");
            await Assert.That(vm.Document.Landings.Count).IsEqualTo(2);
            await Assert.That(vm.Document.Landings[^1].Id).IsEqualTo(top.Id).Because("the biggest draws last, on top");
            await Assert.That(vm.Document.Throws).IsEmpty().Because("no group is focused yet");
            await Assert.That(vm.StatusLine).IsEqualTo("36 grenades from 10 demos");
        }

        vm.ClickLanding(top.Id);
        using (Assert.Multiple())
        {
            await Assert.That(vm.HasFocus).IsTrue();
            await Assert.That(vm.Document.Throws.Count).IsEqualTo(top.Lineups.Sum(l => l.Techniques.Count)).Because("one position per technique");
            await Assert.That(vm.Document.Throws.All(t => t.Trajectory.Count >= 2)).IsTrue().Because("a straight flight stands in for a missing path");
            await Assert.That(vm.FocusLine).StartsWith("Smoke into CTSpawn:");
        }

        GrenadeLineup jump = top.Lineups.First(l => l.JumpThrow);
        LineupTechnique jumping = jump.Techniques.First(t => t.JumpThrow);
        vm.ClickThrow(UtilityBookTabViewModel.PositionKey(jump, jumping));
        LineupDetail detail = vm.Detail!;
        using (Assert.Multiple())
        {
            await Assert.That(detail.UsedLine).IsEqualTo($"Used {jump.Throws.Count} times in {jump.DemoCount} demos");
            await Assert.That(detail.StyleLine).Contains("jump-throw");
            await Assert.That(detail.TechniquesLine).IsEqualTo("jump-throw, left click 4 · standing throw, left click 2");
            await Assert.That(detail.Instances.Count).IsEqualTo(jumping.Throws.Count).Because("the card lists the clicked position's throws");
            await Assert.That(detail.ConsoleText).IsEqualTo(UtilityBookTabViewModel.NoConsoleText)
                .Because("the fixture rows never set release eye angles");
            await Assert.That(detail.HasConsole).IsFalse();
        }

        IndexedGrenade opened = detail.Instances[0].Grenade;
        await detail.Instances[0].WatchCommand.ExecuteAsync(null);
        await Assert.That(playback.Seeks.Single()).IsEqualTo((opened.Demo.Path, opened.Row.ReleaseTick - UtilityBookTabViewModel.WatchLeadTicks));

        vm.Back();
        await Assert.That(vm.HasDetail).IsFalse().Because("Escape closes the card first");
        vm.Back();
        await Assert.That(vm.HasFocus).IsFalse().Because("then leaves the group");

    }

    [Test]
    public async Task TheMapArt_LoadsOncePerMap_AndTheReplacedBundleIsRetired()
    {
        using GrenadeIndex index = Loaded(Library());
        List<string> loads = [];
        List<Action> retired = [];
        using UtilityBookTabViewModel vm = new(index, isBrowser: false, loadMapAsset: map =>
        {
            loads.Add(map);
            return StubAsset(map);
        }, retire: retired.Add);

        vm.SelectedMap = Mirage;
        LoadedMapAsset mirage = vm.MapAsset!;
        int mirageLoads = loads.Count;
        int retiredBefore = retired.Count;
        vm.Refresh();
        vm.Refresh();
        using (Assert.Multiple())
        {
            await Assert.That(loads.Count).IsEqualTo(mirageLoads).Because("an index change on the same map keeps its bundle");
            await Assert.That(vm.MapAsset).IsSameReferenceAs(mirage);
            await Assert.That(retired.Count).IsEqualTo(retiredBefore);
        }

        vm.SelectedMap = "de_inferno";
        using (Assert.Multiple())
        {
            await Assert.That(loads[^1]).IsEqualTo("de_inferno");
            await Assert.That(vm.MapAsset).IsNotSameReferenceAs(mirage);
            await Assert.That(retired.Count).IsEqualTo(retiredBefore + 1).Because("the Mirage bundle is disposed once the host rebinds");
        }
    }

    private static LoadedMapAsset StubAsset(string map) => new()
    {
        Bundle = new MapAssetBundle(1, map, "1", "1", new RadarTransform(0, 0, 1, 0, 1, 1024),
            new WorldBoundsDto(-1000, -1000, 1000, 1000), [], [], []),
        RadarImages = new Dictionary<string, SkiaSharp.SKImage>(StringComparer.Ordinal),
        BakedDir = "."
    };

    [Test]
    public async Task TheCard_CopiesTheSetposSetangLine()
    {
        DemoCacheStore cache = new(null);
        List<GrenadeRow> rows = [];
        for (int n = 1; n <= 2; n++)
        {
            GrenadeRow row = Row("a", GrenadeKind.Smoke, new Vector3(512, 288, -160), new Vector3(-1400, -1400, -170));
            row.ReleaseEyePitch = -18.12f;
            row.ReleaseEyeYaw = -15.3f;
            row.Movement = MovementClass.Running;
            row.AirTimeTicks = 115;
            Indexed(cache, DemoPath(n), Mirage, $"sha{n}", [row]);
        }

        // One single throw somewhere else: hidden by default.
        Indexed(cache, DemoPath(3), Mirage, "sha3", [Row("s", GrenadeKind.Smoke, new Vector3(-600, 900, -100), new Vector3(1500, 300, -170))]);

        using GrenadeIndex index = Loaded(cache);
        using UtilityBookTabViewModel vm = new(index, isBrowser: false, loadMapAsset: _ => null);
        string? copied = null;
        vm.Clipboard = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };
        vm.SelectedMap = Mirage;
        vm.ClickLanding(vm.Groups.Single().Id);
        vm.ClickThrow(vm.Document.Throws.Single().Id);
        LineupDetail detail = vm.Detail!;
        await detail.CopyConsoleCommand.ExecuteAsync(null);

        using (Assert.Multiple())
        {
            await Assert.That(detail.UsedLine).IsEqualTo("Used 2 times in 2 demos");
            await Assert.That(detail.StyleLine).IsEqualTo("Smoke · running throw, left click · Running · 1.8s air time");
            await Assert.That(copied).IsEqualTo("setpos 512.00 288.00 -160.00; setang -18.12 -15.30 0.00")
                .Because("the console line is copy-pasteable at this exact shape");
            await Assert.That(detail.CopyStatus).IsEqualTo("copied");
            await Assert.That(vm.HiddenLine).IsEqualTo("1 position thrown from only once (1 throw) is hidden");
        }

        vm.ShowSingleThrows = true;
        using (Assert.Multiple())
        {
            await Assert.That(vm.Groups.Count).IsEqualTo(2);
            await Assert.That(vm.HiddenLine).IsEqualTo("");
            await Assert.That(vm.HasDetail).IsTrue().Because("the focus and the card survive a filter that keeps them");
        }
    }

    [Test]
    public async Task TwoThrowsFromOneSpot_AcrossTheOriginGridEdge_AreOneLineup_AndBothIdsResolve()
    {
        // 8 units apart but on either side of a 16-unit rounding edge (504 rounds to 32, 511.9 to 32, 519 to 32...);
        // 519.9 and 520.1 straddle 520, the midpoint between 512 and 528.
        DemoCacheStore cache = new(null);
        Indexed(cache, DemoPath(1), Mirage, "sha1", [Row("a", GrenadeKind.Smoke, new Vector3(519.9f, 288, -160), new Vector3(-1400, -1400, -170))]);
        Indexed(cache, DemoPath(2), Mirage, "sha2", [Row("a", GrenadeKind.Smoke, new Vector3(520.1f, 288, -160), new Vector3(-1395, -1402, -170))]);
        using GrenadeIndex index = Loaded(cache);

        GrenadeCluster cluster = index.Query(new GrenadeQuery(Mirage)).Single();
        GrenadeLineup lineup = cluster.Lineups.Single();
        using (Assert.Multiple())
        {
            await Assert.That(lineup.Throws.Count).IsEqualTo(2).Because("one standing spot, split only by the grid");
            await Assert.That(lineup.AliasIds.Count).IsEqualTo(3).Because("its own id and both grid ids");
            foreach (Guid id in lineup.AliasIds)
            {
                await Assert.That(index.DescribeLineup(Mirage, id)?.Id).IsEqualTo(lineup.Id).Because("a strat step that stored either id still resolves");
            }
        }
    }

    [Test]
    [Category("Integration")]
    public async Task TheUtilityMap_RendersTheGroups_ThenAFocusedGroupWithItsCard()
    {
        int mapInk = 0, focusInk = 0;
        bool focused = false, carded = false, cardOnLeft = false;
        HashSet<string> reached = [];
        int stacked = 0;
        await HeadlessSession.RunOnUi(() =>
        {
            using GrenadeIndex index = Loaded(Library());
            using UtilityBookTabViewModel vm = new(index, new RecordingPlayback(), isBrowser: false);
            vm.SelectedMap = Mirage;
            vm.SelectedPlace = "CTSpawn";
            UtilityBookTabView view = new() { DataContext = vm };
            Window window = new() { Width = 1280, Height = 860, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (window.CaptureRenderedFrame() is { } map)
            {
                map.Save(Path.Combine(HeadlessSession.ArtifactDir, "utility-map.png"), new PngBitmapEncoderOptions());
                mapInk = RenderInk(map);
            }

            UtilityMapHost host = view.FindControl<UtilityMapHost>("Map")!;
            LandingGroup top = vm.Groups[0];
            if (host.HostPointOf(top.Landing.X, top.Landing.Y, top.Landing.Z) is { } at)
            {
                host.Click((float)at.X, (float)at.Y);
            }

            focused = vm.HasFocus;
            UtilityThrow? jump = vm.Document.Throws.FirstOrDefault(t => t.JumpThrow);
            if (jump is not null && host.HostPointOf(jump.X, jump.Y, jump.Z) is { } origin)
            {
                // The jump-throw and the standard throw 4 units away stack on one disc: repeated clicks
                // on that spot must reach both.
                stacked = vm.Document.Throws.Count(t => Math.Abs(t.X - jump.X) < 8 && Math.Abs(t.Y - jump.Y) < 8);
                for (int i = 0; i < stacked; i++)
                {
                    host.Click((float)origin.X, (float)origin.Y);
                    if (vm.Detail is { } open)
                    {
                        reached.Add(UtilityBookTabViewModel.PositionKey(open.Lineup, open.Technique!));
                    }
                }

                cardOnLeft = vm.CardOnLeft == origin.X > host.Bounds.Width / 2;
            }

            carded = vm.HasDetail;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (window.CaptureRenderedFrame() is { } card)
            {
                card.Save(Path.Combine(HeadlessSession.ArtifactDir, "utility-focus.png"), new PngBitmapEncoderOptions());
                focusInk = RenderInk(card);
            }

            window.Close();
            return Task.CompletedTask;
        });

        using (Assert.Multiple())
        {
            await Assert.That(focused).IsTrue().Because("a click on the icon focuses the group through the host's hit test");
            await Assert.That(carded).IsTrue().Because("a click on a position opens its card");
            await Assert.That(stacked).IsEqualTo(2).Because("the fixture stacks a jump-throw and a standard throw");
            await Assert.That(reached.Count).IsEqualTo(2).Because("repeated clicks on a stack reach every position in it");
            await Assert.That(cardOnLeft).IsTrue().Because("the card opens on the side away from the selected position");
            await Assert.That(mapInk).IsGreaterThan(500);
            await Assert.That(focusInk).IsGreaterThan(500);
        }
    }

    private static int RenderInk(WriteableBitmap bmp)
    {
        PixelSize size = bmp.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            System.Runtime.InteropServices.Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
        }

        int count = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            if (buffer[i] > 40 || buffer[i + 1] > 40 || buffer[i + 2] > 40)
            {
                count++;
            }
        }

        return count;
    }

    private sealed class SwitchableZones(IZonePlaceResolver current) : IZonePlaceResolverSource
    {
        public IZonePlaceResolver Current { get; set; } = current;

        public IZonePlaceResolver? TryGet(string map) =>
            string.Equals(map, Mirage, StringComparison.OrdinalIgnoreCase) ? Current : null;
    }

    private sealed class RecordingPlayback : ISituationPlayback
    {
        public List<(string Path, int Tick)> Seeks { get; } = [];

        public Task<bool> SeekAsync(string demoPath, int tick)
        {
            Seeks.Add((demoPath, tick));
            return Task.FromResult(true);
        }
    }
}
