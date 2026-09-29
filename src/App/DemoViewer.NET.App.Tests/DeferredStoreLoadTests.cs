#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Stores whose startup read is a queue item: nothing is written over a file that was not read, a
///     caller that needs the data first reads it itself.
/// </summary>
[NotInParallel]
public class DeferredStoreLoadTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    private static DemoCacheRecord Record(string path, int day, string[] t, string[] ct)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
            Map = "de_nuke"
        };
        int slot = 0;
        foreach (string id in t)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = "p" + id[^2..], SteamId64 = id, Team = 2 });
        }

        foreach (string id in ct)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = "p" + id[^2..], SteamId64 = id, Team = 3 });
        }

        DemoCacheStore.StampParse(record);
        DemoCacheStore.StampAnalysis(record);
        return record;
    }

    [Test]
    public async Task AChangeBeforeTheQueuedTeamsReadRuns_ReadsTheFileFirst_SoNoTeamIsLost()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-teams-deferred-{Guid.NewGuid():N}");
        try
        {
            DemoCacheStore cache = new(Path.Combine(root, "cache"));
            TeamIdentityService first = new(root, cache, run: _inline);
            await first.StartAsync();
            using (cache.BeginBatch())
            {
                cache.Upsert(Record("/d/1.dem", 1, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15)));
                cache.Upsert(Record("/d/2.dem", 2, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15)));
            }

            await first.Idle;
            List<Guid> ids = [.. first.Teams.Select(t => t.Id)];
            first.Dispose();

            Action? queuedRead = null;
            TeamIdentityService second = new(root, cache, run: _inline, scheduleLoad: read =>
            {
                queuedRead = read; // the queue has not got to it yet
                return Task.CompletedTask;
            });
            bool loadedAtStart = second.IsLoaded;
            int teamsAtStart = second.Teams.Count;
            second.Rename(ids[0], "Renamed early");
            queuedRead!(); // the queue item runs later and finds the read done

            TeamIdentityService third = new(root, cache, run: _inline);
            using (Assert.Multiple())
            {
                await Assert.That(ids.Count).IsGreaterThanOrEqualTo(2);
                await Assert.That(loadedAtStart).IsFalse();
                await Assert.That(teamsAtStart).IsEqualTo(0).Because("until the read, the tabs show it is loading");
                await Assert.That(second.IsLoaded).IsTrue();
                await Assert.That(third.Teams.Select(t => t.Id)).IsEquivalentTo(ids).Because("the early change read the file before writing it");
                await Assert.That(third.Teams.Single(t => t.Id == ids[0]).Name).IsEqualTo("Renamed early");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task TheReviewQueue_ReadsItsFileOnFirstUse_WhenTheQueuedReadHasNotRun()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-review-deferred-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            ReviewQueue writer = new(root, TimeSpan.Zero);
            writer.Add([ReviewEntry.Clip("/d/a.dem", 1, 2, "note", ReviewSources.Manual, 64)], "Section", "");
            writer.Flush();

            ReviewQueue reader = new(root, scheduleLoad: _ => Task.CompletedTask); // never runs
            bool loadedBefore = reader.Loaded.IsCompleted;
            int entries = reader.Entries.Count;
            using (Assert.Multiple())
            {
                await Assert.That(loadedBefore).IsFalse();
                await Assert.That(entries).IsEqualTo(2).Because("the title card and the clip, read on first use");
                await Assert.That(reader.Loaded.IsCompleted).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
