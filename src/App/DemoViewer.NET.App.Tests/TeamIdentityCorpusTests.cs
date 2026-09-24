#region

using System.Text.Json;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The regression pin for the clustering constants (design §3.3, §7): the anonymised side keys of
///     the owner's library, 277 matchmaking replays and 8 HLTV demos, replayed through the clusterer and
///     held to the chosen row of the measurement table. SteamIDs in the fixture are stable fakes.
/// </summary>
public class TeamIdentityCorpusTests
{
    private sealed record FixtureSide(List<string> Key, string? Clan);

    private sealed record FixtureDemo(string Id, string Kind, long OrderTicks, Dictionary<string, FixtureSide> Sides);

    private sealed record Fixture(int SchemaVersion, string Note, List<FixtureDemo> Demos);

    private static readonly JsonSerializerOptions _readOptions = new() { PropertyNameCaseInsensitive = true };

    private static List<DemoSideInput> Load(string kind)
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        string path = Path.Combine(repo, "tests", "fixtures", "team-identity", "side-keys.v1.json");
        Fixture fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path), _readOptions)!;
        return
        [
            .. fixture.Demos.Where(d => d.Kind == kind).Select(d => new DemoSideInput
            {
                StableKey = DemoCacheStore.StableKey(d.Id),
                Path = d.Id,
                OrderTicks = d.OrderTicks,
                T = Side(d.Sides["2"]),
                Ct = Side(d.Sides["3"])
            })
        ];
    }

    private static SideInput Side(FixtureSide side) => new(side.Key, side.Key, side.Clan);

    private static (TeamsFile Teams, TeamIndexFile Index) Cluster(List<DemoSideInput> inputs)
    {
        TeamsFile teams = new();
        TeamClusterer clusterer = new(teams);
        foreach (DemoSideInput input in inputs.OrderBy(i => i.OrderTicks).ThenBy(i => i.Path, StringComparer.Ordinal))
        {
            clusterer.Assign(input);
        }

        return (teams, clusterer.Finish(1));
    }

    // Every (team, roster) with its assigned sides, newest last.
    private static Dictionary<(Guid, string), List<TeamIndexSide>> RosterSides(TeamIndexFile index)
    {
        Dictionary<(Guid, string), List<TeamIndexSide>> sides = [];
        foreach (TeamIndexDemo demo in index.Demos.Values.OrderBy(d => d.OrderTicks).ThenBy(d => d.Path, StringComparer.Ordinal))
        {
            foreach (TeamIndexSide side in demo.Sides.Values)
            {
                if (side.TeamId is { } team)
                {
                    if (!sides.TryGetValue((team, side.RosterId!), out List<TeamIndexSide>? list))
                    {
                        list = [];
                        sides[(team, side.RosterId!)] = list;
                    }

                    list.Add(side);
                }
            }
        }

        return sides;
    }

    [Test]
    public async Task TheReplays_ClusterToTheChosenRow()
    {
        (TeamsFile teams, TeamIndexFile index) = Cluster(Load("replays"));
        Dictionary<(Guid, string), List<TeamIndexSide>> rosters = RosterSides(index);
        List<TeamIndexSide> assigned = [.. rosters.Values.SelectMany(s => s)];
        int matched = assigned.Count - rosters.Count; // one founder per roster is not a match
        ((Guid team, string roster), List<TeamIndexSide> largest) = rosters.MaxBy(r => r.Value.Count);

        Console.WriteLine($"replays: rosters={rosters.Count} fives={teams.Teams.SelectMany(t => t.Rosters).Count(r => r.HasCoreLineup)} " +
                          $"largest={largest.Count}/{index.Members[team.ToString()][roster].Count} unaffiliated={index.Unaffiliated.Count} " +
                          $"matched={matched} tier1={assigned.Count(s => s.Tier == 1)} standIns={assigned.Count(s => s.StandIn)}");

        using (Assert.Multiple())
        {
            await Assert.That(rosters.Count).IsEqualTo(24).Because("candidates with two or more sides");
            await Assert.That(teams.Teams.SelectMany(t => t.Rosters).Count(r => r.HasCoreLineup)).IsEqualTo(5)
                .Because("rosters whose five was seen twice");
            await Assert.That(largest.Count).IsEqualTo(18);
            await Assert.That(index.Members[team.ToString()][roster].Count).IsEqualTo(29);
            await Assert.That(index.Unaffiliated.Count).IsEqualTo(428);
            await Assert.That(matched).IsEqualTo(90);
            await Assert.That(assigned.Count(s => s.Tier == 1)).IsEqualTo(19).Because("19 of 90 matches at tier 1");
            await Assert.That(assigned.Count(s => s.StandIn)).IsEqualTo(82);
            await Assert.That(assigned.Count(s => s.StandIn && s.Tier == 1)).IsEqualTo(17);
            await Assert.That(teams.Teams.Count).IsEqualTo(24).Because("one auto team per auto roster, none tagged");
            await Assert.That(teams.Teams.All(t => t.NameSource == TeamNameSource.Auto && t.Name.StartsWith("Team of ", StringComparison.Ordinal)))
                .IsTrue().Because("matchmaking carries no clan tag");
        }
    }

    [Test]
    public async Task ThePro_ClusterToThreeRostersAtFiveOfFive()
    {
        (TeamsFile teams, TeamIndexFile index) = Cluster(Load("pro"));
        Dictionary<(Guid, string), List<TeamIndexSide>> rosters = RosterSides(index);

        using (Assert.Multiple())
        {
            await Assert.That(rosters.Count).IsEqualTo(3);
            await Assert.That(index.Unaffiliated.Count).IsEqualTo(0);
            await Assert.That(rosters.Values.Sum(s => s.Count) - 3).IsEqualTo(13).Because("13 matches over 16 sides");
            foreach (((Guid team, string roster), List<TeamIndexSide> sides) in rosters)
            {
                Roster persisted = teams.Find(team)!.Rosters.Single(r => r.Id == roster);
                await Assert.That(persisted.HasCoreLineup).IsTrue();
                await Assert.That(sides[1].Key).IsEquivalentTo(sides[0].Key).Because("the five is established at the second sighting");
                await Assert.That(persisted.CoreLineup!).IsEquivalentTo(sides[0].Key);
                await Assert.That(sides.Skip(1).All(s => s.Overlap == 5)).IsTrue();
                await Assert.That(sides.Any(s => s.StandIn)).IsFalse();
                await Assert.That(sides.Skip(2).All(s => s.Tier == 1)).IsTrue().Because("from the third side the fixed five is the anchor");
            }

            List<string> names = [.. teams.Teams.Select(t => t.Name).Order(StringComparer.OrdinalIgnoreCase)];
            await Assert.That(names).IsEquivalentTo(["FURIA", "NaVi", "Team Vitality"]);
            await Assert.That(teams.Teams.All(t => t.NameSource == TeamNameSource.ClanTag)).IsTrue();
        }
    }
}
