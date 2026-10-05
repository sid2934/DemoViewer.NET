#region

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Extensions.StratBook.Services.Provenance;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The pack's reads of the library: its persisted demo key is the cache's, the side keys read off a library
///     row equal the ones read off the record, and a row loaded from an index older than side players keeps the
///     team index as it was until the sides pass reads the record.
/// </summary>
[NotInParallel]
public class StratBookLibraryTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static DemoCacheRecord Record(string path, long ticks, string[] t, string[] ct, string? tClan = null, string? ctClan = null)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = ticks,
            Sha256 = "sha-" + Path.GetFileNameWithoutExtension(path),
            Map = "de_nuke",
            Server = "Valve CS2 Server",
            TClan = tClan,
            CtClan = ctClan
        };
        int slot = 0;
        foreach (string id in t)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = "t" + id[^2..], SteamId64 = id, Team = 2 });
        }

        foreach (string id in ct)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = "ct" + id[^2..], SteamId64 = id, Team = 3 });
        }

        record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = "coach", SteamId64 = "76561198000000099", Team = 3, IsCoach = true });
        record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = "BOT Rock", SteamId64 = "0", Team = 2, IsBot = true });
        record.Players.Add(new CachedPlayerInfo { Slot = slot, Name = "again", SteamId64 = t[0], Team = 2 });
        DemoCacheStore.StampHeader(record);
        DemoCacheStore.StampParse(record);
        return record;
    }

    private static string[] Ids(int from) => [.. Enumerable.Range(from, 5).Select(n => $"765611980000000{n:D2}")];

    [Test]
    [Arguments("/Users/me/Demos/match.dem")]
    [Arguments(@"C:\Demos\Ünïcode Match.DEM")]
    [Arguments("")]
    public async Task TheDemoKey_IsTheCachesStableKey(string path) =>
        await Assert.That(DemoKeys.StableKey(path)).IsEqualTo(DemoCacheStore.StableKey(path));

    [Test]
    public async Task SideKeys_FromTheRow_EqualSideKeys_FromTheRecord()
    {
        DemoCacheStore cache = new(null);
        DemoCacheRecord record = Record("/d/a.dem", 638000000000000000, Ids(10), Ids(20), "Red", "Blue");
        cache.Upsert(record);

        DemoSideInput fromRow = SideKeys.From(cache.Row("/d/a.dem"))!;
        DemoSideInput fromRecord = SideKeys.From(cache.Library().Detail("/d/a.dem")!)!;
        using (Assert.Multiple())
        {
            await Assert.That(fromRow.SameSides(fromRecord)).IsTrue();
            await Assert.That(fromRow.T.Key).IsEquivalentTo(Ids(10));
            await Assert.That(fromRow.Ct.Key).IsEquivalentTo(Ids(20)).Because("the coach is no member of the key");
            await Assert.That(fromRow.T.Names).IsEquivalentTo(fromRecord.T.Names);
            await Assert.That(fromRow.Ct.Clan).IsEqualTo("Blue");
            await Assert.That(fromRow.StableKey).IsEqualTo(DemoCacheStore.StableKey("/d/a.dem"));
            await Assert.That(fromRow.OrderTicks).IsEqualTo(638000000000000000);
            await Assert.That(fromRow.SourceKind).IsEqualTo(fromRecord.SourceKind);
        }
    }

    [Test]
    public async Task ARowFromAnOlderIndex_KeepsTheTeamIndex_UntilTheSidesPassReadsItsRecord()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-sb-library-" + Guid.NewGuid().ToString("N"));
        try
        {
            string cacheRoot = Path.Combine(root, "cache");
            DemoCacheStore writer = new(cacheRoot);
            writer.Upsert(Record("/d/a.dem", 638000000000000000, Ids(10), Ids(20)));
            writer.Upsert(Record("/d/b.dem", 638000000100000000, Ids(10), Ids(30)));
            writer.SaveIndex();
            using (TeamIdentityService first = new(root, writer.Library(), run: _inline))
            {
                await first.StartAsync();
                await Assert.That(first.DemoCount).IsEqualTo(2);
            }

            // The same cache as a version-2 index left it: no side players on any row.
            string indexPath = Path.Combine(cacheRoot, "index.json");
            JsonObject index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
            index["Version"] = 2;
            foreach (JsonNode? row in index["Entries"]!.AsArray())
            {
                row!.AsObject().Remove("CtPlayers");
                row.AsObject().Remove("TPlayers");
            }

            await File.WriteAllTextAsync(indexPath, index.ToJsonString());

            DemoCacheStore cache = new(cacheRoot);
            IExtensionLibrary library = cache.Library();
            await Assert.That(library.Find("/d/a.dem")!.CtPlayers).IsNull();

            using TeamIdentityService teams = new(root, library, run: _inline);
            await teams.StartAsync();
            TeamAssignment? before = teams.GetAssignment("/d/a.dem");
            await Assert.That(teams.DemoCount).IsEqualTo(2).Because("a row without sides keeps what the team index holds");
            await Assert.That(before?.T.TeamId).IsNotNull();

            // A change to a side-less row lifts nothing either.
            cache.UpdateExisting("/d/b.dem", r => r.Map = "de_inferno");
            await Assert.That(teams.DemoCount).IsEqualTo(2);

            IExtensionRecordPass pass = teams.SidesPass;
            await Assert.That(pass.Wants(library.Find("/d/a.dem")!)).IsTrue();
            pass.Run(library.Detail("/d/a.dem")!, CancellationToken.None);
            await Assert.That(teams.GetAssignment("/d/a.dem")?.T.TeamId).IsEqualTo(before!.T.TeamId)
                .Because("the record's sides are the ones the index already held");

            cache.RefreshIndexRow(cache.TryLoadRecord("/d/a.dem")!);
            await Assert.That(pass.Wants(library.Find("/d/a.dem")!)).IsFalse().Because("the row carries its sides now");
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
    public async Task TheProvenanceSource_ReadsTheRowsItNeeds_FromTheLibrary()
    {
        DemoCacheStore cache = new(null);
        DemoCacheRecord record = Record("/d/a.dem", 638000000000000000, Ids(10), Ids(20), "Red", "Blue");
        cache.Upsert(record);
        using TeamIdentityService teams = new(null, cache.Library(), run: _inline);
        await teams.StartAsync();
        using DemoProvenanceSource source = new(cache.Library(), teams);
        int changes = 0;
        source.Changed += () => changes++;

        await Assert.That(source.Resolve("/d/a.dem")?.Sha256).IsEqualTo("sha-a");
        await Assert.That(source.LabelFor("sha-a")).IsEqualTo(source.Resolve("/d/a.dem")!.Label);

        cache.UpdateExisting("/d/a.dem", r => r.CtClan = "Green");
        await Assert.That(changes).IsGreaterThan(0);
    }

    private static readonly string[] _cacheTypes =
    [
        "DemoCacheStore", "DemoCacheRecord", "DemoCacheIndexEntry", "TryLoadRecord", "LoadRecords", "CachedRound",
        "CachedPlayerInfo", "DemoAnalysisState", "DemoLibraryService", "LibraryTabViewModel", "Services.DemoCache"
    ];

    private static readonly string[] _analysisTypes =
    [
        "Services.Facts", "IRoundFactsSource", "RoundFactsSource", "RoundFactsEvaluator", "RoundFactsRecords",
        "MergedRulesBuild", "StampedRuleset", "FrameClock", "RulesRoundFactsRulesetIdentity"
    ];

    [Test]
    public async Task ThePack_ReadsTheLibraryThroughTheSdk_NotTheAppsDemoCache() =>
        await Assert.That(await FindInPack(_cacheTypes)).IsEmpty();

    [Test]
    public async Task ThePack_ReadsRoundFactsThroughTheLibrarysFacts_NotTheAppsRulesOrClock() =>
        await Assert.That(await FindInPack(_analysisTypes)).IsEmpty();

    private static async Task<List<string>> FindInPack(IReadOnlyList<string> names)
    {
        string pack = Path.Combine(DemoTestHelper.FindRepoRoot()!, "src", "Extensions", "StratBook",
            "DemoViewer.NET.Extensions.StratBook");
        List<string> found = [];
        foreach (string file in Directory.EnumerateFiles(pack, "*.*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(pack, file).Replace('\\', '/');
            if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal)
                || !(file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".axaml", StringComparison.Ordinal)))
            {
                continue;
            }

            string code = WithoutComments(await File.ReadAllTextAsync(file));
            found.AddRange(names
                .Where(name => Regex.IsMatch(code, $@"\b{Regex.Escape(name)}\b"))
                .Select(name => $"{relative}: {name}"));
        }

        return found;
    }

    private static string WithoutComments(string source)
    {
        string stripped = Regex.Replace(source, @"/\*.*?\*/|<!--.*?-->", "", RegexOptions.Singleline);
        return string.Join('\n', stripped.Split('\n').Select(line => line.IndexOf("//", StringComparison.Ordinal) is var at and >= 0
            ? line[..at]
            : line));
    }
}
