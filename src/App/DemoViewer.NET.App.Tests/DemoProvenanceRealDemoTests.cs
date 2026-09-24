#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Provenance;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Demo Provenance Labels against a real replay: the tier-2 record carries the classifier's verdict
///     by name, and with one account of the replay as me the heuristic reads the demo the way its header
///     says (matchmaking for a Valve GOTV recording, scrim otherwise), while nobody-of-ours leaves it
///     unlabeled. Needs no Round Facts rows, so nothing here waits on CS2DemoKit #54.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class DemoProvenanceRealDemoTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static DemoCacheRecord Tier2Record(string path, ParsedDemo parsed)
    {
        (int? ct, int? t, string? ctClan, string? tClan, HashSet<int> coachSlots) = DemoLibraryService.ExtractFinalState(parsed);
        (List<CachedPlayerInfo> players, List<CachedRound> rounds) = DemoLibraryService.ProjectTier2(parsed, coachSlots);
        FileInfo info = new(path);
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = info.Length,
            ModifiedTicks = info.LastWriteTime.Ticks,
            Map = parsed.MapName,
            Server = parsed.ServerName,
            SourceKind = parsed.Profile.SourceKind.ToString(),
            Players = players,
            Rounds = rounds,
            RoundCount = rounds.Count,
            CtScore = ct,
            TScore = t,
            CtClan = ctClan,
            TClan = tClan
        };
        DemoCacheStore.StampParse(record);
        return record;
    }

    [Test]
    public async Task TheReplay_IsUnlabeledForNobody_AndReadsItsHeaderForMe()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        DemoCacheRecord record = Tier2Record(path, parsed);
        DemoSourceKind kind = parsed.Profile.SourceKind;
        bool tagged = !string.IsNullOrWhiteSpace(record.CtClan) && !string.IsNullOrWhiteSpace(record.TClan);
        Console.WriteLine($"{Path.GetFileName(path)}: source kind {kind}, server '{parsed.ServerName}', tags {(tagged ? "both" : "not both")}");

        DemoCacheStore cache = new(null);
        using TeamIdentityService teams = new(null, cache, run: _inline);
        using DemoProvenanceSource source = new(cache, teams);
        await teams.StartAsync();
        cache.Upsert(record);
        await teams.Idle;

        DemoCacheIndexEntry entry = cache.TryGetIndex(path)!;
        using (Assert.Multiple())
        {
            await Assert.That(entry.SourceKind).IsEqualTo(kind.ToString()).Because("the index row mirrors the record's verdict");
            await Assert.That(DemoProvenanceSource.SourceKindOf(entry)).IsEqualTo(kind);
        }

        string? forNobody = source.Resolve(path)!.Label;
        await Assert.That(forNobody).IsEqualTo(tagged ? "official" : null)
            .Because("without an us team or a me account only the clan tags can label a demo");

        // One account of the replay as me: our side resolves through Me, and the label follows the header.
        string me = SideKeys.Side(record, 2).Key[0];
        teams.SetMyAccounts([me]);
        string? forMe = source.Resolve(path)!.Label;
        string expected = tagged ? "official" : kind == DemoSourceKind.GotvMatchmaking ? "matchmaking" : "scrim";
        Console.WriteLine($"me = {me}: label '{forMe}'");
        await Assert.That(forMe).IsEqualTo(expected);
    }
}
