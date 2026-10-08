#region

using System.Text.Json;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Extensions.StratBook.Services.Provenance;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Team Identity Revision: queue play (Valve matchmaking, FACEIT) founds no team, a squad the user
///     confirms is their team there, suggestions wait for the user, and a team another store points at
///     survives a rebuild. Pinned on the anonymised corpus and on synthetic rosters.
/// </summary>
public class TeamIdentityRevisionTests
{
    private sealed record FixtureSide(List<string> Key, string? Clan);

    private sealed record FixtureDemo(string Id, string Kind, long OrderTicks, Dictionary<string, FixtureSide> Sides);

    private sealed record Fixture(int SchemaVersion, string Note, List<FixtureDemo> Demos);

    private static readonly JsonSerializerOptions _readOptions = new() { PropertyNameCaseInsensitive = true };

    private static List<DemoSideInput> Corpus(string kind, string? sourceKind)
    {
        string repo = DemoTestHelper.FindRepoRoot() ?? throw new SkipTestException("repo root not found from the test output directory");
        Fixture fixture = JsonSerializer.Deserialize<Fixture>(
            File.ReadAllText(Path.Combine(repo, "tests", "fixtures", "team-identity", "side-keys.v1.json")), _readOptions)!;
        return
        [
            .. fixture.Demos.Where(d => d.Kind == kind).Select(d => new DemoSideInput
            {
                StableKey = DemoCacheStore.StableKey(d.Id),
                Path = d.Id,
                OrderTicks = d.OrderTicks,
                SourceKind = sourceKind,
                T = new SideInput(d.Sides["2"].Key, d.Sides["2"].Key, d.Sides["2"].Clan),
                Ct = new SideInput(d.Sides["3"].Key, d.Sides["3"].Key, d.Sides["3"].Clan)
            })
        ];
    }

    private static TeamIndexFile Cluster(TeamsFile teams, IEnumerable<DemoSideInput> inputs, IReadOnlySet<Guid>? keep = null)
    {
        TeamClusterer clusterer = new(teams);
        foreach (DemoSideInput input in inputs.OrderBy(i => i.OrderTicks).ThenBy(i => i.Path, StringComparer.Ordinal))
        {
            clusterer.Assign(input, null, TeamSourcePolicy.AutoTracks(input.SourceKind, input.BothClanTags, null));
        }

        return clusterer.Finish(1, keep);
    }

