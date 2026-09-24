#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Team Identity against the real replays folder: the tier-2 projection with the coach read, and
///     clustering over a bounded sample of the folder, reporting the team count without asserting one
///     (the count depends on whose library the folder is). The per-round side join needs Round Facts
///     rows, which the engine row source does not write until CS2DemoKit #54 lands, so that variant is
///     skipped with the reason.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class TeamIdentityRealDemoTests
{
    private const string WaitingOnEngine =
        "waiting on CS2DemoKit #54: SideAtRound joins Round Facts slots, and the engine row source writes none yet";

    /// <summary>How many demos of the folder the clustering sample parses; a full library is minutes per demo.</summary>
    private const string SampleSizeEnvVar = "TEAM_IDENTITY_SAMPLE";

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
    public async Task TheTier2Projection_MarksNoCoach_OnAMatchmakingReplay_AndKeysFivePerSide()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        DemoCacheRecord record = Tier2Record(path, parsed);
        SideInput t = SideKeys.Side(record, 2);
        SideInput ct = SideKeys.Side(record, 3);
        Console.WriteLine($"{Path.GetFileName(path)}: T key {t.Key.Count}, CT key {ct.Key.Count}, coaches {record.Players.Count(p => p.IsCoach)}");

        using (Assert.Multiple())
        {
            await Assert.That(record.Players.Any(p => p.IsCoach)).IsFalse().Because("a matchmaking replay registers no coach");
            await Assert.That(t.Key.Count).IsGreaterThan(0);
            await Assert.That(t.Key.Count).IsLessThanOrEqualTo(5);
            await Assert.That(ct.Key.Count).IsGreaterThan(0);
            await Assert.That(ct.Key.Count).IsLessThanOrEqualTo(5);
            await Assert.That(t.Key.Intersect(ct.Key, StringComparer.Ordinal)).IsEmpty().Because("an account finishes on one side");
            await Assert.That(t.Key.All(id => id.Length > 10)).IsTrue().Because("SteamID64s, never zero");
        }
    }

    [Test]
    public async Task TheReplaysFolder_Clusters_AndReportsTheTeamCount()
    {
        string first = DemoTestHelper.RequireDemo();
        string folder = Path.GetDirectoryName(first)!;
        int sample = int.TryParse(Environment.GetEnvironmentVariable(SampleSizeEnvVar), out int n) && n > 0 ? n : 4;
        List<string> paths =
        [
            first,
            .. Directory.GetFiles(folder, "*.dem").Order(StringComparer.Ordinal)
                .Where(p => !string.Equals(p, first, StringComparison.OrdinalIgnoreCase))
                .Take(sample - 1)
        ];

        DemoCacheStore cache = new(null);
        using TeamIdentityService teams = new(null, cache, run: _inline);
        await teams.StartAsync();
        using (cache.BeginBatch())
        {
            foreach (string path in paths)
            {
                cache.Upsert(Tier2Record(path, DemoTestHelper.GetOrParse(path)));
            }
        }

        await teams.Idle;

        Console.WriteLine($"team identity over {paths.Count} of {Directory.GetFiles(folder, "*.dem").Length} demos in {folder}: " +
                          $"{teams.Teams.Count} teams, {teams.UnaffiliatedCount} unaffiliated sides, {teams.UnclusterableCount} unclusterable demos" +
                          (teams.MeSuggestion is { } me ? $", me suggestion {me.SteamId64} at {me.Share:P0}" : ", no me suggestion"));
        foreach (Team team in teams.Teams)
        {
            Console.WriteLine($"  {team.Name} ({team.NameSource}): {team.Rosters.Count} roster(s), {teams.DemosOf(team.Id).Count} demos");
        }

        using (Assert.Multiple())
        {
            await Assert.That(teams.DemoCount).IsEqualTo(paths.Count);
            foreach (string path in paths)
            {
                TeamAssignment? assignment = teams.GetAssignment(path);
                await Assert.That(assignment).IsNotNull();
                await Assert.That(assignment!.OurSide).IsNull().Because("nothing is us and no me account is set");
                await Assert.That(assignment.OpponentTeamId).IsNull();
            }

            await Assert.That(teams.Teams.All(t => teams.DemosOf(t.Id).Count >= 2)).IsTrue()
                .Because("a single sighting never creates a team");
        }
    }

    [Test]
    public Task SideAtRound_OnTheRealRows() => throw new SkipTestException(WaitingOnEngine);
}
