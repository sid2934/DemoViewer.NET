#region

using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Teams;
using static DemoViewer.NET.AppTests.SuggestedTagsReviewHarness;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The one inbox rule (generated-content.md) in each place generated items appear: only new items show and
///     count, Dismiss and Restore, and one "Show settled (n)" toggle for the rest.
/// </summary>
[NotInParallel]
public class GeneratedInboxTests
{
    internal static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    private static int _stranger = 9000;

    private static string[] Strangers() => Ids(Interlocked.Add(ref _stranger, 5) - 4, _stranger - 3, _stranger - 2, _stranger - 1, _stranger);

    private static DemoCacheRecord Record(string path, int day, string[] t, string sourceKind, string? tClan = null)
    {
        DemoCacheRecord record = new()
        {
            Path = path, Size = 1, ModifiedTicks = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
            Map = "de_mirage", SourceKind = sourceKind, TClan = tClan
        };
        int slot = 0;
        foreach (string id in t)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^3..]}", SteamId64 = id, Team = 2 });
        }

        foreach (string id in Strangers())
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"opp{id[^3..]}", SteamId64 = id, Team = 3 });
        }

        DemoCacheStore.StampParse(record);
        return record;
    }

    /// <summary>A trio in matchmaking with you (id 0), and a scrim team whose fifth changed from 7 to 9.</summary>
    internal static async Task<(TeamIdentityService Service, DemoCacheStore Cache)> Teams(bool setMe = true)
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService service = new(null, cache, run: a =>
        {
            a();
            return Task.CompletedTask;
        });
        await service.StartAsync();
        using (cache.BeginBatch())
        {
            int day = 0;
            for (int i = 0; i < 24; i++)
            {
                cache.Upsert(Record($"/mm/trio{i}.dem", day++, [.. Ids(0, 1, 2), .. Ids(20 + (i % 3), 40 + i)], "GotvMatchmaking"));
            }

            for (int i = 0; i < 6; i++)
            {
                cache.Upsert(Record($"/scrim/a{i}.dem", day++, Ids(3, 4, 5, 6, 7), "Unknown", "Northside"));
            }

            for (int i = 0; i < 6; i++)
            {
                cache.Upsert(Record($"/scrim/b{i}.dem", day++, Ids(3, 4, 5, 6, 9), "Unknown", "Northside"));
            }
        }

        await service.Idle;
        if (setMe)
        {
            service.SetMyAccounts(Ids(0));
        }

        return (service, cache);
    }

    [Test]
    public async Task TheSuggestedQueue_ShowsNewOnly_AndSettledBehindTheToggle()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        SuggestionQueueViewModel queue = new(h.Service, new ProposalTrack(), _ => { }, () => 64, static a => a());
        queue.Attach(DemoPath, Sha);
        int all = queue.Rows.Count;

        await Assert.That(h.Service.Accept(DemoPath, ExecuteId)).IsTrue();
        await Assert.That(h.Service.Reject(DemoPath, DefaultId)).IsTrue();
        queue.Reload();
        using (Assert.Multiple())
        {
            await Assert.That(queue.Rows.Count).IsEqualTo(all - 2);
            await Assert.That(queue.Rows.All(r => r.IsNew)).IsTrue();
            await Assert.That(queue.SettledLabel).IsEqualTo("Show settled (2)");
        }

        queue.ShowSettled = true;
        using (Assert.Multiple())
        {
            await Assert.That(queue.Rows.Single(r => r.Proposal.Id == ExecuteId).StateText).IsEqualTo("accepted");
            await Assert.That(queue.Rows.Single(r => r.Proposal.Id == DefaultId).StateText).IsEqualTo("dismissed");
        }

        queue.Selected = queue.Rows.Single(r => r.Proposal.Id == ExecuteId);
        await Assert.That(queue.Execute(Modules.Playback2D.Playback2DAction.SuggestionAccept)).IsTrue();
        await Assert.That(h.Tags.TryLoad(Sha)!.Instances.Count).IsEqualTo(1).Because("a settled row takes no second verdict");
        await Assert.That(queue.StatusText).IsEqualTo("This suggestion is already accepted.");
    }

    [Test]
    public async Task TheTeamsInbox_ListsADismissal_UnderSettled_WithRestore()
    {
        (TeamIdentityService service, DemoCacheStore cache) = await Teams();
        using TeamsTabViewModel vm = new(service, cache, isBrowser: false);
        SuggestionRow roster = vm.Suggestions.First(s => s.Id.StartsWith("roster:", StringComparison.Ordinal));

        vm.DismissSuggestionCommand.Execute(roster);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Suggestions.Select(s => s.Id)).DoesNotContain(roster.Id);
            await Assert.That(vm.SettledLabel).IsEqualTo("Show settled (1)");
        }

        vm.ShowSettled = true;
        SuggestionRow settled = vm.Suggestions.Single(s => s.Id == roster.Id);
        await Assert.That(settled.IsDismissed).IsTrue();
        vm.RestoreSuggestionCommand.Execute(settled);
        vm.ShowSettled = false;
        await Assert.That(vm.Suggestions.Single(s => s.Id == roster.Id).IsDismissed).IsFalse();
        await Assert.That(vm.SettledLabel).IsEqualTo("Show settled (0)");
    }
}
