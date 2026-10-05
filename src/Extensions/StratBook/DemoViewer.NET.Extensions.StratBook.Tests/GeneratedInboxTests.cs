#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.SuggestedTags;
using DemoViewer.NET.ViewModels.Teams;
using DemoViewer.NET.Views.SuggestedTags;
using static DemoViewer.NET.AppTests.SuggestedTagsReviewHarness;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The one inbox rule in each place generated items appear: only new items show and
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
        TeamIdentityService service = new(null, cache.Library(), run: a =>
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

    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    [Test]
    public async Task TheSuggestedSection_ListsTheLibrary_Filters_AndAcceptsDismissesAndRestores()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        using SuggestedInboxService inbox = new(h.Service, h.Cache.Library(), run: _inline);
        using SuggestedInboxViewModel vm = new(inbox);
        await Assert.That(inbox.IsLoaded).IsFalse().Because("nothing is read until the section is first shown");
        vm.OnActivated(null!);
        await inbox.LoadAsync();

        int pending = h.Service.Load(DemoPath).Pending.Count;
        using (Assert.Multiple())
        {
            await Assert.That(vm.Rows.Count).IsEqualTo(pending);
            await Assert.That(inbox.PendingCount).IsEqualTo(pending).Because("the badge reads the index");
            await Assert.That(vm.Maps).Contains(SuggestedTagsTestData.Map);
            await Assert.That(vm.StatusLine).StartsWith($"{pending} new suggestion");
        }

        vm.DetectorFilter = "execute";
        await Assert.That(vm.Rows.All(r => r.DetectorText == "execute")).IsTrue();
        vm.DetectorFilter = SuggestedInboxViewModel.AllDetectors;

        SuggestedInboxRow execute = vm.Rows.Single(r => r.Item.Entry.Proposal.Id == ExecuteId);
        vm.AcceptCommand.Execute(execute);
        SuggestedInboxRow opener = vm.Rows.Single(r => r.Item.Entry.Proposal.Id == DefaultId);
        vm.DismissCommand.Execute(opener);
        using (Assert.Multiple())
        {
            await Assert.That(h.Tags.TryLoad(Sha)!.Instances.Single().Code).IsEqualTo(execute.Item.Entry.Proposal.Code)
                .Because("Accept writes into that demo's tag document");
            await Assert.That(vm.Rows.Count).IsEqualTo(pending - 2).Because("settled suggestions leave the default view");
            await Assert.That(vm.SettledLabel).IsEqualTo("Show settled (2)");
        }

        vm.ShowSettled = true;
        vm.RestoreCommand.Execute(vm.Rows.Single(r => r.Item.Entry.Proposal.Id == DefaultId));
        vm.ShowSettled = false;
        await Assert.That(vm.Rows.Any(r => r.Item.Entry.Proposal.Id == DefaultId)).IsTrue();
    }

    [Test]
    [Category("Integration")]
    public async Task TheSuggestedSection_Renders() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using SuggestedTagsReviewHarness h = new();
            h.Build();
            using SuggestedInboxService inbox = new(h.Service, h.Cache.Library(), run: _inline);
            using SuggestedInboxViewModel vm = new(inbox);
            await inbox.LoadAsync();
            h.Service.Reject(DemoPath, DefaultId);
            vm.ShowSettled = true;
            SuggestedInboxView view = new() { DataContext = vm };
            Window window = new() { Width = 1100, Height = 700, Content = view };
            window.Show();
            Playback2DTimelineHarness.Pump();
            window.CaptureRenderedFrame()?.Save(Path.Combine(HeadlessSession.ArtifactDir, "suggested-section.png"), new PngBitmapEncoderOptions());
            ItemsControl list = view.FindControl<ItemsControl>("RowsList")!;
            await Assert.That(list.IsEffectivelyVisible).IsTrue();
            window.Close();
        });

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
    public async Task ARejectedSuggestion_Restored_IsPendingAgain_CountsAsPending_AndCanBeAccepted()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        SuggestionQueueViewModel queue = new(h.Service, new ProposalTrack(), _ => { }, () => 64, static a => a());
        queue.Attach(DemoPath, Sha);
        await Assert.That(h.Service.Reject(DemoPath, ExecuteId)).IsTrue();
        queue.Reload();

        queue.ShowSettled = true;
        queue.RestoreRowCommand.Execute(queue.Rows.Single(r => r.Proposal.Id == ExecuteId));
        using (Assert.Multiple())
        {
            await Assert.That(h.Service.Load(DemoPath).Pending.Select(e => e.Proposal.Id)).Contains(ExecuteId);
            await Assert.That(queue.Rows.Single(r => r.Proposal.Id == ExecuteId).IsNew).IsTrue();
            await Assert.That(h.Tags.LoadVerdicts(Sha)!.Verdicts[ExecuteId].Verdict).IsEqualTo("restored");
            VerdictCounts counts = SuggestedTagsTuning.Aggregate(h.Service.Load(DemoPath).Entries)["execute"];
            await Assert.That(counts.Rejected).IsEqualTo(0).Because("tuning treats a restored rejection as not rejected");
            await Assert.That(counts.Pending).IsGreaterThanOrEqualTo(1);
        }

        await Assert.That(h.Service.Accept(DemoPath, ExecuteId)).IsTrue().Because("the next verdict replaces a restored one");
        await Assert.That(h.Tags.LoadVerdicts(Sha)!.Verdicts[ExecuteId].Verdict).IsEqualTo("accepted");
        await Assert.That(h.Service.Restore(DemoPath, ExecuteId)).IsFalse().Because("only a dismissal can be restored");
    }

    [Test]
    [Arguments("squad:ME,1,2,3", "squad:ME,1,2,4", true)]
    [Arguments("squad:ME,1,2,3", "squad:ME,1,2,3,4", true)]
    [Arguments("squad:ME,1,2,3", "squad:ME,1,4,5", false)]
    [Arguments("squad:ME,1", "squad:ME,2", false)]
    [Arguments("roster:T:R:6", "roster:T:R:6,8", true)]
    [Arguments("roster:T:R:6", "roster:T:R:8", false)]
    [Arguments("roster:T:R:6", "roster:T:Q:6", false)]
    [Arguments("merge:A:B", "merge:A:C", false)]
    public async Task ADismissal_HoldsASuggestion_WhoseSteamIdsDifferByOne(string dismissed, string offered, bool held)
    {
        TeamsFile teams = new() { Me = new MeAccounts { SteamIds = ["ME"] }, DismissedSuggestions = [dismissed] };
        TeamSuggestion suggestion = new() { Id = offered, Kind = TeamSuggestionKind.Squad };
        await Assert.That(TeamSuggestions.IsDismissed(suggestion, teams)).IsEqualTo(held);
    }

    [Test]
    public async Task ASquadDismissal_FollowsTheSquad_WhenOnePlayerChanges()
    {
        (TeamIdentityService service, _) = await Teams();
        TeamSuggestion squad = service.Suggestions.Single(s => s.Kind == TeamSuggestionKind.Squad);
        string drifted = squad.Id + "," + Ids(99)[0];

        service.DismissSuggestion(drifted);
        using (Assert.Multiple())
        {
            await Assert.That(service.Suggestions.Any(s => s.Kind == TeamSuggestionKind.Squad)).IsFalse()
                .Because("a dismissal of the squad with one more player still holds");
            await Assert.That(service.DismissedSuggestions.Select(s => s.Id)).Contains(squad.Id);
        }

        service.RestoreSuggestion(squad.Id);
        await Assert.That(service.Suggestions.Select(s => s.Id)).Contains(squad.Id)
            .Because("Restore lifts the dismissal holding it");
    }

    [Test]
    public async Task DriftDoesNotCompound_EachHopIsJudgedAgainstTheOriginalDismissal()
    {
        TeamsFile teams = new() { Me = new MeAccounts { SteamIds = ["ME"] }, DismissedSuggestions = ["squad:ME,A,B"] };
        TeamSuggestion Offer(string id) => new() { Id = id, Kind = TeamSuggestionKind.Squad };

        using (Assert.Multiple())
        {
            await Assert.That(TeamSuggestions.IsDismissed(Offer("squad:ME,A,C"), teams)).IsTrue().Because("one hop: A stays");
            await Assert.That(TeamSuggestions.IsDismissed(Offer("squad:ME,C,D"), teams)).IsFalse()
                .Because("two hops share no player with the squad that was dismissed");
        }

        (TeamIdentityService service, _) = await Teams();
        TeamSuggestion squad = service.Suggestions.Single(s => s.Kind == TeamSuggestionKind.Squad);
        service.DismissSuggestion(squad.Id + "," + Ids(99)[0]);
        await Assert.That(service.DismissedSuggestions.Select(s => s.Id)).Contains(squad.Id);
        service.RestoreSuggestion(squad.Id);
        await Assert.That(service.DismissedSuggestions).IsEmpty()
            .Because("only the dismissed set was stored; nothing was carried as a new anchor");
    }

    [Test]
    public async Task IsThisYou_CanBeDismissed_AndRestoredFromSettled()
    {
        (TeamIdentityService service, DemoCacheStore cache) = await Teams(setMe: false);
        await Assert.That(service.MeSuggestion?.SteamId64).IsEqualTo(Ids(0)[0]);
        using TeamsTabViewModel vm = new(service, cache.Library(), isBrowser: false);

        vm.DismissMeSuggestionCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.HasMeSuggestion).IsFalse();
            await Assert.That(service.DismissedMeSuggestion?.SteamId64).IsEqualTo(Ids(0)[0]);
            await Assert.That(vm.SettledLabel).IsEqualTo("Show settled (1)");
        }

        vm.ShowSettled = true;
        SuggestionRow me = vm.Suggestions.Single(s => s.Id.StartsWith("me:", StringComparison.Ordinal));
        vm.RestoreSuggestionCommand.Execute(me);
        await Assert.That(vm.HasMeSuggestion).IsTrue();
        await Assert.That(service.DismissedMeSuggestion).IsNull();
    }

    [Test]
    public async Task TheTeamsInbox_ListsADismissal_UnderSettled_WithRestore()
    {
        (TeamIdentityService service, DemoCacheStore cache) = await Teams();
        using TeamsTabViewModel vm = new(service, cache.Library(), isBrowser: false);
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
