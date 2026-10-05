#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Teams;
using DemoViewer.NET.Views.Teams;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The revised Teams view over a library shaped like a real one: matchmaking with a regular trio and
///     rotating fills, and a scrim team whose fifth changed. Renders the suggestions inbox, then accepts
///     the squad through the view model and renders the my-team card. The PNGs are the review surface.
/// </summary>
[Category("Integration")]
public class TeamsViewRevisionRenderTests
{
    private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    private static int _stranger = 8000;

    private static string[] Strangers() => Ids(Interlocked.Add(ref _stranger, 5) - 4, _stranger - 3, _stranger - 2, _stranger - 1, _stranger);

    private static DemoCacheRecord Record(string path, int day, string[] t, string[] ct, string sourceKind, string? tClan = null)
    {
        DemoCacheRecord record = new()
        {
            Path = path, Size = 1, ModifiedTicks = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
            Map = "de_mirage", SourceKind = sourceKind, TClan = tClan
        };
        string[] names = ["sid", "kappa", "rook", "vex", "mono", "tilt", "zed", "ash", "juno", "pike"];
        int slot = 0;
        foreach (string id in t)
        {
            int n = int.Parse(id[^4..], System.Globalization.CultureInfo.InvariantCulture);
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = n < names.Length ? names[n] : $"fill{n}", SteamId64 = id, Team = 2 });
        }

        foreach (string id in ct)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"opp{id[^3..]}", SteamId64 = id, Team = 3 });
        }

        DemoCacheStore.StampParse(record);
        return record;
    }

    [Test]
    public async Task TheTeamsView_ShowsTheInbox_ThenTheSquad()
    {
        int inboxInk = 0, squadInk = 0;
        List<string> suggestions = [];
        string myTeam = "";

        await HeadlessSession.RunOnUi(async () =>
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
                // Matchmaking: sid, kappa and rook together with rotating fills; sid solo in between.
                for (int i = 0; i < 12; i++)
                {
                    cache.Upsert(Record($"/mm/trio{i}.dem", day++, [.. Ids(0, 1, 2), .. Ids(20 + (i % 3), 40 + i)], Strangers(), nameof(DemoSourceKind.GotvMatchmaking)));
                }

                for (int i = 0; i < 6; i++)
                {
                    cache.Upsert(Record($"/mm/solo{i}.dem", day++, [.. Ids(0), .. Ids(60 + i, 61 + i, 62 + i, 63 + i)], Strangers(), nameof(DemoSourceKind.GotvMatchmaking)));
                }

                // A scrim team: vex, mono, tilt, zed, ash, then pike replaces ash.
                for (int i = 0; i < 6; i++)
                {
                    cache.Upsert(Record($"/scrim/a{i}.dem", day++, Ids(3, 4, 5, 6, 7), Strangers(), "Unknown", "Northside"));
                }

                for (int i = 0; i < 6; i++)
                {
                    cache.Upsert(Record($"/scrim/b{i}.dem", day++, Ids(3, 4, 5, 6, 9), Strangers(), "Unknown", "Northside"));
                }
            }

            await service.Idle;
            service.SetMyAccounts(Ids(0));

            using TeamsTabViewModel vm = new(service, cache.Library(), isBrowser: false);
            vm.SelectedTeam = vm.Teams.FirstOrDefault();
            TeamsTabView view = new() { DataContext = vm };
            Window window = new() { Width = 1280, Height = 900, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            suggestions = [.. vm.Suggestions.Select(s => s.Text)];
            if (window.CaptureRenderedFrame() is { } inbox)
            {
                inbox.Save(Path.Combine(HeadlessSession.ArtifactDir, "teams-inbox.png"), new PngBitmapEncoderOptions());
                inboxInk = NonBackground(inbox);
            }

            vm.AcceptSuggestionCommand.Execute(vm.Suggestions.First(s => s.Id.StartsWith("squad:", StringComparison.Ordinal)));
            vm.SelectedTeam = vm.Teams.FirstOrDefault(t => t.IsUs);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            myTeam = vm.MyTeamLine + " | " + vm.FillsLine;
            if (window.CaptureRenderedFrame() is { } squad)
            {
                squad.Save(Path.Combine(HeadlessSession.ArtifactDir, "teams-squad.png"), new PngBitmapEncoderOptions());
                squadInk = NonBackground(squad);
            }

            window.Close();
        });

        Console.WriteLine($"[teams] suggestions: {string.Join(" || ", suggestions)}");
        Console.WriteLine($"[teams] my team: {myTeam}");
        using (Assert.Multiple())
        {
            await Assert.That(suggestions.Count).IsEqualTo(2).Because("the squad and the scrim team's roster change");
            await Assert.That(suggestions.Any(s => s.Contains("kappa, rook", StringComparison.Ordinal))).IsTrue();
            await Assert.That(suggestions.Any(s => s.Contains("pike", StringComparison.Ordinal) && s.Contains("ash", StringComparison.Ordinal))).IsTrue();
            await Assert.That(myTeam).Contains("squad: sid, kappa, rook");
            await Assert.That(myTeam).Contains("12 games");
            await Assert.That(myTeam).Contains("regular fills: fill20 (4), fill21 (4), fill22 (4)");
            await Assert.That(inboxInk).IsGreaterThan(500);
            await Assert.That(squadInk).IsGreaterThan(500);
        }
    }

    private static int NonBackground(WriteableBitmap bmp)
    {
        PixelSize size = bmp.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            System.Runtime.InteropServices.Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
        }

        int count = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            if (buffer[i] > 40 || buffer[i + 1] > 40 || buffer[i + 2] > 40)
            {
                count++;
            }
        }

        return count;
    }
}
