#region

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Stats;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The <c>player_match</c> facet is a published format (<c>docs/player-match-format.md</c>): the shipped
///     ruleset still declares the committed sample's columns, the sidecar round-trips, the record carries its
///     accounts beside the stamp, and its fingerprint follows the rulesets it reads, a disabled one included.
/// </summary>
[NotInParallel]
public class PlayerMatchFacetTests
{
    /// <summary>Set to 1 to rewrite the sample from a real demo through <see cref="PlayerMatchParityRealDemoTests" />.</summary>
    internal const string SampleUpdateVar = "PLAYER_MATCH_SAMPLE_UPDATE";

    private static readonly string Shipped = RuleSetLocator.ResolveShippedRulesDirectory();

    private static readonly string[] Dimensions = ["match_id", "map", "player_slot", "player_name", "steam_id", "team"];

    internal static string SamplePath(string repo) => Path.Combine(repo, "tests", "fixtures", "player-match", "player_match.sample.facts.json");

    /// <summary>A sidecar's bytes as the indented JSON the sample is committed as. Numbers keep their written text.</summary>
    internal static string Indent(byte[] sidecar)
    {
        using GZipStream gzip = new(new MemoryStream(sidecar), CompressionMode.Decompress);
        using JsonDocument document = JsonDocument.Parse(gzip);
        using MemoryStream output = new();
        using (Utf8JsonWriter json = new(output, new JsonWriterOptions { Indented = true }))
        {
            document.WriteTo(json);
        }

        return Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    internal static byte[] Gzip(string json)
    {
        using MemoryStream buffer = new();
        using (GZipStream gzip = new(buffer, CompressionLevel.Fastest, true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(json));
        }

        return buffer.ToArray();
    }

    private static FactRow Row(long slot, string? steamId, string? name, long team) => new(
        new Dictionary<string, FactValue>
        {
            ["player_slot"] = FactValue.Of(slot),
            ["steam_id"] = FactValue.Of(steamId),
            ["player_name"] = FactValue.Of(name),
            ["team"] = FactValue.Of(team)
        },
        new Dictionary<string, FactValue>());

    private static CachedPlayerInfo Roster(int slot, string name, string steamId, int team) =>
        new() { Slot = slot, Name = name, SteamId64 = steamId, Team = team };

    private static string WriteUserRuleset(DirectoryInfo user, string fileName, string yaml)
    {
        string path = Path.Combine(user.FullName, fileName);
        File.WriteAllText(path, yaml);
        return path;
    }

    [Test]
    public async Task V1Columns_MatchTheCheckedInSample()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        string path = SamplePath(repo);
        if (!File.Exists(path))
        {
            throw new SkipTestException($"missing the player_match sample; regenerate with {SampleUpdateVar}=1 through PlayerMatchParityRealDemoTests");
        }

        string committed = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        FactTable? table = FactsCodec.Decode(Gzip(committed));
        MergedRulesBuild rules = new(() => YamlConfigLoader.LoadWithOverlay(Shipped, null));
        IReadOnlyList<string> declared =
        [
            .. rules.EnabledDoc(PlayerMatchFacet.RulesetId)!.Show!.Tables.Single(t => t.Name == PlayerMatchFacet.Table).Columns
                .Select(c => c.Label ?? c.Stat)
        ];

        await Assert.That(table).IsNotNull().Because("the sample is a format-1 facts sidecar");
        using (Assert.Multiple())
        {
            await Assert.That(table!.Key).IsEqualTo(PlayerMatchFacet.Key);
            await Assert.That(table.Schema).IsEqualTo(StampedFacts.Schema);
            await Assert.That(table.Grain).IsEqualTo("player_match");
            await Assert.That(string.Join(",", table.Values)).IsEqualTo(string.Join(",", declared))
                .Because("the shipped ruleset declares the committed columns, in order; a change is a new facet version");
            await Assert.That(string.Join(",", table.Dimensions)).IsEqualTo(string.Join(",", Dimensions));
            await Assert.That(table.Rows.Count).IsEqualTo(2);
            await Assert.That(table.Rows.All(r => declared.All(r.Values.ContainsKey))).IsTrue();
            await Assert.That(table.Rows.All(r => r.Values[ColumnRules.Rounds].Kind == FactValueKind.Whole)).IsTrue();
            await Assert.That(table.Rows.All(r => r.Values["HLTV"].Kind == FactValueKind.Number)).IsTrue()
                .Because("a rate is stored unrounded");
            await Assert.That(Indent(FactsCodec.Encode(table))).IsEqualTo(committed).Because("round trip");
        }
    }

