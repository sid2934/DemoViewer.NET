#region

using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A new blank strat starts with A to E in its side's spawn and O1 to O5 in the other's, fanned out, on the
///     round-start step; a strat that already places a token keeps it.
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
    public async Task VertigoAndNuke_PutTheSpawnsOnTheirOwnFloors()
    {
        StratSpawns vertigo = StratSpawns.From(RequireZones("de_vertigo"))!;
        StratSpawns nuke = StratSpawns.From(RequireZones("de_nuke"))!;

        await Assert.That(vertigo.T.All(s => s.LevelMinZ > 11000)).IsTrue();
        await Assert.That(vertigo.Ct.All(s => s.LevelMinZ > 11000)).IsTrue();
        await Assert.That(nuke.T.All(s => s.LevelMinZ is > -512 and < -256)).IsTrue();
    }

    [Test]
    public async Task Seed_PutsOwnSlotsInTheirSpawn_AndOpponentsInTheOther_OnARoundStartHold()
    {
        StratDocument t = StratDocument.Create(Guid.NewGuid(), Team, "de_mirage", "T", "default", "t", Created);
        StratDocument ct = StratDocument.Create(Guid.NewGuid(), Team, "de_mirage", "CT", "setup", "ct", Created);
        Fixed.Seed(t);
        Fixed.Seed(ct);

        StratStep step = t.Steps.Single();
        using (Assert.Multiple())
        {
            await Assert.That(step.AtSeconds).IsEqualTo(t.Clock.RoundSeconds);
            await Assert.That(step.Actor).IsEqualTo(StratVocabulary.ActorAll);
            await Assert.That(step.Verb).IsEqualTo("hold");
            await Assert.That(step.Positions.Select(p => p.Slot))
                .IsEquivalentTo(StratVocabulary.Slots.Concat(StratVocabulary.OpponentSlots));
            await Assert.That(step.Positions.Single(p => p.Slot == "A").X).IsEqualTo(1000);
            await Assert.That(step.Positions.Single(p => p.Slot == "O1").X).IsEqualTo(-1000);
            await Assert.That(step.Positions.Single(p => p.Slot == "O1").LevelMinZ).IsEqualTo(-128);
            await Assert.That(ct.Steps.Single().Positions.Single(p => p.Slot == "A").X).IsEqualTo(-1000);
            await Assert.That(ct.Steps.Single().Positions.Single(p => p.Slot == "O1").X).IsEqualTo(1000);
            await Assert.That(StratValidator.Validate(t).Any(i => i.Severity == StratIssueSeverity.Refusal)).IsFalse();
        }

        StratSceneProjection projection = StratSceneProjection.Build(t, StratPath.MainLine(t));
        await Assert.That(projection.Tracks.Count).IsEqualTo(10);
    }

    [Test]
    public async Task Seed_NeverMovesAPlacedToken_AndAddsTheRestToARoundStartFirstStep()
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_mirage", "T", "default", "d", Created);
        StratStep first = new() { Id = Guid.NewGuid(), AtSeconds = document.Clock.RoundSeconds, Actor = "all", Verb = "move" };
        first.Positions.Add(new StepPosition { Slot = "A", X = 5, Y = 6 });
        StratStep later = new() { Id = Guid.NewGuid(), AtSeconds = 90, Actor = "C", Verb = "move" };
        later.Positions.Add(new StepPosition { Slot = "C", X = 7, Y = 8 });
        document.Steps = [first, later];

        Fixed.Seed(document);

        using (Assert.Multiple())
        {
            await Assert.That(document.Steps.Count).IsEqualTo(2);
            await Assert.That(first.Positions.Single(p => p.Slot == "A").X).IsEqualTo(5);
            await Assert.That(first.Positions.Any(p => p.Slot == "C")).IsFalse().Because("C is placed at a later step");
            await Assert.That(first.Positions.Count).IsEqualTo(9).Because("A kept, eight added");
            await Assert.That(later.Positions.Count).IsEqualTo(1);
        }
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
            await Assert.That(document.Steps.Single().Positions.Count).IsEqualTo(10);
            await Assert.That(store.Load(document.Id).Document!.Steps.Single().Positions.Count).IsEqualTo(10);
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
        using StratBookTabViewModel vm = new(store, null, null, false, spawns: spawns);
        vm.SelectedMap = "de_mirage";

        vm.NewStratCommand.Execute(null);
        vm.NewStratCommand.Execute(null);
        await Assert.That(store.Index.Count).IsEqualTo(0).Because("the create waits for the zones read");

        gate.SetResult();
        await spawns.ForAsync("de_mirage");
        for (int i = 0; i < 200 && store.Index.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        await Assert.That(store.Index.Count).IsEqualTo(1).Because("a second click while waiting makes no second strat");
        await Assert.That(store.Load(store.Index[0].Id).Document!.Steps.Single().Positions.Count).IsEqualTo(10);
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
