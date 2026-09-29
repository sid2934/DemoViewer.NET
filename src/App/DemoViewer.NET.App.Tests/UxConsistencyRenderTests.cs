#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Teams;
using DemoViewer.NET.Views.Teams;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Renders of the owner-approved visible changes from the UI-thread audit: the Teams tab while its
///     teams are read and while a change is applied. PNGs land in <see cref="HeadlessSession.ArtifactDir" />.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class UxConsistencyRenderTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    internal static DemoCacheRecord Record(string path, int day, string[] t, string[] ct, string map = "de_nuke")
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
            Map = map
        };
        int slot = 0;
        foreach (string id in t)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = "p" + id[^2..], SteamId64 = id, Team = 2 });
        }

        foreach (string id in ct)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = "o" + id[^2..], SteamId64 = id, Team = 3 });
        }

        DemoCacheStore.StampParse(record);
        DemoCacheStore.StampAnalysis(record);
        return record;
    }

    internal static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    internal static void Save(Window window, string name)
    {
        if (window.CaptureRenderedFrame() is { } frame)
        {
            string path = Path.Combine(HeadlessSession.ArtifactDir, name);
            frame.Save(path, new PngBitmapEncoderOptions());
            Console.WriteLine($"[ux-render] {path}");
        }
    }

    internal static bool Shows(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);

    [Test]
    public async Task TheTeamsTab_SaysItIsReading_ThenShowsAChangeInProgress()
    {
        bool reading = false, busy = false, actionsDisabled = false;
        await HeadlessSession.RunOnUi(async () =>
        {
            DemoCacheStore cache = new(null);
            TeamIdentityService seeded = new(null, cache, run: _inline);
            await seeded.StartAsync();
            using (cache.BeginBatch())
            {
                cache.Upsert(Record("/d/1.dem", 1, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15)));
                cache.Upsert(Record("/d/2.dem", 2, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15)));
            }

            await seeded.Idle;

            // The same library before its teams are read.
            Action? read = null;
            TeamIdentityService loading = new(null, new DemoCacheStore(null), run: _inline, scheduleLoad: r =>
            {
                read = r;
                return Task.CompletedTask;
            });
            using TeamsTabViewModel before = new(loading, cache, isBrowser: false);
            Window window = new() { Width = 1280, Height = 800, Content = new TeamsTabView { DataContext = before } };
            window.Show();
            Settle();
            reading = Shows(window, "Reading teams…");
            Save(window, "ux-teams-reading.png");
            window.Close();

            TaskCompletionSource landed = new();
            using TeamsTabViewModel vm = new(seeded, cache, isBrowser: false, command: (_, change) =>
            {
                change();
                return landed.Task;
            });
            vm.SelectedTeam = vm.Teams[0];
            vm.RenameText = "Northside";
            Window second = new() { Width = 1280, Height = 800, Content = new TeamsTabView { DataContext = vm } };
            second.Show();
            vm.RenameCommand.Execute(null);
            Settle();
            busy = Shows(second, "Renaming the team…");
            actionsDisabled = second.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "Rename" && !b.IsEffectivelyEnabled);
            Save(second, "ux-teams-busy.png");
            landed.SetResult();
            second.Close();
            read?.Invoke();
        });

        using (Assert.Multiple())
        {
            await Assert.That(reading).IsTrue();
            await Assert.That(busy).IsTrue();
            await Assert.That(actionsDisabled).IsTrue().Because("a second change waits for the first");
        }
    }
}
