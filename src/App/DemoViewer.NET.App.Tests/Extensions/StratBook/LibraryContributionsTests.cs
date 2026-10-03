#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Provenance;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.AppTests.Extensions.StratBook;

/// <summary>
///     The pack's two Library contributions (item 22): <see cref="TeamLibraryContribution" /> (the Team
///     filter) and <see cref="ProvenanceLibraryContribution" /> (the provenance badge). The generic hosting
///     mechanism itself (gate-off means unqueried, Changed re-applies, reset on off) is
///     <see cref="DemoViewer.NET.AppTests.LibraryContributionTests" />'s, over a fake; this class is the
///     two real contributions' own item lists, "Us" semantics, labels and override round trip.
/// </summary>
[NotInParallel]
public class LibraryContributionsTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    // sourceKind defaults to null, matching TeamsModuleTests.Record: a matchmaking source kind makes
    // TeamIdentityService treat the opposing roster as non-recurring by design, which would leave the
    // Team filter test with no team to list.
    private static DemoCacheRecord Record(string path, int day, string[] t, string[] ct, string map = "de_nuke", string? sourceKind = null)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
            Map = map,
            SourceKind = sourceKind
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

    private static DemoEntry Entry(string path, string map = "de_nuke") => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        Directory = "/d",
        FileSizeBytes = 1000,
        Modified = new DateTime(2026, 3, 1),
        MapName = map,
        State = DemoIndexState.Indexed
    };

    // Two rosters that met twice (clusters into one team each), and a demo nobody recurs on.
    private static async Task<(DemoCacheStore Cache, TeamIdentityService Teams)> Library()
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache, run: _inline);
        await teams.StartAsync();
        using (cache.BeginBatch())
        {
            cache.Upsert(Record("/d/a.dem", 1, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15)));
            cache.Upsert(Record("/d/b.dem", 2, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_mirage"));
            cache.Upsert(Record("/d/c.dem", 3, Ids(21, 22, 23, 24, 25), Ids(31, 32, 33, 34, 35)));
        }

        await teams.Idle;
        return (cache, teams);
    }

    [Test]
    public async Task TeamContribution_NothingIsResolved_UntilFilterIsRead()
    {
        bool resolved = false;
        TeamLibraryContribution contribution = new(() =>
        {
            resolved = true;
            throw new InvalidOperationException("must not resolve before Filter is read");
        });

        await Assert.That(resolved).IsFalse().Because("constructing the contribution must not resolve TeamIdentityService");
    }

    [Test]
    public async Task TeamContribution_Filter_ListsAllUsAndEveryTeam_AndMatchesByKey()
    {
        (DemoCacheStore cache, TeamIdentityService teams) = await Library();
        using (teams)
        {
            TeamLibraryContribution contribution = new(() => teams);
            LibraryFilter? filter = contribution.Filter;

            await Assert.That(filter).IsNotNull();
            using (Assert.Multiple())
            {
                await Assert.That(filter!.Items.Count).IsEqualTo(4).Because("All, Us, and two teams");
                await Assert.That(filter.Items[0]).IsEqualTo(new LibraryFilterItem("", "All teams"));
                await Assert.That(filter.Items[1]).IsEqualTo(new LibraryFilterItem("us", "Us"));
                await Assert.That(contribution.BadgeFor(Entry("/d/a.dem"))).IsNull().Because("the Team filter offers no badge");
                await Assert.That(contribution.BadgeLabels).IsEmpty();
            }

            DemoEntry a = Entry("/d/a.dem");
            DemoEntry c = Entry("/d/c.dem");
            Guid teamId = teams.Teams[0].Id;
            string teamKey = teamId.ToString();

            using (Assert.Multiple())
            {
                await Assert.That(filter.Matches(a, teamKey)).IsTrue().Because("a's roster is the team");
                await Assert.That(filter.Matches(c, teamKey)).IsFalse().Because("c's roster never recurs");
                await Assert.That(filter.Matches(a, "us")).IsFalse().Because("nothing is us yet");
            }

            teams.SetUs(teamId);
            await Assert.That(contribution.Filter!.Matches(a, "us")).IsTrue()
                .Because("a's side is now the us team, so \"us\" matches it");
        }
    }

    [Test]
    public async Task ProvenanceContribution_NothingIsResolved_UntilBadgeForOrSetLabelIsCalled()
    {
        bool resolved = false;
        ProvenanceLibraryContribution contribution = new(
            () =>
            {
                resolved = true;
                throw new InvalidOperationException("must not resolve before used");
            },
            () =>
            {
                resolved = true;
                throw new InvalidOperationException("must not resolve before used");
            });

        await Assert.That(resolved).IsFalse().Because("constructing the contribution must not resolve either service");
        await Assert.That(contribution.Filter).IsNull().Because("the provenance chip offers no filter");
    }

    [Test]
    public async Task ProvenanceContribution_BadgeLabels_AreTheFourValues_WithAnAutomaticReset()
    {
        (_, TeamIdentityService teams) = await Library();
        using (teams)
        {
            DemoCacheStore cache = new(null);
            ProvenanceLibraryContribution contribution = new(() => new DemoProvenanceSource(cache, teams), () => teams);

            using (Assert.Multiple())
            {
                await Assert.That(contribution.BadgeLabels).IsEquivalentTo(["official", "scrim", "our scrim", "matchmaking"]);
                await Assert.That(contribution.BadgeResetLabel).IsEqualTo("Automatic");
            }
        }
    }

    [Test]
    public async Task ProvenanceContribution_BadgeFor_ReflectsTheHeuristicAndThenAnOverride()
    {
        (DemoCacheStore cache, TeamIdentityService teams) = await Library();
        using (teams)
        {
            // A matchmaking source kind, so the heuristic resolves "matchmaking" below; Library() leaves
            // it unset so the Team contribution's own test keeps its recurring roster clustered.
            using (cache.BeginBatch())
            {
                cache.Upsert(Record("/d/a.dem", 1, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), sourceKind: "GotvMatchmaking"));
            }

            await teams.Idle;
            teams.SetMyAccounts([Ids(1)[0]]);
            DemoProvenanceSource source = new(cache, teams);
            ProvenanceLibraryContribution contribution = new(() => source, () => teams);
            DemoEntry a = Entry("/d/a.dem");

            LibraryBadge? before = contribution.BadgeFor(a);
            using (Assert.Multiple())
            {
                await Assert.That(before).IsNotNull();
                await Assert.That(before!.IsPinned).IsFalse();
                await Assert.That(before.Tooltip).Contains("automatic");
            }

            contribution.SetLabel(a, "our scrim");
            LibraryBadge? pinned = contribution.BadgeFor(a);
            using (Assert.Multiple())
            {
                await Assert.That(pinned!.Label).IsEqualTo("our scrim");
                await Assert.That(pinned.IsPinned).IsTrue();
                await Assert.That(pinned.Tooltip).Contains("set by you");
            }

            contribution.SetLabel(a, null);
            LibraryBadge? automaticAgain = contribution.BadgeFor(a);
            await Assert.That(automaticAgain!.IsPinned).IsFalse().Because("null goes back to the heuristic");

            source.Dispose();
        }
    }
}
