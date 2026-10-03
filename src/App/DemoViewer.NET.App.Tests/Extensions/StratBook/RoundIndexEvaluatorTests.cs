#region

using System.Text.Json;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The evaluator's staleness and write logic over an in-memory cache and store: wanted from the
///     index row alone and never before Round Facts has written, the sidecar and the stamp written on
///     a held parse, a throw stamping Failed and not re-wanted until retried, forced paths at user
///     priority regardless of the opt-in, the opportunistic hand-off gated on Wants, and a rebuild
///     clearing every fingerprint while the sidecars stay.
/// </summary>
public class RoundIndexEvaluatorTests
{
    private const string Demo = "/d/match.dem";

    private static RoundFactsRows TwoRounds() => Facts(Round(1, 1000, 2000), Round(2, 3000, 4000));

    // A synthetic parse carries no entity data, so the walk yields nothing and the document has rounds
    // with no runs; the evaluator's logic is what is under test, the builder has its own.
    private static ParsedDemo Parse() => RoundIndexTestData.Demo(lastTick: 5000);

    private static (DemoCacheStore Cache, RoundIndexStore Store, RoundIndexEvaluator Evaluator) Wire(
        bool background = true, RoundFactsRows? facts = null, bool withFacts = true)
    {
        DemoCacheStore cache = new(null);
        cache.Upsert(ParsedRecord(Demo, sha: "abc", facts: withFacts ? facts ?? TwoRounds() : null));
        RoundIndexStore store = new(null, cache);
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
        RoundIndexEvaluator evaluator = new(cache, store, sources, () => background, walk: _ => []);
        return (cache, store, evaluator);
    }

    [Test]
    public async Task NotWanted_UntilTheRowCarriesRoundFacts()
    {
        (DemoCacheStore cache, RoundIndexStore store, RoundIndexEvaluator evaluator) = Wire(withFacts: false);
        int written = 0;
        evaluator.Written += _ => written++;

        await Assert.That(evaluator.Wants(Demo)).IsFalse();
        await Assert.That(evaluator.PendingPaths()).IsEmpty();

        evaluator.OnParsedOpportunistically(Demo, Parse());
        evaluator.Evaluate(Demo, Parse());

        using (Assert.Multiple())
        {
            await Assert.That(written).IsEqualTo(0);
            await Assert.That(store.TryRead(Demo)).IsNull().Because("there is no extractor to fall back to");
            await Assert.That(cache.TryLoadRecord(Demo)!.RoundIndex.IsPresent).IsFalse();
        }

        // Round Facts lands: the same pass's next fan-out reaches this one.
        cache.UpdateExisting(Demo, r =>
        {
            r.RoundFacts = TwoRounds();
            r.RoundFactsFingerprint = "rf-A";
        });
        await Assert.That(evaluator.Wants(Demo)).IsTrue();
        await Assert.That(evaluator.PendingPaths()).Contains(Demo);
    }

