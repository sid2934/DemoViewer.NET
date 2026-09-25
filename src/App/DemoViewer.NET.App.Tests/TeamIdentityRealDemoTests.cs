#region

using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Team Identity against the real replays folder: the tier-2 projection with the coach read, and
///     clustering over a bounded sample of the folder, reporting the team count without asserting one
///     (the count depends on whose library the folder is). The per-round side join reads the real
///     engine Round Facts rows for the sample's reference demo.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class TeamIdentityRealDemoTests
{
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

    /// <summary>
    ///     SideAtRound over one real demo's roster and its production Round Facts rows. The clustering
    ///     floor needs two sightings of the same roster, so the record is upserted a second time under a
    ///     distinct stable key (the two-sighting rule, not the roster or the rows, is what is faked); the
    ///     slots and the rows the join reads are the real demo's own.
    /// </summary>
    [Test]
    public async Task SideAtRound_OnTheRealRows()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<ClipRound> rounds = ClipRounds.Derive(parsed);
        if (rounds.Count == 0)
        {
            throw new SkipTestException("demo carries no rounds");
        }

        DemoCacheStore cache = new(null);
        RoundFactsEvaluator evaluator = new(cache, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity());
        RoundFactsSource roundFacts = new(cache, evaluator);
        using TeamIdentityService teams = new(null, cache, roundFacts, run: _inline);
        await teams.StartAsync();

        DemoCacheRecord record = Tier2Record(path, parsed);
        DemoCacheRecord secondSighting = Tier2Record(path, parsed);
        secondSighting.Path = path + ".twin";
        using (cache.BeginBatch())
        {
            cache.Upsert(record);
            cache.Upsert(secondSighting);
        }

        await teams.Idle;
        evaluator.OnParsedOpportunistically(path, parsed);

        Team? onT = teams.TeamOnSide(path, 2);
        Team? onCt = teams.TeamOnSide(path, 3);
        if (onT is null && onCt is null)
        {
            throw new SkipTestException("the real roster did not cluster into a team");
        }

        (Guid teamId, int endSide) = onT is not null ? (onT.Id, 2) : (onCt!.Id, 3);

        // The demo's last round can be a truncated tail with fewer than three of the five still seated;
        // every other round is a real one (RoundIndexRealDemoTests carries the same caveat).
        ClipRound safeLast = rounds.Count > 1 ? rounds[^2] : rounds[^1];

        using (Assert.Multiple())
        {
            await Assert.That(teams.SideAtRound(path, teamId, safeLast.Number)).IsEqualTo(endSide)
                .Because("no sub reached this roster before its last full round");
            await Assert.That(teams.SideAtRound(path, teamId, rounds[0].Number)).IsNotNull()
                .Because("the roster's five sat one real side or the other in round 1 too");
            await Assert.That(teams.SideAtRound(path, Guid.NewGuid(), rounds[0].Number)).IsNull()
                .Because("not a team of this demo");
        }
    }
}
