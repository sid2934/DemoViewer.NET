#region

using System.Numerics;
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
///     The Grenade Index (plan.md §3, Phase 4) over synthetic rows siblings in an in-memory cache: the done
///     bar ("every smoke that landed on Mirage CT in these nine demos" returns clustered origins), the
///     coarse landing grid, the origin dedup and the jump-throw split, a copied demo counted once, the stamp
///     rule, the no-zones fallback, a zones version change re-resolving, the evaluator's merge and a
///     removal; then the Utility Book module's ids, the tab VM over the same index, and its Lineup Cards
///     (plan.md §3, Phase 4): the CS2UTIL field set and the setpos/setang console line's exact format.
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
        cache.WriteSibling(path, GrenadeSidecar.Suffix, GrenadeSidecar.Serialize(document));
        if (stamp)
        {
            DemoCacheStore.StampGrenades(record);
            record.GrenadeState = DemoAnalysisState.Indexed;
            record.GrenadeCount = document.Grenades.Count;
            record.GrenadeWalker = GrenadeWalker.Version;
        }

        cache.Upsert(record);
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
        GrenadeIndex index = new(cache,
            zones ?? new RoundIndexEvaluatorTests.MapZones((Mirage, MirageZones), ("de_inferno", InfernoZones)));
        index.Load();
        return index;
    }

    private static GrenadeQuery SmokesIntoCt(IReadOnlySet<string>? demos = null) => new(Mirage,
        new HashSet<GrenadeKind> { GrenadeKind.Smoke }, new HashSet<string> { "CTSpawn" }, DemoPaths: demos);

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
            await Assert.That(spawn.Lineups.Count).IsEqualTo(3);

            GrenadeLineup a = spawn.Lineups[0];
            await Assert.That(a.Throws.Count).IsEqualTo(9).Because("five units of jitter rounds to one position");
            await Assert.That(a.DemoCount).IsEqualTo(9);
            await Assert.That(a.JumpThrow).IsFalse();
            await Assert.That(a.Origin).IsEqualTo(new WorldPoint(512, 288, -160));

            GrenadeLineup jump = spawn.Lineups[1];
            await Assert.That(jump.Throws.Count).IsEqualTo(4);
            await Assert.That(jump.JumpThrow).IsTrue().Because("the same spot thrown another way is its own lineup");
            GrenadeLineup standing = spawn.Lineups[2];
            await Assert.That(standing.Throws.Count).IsEqualTo(2);
            await Assert.That(standing.JumpThrow).IsFalse();
            await Assert.That(GrenadeIndex.RoundedOrigin(standing.Origin)).IsEqualTo(GrenadeIndex.RoundedOrigin(jump.Origin));

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
        GrenadeIndexEvaluator evaluator = new(cache, () => true,
            walk: _ => new GrenadeWalk(MirageRows(1), 1, ReconstructedInputSource.DecoderName, 4));
        using GrenadeIndex index = new(cache, new RoundIndexEvaluatorTests.MapZones((Mirage, MirageZones)), evaluator);
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
    public async Task TheModule_ContributesOneMainTab_UnderThePersistedIds()
    {
        UtilityBookModule module = new(() => throw new InvalidOperationException("never built here"));
        WorkspaceTabDescriptor tab = module.CreateTabs(null!).Single();
        FeatureDescriptor? feature = FeatureCatalog.ById(UtilityBookModule.TabFeatureId);

        using (Assert.Multiple())
        {
            await Assert.That(module.Id).IsEqualTo("net.demoviewer.utilitybook");
            await Assert.That(tab.TabId).IsEqualTo("utilitybook.browser");
            await Assert.That(tab.Header).IsEqualTo("Utility Book");
            await Assert.That(tab.Placement).IsEqualTo(TabPlacement.Main);
            await Assert.That(tab.Order).IsEqualTo(9).Because("after the Strat Book");
            await Assert.That(tab.DataContext).IsNull();
            await Assert.That(feature).IsNotNull();
            await Assert.That(feature!.Scope).IsEqualTo(FeatureScope.Tab);
            await Assert.That(ShellModuleFeatureGate.DesktopOnlyIds).DoesNotContain(UtilityBookModule.TabFeatureId);
        }
    }

    [Test]
    public async Task TheTab_ListsTheClusters_AndWatchSeeksBeforeTheRelease()
    {
        using GrenadeIndex index = Loaded(Library());
        RecordingPlayback playback = new();
        using UtilityBookTabViewModel vm = new(index, playback, isBrowser: false,
            renderer: () => new GrenadeLineupThumbnailRenderer(_ => null), post: action => action(), decode: _ => null);

        vm.SelectedMap = Mirage;
        vm.SelectedPlace = "CTSpawn";
        await vm.ThumbnailTask;

        using (Assert.Multiple())
        {
            await Assert.That(vm.SelectedKind!.Value).IsEqualTo(GrenadeKind.Smoke).Because("smokes are the default");
            await Assert.That(vm.Places).IsEquivalentTo(new[] { UtilityBookTabViewModel.AnyPlace, "CTSpawn", "TSpawn" });
            await Assert.That(vm.Clusters.Count).IsEqualTo(2);
            await Assert.That(vm.Clusters[0].Title).IsEqualTo("Smoke into CTSpawn");
            await Assert.That(vm.Clusters[0].Summary).IsEqualTo("15 throws from 3 positions");
            await Assert.That(vm.Clusters[0].Lineups[0].OriginText).IsEqualTo("from (512, 288, -160)");
            await Assert.That(vm.Clusters[0].Lineups[1].DetailText).IsEqualTo("4 throws in 4 demos, jump-throw");
            await Assert.That(vm.StatusLine).IsEqualTo("36 grenades from 10 demos");
        }

        await vm.Clusters[0].Lineups[1].WatchCommand.ExecuteAsync(null);
        await Assert.That(playback.Seeks.Single()).IsEqualTo((DemoPath(1), 2000 - UtilityBookTabViewModel.WatchLeadTicks));
    }

    [Test]
    public async Task LineupCards_PrintTheCS2UtilFieldSet_AndTheConsoleLineIsTheSetposSetangFormat()
    {
        DemoCacheStore cache = new(null);
        GrenadeRow row = Row("a", GrenadeKind.Smoke, new Vector3(512, 288, -160), new Vector3(-1400, -1400, -170));
        row.ReleaseEyePitch = -18.12f;
        row.ReleaseEyeYaw = -15.3f;
        row.Movement = MovementClass.Running;
        row.AirTimeTicks = 115;
        Indexed(cache, DemoPath(1), Mirage, "sha1", [row]);

        using GrenadeIndex index = Loaded(cache);
        using UtilityBookTabViewModel vm = new(index, isBrowser: false,
            renderer: () => new GrenadeLineupThumbnailRenderer(_ => null), post: action => action(), decode: _ => null);
        vm.SelectedMap = Mirage;
        await vm.ThumbnailTask;

        GrenadeLineupRow card = vm.Clusters.Single().Lineups.Single();
        using (Assert.Multiple())
        {
            await Assert.That(card.Map).IsEqualTo(Mirage);
            await Assert.That(card.TypeText).IsEqualTo("Smoke");
            await Assert.That(card.JumpThrowText).IsEqualTo("Standard throw");
            await Assert.That(card.MovementText).IsEqualTo("Running");
            await Assert.That(card.AirTimeText).IsEqualTo("1.8s air time").Because("115 ticks at the default 64 tick rate");
            await Assert.That(card.ConsoleText).IsEqualTo("setpos 512.00 288.00 -160.00; setang -18.12 -15.30 0.00")
                .Because("a card is copy-pasteable into a console at this exact shape");
            await Assert.That(card.LandingText).IsEqualTo("at (-1400, -1400, -170)");
            await Assert.That(card.HasThumbnail).IsFalse().Because("no baked bundle on this host");
            await Assert.That(card.ThumbnailNote).IsEqualTo(GrenadeLineupRow.NoRadarNote);
        }
    }

    [Test]
    public async Task ALineupWithNoReleaseAngles_PrintsWhatGrenadeConsoleAsksFor()
    {
        using GrenadeIndex index = Loaded(Library());
        using UtilityBookTabViewModel vm = new(index, isBrowser: false,
            renderer: () => new GrenadeLineupThumbnailRenderer(_ => null), post: action => action(), decode: _ => null);
        vm.SelectedMap = Mirage;
        await vm.ThumbnailTask;

        await Assert.That(vm.Clusters[0].Lineups[0].ConsoleText).IsEqualTo(GrenadeLineupRow.NoConsoleText)
            .Because("the fixture rows never set release eye angles");
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
