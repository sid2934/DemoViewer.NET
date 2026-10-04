#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Provenance;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.AppTests.Extensions.StratBook;

/// <summary>
///     The pack's two Library contributions: <see cref="TeamLibraryContribution" /> (the Team
///     filter) and <see cref="ProvenanceLibraryContribution" /> (the provenance badge). The generic hosting
///     mechanism itself (gate-off means unqueried, Changed re-applies, reset on off) is
///     <c>DemoViewer.NET.AppTests.LibraryContributionTests</c>'s, over a fake; this class is the
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
        (_, TeamIdentityService teams) = await Library();
        using (teams)
        {
            int resolveCalls = 0;
            TeamLibraryContribution contribution = new(() =>
            {
                resolveCalls++;
                return teams;
            });

            await Assert.That(resolveCalls).IsEqualTo(0).Because("constructing the contribution must not resolve TeamIdentityService");

            LibraryFilter? filter = contribution.Filter;

            using (Assert.Multiple())
            {
                await Assert.That(resolveCalls).IsEqualTo(1).Because("reading Filter is the lazy resolve point");
                await Assert.That(filter).IsNotNull();
            }

            // Resolved once and cached: a second read does not resolve again.
            LibraryFilter? second = contribution.Filter;
            await Assert.That(resolveCalls).IsEqualTo(1);
            await Assert.That(second).IsNotNull();
        }
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
                await Assert.That(filter.Tooltip).IsEqualTo("Filter by team");
                await Assert.That(contribution.HasBadge).IsFalse();
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
        (DemoCacheStore cache, TeamIdentityService teams) = await Library();
        using (teams)
        {
            int provenanceResolves = 0;
            int teamsResolves = 0;
            DemoProvenanceSource? source = null;
            ProvenanceLibraryContribution contribution = new(
                () =>
                {
                    provenanceResolves++;
                    return source = new DemoProvenanceSource(cache, teams);
                },
                () =>
                {
                    teamsResolves++;
                    return teams;
                });

            await Assert.That(provenanceResolves).IsEqualTo(0).Because("constructing the contribution must not resolve either service");
            await Assert.That(teamsResolves).IsEqualTo(0);
            await Assert.That(contribution.Filter).IsNull().Because("the provenance chip offers no filter");
            await Assert.That(provenanceResolves).IsEqualTo(0).Because("reading Filter must not resolve anything either");

            DemoEntry a = Entry("/d/a.dem");
            contribution.BadgeFor(a);
            using (Assert.Multiple())
            {
                await Assert.That(provenanceResolves).IsEqualTo(1).Because("BadgeFor is a lazy resolve point");
                await Assert.That(teamsResolves).IsEqualTo(0).Because("BadgeFor never needs the override store");
            }

            contribution.SetLabel(a, "scrim");
            using (Assert.Multiple())
            {
                await Assert.That(teamsResolves).IsEqualTo(1).Because("SetLabel is the other lazy resolve point");
                await Assert.That(provenanceResolves).IsEqualTo(1).Because("resolved once and cached");
            }

            source?.Dispose();
        }
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
                await Assert.That(contribution.HasBadge).IsTrue();
                await Assert.That(contribution.BadgeLabels).IsEquivalentTo(["official", "scrim", "our scrim", "matchmaking"]);
                await Assert.That(contribution.BadgeResetLabel).IsEqualTo("Automatic");
                await Assert.That(contribution.BadgeResetTooltip).IsEqualTo("Let the clan tags, the header and Team Identity decide");
            }
        }
    }

    [Test]
    public async Task ProvenanceContribution_BadgesFor_CallsResolveAllOnce_NotResolvePerEntry()
    {
        (DemoCacheStore cache, TeamIdentityService teams) = await Library();
        using (teams)
        {
            DemoProvenanceSource real = new(cache, teams);
            CountingProvenanceSource counting = new(real);
            ProvenanceLibraryContribution contribution = new(() => counting, () => teams);

            DemoEntry[] entries = [Entry("/d/a.dem"), Entry("/d/b.dem"), Entry("/d/c.dem")];
            IReadOnlyDictionary<string, LibraryBadge?> badges = contribution.BadgesFor(entries);

            using (Assert.Multiple())
            {
                await Assert.That(badges.Count).IsEqualTo(3);
                await Assert.That(counting.ResolveAllCalls).IsEqualTo(1)
                    .Because("a 400-card refresh must be one lock+copy scan of the override list, not 400");
                await Assert.That(counting.ResolveCalls).IsEqualTo(0);
            }

            // A single-entry update still goes through Resolve, not ResolveAll.
            counting.ResolveAllCalls = 0;
            LibraryBadge? single = contribution.BadgeFor(entries[0]);
            using (Assert.Multiple())
            {
                await Assert.That(single).IsNotNull();
                await Assert.That(counting.ResolveCalls).IsEqualTo(1);
                await Assert.That(counting.ResolveAllCalls).IsEqualTo(0);
            }

            real.Dispose();
        }
    }

    // Counts calls so a test can assert BadgesFor costs one ResolveAll, not N Resolve calls.
    private sealed class CountingProvenanceSource(IDemoProvenanceSource inner) : IDemoProvenanceSource
    {
        public int ResolveCalls { get; private set; }
        public int ResolveAllCalls { get; set; }

        public string? LabelFor(string sha256) => inner.LabelFor(sha256);
        public IReadOnlyDictionary<string, string?> LabelsFor(IEnumerable<string> sha256s) => inner.LabelsFor(sha256s);

        public DemoProvenance? Resolve(string demoPath)
        {
            ResolveCalls++;
            return inner.Resolve(demoPath);
        }

        public IReadOnlyDictionary<string, DemoProvenance> ResolveAll(IEnumerable<string> demoPaths)
        {
            ResolveAllCalls++;
            return inner.ResolveAll(demoPaths);
        }

        public event Action? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
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
