#region

using System.Text.Json;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Strats.Mining;
using DemoViewer.NET.Services.Tags;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Strat Mining's inbox over a synthetic library: three demos run the same A take and one takes B; mining
///     finds the A execute from cached files alone, promoting it saves a valid strat built from the medoid round
///     and writes each member round as a run the record counts, and a dismissal survives a restart.
/// </summary>
[NotInParallel]
public class StratMiningServiceTests
{
    private const int Rate = 64;
    private const int FreezeEnd = 1000;
    private const string Map = "de_mirage";

    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    internal static readonly RoundIndexPlaceSources _sources = new(() => RoundIndexTokenSource.Pawn);

    private static string Sha(int n) => new string((char)('a' + n), 64);

    // Places: 0 TSpawn, 1 Palace, 2 BombsiteA, 3 BombsiteB, 4 CTSpawn.
    private static int Place(float x, float y) => x >= 900 ? y >= 0 ? 2 : 3 : x > 300 ? 1 : 0;

    internal static DemoCacheRecord Record(int n, BombSite site)
    {
        DemoCacheRecord record = new()
        {
            Path = $"/d/m{n}.dem",
            Size = 1000 + n,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(n).Ticks,
            Map = Map,
            Sha256 = Sha(n),
            RoundFacts = new RoundFactsRows
            {
                Schema = DemoCacheRecord.RoundFactsSchema,
                Clock = new RoundFactsClock { TickRate = Rate, FrameCount = 100_000, LastTick = 100_000 },
                Rounds =
                [
                    new RoundFacts
                    {
                        Number = 1,
                        IsLive = true,
                        FreezeEndTick = FreezeEnd,
                        PlantTick = FreezeEnd + 50 * Rate,
                        PlantSite = site,
                        PlanterSlot = 0,
                        EndTick = FreezeEnd + 80 * Rate,
                        WinnerSide = 2,
                        Ct = new SideFacts { Side = 3, Slots = [5, 6, 7, 8, 9], BuyType = BuyType.Full },
                        T = new SideFacts { Side = 2, Slots = [0, 1, 2, 3, 4], BuyType = BuyType.Full }
                    }
                ]
            }
        };
        for (int slot = 0; slot < 10; slot++)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot, Name = $"p{slot}", SteamId64 = $"7656{n}{slot:D3}", Team = slot < 5 ? 2 : 3 });
        }

        DemoCacheStore.StampParse(record);
        DemoCacheStore.StampAnalysis(record);
        return record;
    }

    // The Ts walk from spawn to the site over 45 s and stay; the CTs hold. `jitter` moves one demo's walk a little.
    internal static RoundPositionsDocument Positions(int n, BombSite site, float jitter) => new()
    {
        Fingerprint = _sources.FingerprintFor(Map),
        Demo = new RoundPositionsDemo { StableKey = DemoCacheStore.StableKey($"/d/m{n}.dem"), Sha256 = Sha(n) },
        CadenceTicks = Rate,
        Places = ["TSpawn", "Palace", "BombsiteA", "BombsiteB", "CTSpawn"],
        Rounds =
        [
            new RoundPositionsRound
            {
                Number = 1,
                FreezeEndTick = FreezeEnd,
                Ct = [5, 6, 7, 8, 9],
                Pos =
                [
                    .. Enumerable.Range(0, 80).Select(step =>
                    {
                        float t = Math.Min(1, step / 45f);
                        List<RoundPosition> at = [];
                        for (int slot = 0; slot < 5; slot++)
                        {
                            float x = t * 1000 + slot * 20 + jitter;
                            float y = (site == BombSite.A ? 1 : -1) * t * (300 + slot * 40);
                            at.Add(new RoundPosition(slot, (int)x, (int)y, 0, Place(x, y)));
                        }

                        for (int slot = 5; slot < 10; slot++)
                        {
                            at.Add(new RoundPosition(slot, 2000, slot * 100, 0, 4));
                        }

                        return at;
                    })
                ]
            }
        ]
    };

    internal sealed class Library : IDisposable
    {
        public required DemoCacheStore Cache { get; init; }
        public required RoundIndexStore Positions { get; init; }
        public required StratStore Strats { get; init; }
        public required TagStore Tags { get; init; }
        public required string Root { get; init; }

        public StratMiningService Service(Func<Action, Task>? run = null) =>
            new(Cache, Positions, _sources.FingerprintFor, null, null, Strats, Tags,
                Path.Combine(Root, "cache"), Root, run: run ?? _inline) { QuietDelay = Timeout.InfiniteTimeSpan };

        public void Dispose()
        {
            Positions.Dispose();
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
        }

        public static Library Create()
        {
            DemoCacheStore cache = new(null);
            RoundIndexStore positions = new(null, cache);
            using (cache.BeginBatch())
            {
                for (int n = 1; n <= 4; n++)
                {
                    cache.Upsert(Record(n, n == 4 ? BombSite.B : BombSite.A));
                }
            }

            for (int n = 1; n <= 4; n++)
            {
                positions.WritePositions($"/d/m{n}.dem", Positions(n, n == 4 ? BombSite.B : BombSite.A, n * 40));
            }

            return new Library
            {
                Cache = cache,
                Positions = positions,
                Strats = new StratStore(null),
                Tags = new TagStore(null),
                Root = Path.Combine(Path.GetTempPath(), "dv-mining-" + Guid.NewGuid().ToString("N"))
            };
        }
    }

    [Test]
    public async Task Mining_FindsTheATake_AndPromotingItSavesAStrat_WithItsRuns()
    {
        using Library library = Library.Create();
        using StratMiningService service = library.Service();
        await service.MineAsync();

        MinedPattern execute = service.Patterns.Select(p => p.Pattern).Single(p => p.Kind == PatternKind.Execute);
        using (Assert.Multiple())
        {
            await Assert.That(execute.Site).IsEqualTo("A");
            await Assert.That(execute.Members.Select(m => m.DemoPath).Order()).IsEquivalentTo(["/d/m1.dem", "/d/m2.dem", "/d/m3.dem"])
                .Because("the B take is another site");
            await Assert.That(execute.UtilityCompared).IsFalse().Because("no grenade rows were indexed");
            await Assert.That(service.LastRead.Demos).IsEqualTo(4);
        }

        StratDocument doc = service.Promote(execute.Key, StratOwner.Me(), new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc))!;
        using (Assert.Multiple())
        {
            await Assert.That(doc).IsNotNull();
            await Assert.That(library.Strats.Index.Single().Id).IsEqualTo(doc.Id);
            await Assert.That(doc.Type).IsEqualTo("execute");
            await Assert.That(doc.TargetSite).IsEqualTo("A");
            await Assert.That(doc.Tempo).IsEqualTo("mid");
            await Assert.That(doc.Economy).IsEqualTo("full");
            await Assert.That(doc.Tags).IsEquivalentTo([MinedStratBuilder.Tag]);
            await Assert.That(doc.Steps[0].Verb).IsEqualTo("hold");
            await Assert.That(doc.Steps.Select(s => s.Verb)).Contains("plant");
            await Assert.That(doc.Steps.Select(s => s.Verb)).Contains("move");
            await Assert.That(doc.Notes).Contains("Mined from 3 rounds in 3 demos. Won 3 of 3.");
            await Assert.That(StratValidator.Validate(doc).Where(i => i.Severity == StratIssueSeverity.Refusal)).IsEmpty();
            await Assert.That(doc.Extra![MinedStratBuilder.ExtraKey].GetProperty("Members").GetArrayLength()).IsEqualTo(3);
            await Assert.That(service.Patterns.Single(p => p.Pattern.Key == execute.Key).StratId).IsEqualTo(doc.Id);
        }

        StratRecord record = StratEvidence.Build(doc.Id, doc.Revision, doc.Side,
            [.. Enumerable.Range(1, 3).Select(n => library.Tags.TryLoad(Sha(n))!)]);
        await Assert.That(record.Total.Run).IsEqualTo(3).Because("each member round is a run of the new strat");
        await Assert.That(new StratEvidenceService(library.Tags).Compute(doc).Total.Run).IsEqualTo(3)
            .Because("the record panel finds the runs through the tag index");
    }

    [Test]
    public async Task ACancelledPreview_StopsBuilding_AndReturnsNothing()
    {
        using Library library = Library.Create();
        using StratMiningService service = library.Service();
        await service.MineAsync();
        MinedPattern execute = service.Patterns.Select(p => p.Pattern).Single(p => p.Kind == PatternKind.Execute);
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();

        Assert.Throws<OperationCanceledException>(() => service.Build(execute, StratOwner.Me(), DateTime.UtcNow, cancel.Token));
        await Assert.That(await service.PreviewAsync(execute, StratOwner.Me(), DateTime.UtcNow, cancel.Token)).IsNull();
    }

    [Test]
    public async Task ACachedGrenade_BecomesAThrowStep_WithItsLandingAndAnArrow()
    {
        RoundPositionsDocument positions = Positions(1, BombSite.A, 0);
        RoundFacts facts = Record(1, BombSite.A).RoundFacts!.Rounds[0];
        GrenadeRow incendiary = new()
        {
            Id = "g1",
            Kind = GrenadeKind.Incendiary,
            ThrowerSlot = 2,
            ThrowerTeam = 2,
            RoundNumber = 1,
            ReleaseTick = FreezeEnd + 5 * Rate,
            ReleasePosition = new WorldPoint(100, 50, 0),
            DetonationTick = FreezeEnd + 7 * Rate,
            DetonationPosition = new WorldPoint(1200, 400, 0)
        };
        Dictionary<int, (ulong, string)> players = Enumerable.Range(0, 10).ToDictionary(s => s, s => ((ulong)(100 + s), $"p{s}"));

        RoundCapture capture = CachedRoundCapture.Build(positions, positions.Rounds[0], facts, [incendiary], players, Rate, FreezeEnd + 60 * Rate);
        IReadOnlyDictionary<char, ulong> slots = StratFromRound.SlotMap(capture.FreezeEnd.Pawns.Where(p => p.Team == 2), null, null);
        StratCaptureOptions options = new(2, StratFromRound.Tokens(capture.FreezeEnd.Pawns, 2, slots), 115, true, StratFromRound.QuantizedLevel);
        StratStep thrown = StratFromRound.Steps(capture, options).Single(s => s.Verb == "throw");

        using (Assert.Multiple())
        {
            await Assert.That(capture.Moments.Select(m => m.Trigger)).Contains(CaptureTrigger.Plant);
            await Assert.That(thrown.Utility!.Kind).IsEqualTo("molotov").Because("a fire is a molotov whichever side threw it");
            await Assert.That(thrown.Utility.Landing!.X).IsEqualTo(1200);
            await Assert.That(thrown.Actor).IsEqualTo("C").Because("slot 2 is the third T in slot order");
            await Assert.That(thrown.Strokes).HasCount().EqualTo(1).Because("the arrow from the release point");
            await Assert.That(thrown.AtSeconds).IsEqualTo(108);
        }
    }

    internal static MinedPattern Pattern(string key, params int[] rounds)
    {
        List<MinedMember> members = [.. rounds.Select(r => new MinedMember($"/d/m{r}.dem", Sha(r % 20), r, null, true, BuyType.Full, 0, 0, Rate, 0))];
        return new MinedPattern
        {
            Key = key,
            Kind = PatternKind.Setup,
            Map = Map,
            Side = 2,
            Members = members,
            Medoid = new RoundSignature { DemoPath = "/d/m1.dem", Round = 1, Map = Map, Side = 2, Kind = PatternKind.Setup, Anchors = [] }
        };
    }

    [Test]
    public async Task ADismissedPattern_WhoseKeyMoved_HandsItsStateToThePatternHoldingItsRounds()
    {
        List<MinedPattern> before = [Pattern("old", 1, 2, 3), Pattern("kept", 7, 8), Pattern("quiet", 11, 12)];
        List<MinedPattern> after = [Pattern("new", 2, 3, 4), Pattern("kept", 7, 8, 9), Pattern("other", 1, 11)];

        Dictionary<string, string> moves = StratMiningService.KeyMoves(before, after, k => k is "old" or "kept");

        await Assert.That(moves).IsEquivalentTo(new Dictionary<string, string> { ["old"] = "new" })
            .Because("two of old's three rounds are in new; kept kept its key; quiet had no state to carry");
    }

    [Test]
    public async Task ADismissal_SurvivesARestart_AndARemine()
    {
        using Library library = Library.Create();
        string key;
        using (StratMiningService first = library.Service())
        {
            await first.MineAsync();
            key = first.Patterns[0].Pattern.Key;
            first.Dismiss(key);
        }

        using StratMiningService second = library.Service();
        await Assert.That(second.Patterns.Single(p => p.Pattern.Key == key).Dismissed).IsTrue().Because("read back from disk");
        await second.MineAsync();
        await Assert.That(second.Patterns.Single(p => p.Pattern.Key == key).Dismissed).IsTrue();
        second.Restore(key);
        await Assert.That(second.Patterns.Single(p => p.Pattern.Key == key).Dismissed).IsFalse();
    }
}
