#region

using System.Collections.Concurrent;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A new blank strat starts with A to E in its side's spawn and O1 to O5 in the other's, fanned out, as its
///     start; a strat that already has a start keeps it.
/// </summary>
public class StratSpawnTests
{
    private static readonly string[] ShippedMaps =
        ["de_ancient", "de_anubis", "de_cache", "de_dust2", "de_inferno", "de_mirage", "de_nuke", "de_overpass", "de_train", "de_vertigo"];

    private static readonly StratSpawns Fixed = new(
        [.. Enumerable.Range(0, 5).Select(i => new SpawnSpot(1000 + 100 * i, 0, 64))],
        [.. Enumerable.Range(0, 5).Select(i => new SpawnSpot(-1000 - 100 * i, 0, -128))]);

    [Test]
    [MethodDataSource(nameof(Maps))]
    public async Task EveryShippedMap_HasFiveSpreadSpotsPerSide_InsideItsBuyZone(string map)
    {
        ZoneSet zones = RequireZones(map);
        StratSpawns spawns = StratSpawns.From(zones)!;

        await Assert.That(spawns).IsNotNull();
        foreach ((string team, IReadOnlyList<SpawnSpot> spots) in new[] { ("T", spawns.T), ("CT", spawns.Ct) })
        {
            ZoneVolume buy = zones.Volumes.Where(v => v.Kind == ZoneVolumeKind.Buyzone && v.Team == team)
                .OrderByDescending(v => (v.Max.X - v.Min.X) * (v.Max.Y - v.Min.Y)).First();
            using (Assert.Multiple())
            {
                await Assert.That(spots.Count).IsEqualTo(5);
                foreach (SpawnSpot spot in spots)
                {
                    await Assert.That(spot.X).IsBetween(buy.Min.X - 1, buy.Max.X + 1).Because($"{map} {team}");
                    await Assert.That(spot.Y).IsBetween(buy.Min.Y - 1, buy.Max.Y + 1).Because($"{map} {team}");
                    await Assert.That(spot.LevelMinZ).IsBetween(buy.Min.Z - 128, buy.Max.Z + 64)
                        .Because($"{map} {team}: the token is on the spawn's floor, not level 0");
                }

                for (int i = 0; i < spots.Count; i++)
                {
                    for (int j = i + 1; j < spots.Count; j++)
                    {
                        double d = Math.Sqrt(Math.Pow(spots[i].X - spots[j].X, 2) + Math.Pow(spots[i].Y - spots[j].Y, 2));
                        await Assert.That(d).IsGreaterThanOrEqualTo(StratSpawns.Spacing / 2).Because($"{map} {team}: tokens do not stack");
                    }
                }
            }
        }
    }

    [Test]
    public async Task VertigoAndNuke_PutTheSpawnsOnTheBandsTheCanvasDraws()
    {
        StratSpawns vertigo = StratSpawns.From(RequireZones("de_vertigo"), StratFromRound.FloorLevelKeys(Floors("de_vertigo")))!;
        StratSpawns nuke = StratSpawns.From(RequireZones("de_nuke"), StratFromRound.FloorLevelKeys(Floors("de_nuke")))!;

        // Bundle floors: vertigo splits at Z 11728 (T spawn below, CT spawn above); nuke at -528, both spawns above.
        double below = MapSpace.QuantizeZ(-100000);
        using (Assert.Multiple())
        {
            await Assert.That(vertigo.T.Select(s => s.LevelMinZ).Distinct()).IsEquivalentTo(new[] { below });
            await Assert.That(vertigo.Ct.Select(s => s.LevelMinZ).Distinct()).IsEquivalentTo(new[] { MapSpace.QuantizeZ(11728) });
            await Assert.That(nuke.T.Select(s => s.LevelMinZ).Distinct()).IsEquivalentTo(new[] { MapSpace.QuantizeZ(-528) });
            await Assert.That(nuke.Ct.Select(s => s.LevelMinZ).Distinct()).IsEquivalentTo(new[] { MapSpace.QuantizeZ(-528) });
        }
    }

