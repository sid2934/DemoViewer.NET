#region

using System.Text.Json;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Team Identity over synthetic rosters (design §7, items 1 to 13): the matching rule and its ties,
///     the two tiers, names, us and me, merge, split, the id-preserving rebuild, overrides, the two
///     stores and the coach exclusion. No demo files.
/// </summary>
public class TeamIdentityTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), $"dv-teams-{Guid.NewGuid():N}");

    /// <summary>Fake accounts: "7656" plus the number, so a key reads as the numbers it was built from.</summary>
    private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    private static long Day(int n) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(n).Ticks;

    private static DemoCacheRecord Record(string path, int day, string[] t, string[] ct,
        string? tClan = null, string? ctClan = null, string? sha = null)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = Day(day),
            Sha256 = sha,
            Map = "de_nuke",
            TClan = tClan,
            CtClan = ctClan
        };
        int slot = 0;
        foreach (string id in t)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 2 });
        }

        foreach (string id in ct)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 3 });
        }

        DemoCacheStore.StampParse(record);
        return record;
    }

    private static (DemoCacheStore Cache, TeamIdentityService Teams) Fresh(string? root = null, IRoundFactsSource? facts = null)
    {
        DemoCacheStore cache = new(root is null ? null : Path.Combine(root, "cache"));
        TeamIdentityService teams = new(root, cache, facts, run: _inline);
        return (cache, teams);
    }

    // Opponents that never recur, so only the side under test clusters.
    private static int _stranger = 500;

    private static string[] Strangers() => Ids(Interlocked.Add(ref _stranger, 5) - 4, _stranger - 3, _stranger - 2, _stranger - 1, _stranger);

    // Start first (an empty rebuild), then one batch, so the records are assigned in a single pass
    // rather than assigned and then rebuilt against their own snapshots.
    private static async Task<TeamIdentityService> Seed(DemoCacheStore cache, TeamIdentityService teams, params DemoCacheRecord[] records)
    {
        await teams.StartAsync();
        using (cache.BeginBatch())
        {
            foreach (DemoCacheRecord record in records)
            {
                cache.Upsert(record);
            }
        }

        await teams.Idle;
        return teams;
    }

    // ── 1. Matching ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ThreeOfFiveMatches_TwoDoesNot_AndWingmanNeedsBoth()
    {
        (DemoCacheStore cache, TeamIdentityService teams) = Fresh();
        await Seed(cache, teams,
            Record("/d/a1.dem", 1, Ids(1, 2, 3, 4, 5), Strangers()),
            Record("/d/a2.dem", 2, Ids(1, 2, 3, 11, 12), Strangers()), // three of the five
            Record("/d/a3.dem", 3, Ids(1, 2, 13, 14, 15), Strangers()), // two of the five
            Record("/d/w1.dem", 4, Ids(21, 22), Ids(23, 24)),
            Record("/d/w2.dem", 5, Ids(21, 22), Ids(25, 26)), // both: matches
            Record("/d/w3.dem", 6, Ids(21, 27), Ids(28, 29)), // one of two: does not
            Record("/d/f1.dem", 7, Ids(31, 32, 33, 34), Strangers()),
            Record("/d/f2.dem", 8, Ids(31, 32, 33, 35), Strangers())); // three of a four-side

        TeamAssignment a1 = teams.GetAssignment("/d/a1.dem")!;
        using (Assert.Multiple())
        {
            await Assert.That(a1.T.TeamId).IsNotNull();
            await Assert.That(teams.GetAssignment("/d/a2.dem")!.T.TeamId).IsEqualTo(a1.T.TeamId);
            await Assert.That(teams.GetAssignment("/d/a2.dem")!.T.Overlap).IsEqualTo(3);
            await Assert.That(teams.GetAssignment("/d/a3.dem")!.T.TeamId).IsNull().Because("two of five is another roster");
            await Assert.That(teams.GetAssignment("/d/w2.dem")!.T.TeamId).IsEqualTo(teams.GetAssignment("/d/w1.dem")!.T.TeamId);
            await Assert.That(teams.GetAssignment("/d/w3.dem")!.T.TeamId).IsNull().Because("k = min(3, 2) needs both wingmen");
            await Assert.That(teams.GetAssignment("/d/f2.dem")!.T.TeamId).IsEqualTo(teams.GetAssignment("/d/f1.dem")!.T.TeamId)
                .Because("a four-player side matches on three");
        }
    }

    [Test]
    public async Task Ties_GoToAnEstablishedFive_ThenToTheRosterFoundedFirst()
    {
        (DemoCacheStore cache, TeamIdentityService teams) = Fresh();
        await Seed(cache, teams,
            // R2 first: two sides sharing four, no five established.
            Record("/d/r2a.dem", 1, Ids(3, 4, 5, 6, 7), Strangers()),
            Record("/d/r2b.dem", 2, Ids(3, 4, 5, 6, 8), Strangers()),
            // R1 later, its five established at the second sighting.
            Record("/d/r1a.dem", 3, Ids(1, 2, 3, 4, 5), Strangers()),
            Record("/d/r1b.dem", 4, Ids(1, 2, 3, 4, 5), Strangers()),
            // Overlap three with both: the established five wins although R2 was founded first.
            Record("/d/tie.dem", 5, Ids(3, 4, 5, 9, 10), Strangers()),
            // Two unestablished rosters at equal overlap: founded first.
            Record("/d/u1a.dem", 6, Ids(41, 42, 43, 44, 45), Strangers()),
            Record("/d/u1b.dem", 7, Ids(41, 42, 43, 44, 46), Strangers()),
            Record("/d/u2a.dem", 8, Ids(43, 47, 48, 49, 50), Strangers()),
            Record("/d/u2b.dem", 9, Ids(43, 47, 48, 49, 51), Strangers()),
            Record("/d/tie2.dem", 10, Ids(43, 44, 45, 47, 48), Strangers()));

        using (Assert.Multiple())
        {
            await Assert.That(teams.GetAssignment("/d/tie.dem")!.T.TeamId).IsEqualTo(teams.GetAssignment("/d/r1a.dem")!.T.TeamId);
            await Assert.That(teams.GetAssignment("/d/tie.dem")!.T.Tier).IsEqualTo(1);
            await Assert.That(teams.GetAssignment("/d/tie2.dem")!.T.TeamId).IsEqualTo(teams.GetAssignment("/d/u1a.dem")!.T.TeamId);
        }
    }

    // ── 2. Never both sides ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ARoster_NeverTakesBothSides_TheLargerOverlapKeepsIt()
    {
        (DemoCacheStore cache, TeamIdentityService teams) = Fresh();
        await Seed(cache, teams,
            Record("/d/a.dem", 1, Ids(1, 2, 3, 4, 5), Strangers()),
            Record("/d/b.dem", 2, Ids(1, 2, 3, 4, 5), Strangers()),
            Record("/d/mirror.dem", 3, Ids(1, 2, 3, 21, 22), Ids(1, 2, 3, 4, 23)));

        TeamAssignment mirror = teams.GetAssignment("/d/mirror.dem")!;
        using (Assert.Multiple())
        {
            await Assert.That(mirror.Ct.TeamId).IsEqualTo(teams.GetAssignment("/d/a.dem")!.T.TeamId).Because("four beats three");
            await Assert.That(mirror.T.TeamId).IsNull();
        }
    }

    // ── 3. Founding and the five ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task TwoSightingsFoundARoster_OneDoesNot_AndTheSameFiveTwiceFixesIt()
    {
        (DemoCacheStore cache, TeamIdentityService teams) = Fresh();
        await Seed(cache, teams,
            Record("/d/one.dem", 1, Ids(61, 62, 63, 64, 65), Strangers()),
            Record("/d/a.dem", 2, Ids(1, 2, 3, 4, 5), Strangers()),
            Record("/d/b.dem", 3, Ids(1, 2, 3, 4, 5), Strangers()),
            Record("/d/c.dem", 4, Ids(11, 12, 13, 14, 15), Strangers()),
            Record("/d/d.dem", 5, Ids(11, 12, 13, 16, 17), Strangers()));

        Team five = teams.TeamOnSide("/d/a.dem", 2)!;
        Team three = teams.TeamOnSide("/d/c.dem", 2)!;
        using (Assert.Multiple())
        {
            await Assert.That(teams.GetAssignment("/d/one.dem")!.T.TeamId).IsNull().Because("a single sighting never creates a team");
            await Assert.That(teams.UnaffiliatedCount).IsEqualTo(1 + 5).Because("the lone side and five stranger sides");
            await Assert.That(teams.Teams.Count).IsEqualTo(2);
            await Assert.That(five.Rosters[0].CoreLineup!).IsEquivalentTo(Ids(1, 2, 3, 4, 5));
            await Assert.That(three.Rosters[0].CoreLineup).IsNull();
            await Assert.That(three.Rosters[0].ExtendedCore!.Order(StringComparer.Ordinal)).IsEquivalentTo(Ids(11, 12, 13, 14, 15, 16, 17));
        }
    }

    // ── 4. Two tiers ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task OnceAFiveExists_ItIsTheAnchor_AndTheOldExtendedCoreIsNot()
    {
        (DemoCacheStore cache, TeamIdentityService teams) = Fresh();
        await Seed(cache, teams,
            Record("/d/s1.dem", 1, Ids(1, 2, 3, 4, 6), Strangers()),
            Record("/d/s2.dem", 2, Ids(1, 2, 3, 5, 7), Strangers()),
            Record("/d/s3.dem", 3, Ids(1, 2, 3, 4, 5), Strangers()),
            Record("/d/s4.dem", 4, Ids(1, 2, 3, 4, 5), Strangers()), // establishes the five
            Record("/d/standin.dem", 5, Ids(1, 2, 3, 30, 31), Strangers()),
            Record("/d/old.dem", 6, Ids(1, 2, 6, 7, 40), Strangers())); // two of the five, four of the old core

        Guid team = teams.GetAssignment("/d/s1.dem")!.T.TeamId!.Value;
        TeamAssignment standIn = teams.GetAssignment("/d/standin.dem")!;
        using (Assert.Multiple())
        {
            await Assert.That(teams.GetAssignment("/d/s4.dem")!.T.Tier).IsEqualTo(2).Because("the sighting that establishes the five matched the extended core");
            await Assert.That(standIn.T.TeamId).IsEqualTo(team);
            await Assert.That(standIn.T.Tier).IsEqualTo(1);
            await Assert.That(standIn.T.StandIn).IsTrue();
            await Assert.That(teams.GetAssignment("/d/old.dem")!.T.TeamId).IsNull().Because("the fixed five is the anchor now");
        }
    }

    [Test]
    public async Task BeforeAFive_TheExtendedCoreOfSevenIsTheAnchor()
    {
        (DemoCacheStore cache, TeamIdentityService teams) = Fresh();
        List<DemoCacheRecord> records = [];
        for (int i = 0; i < 20; i++)
        {
            records.Add(Record($"/d/r{i:D2}.dem", i + 1, Ids(1, 2, 3, 4, 100 + i), Strangers()));
        }

        records.Add(Record("/d/acq.dem", 30, Ids(1, 90, 91), Strangers()));
        await Seed(cache, teams, [.. records]);

        Team team = teams.TeamOnSide("/d/r00.dem", 2)!;
        using (Assert.Multiple())
        {
            await Assert.That(team.Rosters[0].CoreLineup).IsNull().Because("no five ever repeated");
            await Assert.That(team.Rosters[0].ExtendedCore!.Order(StringComparer.Ordinal)).IsEquivalentTo(Ids(1, 2, 3, 4, 100, 101, 102))
                .Because("the constants plus the longest-standing fifths");
            await Assert.That(teams.GetAssignment("/d/acq.dem")!.T.TeamId).IsNull()
                .Because("the owner plus two acquaintances outside the core shares one");
        }
    }

    // ── 5. Names ─────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ClanTags_FoldCaseInsensitively_AndAUserNameIsNeverOverwritten()
    {
        (DemoCacheStore cache, TeamIdentityService teams) = Fresh();
        await Seed(cache, teams,
            Record("/d/1.dem", 1, Ids(1, 2, 3, 4, 5), Strangers(), tClan: "Furia"),
            Record("/d/2.dem", 2, Ids(1, 2, 3, 4, 5), Strangers(), tClan: "FURIA"),
            Record("/d/3.dem", 3, Ids(1, 2, 3, 4, 5), Strangers(), tClan: "FURIA"),
            Record("/d/4.dem", 4, Ids(1, 2, 3, 4, 5), Strangers(), tClan: "FURIA"),
            Record("/d/5.dem", 5, Ids(1, 2, 3, 4, 5), Strangers(), tClan: "FURIA"));

        Team team = teams.Teams.Single();
        await Assert.That(team.Name).IsEqualTo("FURIA").Because("one folded name, the most recent spelling");
        await Assert.That(team.NameSource).IsEqualTo(TeamNameSource.ClanTag);

        teams.Rename(team.Id, "FURIA Esports");
        cache.Upsert(Record("/d/6.dem", 6, Ids(1, 2, 3, 4, 5), Strangers(), tClan: "furia"));
        await teams.Idle;

        using (Assert.Multiple())
        {
            await Assert.That(teams.Teams.Single().Name).IsEqualTo("FURIA Esports");
            await Assert.That(teams.Teams.Single().NameSource).IsEqualTo(TeamNameSource.User);
            await Assert.That(teams.DemosOf(team.Id).Count).IsEqualTo(6);
        }
    }

    // ── 6. Us and me ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task OurSide_ResolvesOverrideThenTeamThenMe_AndSetUsClearsThePrevious()
    {
        (DemoCacheStore cache, TeamIdentityService teams) = Fresh();
        await Seed(cache, teams,
            Record("/d/a.dem", 1, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15)),
            Record("/d/b.dem", 2, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15)),
            Record("/d/me.dem", 3, Ids(21, 22, 23, 24, 25), Ids(13, 31, 32, 33, 34)),
            Record("/d/none.dem", 4, Ids(41, 42, 43, 44, 45), Ids(51, 52, 53, 54, 55)));

        Guid us = teams.TeamOnSide("/d/a.dem", 2)!.Id;
        Guid them = teams.TeamOnSide("/d/a.dem", 3)!.Id;
        await Assert.That(teams.GetAssignment("/d/a.dem")!.OurSide).IsNull().Because("nothing is us yet");

        teams.SetUs(us);
        teams.SetMyAccounts(Ids(13));
        TeamAssignment a = teams.GetAssignment("/d/a.dem")!;
        TeamAssignment me = teams.GetAssignment("/d/me.dem")!;
        using (Assert.Multiple())
        {
            await Assert.That(a.OurSide).IsEqualTo(2);
            await Assert.That(a.Source).IsEqualTo(OurSideSource.Team).Because("the team rule fires before the me account on the other side");
            await Assert.That(a.OpponentTeamId).IsEqualTo(them);
            await Assert.That(me.OurSide).IsEqualTo(3);
            await Assert.That(me.Source).IsEqualTo(OurSideSource.Me);
            await Assert.That(me.OpponentTeamId).IsNull().Because("the other side is unaffiliated");
            await Assert.That(teams.GetAssignment("/d/none.dem")!.OurSide).IsNull();
            await Assert.That(teams.GetAssignment("/d/none.dem")!.OpponentTeamId).IsNull().Because("no our side, no opponent");
            await Assert.That(teams.OurDemos().Count).IsEqualTo(3);
            await Assert.That(teams.DemosAgainst(them).Select(d => d.Path)).IsEquivalentTo(["/d/b.dem", "/d/a.dem"]);
        }

        teams.Override("/d/a.dem", 3, us);
        await Assert.That(teams.GetAssignment("/d/a.dem")!.OurSide).IsEqualTo(3);
        await Assert.That(teams.GetAssignment("/d/a.dem")!.Source).IsEqualTo(OurSideSource.Override);

        teams.SetUs(them);
        using (Assert.Multiple())
        {
            await Assert.That(teams.Us!.Id).IsEqualTo(them);
            await Assert.That(teams.AllTeams.Count(t => t.IsUs)).IsEqualTo(1);
            await Assert.That(teams.GetAssignment("/d/b.dem")!.OurSide).IsEqualTo(3);
        }
    }

    [Test]
    public async Task TheMeSuggestion_NamesTheMajorityAccount_AndWritesOnlyOnConfirmation()
    {
        (DemoCacheStore cache, TeamIdentityService teams) = Fresh();
        List<DemoCacheRecord> records = [];
        for (int i = 0; i < 20; i++)
        {
            // The owner (1) with a different four every time, against strangers: no roster, one account.
            records.Add(Record($"/d/q{i:D2}.dem", i + 1, [.. Ids(1), .. Ids(200 + i * 4, 201 + i * 4, 202 + i * 4, 203 + i * 4)], Strangers()));
        }

        await Seed(cache, teams, [.. records]);

        using (Assert.Multiple())
        {
            await Assert.That(teams.MeSuggestion).IsNotNull();
            await Assert.That(teams.MeSuggestion!.SteamId64).IsEqualTo(Ids(1)[0]);
            await Assert.That(teams.MeSuggestion.Share).IsEqualTo(1.0);
            await Assert.That(teams.MyAccounts).IsEmpty().Because("nothing is written without confirmation");
            await Assert.That(teams.OurDemos()).IsEmpty();
        }

        teams.ConfirmMeSuggestion();
        using (Assert.Multiple())
        {
            await Assert.That(teams.MyAccounts).IsEquivalentTo(Ids(1));
            await Assert.That(teams.MeSuggestion).IsNull();
            await Assert.That(teams.OurDemos().Count).IsEqualTo(20);
        }
    }

    // ── 7. Merge ─────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Merge_ReassignsSides_KeepsBothAnchors_AndARebuildNeverRecreatesTheTombstone()
    {
        string root = TempRoot();
        try
        {
            (DemoCacheStore cache, TeamIdentityService teams) = Fresh(root);
            await Seed(cache, teams,
                Record("/d/a1.dem", 1, Ids(1, 2, 3, 4, 5), Strangers()),
                Record("/d/a2.dem", 2, Ids(1, 2, 3, 4, 5), Strangers()),
                Record("/d/b1.dem", 3, Ids(4, 5, 6, 7, 8), Strangers()), // two of A's five: a new roster
                Record("/d/b2.dem", 4, Ids(4, 5, 6, 7, 8), Strangers()));

            Guid a = teams.TeamOnSide("/d/a1.dem", 2)!.Id;
            Guid b = teams.TeamOnSide("/d/b1.dem", 2)!.Id;
            await Assert.That(a).IsNotEqualTo(b);

            teams.Rename(a, "Old Guard");
            teams.Merge(a, b);
            Team merged = teams.Teams.Single();
            using (Assert.Multiple())
            {
                await Assert.That(merged.Id).IsEqualTo(a);
                await Assert.That(merged.Name).IsEqualTo("Old Guard");
                await Assert.That(merged.Rosters.Count).IsEqualTo(2);
                await Assert.That(merged.Rosters.Select(r => r.CoreLineup!.Count)).IsEquivalentTo([5, 5]).Because("each roster keeps its own five");
                await Assert.That(merged.MergedFrom).IsEquivalentTo([b]);
                await Assert.That(teams.DemosOf(a).Count).IsEqualTo(4);
                await Assert.That(teams.GetAssignment("/d/b2.dem")!.T.TeamId).IsEqualTo(a);
            }

            TeamsFile file = JsonSerializer.Deserialize<TeamsFile>(File.ReadAllText(Path.Combine(root, "teams.json")), TeamsFile.JsonOptions)!;
            await Assert.That(file.Tombstones).IsEquivalentTo([b]);

            cache.SaveIndex(); // the library persists index.json itself; the sidecars are already on disk
            teams.Dispose();
            File.Delete(Path.Combine(root, "cache", "team-index.json"));
            (_, TeamIdentityService rebuilt) = Fresh(root);
            await rebuilt.StartAsync();
            using (Assert.Multiple())
            {
                await Assert.That(rebuilt.AllTeams.Select(t => t.Id)).IsEquivalentTo([a]);
                await Assert.That(rebuilt.DemosOf(a).Count).IsEqualTo(4);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ── 8. Split ─────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Split_MakesANewTeam_AndBothSurviveARebuild()
    {
        string root = TempRoot();
        try
        {
            (DemoCacheStore cache, TeamIdentityService teams) = Fresh(root);
            await Seed(cache, teams,
                Record("/d/a1.dem", 1, Ids(1, 2, 3, 4, 5), Strangers()),
                Record("/d/a2.dem", 2, Ids(1, 2, 3, 4, 5), Strangers()),
                Record("/d/s1.dem", 3, Ids(3, 4, 5, 6, 7), Strangers()), // a sister roster sharing three
                Record("/d/s2.dem", 4, Ids(3, 4, 5, 6, 7), Strangers()));

            Guid original = teams.TeamOnSide("/d/a1.dem", 2)!.Id;
            await Assert.That(teams.TeamOnSide("/d/s2.dem", 2)!.Id).IsEqualTo(original).Because("three shared members cluster together");

            Guid sister = teams.Split(original, [new DemoSideRef("/d/s1.dem", 2), new DemoSideRef("/d/s2.dem", 2)], "Academy");
            using (Assert.Multiple())
            {
                await Assert.That(sister).IsNotEqualTo(original);
                await Assert.That(teams.DemosOf(sister).Select(d => d.Path)).IsEquivalentTo(["/d/s2.dem", "/d/s1.dem"]);
                await Assert.That(teams.DemosOf(original).Select(d => d.Path)).IsEquivalentTo(["/d/a2.dem", "/d/a1.dem"]);
                await Assert.That(teams.AllTeams.Single(t => t.Id == sister).Rosters[0].CoreLineup!).IsEquivalentTo(Ids(3, 4, 5, 6, 7));
            }

            cache.SaveIndex(); // the library persists index.json itself; the sidecars are already on disk
            teams.Dispose();
            File.Delete(Path.Combine(root, "cache", "team-index.json"));
            (_, TeamIdentityService rebuilt) = Fresh(root);
            await rebuilt.StartAsync();
            using (Assert.Multiple())
            {
                await Assert.That(rebuilt.AllTeams.Select(t => t.Id)).IsEquivalentTo([original, sister]);
                await Assert.That(rebuilt.DemosOf(sister).Count).IsEqualTo(2);
                await Assert.That(rebuilt.DemosOf(original).Count).IsEqualTo(2);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ── 9. Rebuild ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ARebuild_KeepsTheIdAndTheName_OnTheSameDemos()
    {
        string root = TempRoot();
        try
        {
            (DemoCacheStore cache, TeamIdentityService teams) = Fresh(root);
            await Seed(cache, teams,
                Record("/d/a1.dem", 1, Ids(1, 2, 3, 4, 5), Strangers()),
                Record("/d/a2.dem", 2, Ids(1, 2, 3, 4, 6), Strangers()),
                Record("/d/a3.dem", 3, Ids(1, 2, 3, 5, 6), Strangers()));

            Guid id = teams.TeamOnSide("/d/a1.dem", 2)!.Id;
            teams.Rename(id, "The Trio");
            cache.SaveIndex(); // the library persists index.json itself; the sidecars are already on disk
            teams.Dispose();

            File.Delete(Path.Combine(root, "cache", "team-index.json"));
            (_, TeamIdentityService rebuilt) = Fresh(root);
            await Assert.That(rebuilt.NeedsRebuild).IsTrue();
            await rebuilt.StartAsync();

            using (Assert.Multiple())
            {
                await Assert.That(rebuilt.NeedsRebuild).IsFalse();
                await Assert.That(rebuilt.Teams.Single().Id).IsEqualTo(id);
                await Assert.That(rebuilt.Teams.Single().Name).IsEqualTo("The Trio");
                await Assert.That(rebuilt.DemosOf(id).Select(d => d.Path)).IsEquivalentTo(["/d/a3.dem", "/d/a2.dem", "/d/a1.dem"]);
                await Assert.That(File.Exists(Path.Combine(root, "cache", "team-index.json"))).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ── 10. Overrides ────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Overrides_KeyByStableKeyUntilTheHashAppears_ThenBySha256()
    {
        string root = TempRoot();
        try
        {
            (DemoCacheStore cache, TeamIdentityService teams) = Fresh(root);
            await Seed(cache, teams,
                Record("/d/a1.dem", 1, Ids(1, 2, 3, 4, 5), Strangers()),
                Record("/d/a2.dem", 2, Ids(1, 2, 3, 4, 5), Strangers()),
                Record("/d/x.dem", 3, Ids(21, 22, 23, 24, 25), Strangers()));

            Guid team = teams.TeamOnSide("/d/a1.dem", 2)!.Id;
            teams.Override("/d/x.dem", 2, team);
            TeamsFile file = JsonSerializer.Deserialize<TeamsFile>(File.ReadAllText(Path.Combine(root, "teams.json")), TeamsFile.JsonOptions)!;
            using (Assert.Multiple())
            {
                await Assert.That(file.Overrides.Single().DemoStableKey).IsEqualTo(DemoCacheStore.StableKey("/d/x.dem"));
                await Assert.That(file.Overrides.Single().DemoSha256).IsNull();
                await Assert.That(teams.GetAssignment("/d/x.dem")!.T.TeamId).IsEqualTo(team);
                await Assert.That(teams.GetAssignment("/d/x.dem")!.T.Tier).IsEqualTo(0).Because("an override consults no anchor");
            }

            cache.Upsert(Record("/d/x.dem", 3, Ids(21, 22, 23, 24, 25), Strangers(), sha: "ab12"));
            await teams.Idle;
            file = JsonSerializer.Deserialize<TeamsFile>(File.ReadAllText(Path.Combine(root, "teams.json")), TeamsFile.JsonOptions)!;
            using (Assert.Multiple())
            {
                await Assert.That(file.Overrides.Single().DemoSha256).IsEqualTo("ab12");
                await Assert.That(file.Overrides.Single().DemoStableKey).IsNull();
                await Assert.That(teams.GetAssignment("/d/x.dem")!.T.TeamId).IsEqualTo(team);
            }

            teams.Override("/d/a2.dem", 2, null);
            await Assert.That(teams.GetAssignment("/d/a2.dem")!.T.TeamId).IsNull().Because("not a team");
            await Assert.That(teams.DemosOf(team).Select(d => d.Path)).IsEquivalentTo(["/d/x.dem", "/d/a1.dem"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ── 11. Stores ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Stores_WriteAtomically_RebuildOnACorruptIndex_AndRefuseACorruptTeamsFile()
    {
        string root = TempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "cache"));
            File.WriteAllText(Path.Combine(root, "cache", "team-index.json"), "{ not json");
            (DemoCacheStore cache, TeamIdentityService teams) = Fresh(root);
            await Assert.That(teams.NeedsRebuild).IsTrue().Because("a corrupt index is a cache to rebuild, not a crash");
            await Seed(cache, teams,
                Record("/d/a1.dem", 1, Ids(1, 2, 3, 4, 5), Strangers()),
                Record("/d/a2.dem", 2, Ids(1, 2, 3, 4, 5), Strangers()));

            using (Assert.Multiple())
            {
                await Assert.That(teams.TeamsFileProblem).IsNull();
                await Assert.That(File.Exists(Path.Combine(root, "teams.json"))).IsTrue();
                await Assert.That(JsonSerializer.Deserialize<TeamIndexFile>(File.ReadAllText(Path.Combine(root, "cache", "team-index.json")),
                    TeamsFile.JsonOptions)!.Demos.Count).IsEqualTo(2);
                await Assert.That(Directory.GetFiles(root, ".dc-*.tmp", SearchOption.AllDirectories)).IsEmpty().Because("temp files are replaced, never left");
            }

            cache.SaveIndex(); // the library persists index.json itself; the sidecars are already on disk
            teams.Dispose();
            const string junk = "{ \"schemaVersion\": 1, \"teams\": [ oops";
            File.WriteAllText(Path.Combine(root, "teams.json"), junk);
            (_, TeamIdentityService refused) = Fresh(root);
            await refused.StartAsync();
            refused.SetMyAccounts(Ids(1));
            using (Assert.Multiple())
            {
                await Assert.That(refused.TeamsFileProblem).IsNotNull();
                await Assert.That(File.ReadAllText(Path.Combine(root, "teams.json"))).IsEqualTo(junk).Because("a file that could not be read is never overwritten");
                await Assert.That(refused.MyAccounts).IsEquivalentTo(Ids(1)).Because("the session still works in memory");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task TheBrowserHost_IsSessionOnly_AndEveryApiStillWorks()
    {
        (DemoCacheStore cache, TeamIdentityService teams) = Fresh();
        await Seed(cache, teams,
            Record("/d/a1.dem", 1, Ids(1, 2, 3, 4, 5), Strangers()),
            Record("/d/a2.dem", 2, Ids(1, 2, 3, 4, 5), Strangers()));

        using (Assert.Multiple())
        {
            await Assert.That(teams.IsSessionOnly).IsTrue();
            await Assert.That(teams.Teams.Count).IsEqualTo(1);
            await Assert.That(teams.DemoCount).IsEqualTo(2);
        }
    }

    // ── 12. Schema snapshot ──────────────────────────────────────────────────────────────────────

    // A fixed user file and fixed inputs, so the two files serialize the same way on every build. No
    // auto team is founded here: their ids are fresh GUIDs and would not pin.
    private static (TeamsFile Teams, TeamIndexFile Index) FixtureFiles()
    {
        Guid furia = Guid.Parse("3f2a0000-0000-4000-8000-000000000001");
        Guid gone = Guid.Parse("9c1b0000-0000-4000-8000-000000000002");
        TeamsFile teams = new()
        {
            Me = new MeAccounts { SteamIds = ["76561198000000001"] },
            Teams =
            [
                new Team
                {
                    Id = furia,
                    Name = "FURIA",
                    NameSource = TeamNameSource.ClanTag,
                    Rosters =
                    [
                        new Roster
                        {
                            Id = "r1",
                            Since = new DateOnly(2026, 6, 1),
                            CoreLineup = [.. Ids(1, 2, 3, 4, 5)],
                            ExtendedCore = [.. Ids(1, 2, 3, 4, 5)]
                        }
                    ],
                    MergedFrom = [gone]
                }
            ],
            Tombstones = [gone],
            Overrides = [new TeamOverride { DemoSha256 = "ab12", Side = 3, TeamId = null }],
            Provenance = new ProvenanceSection
            {
                Overrides = [new ProvenanceOverride { DemoSha256 = "ab12", Label = "our scrim" }]
            }
        };
        TeamClusterer clusterer = new(teams);
        clusterer.Assign(new DemoSideInput
        {
            StableKey = "demo1",
            Path = "C:\\demos\\one.dem",
            Sha256 = null,
            OrderTicks = Day(160),
            T = new SideInput(Ids(1, 2, 3, 4, 6), ["art", "yuurih", "KSCERATO", "chelo", "skullz"], "FURIA"),
            Ct = new SideInput(Ids(11, 12, 13, 14, 15), ["a", "b", "c", "d", "e"], null)
        });
        clusterer.Assign(new DemoSideInput
        {
            StableKey = "demo2",
            Path = "C:\\demos\\two.dem",
            Sha256 = "ab12",
            OrderTicks = Day(161),
            T = new SideInput(Ids(1, 2, 3, 4, 5), ["art", "yuurih", "KSCERATO", "chelo", "FalleN"], "FURIA"),
            Ct = new SideInput(Ids(21, 22, 23, 24, 25), ["f", "g", "h", "i", "j"], null)
        }, new Dictionary<int, Guid?> { [3] = null });
        return (teams, clusterer.Finish(Day(200)));
    }

    private static string Lf(string json) => json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    [Test]
    public async Task V1Schema_MatchesTheCheckedInSamples()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        (TeamsFile teams, TeamIndexFile index) = FixtureFiles();
        // The serializer indents with the platform newline; the committed sample is LF.
        string teamsJson = Lf(JsonSerializer.Serialize(teams, TeamsFile.JsonOptions));
        string indexJson = Lf(JsonSerializer.Serialize(index, TeamsFile.JsonOptions));
        string teamsPath = Path.Combine(repo, "tests", "fixtures", "team-identity", "teams.sample.json");
        string indexPath = Path.Combine(repo, "tests", "fixtures", "team-identity", "team-index.sample.json");
        if (Environment.GetEnvironmentVariable("TI_GOLDEN_UPDATE") == "1")
        {
            File.WriteAllText(teamsPath, teamsJson);
            File.WriteAllText(indexPath, indexJson);
        }

        if (!File.Exists(teamsPath) || !File.Exists(indexPath))
        {
            throw new SkipTestException("missing the team-identity samples; regenerate with TI_GOLDEN_UPDATE=1");
        }

        TeamsFile? teamsBack = JsonSerializer.Deserialize<TeamsFile>(File.ReadAllText(teamsPath), TeamsFile.JsonOptions);
        TeamIndexFile? indexBack = JsonSerializer.Deserialize<TeamIndexFile>(File.ReadAllText(indexPath), TeamsFile.JsonOptions);
        using (Assert.Multiple())
        {
            await Assert.That(teamsJson).IsEqualTo(File.ReadAllText(teamsPath).Replace("\r\n", "\n", StringComparison.Ordinal))
                .Because("teams.json is a published format; this build still writes the committed shape");
            await Assert.That(indexJson).IsEqualTo(File.ReadAllText(indexPath).Replace("\r\n", "\n", StringComparison.Ordinal))
                .Because("team-index.json is a published format; this build still writes the committed shape");
            await Assert.That(Lf(JsonSerializer.Serialize(teamsBack, TeamsFile.JsonOptions))).IsEqualTo(teamsJson).Because("round trip");
            await Assert.That(Lf(JsonSerializer.Serialize(indexBack, TeamsFile.JsonOptions))).IsEqualTo(indexJson).Because("round trip");
            await Assert.That(indexBack!.Demos["demo1"].Side(2)!.Tier).IsEqualTo(1);
            await Assert.That(indexBack.Demos["demo1"].Side(2)!.StandIn).IsTrue();
            await Assert.That(indexBack.Demos["demo2"].Side(3)!.Override).IsTrue();
            await Assert.That(teamsBack!.Teams.Single().Rosters.Single().CoreLineup).IsEquivalentTo(Ids(1, 2, 3, 4, 5))
                .Because("a rebuild never rewrites an established five");
            await Assert.That(teamsBack.Provenance.Overrides.Single().Label).IsEqualTo("our scrim")
                .Because("the provenance section rides in the same file");
        }
    }

    // ── 13. Coaches ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ACoach_IsNotInTheSideKey()
    {
        DemoCacheRecord record = Record("/d/coach.dem", 1, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15));
        record.Players.Add(new CachedPlayerInfo { Slot = 10, Name = "coach", SteamId64 = Ids(99)[0], Team = 3, IsCoach = true });
        record.Players.Add(new CachedPlayerInfo { Slot = 11, Name = "BOT Rock", SteamId64 = "", Team = 2, IsBot = true });

        SideInput ct = SideKeys.Side(record, 3);
        SideInput t = SideKeys.Side(record, 2);
        using (Assert.Multiple())
        {
            await Assert.That(record.Players.Count(p => p.Team == 3)).IsEqualTo(6);
            await Assert.That(ct.Key).IsEquivalentTo(Ids(11, 12, 13, 14, 15)).Because("a six-member side with one coach yields a five-member key");
            await Assert.That(t.Key).IsEquivalentTo(Ids(1, 2, 3, 4, 5)).Because("bots are not in a key either");
            await Assert.That(ct.Names).Contains("p0011");
        }
    }

    // ── SideAtRound (overview correction 9) ──────────────────────────────────────────────────────

    private sealed class FakeFacts(RoundFactsRows rows) : IRoundFactsSource
    {
        public int Schema => 1;

        public event Action<string>? Updated
        {
            add { }
            remove { }
        }

        public RoundFactsRows? TryGet(string demoPath) => rows;

        public RoundFacts? RoundAt(string demoPath, int frameClockTick) => null;

        public IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> Query(RoundFactsFilter filter) => [];

        public IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null) => [];
    }

    [Test]
    public async Task SideAtRound_JoinsTheRoundFactsSlots_AgainstTheTeamsKey()
    {
        // Slots 0-4 are T at the end of the demo (the roster's five), 5-9 are CT. In round 1 the five
        // started on CT and swapped at halftime, so round 1 says CT and round 13 says T.
        RoundFactsRows rows = RoundIndexTestData.Facts(
            RoundIndexTestData.Round(1, 1000, 2000, ctSlots: [0, 1, 2, 3, 4], tSlots: [5, 6, 7, 8, 9]),
            RoundIndexTestData.Round(13, 50000, 51000, ctSlots: [5, 6, 7, 8, 9], tSlots: [0, 1, 2, 3, 4]));
        (DemoCacheStore cache, TeamIdentityService teams) = Fresh(facts: new FakeFacts(rows));
        await Seed(cache, teams,
            Record("/d/a1.dem", 1, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15)),
            Record("/d/a2.dem", 2, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15)));

        Guid team = teams.TeamOnSide("/d/a2.dem", 2)!.Id;
        using (Assert.Multiple())
        {
            await Assert.That(teams.GetAssignment("/d/a2.dem")!.T.TeamId).IsEqualTo(team).Because("the end-of-demo side is T");
            await Assert.That(teams.SideAtRound("/d/a2.dem", team, 1)).IsEqualTo(3);
            await Assert.That(teams.SideAtRound("/d/a2.dem", team, 13)).IsEqualTo(2);
            await Assert.That(teams.SideAtRound("/d/a2.dem", team, 7)).IsNull().Because("no such round");
            await Assert.That(teams.SideAtRound("/d/a2.dem", Guid.NewGuid(), 1)).IsNull().Because("not a team of this demo");
        }
    }

    [Test]
    public async Task Removal_DropsTheDemo_AndAnAutoTeamWithNoSidesGoesWithIt()
    {
        (DemoCacheStore cache, TeamIdentityService teams) = Fresh();
        await Seed(cache, teams,
            Record("/d/a1.dem", 1, Ids(1, 2, 3, 4, 5), Strangers()),
            Record("/d/a2.dem", 2, Ids(1, 2, 3, 4, 5), Strangers()));
        await Assert.That(teams.Teams.Count).IsEqualTo(1);

        Guid id = teams.Teams.Single().Id;
        cache.Remove("/d/a2.dem");
        await teams.Idle;
        using (Assert.Multiple())
        {
            await Assert.That(teams.DemoCount).IsEqualTo(1);
            await Assert.That(teams.Teams.Single().Id).IsEqualTo(id).Because("an existing roster keeps its id on its remaining side");
            await Assert.That(teams.DemosOf(id).Count).IsEqualTo(1);
            await Assert.That(teams.GetAssignment("/d/a2.dem")).IsNull();
        }
    }
}