    [Test]
    public async Task FacetPlayers_FoldReconnectSlots_AndTakeNamesFromTheRoster()
    {
        FactRow[] rows =
        [
            Row(3, "76561190000000003", "Officer", 0),
            Row(4, "76561190000000003", "grim", 3),
            Row(12, "76561190000000012", "Player 12", 3),
            Row(9, null, "Player 9", 0),
            Row(2, "0", "BOT Kirk", 2),
            Row(7, "76561190000000007", "Player 7", 2),
            Row(8, "76561190000000008", "renamed", 2)
        ];
        CachedPlayerInfo[] roster =
        [
            Roster(3, "grim", "76561190000000003", 0),
            Roster(4, "grim", "76561190000000003", 3),
            Roster(12, "jeorge", "76561190000000012", 3)
        ];

        List<FacetPlayer> players = PlayerMatchFacet.Players(rows, roster);

        using (Assert.Multiple())
        {
            await Assert.That(players.Select(p => p.SteamId64))
                .IsEquivalentTo(["76561190000000003", "76561190000000007", "76561190000000008", "76561190000000012"]);
            await Assert.That(players[0].Slots).IsEquivalentTo([3, 4]);
            await Assert.That(players[0].Names).IsEquivalentTo(["grim"]).Because("the forward run names slot 3 after its earlier occupant");
            await Assert.That(players[1].Names).IsEmpty().Because("a placeholder is never a name");
            await Assert.That(players[2].Names).IsEquivalentTo(["renamed"]).Because("with no roster name the run's name stands");
            await Assert.That(players[3].Names).IsEquivalentTo(["jeorge"]);
        }
    }

    [Test]
    public async Task TheIndexRowMirrorsFacetPlayers_AndAPlainRecordWritesNone()
    {
        DemoCacheRecord record = new() { Path = "/d/a.dem", Size = 1, ModifiedTicks = 1 };
        string plain = JsonSerializer.Serialize(record);
        string plainRow = JsonSerializer.Serialize(record.ToIndexEntry());
        record.FacetPlayers = [new FacetPlayer { SteamId64 = "76561190000000003", Names = ["grim"], Slots = [3, 4] }];
        DemoCacheIndexEntry row = record.ToIndexEntry();
        DemoCacheRecord copy = new() { Path = record.Path };
        copy.FillMissingFrom(record);

        using (Assert.Multiple())
        {
            await Assert.That(plain).DoesNotContain("FacetPlayers").Because("the field is additive: a record without it writes the old bytes");
            await Assert.That(plainRow).DoesNotContain("FacetPlayers");
            await Assert.That(row.FacetPlayers).IsSameReferenceAs(record.FacetPlayers);
            await Assert.That(JsonSerializer.Deserialize<DemoCacheIndexEntry>(JsonSerializer.Serialize(row))!.FacetPlayers!.Single().Slots).IsEquivalentTo([3, 4]);
            await Assert.That(copy.FacetPlayers).IsSameReferenceAs(record.FacetPlayers);
        }
    }

    [Test]
    public async Task AWrite_SetsFacetPlayersBesideTheStamp()
    {
        MergedRulesBuild rules = PlayerMatchParityRealDemoTests.AppRules();
        StampedFacts stamped = new(rules);
        DemoCacheStore cache = new(null);
        const string path = "/demos/a.dem";
        cache.Upsert(new DemoCacheRecord { Path = path, Size = 1, Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 } });
        FactsEvaluator evaluator = new(cache, stamped);
        AnalysisFacts facts = new(cache, stamped, new RoundFactsSource(cache));
        bool wanted = evaluator.Wants(path);

        evaluator.Evaluate(path, MergedRulesBuildTests.TwoFrameDemo());