    private static List<FloorSlice> Floors(string map)
    {
        string dir = MapAssetBundleReader.FindBundleDirectory(map)!;
        return MapAssetBundleReader.TryRead(dir)!.Floors!.Select(f => new FloorSlice(f.MinZ, f.MaxZ)).ToList();
    }

    [Test]
    public async Task PlaceStart_PutsOwnSlotsInTheirSpawn_AndOpponentsInTheOther_AsASpawnStart()
    {
        StratDocument t = StratDocument.Create(Guid.NewGuid(), Team, "de_mirage", "T", "default", "t", Created);
        StratDocument ct = StratDocument.Create(Guid.NewGuid(), Team, "de_mirage", "CT", "setup", "ct", Created);
        Fixed.PlaceStart(t);
        Fixed.PlaceStart(ct);

        StratStart start = t.Start!;
        using (Assert.Multiple())
        {
            await Assert.That(t.Steps).IsEmpty().Because("the start is not a step");
            await Assert.That(start.Kind).IsEqualTo(StratStart.SpawnKind);
            await Assert.That(start.Positions.Select(p => p.Slot)).IsEquivalentTo(StratVocabulary.Slots.Concat(StratVocabulary.OpponentSlots));
            await Assert.That(start.Positions.Single(p => p.Slot == "A").X).IsEqualTo(1000);
            await Assert.That(start.Positions.Single(p => p.Slot == "O1").X).IsEqualTo(-1000);
            await Assert.That(start.Positions.Single(p => p.Slot == "O1").LevelMinZ).IsEqualTo(-128);
            await Assert.That(ct.Start!.Positions.Single(p => p.Slot == "A").X).IsEqualTo(-1000);
            await Assert.That(ct.Start.Positions.Single(p => p.Slot == "O1").X).IsEqualTo(1000);
            await Assert.That(StratValidator.Validate(t).Any(i => i.Severity == StratIssueSeverity.Refusal)).IsFalse();
        }

        StratSceneProjection projection = StratSceneProjection.Build(t, StratPath.MainLine(t));
        await Assert.That(projection.Tracks.Count).IsEqualTo(10);
    }

    [Test]
    public async Task PlaceStart_KeepsAStartAlreadyThere()
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_mirage", "T", "default", "d", Created);
        document.Start = new StratStart { Kind = StratStart.CustomKind, Positions = [new StartPosition { Slot = "A", X = 5, Y = 6 }] };

        Fixed.PlaceStart(document);