    [Test]
    public async Task ARowClaimingFactsTheRecordLacks_IsRealigned_SoTheDemoIsNotRequeuedForever()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-ri-realign-{Guid.NewGuid():N}");
        try
        {
            DemoCacheStore first = new(root);
            first.Upsert(ParsedRecord(Demo, sha: "abc", facts: TwoRounds()));
            first.UpdateExisting(Demo, r => r.RoundFactsFingerprint = "rf-A");
            first.SaveIndex();

            // A later write replaced the sidecar without the facts and index.json was never saved again:
            // the state a restart found four real demos in, where the coordinator re-parsed them forever.
            DemoCacheRecord stale = first.TryLoadRecord(Demo)!;
            stale.RoundFacts = null;
            stale.RoundFactsFingerprint = null;
            await File.WriteAllTextAsync(first.SidecarPathFor(Demo)!, JsonSerializer.Serialize(stale));

            DemoCacheStore cache = new(root);
            RoundIndexEvaluator evaluator = new(cache, new RoundIndexStore(null, cache),
                new RoundIndexPlaceSources(() => RoundIndexTokenSource.Pawn), () => true, walk: _ => []);
            await Assert.That(evaluator.Wants(Demo)).IsTrue().Because("the stale row claims Round Facts");

            evaluator.Evaluate(Demo, Parse());

            using (Assert.Multiple())
            {
                await Assert.That(evaluator.Wants(Demo)).IsFalse();
                await Assert.That(cache.TryGetIndex(Demo)!.RoundFactsSchema).IsEqualTo(0)
                    .Because("the row now matches the sidecar, so Round Facts wants the demo again");
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
    public async Task Evaluate_WritesTheSidecarThenTheStamp_AndRaisesIndexed()
    {
        (DemoCacheStore cache, RoundIndexStore store, RoundIndexEvaluator evaluator) = Wire();
        List<RoundIndexedEvent> written = [];
        List<RoundIndexedEvent> indexed = [];
        evaluator.Written += written.Add;
        evaluator.Indexed += indexed.Add;

        evaluator.Evaluate(Demo, Parse());
        DemoCacheRecord record = cache.TryLoadRecord(Demo)!;
        DemoCacheIndexEntry entry = cache.TryGetIndex(Demo)!;
        RoundIndexDocument? document = store.TryRead(Demo);

        using (Assert.Multiple())
        {
            await Assert.That(document).IsNotNull();
            await Assert.That(document!.Demo.Sha256).IsEqualTo("abc");
            await Assert.That(document.Demo.StableKey).IsEqualTo(DemoCacheStore.StableKey(Demo));
            await Assert.That(document.Demo.FileName).IsEqualTo("match.dem");
            await Assert.That(document.Rounds.Count).IsEqualTo(2);
            await Assert.That(document.Fingerprint).IsEqualTo("ri1;cadence=1;token=1;rf=1;src=pawn;pos=2");
            await Assert.That(record.RoundIndex.IsPresent).IsTrue();
            await Assert.That(record.RoundIndexState).IsEqualTo(RoundIndexState.Indexed);
            await Assert.That(record.RoundIndexFingerprint).IsEqualTo(document.Fingerprint);
            await Assert.That(record.RoundFacts).IsNotNull().Because("the rows stay");
            await Assert.That(entry.RoundIndexState).IsEqualTo(RoundIndexState.Indexed);
            await Assert.That(entry.RoundIndexComputedAtTicks).IsEqualTo(record.RoundIndex.ComputedAtTicks);
            await Assert.That(written.Count).IsEqualTo(1);
            await Assert.That(indexed.Count).IsEqualTo(1);
            await Assert.That(indexed[0].DemoPath).IsEqualTo(Demo);
            await Assert.That(indexed[0].DemoSha256).IsEqualTo("abc");
            await Assert.That(indexed[0].Map).IsEqualTo("de_nuke");
            await Assert.That(indexed[0].ComputedAtTicks).IsEqualTo(record.RoundIndex.ComputedAtTicks);
            await Assert.That(evaluator.Wants(Demo)).IsFalse();
            await Assert.That(evaluator.PendingPaths()).IsEmpty();
        }

        // A second pass sees a current index and writes nothing.
        evaluator.Evaluate(Demo, Parse());
        await Assert.That(written.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AThrow_StampsFailed_WhichIsNotReWanted_UntilRetried()
    {
        (DemoCacheStore cache, _, RoundIndexEvaluator evaluator) = Wire();
        // A side with no slot list makes the builder throw on the join; any throw on the build or write
        // path lands in the same place.
        cache.UpdateExisting(Demo, r => r.RoundFacts!.Rounds[0].Ct.Slots = null!);

        evaluator.Evaluate(Demo, Parse());
        DemoCacheIndexEntry entry = cache.TryGetIndex(Demo)!;

        using (Assert.Multiple())
        {
            await Assert.That(entry.RoundIndexState).IsEqualTo(RoundIndexState.Failed);
            await Assert.That(entry.RoundIndexSchema).IsEqualTo(0).Because("no stamp without a file");
            await Assert.That(evaluator.Wants(Demo)).IsFalse().Because("a failed row is never re-queued on its own");
            await Assert.That(evaluator.PendingPaths()).IsEmpty();
        }

        evaluator.Request(Demo);

        using (Assert.Multiple())
        {
            await Assert.That(cache.TryGetIndex(Demo)!.RoundIndexState).IsEqualTo(RoundIndexState.Pending);
            await Assert.That(evaluator.Wants(Demo)).IsTrue();
            await Assert.That(evaluator.PriorityFor(Demo)).IsEqualTo(Services.DemoProcessing.DemoJobPriority.UserRequested);
        }

        evaluator.OnFailed(Demo);
        await Assert.That(evaluator.PriorityFor(Demo)).IsEqualTo(Services.DemoProcessing.DemoJobPriority.Background)
            .Because("a failed parse clears the forced flag and leaves the stamp alone");
    }

    [Test]
    public async Task WithBackgroundOff_OnlyForcedPathsAreWanted_AndTheHandOffIsGated()
    {
        (DemoCacheStore cache, RoundIndexStore store, RoundIndexEvaluator evaluator) = Wire(background: false);

        await Assert.That(evaluator.Wants(Demo)).IsFalse();
        await Assert.That(evaluator.PendingPaths()).IsEmpty();

        evaluator.OnParsedOpportunistically(Demo, Parse());
        await Assert.That(store.TryRead(Demo)).IsNull().Because("the hand-off is a no-op when the demo is not wanted");

        evaluator.Request(Demo);
        using (Assert.Multiple())
        {
            await Assert.That(evaluator.Wants(Demo)).IsTrue();
            await Assert.That(evaluator.PendingPaths()).Contains(Demo);
            await Assert.That(evaluator.PriorityFor(Demo)).IsEqualTo(Services.DemoProcessing.DemoJobPriority.UserRequested);
        }

        evaluator.OnParsedOpportunistically(Demo, Parse());
        using (Assert.Multiple())
        {
            await Assert.That(store.TryRead(Demo)).IsNotNull();
            await Assert.That(cache.TryGetIndex(Demo)!.RoundIndexState).IsEqualTo(RoundIndexState.Indexed);
            await Assert.That(evaluator.PriorityFor(Demo)).IsEqualTo(Services.DemoProcessing.DemoJobPriority.Background)
                .Because("the forced flag clears once the demo is evaluated");
        }
    }

    [Test]
    public async Task RebuildAll_ClearsEveryFingerprint_AndKeepsTheSidecars()
    {
        (DemoCacheStore cache, RoundIndexStore store, RoundIndexEvaluator evaluator) = Wire();
        evaluator.Evaluate(Demo, Parse());
        int changed = 0;
        cache.Changed += _ => changed++;

        evaluator.RebuildAll();

        using (Assert.Multiple())
        {
            await Assert.That(changed).IsEqualTo(1).Because("a batch raises Changed once");
            await Assert.That(cache.TryGetIndex(Demo)!.RoundIndexFingerprint).IsNull();
            await Assert.That(cache.TryGetIndex(Demo)!.RoundIndexState).IsEqualTo(RoundIndexState.Indexed)
                .Because("stale, not gone: the old rows keep answering until replaced");
            await Assert.That(store.TryRead(Demo)).IsNotNull();
            await Assert.That(evaluator.Wants(Demo)).IsTrue();
            await Assert.That(evaluator.StaleCount()).IsEqualTo(1);
        }
    }

    [Test]
    public async Task TheFingerprint_MovesWithTheTokenSource_PerMap()
    {
        RoundIndexTokenSource mode = RoundIndexTokenSource.Pawn;
        RoundIndexBuilderTests.FakeZoneResolver nuke = new("zv-nuke", _ => "Ramp");
        RoundIndexPlaceSources sources = new(() => mode, new MapZones(("de_nuke", nuke)));

        string pawnNuke = sources.FingerprintFor("de_nuke");
        mode = RoundIndexTokenSource.Zones;
        string zonesNuke = sources.FingerprintFor("de_nuke");
        string zonesDust = sources.FingerprintFor("de_dust2");

        using (Assert.Multiple())
        {
            await Assert.That(pawnNuke).IsEqualTo("ri1;cadence=1;token=1;rf=1;src=pawn;pos=2");
            await Assert.That(zonesNuke).IsEqualTo("ri1;cadence=1;token=1;rf=1;src=zones;zv=zv-nuke;pos=2");
            await Assert.That(zonesDust).IsEqualTo(pawnNuke).Because("a map without zones falls back to the pawn");
            await Assert.That(sources.IsZoneFallback("de_dust2")).IsTrue();
            await Assert.That(sources.IsZoneFallback("de_nuke")).IsFalse();
            await Assert.That(sources.SourceFor("de_nuke")).IsTypeOf<ZonePlaceSource>();
            await Assert.That(sources.SourceFor("de_dust2")).IsTypeOf<PawnPlaceSource>();
            await Assert.That(sources.SourceFor(null)).IsTypeOf<PawnPlaceSource>();
        }
    }

    internal sealed class MapZones(params (string Map, IZonePlaceResolver Resolver)[] maps) : IZonePlaceResolverSource
    {
        public IZonePlaceResolver? TryGet(string map) =>
            maps.FirstOrDefault(m => string.Equals(m.Map, map, StringComparison.OrdinalIgnoreCase)).Resolver;
    }
}