    // The account on the most sides: the owner of the replay library.
    private static string Owner(IEnumerable<DemoSideInput> inputs) =>
        inputs.SelectMany(i => i.T.Key.Concat(i.Ct.Key))
            .GroupBy(id => id, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .First().Key;

    private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    private static DemoSideInput Demo(string id, int day, string[] t, string[] ct, string? tClan = null, string? ctClan = null,
        string? sourceKind = null) => new()
    {
        StableKey = DemoCacheStore.StableKey(id),
        Path = id,
        OrderTicks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
        SourceKind = sourceKind,
        T = new SideInput(t, t, tClan),
        Ct = new SideInput(ct, ct, ctClan)
    };

    private static int _stranger = 7000;

    private static string[] Strangers() => Ids(Interlocked.Add(ref _stranger, 5) - 4, _stranger - 3, _stranger - 2, _stranger - 1, _stranger);

    // ── The source gate ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task TheGate_ReadsQueuePlay_FromTheHeaderTheServerAndThePin()
    {
        using (Assert.Multiple())
        {
            await Assert.That(TeamSourcePolicy.EffectiveKind(null, "Valve Counter-Strike 2 us_east Server (srcds)"))
                .IsEqualTo(DemoSourceKind.GotvMatchmaking);
            await Assert.That(TeamSourcePolicy.EffectiveKind(null, "FACEIT.com register to play here")).IsEqualTo(DemoSourceKind.Faceit)
                .Because("the engine reads FACEIT server names as Unknown");
            await Assert.That(TeamSourcePolicy.EffectiveKind("Unknown", "FACEIT.com")).IsEqualTo(DemoSourceKind.Faceit);
            await Assert.That(TeamSourcePolicy.EffectiveKind(null, "ESL Match Server #1")).IsEqualTo(DemoSourceKind.Unknown);

            await Assert.That(TeamSourcePolicy.AutoTracks("GotvMatchmaking", false, null)).IsFalse();
            await Assert.That(TeamSourcePolicy.AutoTracks("Faceit", false, null)).IsFalse();
            await Assert.That(TeamSourcePolicy.AutoTracks("Unknown", false, null)).IsTrue().Because("scrim and tournament servers are Unknown");
            await Assert.That(TeamSourcePolicy.AutoTracks("HltvPro", false, null)).IsTrue();
            await Assert.That(TeamSourcePolicy.AutoTracks(null, false, null)).IsTrue();
            await Assert.That(TeamSourcePolicy.AutoTracks("Faceit", true, null)).IsTrue().Because("both clan tags: a league on those servers");
            await Assert.That(TeamSourcePolicy.AutoTracks("GotvMatchmaking", false, DemoProvenanceLabel.Scrim)).IsTrue();
            await Assert.That(TeamSourcePolicy.AutoTracks("HltvPro", true, DemoProvenanceLabel.Matchmaking)).IsFalse()
                .Because("the user's pin decides over every signal");
        }
    }

    [Test]
    public async Task TheReplays_AsMatchmaking_FoundNoTeam()
    {
        TeamsFile teams = new();
        TeamIndexFile index = Cluster(teams, Corpus("replays", nameof(DemoSourceKind.GotvMatchmaking)));
        using (Assert.Multiple())
        {
            await Assert.That(teams.Teams).IsEmpty().Because("queue play founds nothing");
            await Assert.That(index.Unaffiliated).IsEmpty().Because("no candidates are kept in queue play");
            await Assert.That(index.Demos.Values.All(d => !d.Tracked)).IsTrue();
            await Assert.That(index.Demos.Values.SelectMany(d => d.Sides.Values).Any(s => s.StandIn)).IsFalse();
        }
    }

    [Test]
    public async Task ThePro_StillClusterAsBefore_AndSuggestNothing()
    {
        TeamsFile teams = new();
        TeamIndexFile index = Cluster(teams, Corpus("pro", null));
        using (Assert.Multiple())
        {
            await Assert.That(teams.Teams.Count).IsEqualTo(3);
            await Assert.That(TeamSuggestions.Compute(teams, index)).IsEmpty()
                .Because("three stable fives with distinct tags: no roster change, no merge, no me");
            foreach (Team team in teams.Teams)
            {
                int sides = index.Demos.Values.SelectMany(d => d.Sides.Values).Count(s => s.TeamId == team.Id);
                List<string> active = [.. TeamSuggestions.ActiveRoster(index, team.Id).Select(p => p.SteamId64).Order(StringComparer.Ordinal)];
                List<string> expected = sides >= TeamSuggestions.ActiveMinAppearances
                    ? [.. team.Rosters.Single().CoreLineup!.Order(StringComparer.Ordinal)]
                    : [];
                await Assert.That(active).IsEquivalentTo(expected)
                    .Because("who plays now is the five once a team has five sides, and nobody before");
            }
        }
    }

    // ── The squad ────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task TheReplays_SuggestTheOwnersSquad_AndTheSquadMatchesWithoutStandIns()
    {
        List<DemoSideInput> inputs = Corpus("replays", nameof(DemoSourceKind.GotvMatchmaking));
        string owner = Owner(inputs);
        TeamsFile teams = new() { Me = new MeAccounts { SteamIds = [owner] } };
        TeamIndexFile index = Cluster(teams, inputs);

        TeamSuggestion squad = TeamSuggestions.Compute(teams, index).Single(s => s.Kind == TeamSuggestionKind.Squad);
        Console.WriteLine($"squad: {string.Join(", ", squad.Players.Select(p => $"{p.SteamId64}:{p.Games}"))} together={squad.GamesTogether} of {squad.MyGames}");
        using (Assert.Multiple())
        {
            await Assert.That(squad.Players[0].SteamId64).IsEqualTo(owner).Because("the squad starts with you");
            await Assert.That(squad.Players.Count).IsEqualTo(3).Because("the user and the two regular partners the design measured");
            await Assert.That(squad.GamesTogether).IsGreaterThanOrEqualTo(TeamSuggestions.SquadMinGames);
        }

        // Accept it the way the service does: an us team with a squad roster, then replay.
        TeamsFile accepted = new() { Me = teams.Me };
        accepted.Teams.Add(new Team
        {
            Id = Guid.NewGuid(),
            Name = "My team",
            NameSource = TeamNameSource.User,
            IsUs = true,
            Rosters = [new Roster { Id = "squad", Since = DateOnly.MinValue, Squad = [.. squad.Players.Select(p => p.SteamId64)] }]
        });
        TeamIndexFile after = Cluster(accepted, inputs);
        List<TeamIndexSide> ours = [.. after.Demos.Values.SelectMany(d => d.Sides.Values).Where(s => s.TeamId is not null)];
        using (Assert.Multiple())
        {
            await Assert.That(accepted.Teams.Count).IsEqualTo(1).Because("still no auto team");
            await Assert.That(ours.Count).IsEqualTo(squad.GamesTogether).Because("a trio squad needs all three on the side");
            await Assert.That(ours.All(s => s.Tier == 3 && !s.StandIn)).IsTrue().Because("fills are not stand-ins");
            await Assert.That(accepted.Teams[0].Rosters[0].HasCoreLineup).IsFalse().Because("a squad never becomes a fixed five");
            await Assert.That(TeamSuggestions.Compute(accepted, after).Any(s => s.Kind == TeamSuggestionKind.Squad)).IsFalse();
        }
    }

    [Test]
    public async Task ASquadOfFive_MatchesOnThree_AndATrioNeedsAllThree()
    {
        TeamsFile teams = new();
        teams.Teams.Add(new Team
        {
            Id = Guid.NewGuid(), Name = "Five", NameSource = TeamNameSource.User,
            Rosters = [new Roster { Id = "s", Squad = [.. Ids(1, 2, 3, 4, 5)] }]
        });
        teams.Teams.Add(new Team
        {
            Id = Guid.NewGuid(), Name = "Trio", NameSource = TeamNameSource.User, IsUs = true,
            Rosters = [new Roster { Id = "s", Squad = [.. Ids(11, 12, 13)] }]
        });
        TeamIndexFile index = Cluster(teams,
        [
            Demo("/d/5a.dem", 1, Ids(1, 2, 3, 91, 92), Strangers(), sourceKind: "GotvMatchmaking"),
            Demo("/d/5b.dem", 2, Ids(1, 2, 93, 94, 95), Strangers(), sourceKind: "GotvMatchmaking"),
            Demo("/d/3a.dem", 3, Ids(11, 12, 13, 96, 97), Strangers(), sourceKind: "Faceit"),
            Demo("/d/3b.dem", 4, Ids(11, 12, 96, 97, 98), Strangers(), sourceKind: "Faceit")
        ]);
        Guid five = teams.Teams[0].Id;
        Guid trio = teams.Teams[1].Id;
        using (Assert.Multiple())
        {
            await Assert.That(index.Demos[DemoCacheStore.StableKey("/d/5a.dem")].Side(2)!.TeamId).IsEqualTo(five);
            await Assert.That(index.Demos[DemoCacheStore.StableKey("/d/5b.dem")].Side(2)!.TeamId).IsNull();
            await Assert.That(index.Demos[DemoCacheStore.StableKey("/d/3a.dem")].Side(2)!.TeamId).IsEqualTo(trio);
            await Assert.That(index.Demos[DemoCacheStore.StableKey("/d/3b.dem")].Side(2)!.TeamId).IsNull()
                .Because("two of a trio is not the trio");
            await Assert.That(teams.Teams.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task AnUsTeamWithOnlyARollingCore_TakesNoQueueSide_UntilItHasASquad()
    {
        // The shape of a library clustered before the gate: an us team whose one roster is a rolling core.
        Team us = new()
        {
            Id = Guid.NewGuid(), Name = "My team", NameSource = TeamNameSource.User, IsUs = true,
            Rosters = [new Roster { Id = "r1", ExtendedCore = [.. Ids(1, 2, 3, 41, 42)] }]
        };
        TeamsFile teams = new();
        teams.Teams.Add(us);
        List<DemoSideInput> queue =
        [
            Demo("/d/q1.dem", 1, Ids(1, 2, 3, 43, 44), Strangers(), sourceKind: "GotvMatchmaking"),
            Demo("/d/q2.dem", 2, Ids(1, 2, 3, 45, 46), Strangers(), sourceKind: "GotvMatchmaking")
        ];
        TeamIndexFile before = Cluster(teams, queue);
        us.Rosters.Add(new Roster { Id = "squad", Squad = [.. Ids(1, 2, 3)] });
        TeamIndexFile after = Cluster(teams, queue);
        using (Assert.Multiple())
        {
            await Assert.That(before.Demos.Values.All(d => d.Side(2)!.TeamId is null)).IsTrue()
                .Because("a rolling core would absorb every fill it meets in queue play");
            await Assert.That(after.Demos.Values.All(d => d.Side(2)!.RosterId == "squad")).IsTrue();
            await Assert.That(teams.Find(us.Id)).IsNotNull().Because("a user team survives with no side");
            await Assert.That(TeamSuggestions.Compute(new TeamsFile { Me = new MeAccounts { SteamIds = [Ids(1)[0]] }, Teams = [us] }, before)
                .Any(s => s.Kind == TeamSuggestionKind.Squad)).IsFalse().Because("two games is under the squad bar");
        }
    }

    // ── Suggestions ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AReplacementInFiveOfTheLastTen_SuggestsANewRosterInTheSameTeam()
    {
        List<DemoSideInput> inputs = [];
        for (int day = 1; day <= 6; day++)
        {
            inputs.Add(Demo($"/d/old{day}.dem", day, Ids(1, 2, 3, 4, 5), Strangers()));
        }

        for (int day = 7; day <= 12; day++)
        {
            inputs.Add(Demo($"/d/new{day}.dem", day, Ids(1, 2, 3, 4, 6), Strangers()));
        }

        TeamsFile teams = new();
        TeamIndexFile index = Cluster(teams, inputs);
        TeamSuggestion change = TeamSuggestions.Compute(teams, index).Single();
        using (Assert.Multiple())
        {
            await Assert.That(teams.Teams.Count).IsEqualTo(1).Because("four of the five keep it one team");
            await Assert.That(change.Kind).IsEqualTo(TeamSuggestionKind.RosterChange);
            await Assert.That(change.Players.Select(p => p.SteamId64)).IsEquivalentTo(Ids(6));
            await Assert.That(change.Left.Select(p => p.SteamId64)).IsEquivalentTo(Ids(5));
            await Assert.That(change.Since).IsEqualTo(new DateOnly(2026, 1, 8)).Because("the newcomer's first side inside the window");
            await Assert.That(TeamSuggestions.ActiveRoster(index, teams.Teams[0].Id).Select(p => p.SteamId64).Order(StringComparer.Ordinal))
                .IsEquivalentTo(Ids(1, 2, 3, 4, 6));
        }

        // Dismissed, it stays gone for the same proposal.
        teams.DismissedSuggestions.Add(change.Id);
        await Assert.That(TeamSuggestions.Compute(teams, index)).IsEmpty();
    }

    [Test]
    public async Task AShortStandIn_UnderFiveOfTen_SuggestsNothing()
    {
        List<DemoSideInput> inputs = [];
        for (int day = 1; day <= 8; day++)
        {
            inputs.Add(Demo($"/d/s{day}.dem", day, Ids(1, 2, 3, 4, 5), Strangers()));
        }

        for (int day = 9; day <= 12; day++)
        {
            inputs.Add(Demo($"/d/st{day}.dem", day, Ids(1, 2, 3, 4, 7), Strangers()));
        }

        TeamsFile teams = new();
        TeamIndexFile index = Cluster(teams, inputs);
        await Assert.That(TeamSuggestions.Compute(teams, index)).IsEmpty().Because("four of ten is a stand-in, not a roster change");
    }

    [Test]
    public async Task TwoTeamsWithOneTag_SuggestAMerge_IntoTheOlder()
    {
        TeamsFile teams = new();
        TeamIndexFile index = Cluster(teams,
        [
            Demo("/d/a1.dem", 1, Ids(1, 2, 3, 4, 5), Strangers(), tClan: "FURIA"),
            Demo("/d/a2.dem", 2, Ids(1, 2, 3, 4, 5), Strangers(), tClan: "FURIA"),
            Demo("/d/b1.dem", 30, Ids(21, 22, 23, 24, 25), Strangers(), tClan: "Furia"),
            Demo("/d/b2.dem", 31, Ids(21, 22, 23, 24, 25), Strangers(), tClan: "Furia")
        ]);
        TeamSuggestion merge = TeamSuggestions.Compute(teams, index).Single();
        Team older = teams.Teams.OrderBy(t => t.Rosters[0].Since).First();
        using (Assert.Multiple())
        {
            await Assert.That(merge.Kind).IsEqualTo(TeamSuggestionKind.MergeByTag);
            await Assert.That(merge.TeamId).IsEqualTo(older.Id);
            await Assert.That(merge.OtherTeamId).IsNotEqualTo(older.Id);
        }
    }

    // ── Referenced teams ─────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AnAutoTeamAStoreReferences_SurvivesARebuildThatGivesItNoSide()
    {
        TeamsFile teams = new();
        Cluster(teams,
        [
            Demo("/d/r1.dem", 1, Ids(1, 2, 3, 4, 5), Strangers()),
            Demo("/d/r2.dem", 2, Ids(1, 2, 3, 4, 5), Strangers())
        ]);
        Guid id = teams.Teams.Single().Id;

        // The same demos, now queue play: the auto team gets no side.
        TeamsFile kept = JsonSerializer.Deserialize<TeamsFile>(JsonSerializer.Serialize(teams, TeamsFile.JsonOptions), TeamsFile.JsonOptions)!;
        TeamsFile dropped = JsonSerializer.Deserialize<TeamsFile>(JsonSerializer.Serialize(teams, TeamsFile.JsonOptions), TeamsFile.JsonOptions)!;
        List<DemoSideInput> queue =
        [
            Demo("/d/r1.dem", 1, Ids(1, 2, 3, 4, 5), Strangers(), sourceKind: "GotvMatchmaking"),
            Demo("/d/r2.dem", 2, Ids(1, 2, 3, 4, 5), Strangers(), sourceKind: "GotvMatchmaking")
        ];
        Cluster(kept, queue, new HashSet<Guid> { id });
        Cluster(dropped, queue);
        using (Assert.Multiple())
        {
            await Assert.That(kept.Find(id)).IsNotNull().Because("a strat book owns it");
            await Assert.That(dropped.Find(id)).IsNull().Because("unreferenced auto teams still go");
        }
    }

    // ── The service ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AProvenancePin_ReclustersThroughTheService()
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService service = new(null, cache.Library(), run: a =>
        {
            a();
            return Task.CompletedTask;
        });
        await service.StartAsync();
        using (cache.BeginBatch())
        {
            foreach ((string path, int day) in new[] { ("/d/q1.dem", 1), ("/d/q2.dem", 2) })
            {
                DemoCacheRecord record = new()
                {
                    Path = path, Size = 1, ModifiedTicks = new DateTime(2026, 2, day, 0, 0, 0, DateTimeKind.Utc).Ticks,
                    Map = "de_nuke", SourceKind = nameof(DemoSourceKind.GotvMatchmaking)
                };
                int slot = 0;
                foreach (string id in Ids(1, 2, 3, 4, 5))
                {
                    record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = id, SteamId64 = id, Team = 2 });
                }

                foreach (string id in Strangers())
                {
                    record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = id, SteamId64 = id, Team = 3 });
                }

                DemoCacheStore.StampParse(record);
                cache.Upsert(record);
            }
        }

        await service.Idle;
        await Assert.That(service.AllTeams).IsEmpty().Because("two matchmaking demos found nothing");

        service.SetProvenanceOverride("/d/q1.dem", DemoProvenanceLabel.Scrim);
        service.SetProvenanceOverride("/d/q2.dem", DemoProvenanceLabel.Scrim);
        await Assert.That(service.AllTeams.Count).IsEqualTo(1).Because("pinned as scrims, the five is a team");

        service.SetSquad(Ids(1, 2, 3));
        using (Assert.Multiple())
        {
            await Assert.That(service.Us).IsNotNull();
            await Assert.That(service.Us!.Rosters.Single(r => r.IsSquad).Squad!).IsEquivalentTo(Ids(1, 2, 3));
        }
    }
}