        using (Assert.Multiple())
        {
            await Assert.That(document.Start.Kind).IsEqualTo(StratStart.CustomKind);
            await Assert.That(document.Start.Positions.Single().X).IsEqualTo(5);
        }
    }

    [Test]
    [MethodDataSource(nameof(Maps))]
    public async Task EveryShippedMap_NamesTheSpawnPlace_OnEachSpot(string map)
    {
        StratSpawns spawns = StratSpawns.From(RequireZones(map))!;
        await Assert.That(spawns.T.Concat(spawns.Ct).All(s => !string.IsNullOrEmpty(s.Place))).IsTrue().Because(map);
    }

    [Test]
    public async Task NewStrat_StartsItsTokensInTheSpawns_BeforeTheFirstCommit()
    {
        StratStore store = new(null);
        StratSpawnSource spawns = new(_ => Fixed);
        await spawns.ForAsync("de_mirage");
        using StratBookTabViewModel vm = new(store, null, null, false, spawns: spawns);
        vm.SelectedMap = "de_mirage";

        vm.NewStratCommand.Execute(null);

        StratDocument document = vm.Session.Document!;
        using (Assert.Multiple())
        {
            await Assert.That(document.Revision).IsEqualTo(1).Because("the spawns are in the created revision, not an edit after it");
            await Assert.That(document.Start!.Positions.Count).IsEqualTo(10);
            await Assert.That(document.Steps).IsEmpty();
            await Assert.That(store.Load(document.Id).Document!.Start!.Positions.Count).IsEqualTo(10);
        }
    }

    [Test]
    public async Task NewStrat_OnAColdMap_CreatesWhenTheZonesLand()
    {
        StratStore store = new(null);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StratSpawnSource spawns = new(_ =>
        {
            gate.Task.Wait();
            return Fixed;
        });
        ConcurrentQueue<Action> posted = new();
        using StratBookTabViewModel vm = new(store, null, posted.Enqueue, false, spawns: spawns);
        Drain(posted);
        vm.SelectedMap = "de_mirage";

        vm.NewStratCommand.Execute(null);
        vm.NewStratCommand.Execute(null);
        await Assert.That(store.Index.Count).IsEqualTo(0).Because("the create waits for the zones read");

        gate.SetResult();
        await spawns.ForAsync("de_mirage");
        for (int i = 0; i < 500 && posted.IsEmpty; i++)
        {
            await Task.Delay(10);
        }

        Drain(posted);

        await Assert.That(store.Index.Count).IsEqualTo(1).Because("a second click while waiting makes no second strat");
        await Assert.That(vm.Session.Document!.Start!.Positions.Count).IsEqualTo(10);
    }

    [Test]
    public async Task APendingCreate_DisablesNewStrat_AndLandsWithoutClosingTheStratOpenedMeanwhile()
    {
        StratStore store = new(null);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StratSpawnSource spawns = new(map =>
        {
            if (map == "de_mirage")
            {
                gate.Task.Wait();
            }

            return Fixed;
        });
        await spawns.ForAsync("de_dust2");
        ConcurrentQueue<Action> posted = new();
        using StratBookTabViewModel vm = new(store, null, posted.Enqueue, false, spawns: spawns);
        vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
        vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
        Drain(posted);

        vm.SelectedMap = "de_mirage";
        vm.NewStratCommand.Execute(null);
        await Assert.That(vm.NewStratCommand.CanExecute(null)).IsFalse().Because("one create at a time");

        vm.SelectedMap = "de_dust2";
        vm.NewStratCommand.Execute(null);
        await Assert.That(store.Index.Count).IsEqualTo(0).Because("a click while one is pending does nothing");

        StratDocument dust = store.Create(vm.SelectedOwner!.Owner, "de_dust2", "T", "default", "dust");
        Drain(posted);
        vm.SelectedStrat = vm.Strats.Single(r => r.Id == dust.Id);
        Drain(posted);
        await Assert.That(vm.Session.Document?.Id).IsEqualTo(dust.Id);

        gate.SetResult();
        await spawns.ForAsync("de_mirage");
        for (int i = 0; i < 500 && posted.IsEmpty; i++)
        {
            await Task.Delay(10);
        }

        Drain(posted);

        using (Assert.Multiple())
        {
            await Assert.That(store.Index.Count(e => e.Map == "de_mirage")).IsEqualTo(1).Because("the late create still lands");
            await Assert.That(vm.Session.Document?.Id).IsEqualTo(dust.Id).Because("it does not close the strat opened meanwhile");
            await Assert.That(vm.SelectedStrat?.Id).IsEqualTo(dust.Id);
            await Assert.That(vm.NewStratCommand.CanExecute(null)).IsTrue();
        }
    }

    private static void Drain(ConcurrentQueue<Action> posted)
    {
        while (posted.TryDequeue(out Action? action))
        {
            action();
        }
    }

    public static IEnumerable<Func<string>> Maps() => ShippedMaps.Select(m => (Func<string>)(() => m));

    private static ZoneSet RequireZones(string map)
    {
        string? dir = MapAssetBundleReader.FindBundleDirectory(map);
        if (ZoneAssetPipeline.TryReadBaked(dir) is not { } zones)
        {
            throw new SkipTestException($"no {map} {ZoneAssetPipeline.FileName} in this checkout");
        }

        return zones;
    }
}
