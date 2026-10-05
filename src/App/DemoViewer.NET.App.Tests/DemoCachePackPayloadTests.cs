#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The demo cache record's pack seam: a pack's payload rides the
///     record as an opaque JSON object under its id, its stamps are what core and the index compare, a
///     record or index row written in the old flat shape folds on read and reports no new work, and the
///     index row does not grow for it.
/// </summary>
[NotInParallel]
public class DemoCachePackPayloadTests
{
    private const string Demo = "/d/match.dem";
    private const string FakePack = "net.demoviewer.pack.fake";
    private const string FakeFacet = "fake-facet";

    private static readonly string[] LegacyMembers =
    [
        "\"RoundFacts\":", "\"RoundFactsFingerprint\":", "\"RoundFactsSchema\":", "\"RoundIndex\":", "\"RoundIndexSchema\":",
        "\"RoundIndexComputedAtTicks\":", "\"RoundIndexState\":", "\"RoundIndexFingerprint\":", "\"RoundIndexRowCount\":",
        "\"SuggestionsFingerprint\":", "\"SuggestionCount\":", "\"Grenades\":", "\"GrenadeSchema\":", "\"GrenadeState\":",
        "\"GrenadeCount\":", "\"GrenadeWalker\":", "\"GrenadeInputCoverage\":"
    ];

    private static string TempRoot(string tag) => Path.Combine(Path.GetTempPath(), $"dv-pack-{tag}-{Guid.NewGuid():N}");

    // ── The new shape ────────────────────────────────────────────────────────