        using (Assert.Multiple())
        {
            await Assert.That(wanted).IsTrue().Because("a parsed demo with no facet is the backlog");
            await Assert.That(facts.Status(path, PlayerMatchFacet.Key)).IsEqualTo(FactStatus.Current);
            await Assert.That(cache.TryGetIndex(path)!.FacetPlayers).IsNotNull().Because("the stamp and the accounts land in one update");
            await Assert.That(evaluator.Wants(path)).IsFalse();
            await Assert.That(rules.Fingerprint(64).Fingerprint).IsEqualTo(MergedRulesBuildTests.ShippedFingerprint64)
                .Because("the facet is stamped on its own, so the highlights stamp never moves");
        }
    }

    [Test]
    public async Task EditingAPlayerStatsCompute_MovesTheFingerprint()
    {
        DirectoryInfo user = Directory.CreateTempSubdirectory("dv-pm-edit-");
        try
        {
            MergedRulesBuild rules = PlayerMatchParityRealDemoTests.AppRules(user.FullName);
            StampedFacts stamped = new(rules);
            string before = stamped.Fingerprint(PlayerMatchFacet.RulesetId, 64)!;
            string roundFacts = stamped.Fingerprint(RoundFactsFingerprint.RulesetId, 64)!;

            string yaml = await File.ReadAllTextAsync(Path.Combine(Shipped, "player_stats.rules.yaml"));
            WriteUserRuleset(user, "player_stats.rules.yaml", yaml.Replace("TotalEnemyDmg / round.number", "TotalEnemyDmg / round.number / 1", StringComparison.Ordinal));
            rules.Invalidate();

            using (Assert.Multiple())
            {
                await Assert.That(before).IsNotNull();
                await Assert.That(stamped.Fingerprint(PlayerMatchFacet.RulesetId, 64)).IsNotEqualTo(before);
                await Assert.That(stamped.Fingerprint(RoundFactsFingerprint.RulesetId, 64)).IsEqualTo(roundFacts);
            }
        }
        finally
        {
            user.Delete(true);
        }
    }

    [Test]
    public async Task DisablingKast_StampsTheFacetFailed_UntilItComesBack()
    {
        DirectoryInfo user = Directory.CreateTempSubdirectory("dv-pm-kast-");
        try
        {
            MergedRulesBuild rules = PlayerMatchParityRealDemoTests.AppRules(user.FullName);
            StampedFacts stamped = new(rules);
            string enabled = stamped.Fingerprint(PlayerMatchFacet.RulesetId, 64)!;
            string kast = WriteUserRuleset(user, "kast.rules.yaml", "ruleset: kast\nenabled: false\n");
            rules.Invalidate();

            DemoCacheStore cache = new(null);
            const string path = "/demos/a.dem";
            cache.Upsert(new DemoCacheRecord { Path = path, Size = 1, Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 } });
            FactsEvaluator evaluator = new(cache, stamped);
            AnalysisFacts facts = new(cache, stamped, new RoundFactsSource(cache));
            string? disabled = stamped.Fingerprint(PlayerMatchFacet.RulesetId, 64);
            bool wanted = evaluator.Wants(path);

            evaluator.Evaluate(path, MergedRulesBuildTests.TwoFrameDemo());
            PackStamp? stamp = cache.TryGetIndex(path)?.Stamp(StampedFacts.StampId(PlayerMatchFacet.RulesetId));
            FactStatus failed = facts.Status(path, PlayerMatchFacet.Key);
            bool wantedAfter = evaluator.Wants(path);

            File.Delete(kast);
            rules.Invalidate();

            using (Assert.Multiple())
            {
                await Assert.That(disabled).IsNotNull().Because("a missing dependency still leaves something to stamp");
                await Assert.That(disabled).IsNotEqualTo(enabled);
                await Assert.That(wanted).IsTrue();
                await Assert.That(stamp?.State).IsEqualTo(DemoAnalysisState.Failed);
                await Assert.That(stamp?.Fingerprint).IsEqualTo(disabled);
                await Assert.That(failed).IsEqualTo(FactStatus.Failed);
                await Assert.That(cache.TryGetIndex(path)!.FacetPlayers).IsNull();
                await Assert.That(wantedAfter).IsFalse().Because("a failed write under the same fingerprint is settled");
                await Assert.That(stamped.Fingerprint(PlayerMatchFacet.RulesetId, 64)).IsEqualTo(enabled);
                await Assert.That(evaluator.Wants(path)).IsTrue().Because("kast is back, so the facet is written again");
            }
        }
        finally
        {
            user.Delete(true);
        }
    }
}
