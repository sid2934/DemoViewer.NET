#region

using Avalonia.Controls;
using Avalonia.Threading;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Library;
using DemoViewer.NET.Views.Library;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A real <see cref="LibraryTabView" /> bound to a live <see cref="TeamIdentityService" />: the team
///     filter ComboBox's <c>SelectedItem</c> is TwoWay, so clearing <c>AvailableTeams</c> mid-rebuild with
///     the view actually attached is the one scenario that exercises the binding write-back the headless
///     VM-only tests in <c>TeamsModuleTests</c> and <c>StratBookShellTests</c> cannot reach.
/// </summary>
[Category("Integration")]
public class LibraryTeamFilterRenderTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    private static DemoCacheRecord Record(string path, int day, string[] t, string[] ct, string map = "de_nuke")
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
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 2 });
        }

        foreach (string id in ct)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 3 });
        }

        DemoCacheStore.StampParse(record);
        return record;
    }

    [Test]
    public async Task SelectedTeam_SurvivesARename_AndThenAPackToggle_WithTheRealViewAttached() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            DemoCacheStore cache = new(null);
            TeamIdentityService teams = new(null, cache, run: _inline);
            await teams.StartAsync();
            // Two rosters recurring across a and b (so each clusters into a team), and a one-off on c
            // (no recurrence, no team): the same shape TeamsModuleTests.Library() uses.
            using (cache.BeginBatch())
            {
                cache.Upsert(Record("/d/a.dem", 1, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15)));
                cache.Upsert(Record("/d/b.dem", 2, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_mirage"));
                cache.Upsert(Record("/d/c.dem", 3, Ids(21, 22, 23, 24, 25), Ids(31, 32, 33, 34, 35)));
            }

            await teams.Idle;

            DemoLibraryService library = new(a => a(),
                Path.Combine(Path.GetTempPath(), "dvlibteamrender_" + Guid.NewGuid().ToString("N") + ".json"));
            foreach (string path in (string[]) ["/d/a.dem", "/d/b.dem", "/d/c.dem"])
            {
                library.Entries.Add(new DemoEntry
                {
                    FilePath = path,
                    FileName = Path.GetFileName(path),
                    Directory = "/d",
                    FileSizeBytes = 1000,
                    Modified = new DateTime(2026, 3, 1),
                    MapName = "de_nuke",
                    State = DemoIndexState.Indexed
                });
            }

            bool packEnabled = true;
            LibraryTabViewModel vm = new(library, _ => Task.CompletedTask,
                () => Task.FromResult<IReadOnlyList<string>>([]), teams: teams, packEnabled: () => packEnabled);

            LibraryTabView view = new() { DataContext = vm };
            Window window = new() { Width = 1280, Height = 800, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            ComboBox combo = view.FindControl<ComboBox>("TeamFilterCombo")!;
            TeamFilterItem teamA = vm.AvailableTeams[2];
            combo.SelectedItem = teamA;
            Dispatcher.UIThread.RunJobs();

            using (Assert.Multiple())
            {
                await Assert.That(vm.SelectedTeam).IsEqualTo(teamA);
                await Assert.That(vm.FilteredEntries.Count).IsEqualTo(2)
                    .Because("the ComboBox pick reached the VM and the filter narrowed to the team's two demos");
            }

            // A rebuild while the pack stays ON: a rename reaches AvailableTeams through _teams.Changed,
            // the same Clear()-then-Add() the ComboBox is attached for. The selection must come back by
            // id, and the filter must not read as cleared in between.
            teams.Rename(teamA.TeamId!.Value, "Renamed Team");
            Dispatcher.UIThread.RunJobs();

            using (Assert.Multiple())
            {
                await Assert.That(vm.SelectedTeam.TeamId).IsEqualTo(teamA.TeamId)
                    .Because("the selection survives the rebuild by id");
                await Assert.That(vm.SelectedTeam.Display).IsEqualTo("Renamed Team");
                await Assert.That(combo.SelectedItem).IsEqualTo(vm.SelectedTeam)
                    .Because("the ComboBox's own selection, not just the VM property, tracked the rebuild");
                await Assert.That(vm.FilteredEntries.Count).IsEqualTo(2)
                    .Because("the filter never read as cleared across the rebuild");
            }

            // Off: the surface goes, the filter clears (by design).
            packEnabled = false;
            vm.RefreshPackGate();
            Dispatcher.UIThread.RunJobs();

            using (Assert.Multiple())
            {
                await Assert.That(vm.HasTeamFilter).IsFalse();
                await Assert.That(vm.SelectedTeam).IsEqualTo(TeamFilterItem.All);
                await Assert.That(vm.FilteredEntries.Count).IsEqualTo(3);
            }

            // On again: no crash, and a sane, non-corrupted end state.
            packEnabled = true;
            vm.RefreshPackGate();
            Dispatcher.UIThread.RunJobs();

            using (Assert.Multiple())
            {
                await Assert.That(vm.HasTeamFilter).IsTrue();
                await Assert.That(vm.SelectedTeam).IsEqualTo(TeamFilterItem.All);
                await Assert.That(vm.AvailableTeams.Count).IsEqualTo(4).Because("All, Us, and the two teams");
                await Assert.That(combo.SelectedItem).IsEqualTo(TeamFilterItem.All);
            }

            window.Close();
            teams.Dispose();
        });
}