    [Test]
    public async Task NewShape_RoundTripsThroughTheStore_AndTheIndexMirrorsTheStamps()
    {
        string root = TempRoot("roundtrip");
        try
        {
            DemoCacheStore store = new(root);
            IPackPayloads payloads = store.Payloads(FakePack);
            DemoCacheRecord record = ParsedRecord(Demo, sha: "abc");
            payloads.Write(record, new FakePayload { Name = "one", Values = [1, 2, 3] });
            record.SetStamp(new PackStamp(FakeFacet, 2, "fp-1") { ComputedAtTicks = 77, Count = 3 });
            record.SetStamp(new PackStamp("other", 1, null) { State = DemoAnalysisState.Failed });
            store.Upsert(record);
            store.SaveIndex();

            DemoCacheStore reopened = new(root);
            DemoCacheRecord loaded = reopened.TryLoadRecord(Demo)!;
            DemoCacheIndexEntry entry = reopened.TryGetIndex(Demo)!;
            FakePayload? payload = reopened.Payloads(FakePack).Read<FakePayload>(loaded);
            string indexJson = await File.ReadAllTextAsync(Path.Combine(root, "index.json"));

            using (Assert.Multiple())
            {
                await Assert.That(payload).IsNotNull();
                await Assert.That(payload!.Name).IsEqualTo("one");
                await Assert.That(payload.Values).IsEquivalentTo([1, 2, 3]);
                await Assert.That(loaded.PackStamps).IsEquivalentTo(record.PackStamps);
                await Assert.That(entry.PackStamps).IsEquivalentTo(record.PackStamps);
                await Assert.That(entry.IsPackCurrent(FakeFacet, 2, "fp-1")).IsTrue();
                await Assert.That(entry.IsPackCurrent(FakeFacet, 1, "fp-1")).IsFalse().Because("another schema is stale");
                await Assert.That(entry.NeedsPack(FakeFacet, 2, "fp-2")).IsTrue();
                await Assert.That(entry.NeedsPack("other", 1, null)).IsFalse().Because("a failed facet waits for the user");
                await Assert.That(entry.NeedsPack("never", 1, "x")).IsTrue();
                await Assert.That(loaded.Packs.Keys).IsEquivalentTo([FakePack]);
                await Assert.That(loaded.UnknownMembers).IsNull();
                await Assert.That(indexJson).Contains("\"PackStamps\":[{\"Id\":\"fake-facet\"");
                await Assert.That(indexJson).DoesNotContain("\"Packs\"").Because("the index carries stamps, never payloads");
                foreach (string legacy in LegacyMembers)
                {
                    await Assert.That(indexJson).DoesNotContain(legacy);
                }
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Payloads_ReadAndWriteTyped_ByRecordAndByPath()
    {
        DemoCacheStore store = new(null);
        IPackPayloads payloads = store.Payloads(FakePack);
        store.Upsert(ParsedRecord(Demo));

        await Assert.That(payloads.Read<FakePayload>(Demo)).IsNull().Because("no payload yet");
        await Assert.That(payloads.Read<FakePayload>("/d/unknown.dem")).IsNull().Because("not cached at all");

        payloads.Write(Demo, new FakePayload { Name = "by path" });
        await Assert.That(payloads.Read<FakePayload>(Demo)!.Name).IsEqualTo("by path");
        await Assert.That(store.Payloads("another").Read<FakePayload>(Demo)).IsNull().Because("keyed by pack id");

        store.UpdateExisting(Demo, r => r.Packs[FakePack] = JsonSerializer.SerializeToElement(42));
        await Assert.That(payloads.Read<FakePayload>(Demo)).IsNull().Because("a payload the pack cannot read is not cached");

        payloads.Write<FakePayload>(Demo, null);
        await Assert.That(store.TryLoadRecord(Demo)!.Packs).IsEmpty();
    }

    [Test]
    public async Task MarkFailed_ClearFailed_SetStamp_KeepTheRestOfTheStamp()
    {
        DemoCacheRecord record = ParsedRecord(Demo);
        record.MarkFailed(FakeFacet);
        await Assert.That(record.Stamp(FakeFacet)).IsEqualTo(new PackStamp(FakeFacet, 0, null) { State = DemoAnalysisState.Failed });

        record.SetStamp(new PackStamp(FakeFacet, 3, "fp") { Count = 9, ComputedAtTicks = 5 });
        record.MarkFailed(FakeFacet);
        PackStamp failed = record.Stamp(FakeFacet)!;
        using (Assert.Multiple())
        {
            await Assert.That(failed.State).IsEqualTo(DemoAnalysisState.Failed);
            await Assert.That(failed.Schema).IsEqualTo(3);
            await Assert.That(failed.Fingerprint).IsEqualTo("fp");
            await Assert.That(failed.Count).IsEqualTo(9);
            await Assert.That(record.PackStamps.Count).IsEqualTo(1).Because("SetStamp replaces by id");
        }

        record.ClearFailed(FakeFacet);
        await Assert.That(record.Stamp(FakeFacet)!.State).IsEqualTo(DemoAnalysisState.Pending);
        record.ClearFailed("missing");
        await Assert.That(record.PackStamps.Count).IsEqualTo(1).Because("clearing an unknown facet adds nothing");
    }

    // ── The old shape ────────────────────────────────────────────────────────

    [Test]
    public async Task OldShape_FoldsOnRead_ReportsNoPendingWork_AndWritesTheNewShapeOnTheNextSave()
    {
        string root = TempRoot("legacy");
        try
        {
            RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
            string roundIndexFingerprint = sources.FingerprintFor("de_nuke");
            DemoCacheStore probe = new(null);
            SuggestedTagsService probeService = new(probe.Library(), probe.RoundFacts(), new ProposalStore(probe.Data()), null, new SiteRegionStore(null),
                () => DetectorProfile.Default, () => true, () => true);
            string suggestionsFingerprint = probeService.FingerprintFor("de_nuke");

            // The record and the index row exactly as the store wrote them in the old flat shape.
            RoundFactsRows rows = Facts(Round(1, 1000, 2000), Round(2, 3000, 4000));
            string key = DemoCacheStore.StableKey(Demo);
            Directory.CreateDirectory(Path.Combine(root, "demos"));
            await File.WriteAllBytesAsync(Path.Combine(root, "demos", key + ".json.gz"),
                SidecarJson.Gzip(Encoding.UTF8.GetBytes(OldRecordJson(rows, roundIndexFingerprint, suggestionsFingerprint))));
            await File.WriteAllTextAsync(Path.Combine(root, "index.json"),
                OldIndexJson(roundIndexFingerprint, suggestionsFingerprint));

            DemoCacheStore cache = new(root);
            DemoCacheIndexEntry entry = cache.TryGetIndex(Demo)!;
            DemoCacheRecord record = cache.TryLoadRecord(Demo)!;

            RoundFactsEvaluator roundFacts = new(cache, new NoRows(), new FixedIdentity("rf-A"));
            RoundIndexEvaluator roundIndex = new(cache.Library(), cache.RoundFacts(), new RoundIndexStore(cache.Data()), sources, () => true, walk: _ => []);
            SuggestedTagsService suggestions = new(cache.Library(), cache.RoundFacts(), new ProposalStore(cache.Data()), null, new SiteRegionStore(null),
                () => DetectorProfile.Default, () => true, () => true);
            GrenadeIndexEvaluator grenades = new(cache.Library(), cache.Grenades(), () => true);

            using (Assert.Multiple())
            {
                // Folded: the payload and every stamp, from both the sidecar and the index row. The old row never
                // mirrored the grenade write time, so that one tick count differs until the next save re-projects it.
                await Assert.That(entry.PackStamps.Select(s => s with { ComputedAtTicks = 0 }))
                    .IsEquivalentTo(record.PackStamps.Select(s => s with { ComputedAtTicks = 0 }));
                await Assert.That(record.PackStamps.Select(s => s.Id)).IsEquivalentTo(
                    [RoundFactsEvaluator.EvaluatorId, RoundIndexEvaluator.EvaluatorId, SuggestedTagsService.EvaluatorId, GrenadeIndexEvaluator.EvaluatorId]);
                await Assert.That(record.RoundFacts!.Rounds.Count).IsEqualTo(2).Because("the rows are the record's own member");
                await Assert.That(record.Packs[StratBookPack.PackId].GetProperty("GrenadeInputCoverage").GetDouble()).IsEqualTo(0.5);
                await Assert.That(record.Packs[StratBookPack.PackId].TryGetProperty("RoundFacts", out _)).IsFalse()
                    .Because("only the rows leave the payload");
                await Assert.That(record.RoundFactsStamp()).IsEqualTo(new PackStamp(RoundFactsEvaluator.EvaluatorId, RoundFactsRecords.Schema, "rf-A"));
                await Assert.That(record.Stamp(RoundIndexEvaluator.EvaluatorId)).IsEqualTo(
                    new PackStamp(RoundIndexEvaluator.EvaluatorId, RoundIndexStore.Schema, roundIndexFingerprint) { ComputedAtTicks = 100, Count = 12 });
                await Assert.That(record.Stamp(SuggestedTagsService.EvaluatorId)).IsEqualTo(
                    new PackStamp(SuggestedTagsService.EvaluatorId, ProposalStore.Schema, suggestionsFingerprint) { Count = 3 });
                await Assert.That(record.Stamp(GrenadeIndexEvaluator.EvaluatorId)).IsEqualTo(
                    new PackStamp(GrenadeIndexEvaluator.EvaluatorId, GrenadeStore.Schema, GrenadeWalker.Version) { ComputedAtTicks = 200, Count = 60 });
                await Assert.That(record.UnknownMembers).IsNull();
                await Assert.That(entry.UnknownMembers).IsNull();

                // Round Facts reads its stamp on the record as current: nothing to do.
                await Assert.That(roundFacts.Wants(Demo)).IsFalse();
                await Assert.That(roundFacts.PendingPaths()).IsEmpty();
                await Assert.That(entry.Stamp(SuggestedTagsService.EvaluatorId)!.Count).IsEqualTo(3);
                await Assert.That(entry.Stamp(RoundIndexEvaluator.EvaluatorId)!.ComputedAtTicks).IsEqualTo(100);

                // The round index, the proposals and the grenades keep their stamps in the per-demo data now: the
                // folded stamps on the record are not theirs any more, so each rebuilds the demo once.
                await Assert.That(roundIndex.Wants(Demo)).IsTrue();
                await Assert.That(suggestions.Wants(Demo)).IsTrue();
                await Assert.That(grenades.Wants(Demo)).IsTrue();
                await Assert.That(grenades.IsCurrent(Demo)).IsFalse();

                // The same fixture under another ruleset is stale, so the "nothing pending" above is a decision, not a blind spot.
                await Assert.That(new RoundFactsEvaluator(cache, new NoRows(), new FixedIdentity("rf-B")).Wants(Demo)).IsTrue();
            }

            // The next save writes the new shape and drops the flat members.
            cache.UpdateExisting(Demo, _ => { });
            cache.SaveIndex();
            string sidecar = Encoding.UTF8.GetString(Inflate(await File.ReadAllBytesAsync(cache.SidecarPathFor(Demo)!)));
            string index = await File.ReadAllTextAsync(Path.Combine(root, "index.json"));
            DemoCacheStore reopened = new(root);

            using (Assert.Multiple())
            {
                await Assert.That(sidecar).Contains("\"Packs\":{\"net.demoviewer.pack.stratbook\":{\"GrenadeInputCoverage\":0.5}}");
                await Assert.That(sidecar).Contains("\"PackStamps\":[");
                await Assert.That(index).Contains("\"PackStamps\":[");
                foreach (string legacy in LegacyMembers)
                {
                    await Assert.That(index).DoesNotContain(legacy);

                    // The rows are the record's own member and the coverage rides the pack payload, so both still appear.
                    if (legacy is not ("\"RoundFacts\":" or "\"GrenadeInputCoverage\":"))
                    {
                        await Assert.That(sidecar).DoesNotContain(legacy);
                    }
                }

                await Assert.That(sidecar).DoesNotContain("\"RoundFactsFingerprint\"");
                await Assert.That(sidecar.IndexOf("\"RoundFacts\":", StringComparison.Ordinal))
                    .IsLessThan(sidecar.IndexOf("\"Packs\":", StringComparison.Ordinal)).Because("the rows sit on the record, outside the payload");

                await Assert.That(reopened.TryGetIndex(Demo)!.PackStamps).IsEquivalentTo(record.PackStamps);
                await Assert.That(reopened.TryLoadRecord(Demo)!.PackStamps).IsEquivalentTo(record.PackStamps);
                await Assert.That(reopened.RoundFactsOf(Demo)!.Rounds.Count).IsEqualTo(2);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // Rows a build wrote into the Strat Book's payload lift onto the record on read. Only that member moves:
    // the coverage beside it stays, the stamp is untouched so the rows stay current, and a payload left empty goes.
    [Test]
    public async Task PayloadShape_RoundFactsLiftOntoTheRecord_AndOnlyThatMemberLeavesThePayload()
    {
        RoundFactsRows rows = Facts(Round(1, 1000, 2000), Round(2, 3000, 4000));
        JsonObject Record(string path, bool coverage)
        {
            JsonObject payload = new() { ["RoundFacts"] = JsonSerializer.SerializeToNode(rows) };
            if (coverage)
            {
                payload["GrenadeInputCoverage"] = 0.5;
            }

            return new JsonObject
            {
                ["Path"] = path,
                ["Packs"] = new JsonObject { [StratBookPack.PackId] = payload },
                ["PackStamps"] = new JsonArray(new JsonObject { ["Id"] = "roundfacts", ["Schema"] = 1, ["Fingerprint"] = "rf-A", ["State"] = 1 })
            };
        }

        string withCoverage = Record("/d/a.dem", true).ToJsonString();
        string rowsOnly = Record("/d/b.dem", false).ToJsonString();

        DemoCacheRecord a = JsonSerializer.Deserialize<DemoCacheRecord>(withCoverage)!;
        DemoCacheRecord b = JsonSerializer.Deserialize<DemoCacheRecord>(rowsOnly)!;

        using (Assert.Multiple())
        {
            await Assert.That(a.RoundFacts?.Rounds.Count).IsEqualTo(2);
            await Assert.That(a.Packs[StratBookPack.PackId].TryGetProperty("RoundFacts", out _)).IsFalse();
            await Assert.That(a.Packs[StratBookPack.PackId].GetProperty("GrenadeInputCoverage").GetDouble()).IsEqualTo(0.5);
            await Assert.That(a.IsRoundFactsCurrent("rf-A")).IsTrue().Because("the stamp rides the record, untouched by the lift");
            await Assert.That(b.RoundFacts?.Rounds.Count).IsEqualTo(2);
            await Assert.That(b.Packs).IsEmpty().Because("nothing else rode the payload");
            await Assert.That(JsonSerializer.Serialize(b)).DoesNotContain("net.demoviewer.pack.stratbook")
                .Because("the next save writes the new shape");
        }
    }

    [Test]
    public async Task OldShape_WithUntouchedFacets_FoldsOnlyWhatWasWritten()
    {
        // A record indexed while the pack was off: the flat fields are present at their defaults.
        const string json = """
            {"Path":"/d/off.dem","Size":10,"ModifiedTicks":20,"Parse":{"Schema":1,"ComputedAtTicks":5},
             "RoundFacts":null,"RoundFactsFingerprint":null,
             "RoundIndex":{"Schema":0,"ComputedAtTicks":0},"RoundIndexState":0,"RoundIndexFingerprint":null,"RoundIndexRowCount":0,
             "SuggestionsFingerprint":null,"SuggestionCount":0,
             "Grenades":{"Schema":0,"ComputedAtTicks":0},"GrenadeState":2,"GrenadeCount":0,"GrenadeWalker":null,"GrenadeInputCoverage":0}
            """;
        DemoCacheRecord record = JsonSerializer.Deserialize<DemoCacheRecord>(json)!;
        DemoCacheIndexEntry entry = JsonSerializer.Deserialize<DemoCacheIndexEntry>(
            """{"Path":"/d/off.dem","ParseSchema":1,"RoundFactsSchema":0,"RoundIndexSchema":0,"RoundIndexState":0,"SuggestionCount":0,"GrenadeSchema":0,"GrenadeState":2}""")!;

        using (Assert.Multiple())
        {
            await Assert.That(record.Packs).IsEmpty().Because("no rows and no coverage: no payload");
            await Assert.That(record.PackStamps.Select(s => s.Id)).IsEquivalentTo([GrenadeIndexEvaluator.EvaluatorId])
                .Because("only the failed grenade walk left a mark");
            await Assert.That(record.Stamp(GrenadeIndexEvaluator.EvaluatorId)!.State).IsEqualTo(DemoAnalysisState.Failed);
            await Assert.That(record.NeedsPack(GrenadeIndexEvaluator.EvaluatorId, GrenadeStore.Schema, GrenadeWalker.Version)).IsFalse()
                .Because("a failure stays excluded across the fold");
            await Assert.That(record.NeedsRoundFacts("rf-A")).IsTrue();
            await Assert.That(entry.PackStamps).IsEquivalentTo(record.PackStamps);
        }
    }

    // ── Core decides from stamps alone ───────────────────────────────────────

    [Test]
    public async Task Coordinator_SubmitsAPackEvaluator_FromItsStampAlone()
    {
        DemoCacheStore cache = new(null);
        DemoCacheRecord record = ParsedRecord(Demo);
        // A payload nothing in core can read as anything: the decision below cannot have looked at it.
        record.Packs[FakePack] = JsonSerializer.SerializeToElement("opaque");
        record.SetStamp(new PackStamp(FakeFacet, 1, "fp-1"));
        cache.Upsert(record);

        int parses = 0;
        DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(), _ =>
        {
            Interlocked.Increment(ref parses);
            return SyntheticParsedDemo.Create([], [], new Dictionary<int, PlayerInfo>(), null, "de_test", 0, 1f / 64, "s", "c",
                "csgo", 0, 0, 0, "v", "", "", DemoProfile.Unknown);
        });
        StampedFake evaluator = new(cache, FakeFacet, 1, "fp-1");
        using DemoScheduler coordinator = new([evaluator], queue, () => [Demo]);

        coordinator.RecheckAll();
        await Task.Delay(50);
        await Assert.That(evaluator.Evaluated).IsEqualTo(0).Because("the stamp is current");
        await Assert.That(parses).IsEqualTo(0);

        cache.UpdateExisting(Demo, r => r.SetStamp(new PackStamp(FakeFacet, 1, "fp-0")));
        coordinator.RecheckAll();
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (evaluator.Evaluated == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        using (Assert.Multiple())
        {
            await Assert.That(evaluator.Evaluated).IsEqualTo(1).Because("a stale stamp is submitted once");
            await Assert.That(parses).IsEqualTo(1);
            await Assert.That(cache.TryGetIndex(Demo)!.IsPackCurrent(FakeFacet, 1, "fp-1")).IsTrue().Because("the evaluator stamped on its own write");
            await Assert.That(cache.TryLoadRecord(Demo)!.Packs[FakePack].GetString()).IsEqualTo("opaque").Because("the payload went untouched");
        }
    }

    // ── Cost ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task IndexAndRecord_DoNotGrow_AgainstTheOldShape()
    {
        const int demos = 400;
        string roundIndexFingerprint = new RoundIndexPlaceSources(() => RoundIndexTokenSource.Pawn).FingerprintFor("de_nuke");
        string suggestionsFingerprint = Convert.ToHexStringLower(SHA256.HashData("detector set"u8));
        string roundFactsFingerprint = Convert.ToHexStringLower(SHA256.HashData("round facts"u8));

        (long oldAll, long newAll) = IndexBytes(demos, i => AllFacets(i, roundFactsFingerprint, roundIndexFingerprint, suggestionsFingerprint));
        (long oldTypical, long newTypical) = IndexBytes(demos, i => TypicalFacets(i, roundFactsFingerprint, roundIndexFingerprint));
        (long oldRecord, long newRecord) = RecordBytes(roundFactsFingerprint, roundIndexFingerprint, suggestionsFingerprint);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[index size] {demos} rows, all four facets: old {oldAll} B, new {newAll} B ({(newAll - oldAll) / (double)demos:+0.0;-0.0} B/row)"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[index size] {demos} rows, round facts + round index: old {oldTypical} B, new {newTypical} B ({(newTypical - oldTypical) / (double)demos:+0.0;-0.0} B/row)"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[record size] one record with rows, gzipped: old {oldRecord} B, new {newRecord} B"));

        using (Assert.Multiple())
        {
            await Assert.That(newAll).IsLessThanOrEqualTo(oldAll + 60 * demos).Because("the four stamps cost at most ~60 B more per row than the flat mirrors");
            await Assert.That(newTypical).IsLessThan(oldTypical).Because("facets never written cost nothing");
            await Assert.That(newRecord).IsLessThanOrEqualTo(oldRecord + 64).Because("the record wraps the same members");
        }
    }

    private static (long Old, long New) IndexBytes(int demos, Func<int, DemoCacheRecord> record)
    {
        DemoCacheIndexFile file = new();
        JsonArray oldRows = [];
        for (int i = 0; i < demos; i++)
        {
            DemoCacheIndexEntry entry = record(i).ToIndexEntry();
            file.Entries.Add(entry);
            oldRows.Add(OldShape(JsonSerializer.SerializeToNode(entry)!.AsObject(), entry.PackStamps, null));
        }

        JsonObject oldFile = new() { ["Version"] = 2, ["LegacyMigrationVersion"] = 0, ["Entries"] = oldRows };
        return (Encoding.UTF8.GetByteCount(oldFile.ToJsonString()), JsonSerializer.SerializeToUtf8Bytes(file).Length);
    }

    private static (long Old, long New) RecordBytes(string roundFacts, string roundIndex, string suggestions)
    {
        DemoCacheRecord record = AllFacets(1, roundFacts, roundIndex, suggestions);
        JsonObject json = JsonSerializer.SerializeToNode(record)!.AsObject();
        JsonNode? payload = json["Packs"]!["net.demoviewer.pack.stratbook"]!.DeepClone();
        json.Remove("Packs");
        JsonObject old = OldShape(json, record.PackStamps, payload!.AsObject());
        return (Gzip(old.ToJsonString()), Gzip(JsonSerializer.Serialize(record)));
    }

    // Rebuilds the pre-item-21 members from the stamps (and, for a record, the payload), as the store wrote them.
    private static JsonObject OldShape(JsonObject json, List<PackStamp> stamps, JsonObject? payload)
    {
        json.Remove("PackStamps");
        PackStamp? rf = stamps.Find(s => s.Id == RoundFactsEvaluator.EvaluatorId);
        PackStamp? ri = stamps.Find(s => s.Id == RoundIndexEvaluator.EvaluatorId);
        PackStamp? sg = stamps.Find(s => s.Id == SuggestedTagsService.EvaluatorId);
        PackStamp? gr = stamps.Find(s => s.Id == GrenadeIndexEvaluator.EvaluatorId);
        if (payload is null)
        {
            json["RoundFactsSchema"] = rf?.Schema ?? 0;
        }
        else
        {
            if (payload["RoundFacts"] is { } rows)
            {
                json["RoundFacts"] = rows.DeepClone();
            }
        }

        json["RoundFactsFingerprint"] = rf?.Fingerprint;
        if (payload is null)
        {
            json["RoundIndexSchema"] = ri?.Schema ?? 0;
            json["RoundIndexComputedAtTicks"] = ri?.ComputedAtTicks ?? 0;
        }
        else
        {
            json["RoundIndex"] = new JsonObject { ["Schema"] = ri?.Schema ?? 0, ["ComputedAtTicks"] = ri?.ComputedAtTicks ?? 0 };
        }

        json["RoundIndexState"] = (int)(ri?.State ?? DemoAnalysisState.Pending);
        json["RoundIndexFingerprint"] = ri?.Fingerprint;
        json["RoundIndexRowCount"] = ri?.Count ?? 0;
        json["SuggestionsFingerprint"] = sg?.Fingerprint;
        json["SuggestionCount"] = sg?.Count ?? 0;
        if (payload is null)
        {
            json["GrenadeSchema"] = gr?.Schema ?? 0;
        }
        else
        {
            json["Grenades"] = new JsonObject { ["Schema"] = gr?.Schema ?? 0, ["ComputedAtTicks"] = gr?.ComputedAtTicks ?? 0 };
        }

        json["GrenadeState"] = (int)(gr?.State ?? DemoAnalysisState.Pending);
        json["GrenadeCount"] = gr?.Count ?? 0;
        json["GrenadeWalker"] = gr?.Fingerprint;
        if (payload is not null)
        {
            json["GrenadeInputCoverage"] = payload["GrenadeInputCoverage"]?.GetValue<double>() ?? 0;
        }

        return json;
    }

    private static DemoCacheRecord AllFacets(int i, string roundFacts, string roundIndex, string suggestions)
    {
        DemoCacheRecord record = TypicalFacets(i, roundFacts, roundIndex);
        record.SetStamp(new PackStamp(SuggestedTagsService.EvaluatorId, ProposalStore.Schema, suggestions) { Count = 3 });
        record.SetStamp(new PackStamp(GrenadeIndexEvaluator.EvaluatorId, GrenadeStore.Schema, GrenadeWalker.Version)
        {
            ComputedAtTicks = 638_000_000_000_000_000 + i,
            Count = 60
        });
        record.Packs[StratBookPack.PackId] = JsonSerializer.SerializeToElement(new { GrenadeInputCoverage = 0.998 });
        return record;
    }

    private static DemoCacheRecord TypicalFacets(int i, string roundFacts, string roundIndex)
    {
        DemoCacheRecord record = ParsedRecord($"/demos/match-{i:D4}.dem", sha: Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(i))),
            facts: Facts(Round(1, 1000, 2000), Round(2, 3000, 4000)));
        record.SetRoundFacts(record.RoundFacts!, roundFacts);
        record.SetStamp(new PackStamp(RoundIndexEvaluator.EvaluatorId, RoundIndexStore.Schema, roundIndex)
        {
            ComputedAtTicks = 638_000_000_000_000_000 + i,
            Count = 1500
        });
        for (int slot = 0; slot < 10; slot++)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot, Name = $"player {slot}", SteamId64 = $"7656119800000{slot:D4}", Team = slot < 5 ? 2 : 3 });
        }

        return record;
    }

    private static string OldRecordJson(RoundFactsRows rows, string roundIndexFingerprint, string suggestionsFingerprint)
    {
        JsonObject json = new()
        {
            ["Path"] = Demo,
            ["Size"] = 10,
            ["ModifiedTicks"] = 20,
            ["Sha256"] = "abc",
            ["Map"] = "de_nuke",
            ["Parse"] = new JsonObject { ["Schema"] = 1, ["ComputedAtTicks"] = 1 },
            ["RoundFacts"] = JsonSerializer.SerializeToNode(rows),
            ["RoundFactsFingerprint"] = "rf-A",
            ["RoundIndex"] = new JsonObject { ["Schema"] = 1, ["ComputedAtTicks"] = 100 },
            ["RoundIndexState"] = 1,
            ["RoundIndexFingerprint"] = roundIndexFingerprint,
            ["RoundIndexRowCount"] = 12,
            ["SuggestionsFingerprint"] = suggestionsFingerprint,
            ["SuggestionCount"] = 3,
            ["Grenades"] = new JsonObject { ["Schema"] = 1, ["ComputedAtTicks"] = 200 },
            ["GrenadeState"] = 1,
            ["GrenadeCount"] = 60,
            ["GrenadeWalker"] = GrenadeWalker.Version,
            ["GrenadeInputCoverage"] = 0.5
        };
        return json.ToJsonString();
    }

    private static string OldIndexJson(string roundIndexFingerprint, string suggestionsFingerprint)
    {
        JsonObject row = new()
        {
            ["Path"] = Demo,
            ["Size"] = 10,
            ["ModifiedTicks"] = 20,
            ["Sha256"] = "abc",
            ["Map"] = "de_nuke",
            ["ParseSchema"] = 1,
            ["RoundFactsSchema"] = 1,
            ["RoundFactsFingerprint"] = "rf-A",
            ["RoundIndexSchema"] = 1,
            ["RoundIndexComputedAtTicks"] = 100,
            ["RoundIndexState"] = 1,
            ["RoundIndexFingerprint"] = roundIndexFingerprint,
            ["RoundIndexRowCount"] = 12,
            ["SuggestionsFingerprint"] = suggestionsFingerprint,
            ["SuggestionCount"] = 3,
            ["GrenadeSchema"] = 1,
            ["GrenadeState"] = 1,
            ["GrenadeCount"] = 60,
            ["GrenadeWalker"] = GrenadeWalker.Version
        };
        return new JsonObject { ["Version"] = 2, ["LegacyMigrationVersion"] = 1, ["Entries"] = new JsonArray(row) }.ToJsonString();
    }

    private static long Gzip(string json)
    {
        using MemoryStream buffer = new();
        using (System.IO.Compression.GZipStream gzip = new(buffer, System.IO.Compression.CompressionLevel.Optimal, true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(json));
        }

        return buffer.Length;
    }

    private static byte[] Inflate(byte[] gzipped)
    {
        using MemoryStream input = new(gzipped);
        using System.IO.Compression.GZipStream gzip = new(input, System.IO.Compression.CompressionMode.Decompress);
        using MemoryStream output = new();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private sealed class FakePayload
    {
        public string Name { get; set; } = "";
        public List<int> Values { get; set; } = [];
    }

    private sealed class NoRows : IRoundFactsRowSource
    {
        public RoundFactsTable Rows(ParsedDemo parsed) => RoundFactsTable.Unavailable("test");
    }

    private sealed class FixedIdentity(string value) : IRoundFactsRulesetIdentity
    {
        public string? Fingerprint(int tickRate) => value;
    }

    // A pack evaluator as core sees one: it wants a demo from the index row's stamp and stamps on its write.
    private sealed class StampedFake(DemoCacheStore cache, string facet, int schema, string fingerprint) : IDemoEvaluator
    {
        private int _evaluated;

        public int Evaluated => _evaluated;

        public string Id => "fake";

        public bool Wants(string path) => cache.TryGetIndex(path) is { } entry && entry.NeedsPack(facet, schema, fingerprint);

        public void Evaluate(string path, ParsedDemo parsed)
        {
            Interlocked.Increment(ref _evaluated);
            cache.UpdateExisting(path, r => r.SetStamp(new PackStamp(facet, schema, fingerprint)));
        }
    }
}
