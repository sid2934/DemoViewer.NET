#region

using System.Globalization;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The <c>player_event</c> facet a library visit writes, against the <c>player_match</c> facet of the same
///     visit: every kill names both accounts, and each account's kills are its <c>TotalK</c> plus its
///     <c>TeamKills</c>. Read in place from <c>DEMO_PATH</c> (with <c>DEMO_PATH_PICK</c>).
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class PlayerEventRealDemoTests
{
    // The first rounds of a real demo, the accounts and names replaced by slot.
    private static void WriteSamples(string repo, FactTable kills, FactTable deaths)
    {
        foreach (FactTable table in (FactTable[]) [kills, deaths])
        {
            List<FactRow> rows = [];
            foreach (FactRow real in table.Rows.Where(r => r.Dimensions["round_number"].AsInteger() <= 2))
            {
                long slot = real.Dimensions["player_slot"].AsInteger()!.Value;
                Dictionary<string, FactValue> dimensions = new(real.Dimensions, StringComparer.Ordinal)
                {
                    ["player_name"] = FactValue.Of(string.Create(CultureInfo.InvariantCulture, $"player_{slot}")),
                    ["steam_id"] = FactValue.Of(string.Create(CultureInfo.InvariantCulture, $"765611900000000{slot:00}"))
                };
                rows.Add(new FactRow(dimensions, real.Values));
            }

            string path = PlayerEventFacetTests.SamplePath(repo, table.Key.Output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, PlayerMatchFacetTests.Indent(FactsCodec.Encode(table with { Fingerprint = "sample", Rows = rows })));
        }
    }

    [Test]
    public async Task EveryKill_NamesBothAccounts_AndAddsUpToPlayerMatch()
    {
        string path = DemoTestHelper.RequireDemo();
        MergedRulesBuild rules = PlayerMatchParityRealDemoTests.AppRules();
        StampedFacts stamped = new(rules);
        DemoCacheStore cache = new(null);
        cache.Upsert(new DemoCacheRecord
        {
            Path = path,
            Size = new FileInfo(path).Length,
            Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 }
        });
        FactsEvaluator facts = new(cache, stamped);
        AnalysisFacts library = new(cache, stamped, new RoundFactsSource(cache));
        IReadOnlySet<string> cut = rules.StampedOutputs(id => facts.Records(path, id));
        using (DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None)))
        {
            facts.EvaluateForward(path, ForwardDemoPass.Run(reader, ForwardNeeds.Rules, rules.Docs, outputs: cut));
        }

        FactTable killRows = library.TryGet(path, PlayerEventFacet.KillsKey) ?? throw new InvalidOperationException("the visit wrote no kills");
        FactTable deathRows = library.TryGet(path, PlayerEventFacet.DeathsKey) ?? throw new InvalidOperationException("the visit wrote no deaths");
        FactTable facet = library.TryGet(path, PlayerMatchFacet.Key) ?? throw new InvalidOperationException("the visit wrote no player_match");
        if (Environment.GetEnvironmentVariable(PlayerEventFacetTests.SampleUpdateVar) == "1" && DemoTestHelper.FindRepoRoot() is { } repo)
        {
            WriteSamples(repo, killRows, deathRows);
        }

        List<PlayerKill> kills = PlayerEventFacet.Kills(killRows.Rows, deathRows.Rows);
        HashSet<long> botSlots =
        [
            .. deathRows.Rows.Where(r => r.Dimensions["steam_id"].AsText() is null or "0").Select(r => r.Dimensions["player_slot"].AsInteger()!.Value)
        ];
        List<string> mismatches = [];
        foreach (IGrouping<string, FactRow> account in facet.Rows
                     .Where(r => r.Dimensions["steam_id"].AsText() is { Length: > 0 } id && id != "0")
                     .GroupBy(r => r.Dimensions["steam_id"].AsText()!))
        {
            double expected = account.Sum(r => (r.Values["TotalK"].AsNumber() ?? 0) + (r.Values["TeamKills"].AsNumber() ?? 0));
            int counted = kills.Count(k => k.Killer == account.Key);
            if (counted != expected)
            {
                mismatches.Add($"{account.Key}: {counted} kill rows, player_match {expected}");
            }
        }

        DemoCacheIndexEntry row = cache.TryGetIndex(path)!;
        using (Assert.Multiple())
        {
            await Assert.That(library.Status(path, PlayerEventFacet.KillsKey)).IsEqualTo(FactStatus.Current);
            await Assert.That(library.Status(path, PlayerEventFacet.DeathsKey)).IsEqualTo(FactStatus.Current);
            await Assert.That(row.Stamp(StampedFacts.StampId(PlayerEventFacet.RulesetId))?.State).IsEqualTo(DemoAnalysisState.Indexed);
            await Assert.That(killRows.Grain).IsEqualTo("player_event");
            await Assert.That(kills.Count).IsGreaterThan(50);
            await Assert.That(kills.Count(k => k.Victim is null))
                .IsEqualTo(killRows.Rows.Count(r => botSlots.Contains(r.Values["victim_slot"].AsInteger()!.Value)))
                .Because("every kill pairs with its death, and only a bot victim has no account");
            await Assert.That(kills.All(k => k.Killer != k.Victim)).IsTrue();
            await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches));
            await Assert.That(deathRows.Rows.Count).IsGreaterThanOrEqualTo(killRows.Rows.Count).Because("every kill is a death; a fall is a death alone");
        }
    }
}
