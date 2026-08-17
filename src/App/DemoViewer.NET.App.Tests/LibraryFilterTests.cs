#region

using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.ViewModels.Library;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Filter logic for the demo-library tab: the MULTI-select map filter, the single-select player filter,
///     their intersection with free-text search, and Clear. Pure view-model behaviour over an injected
///     <see cref="DemoLibraryService" /> with entries added directly (no scan, no UI thread needed).
/// </summary>
[NotInParallel]
public class LibraryFilterTests
{
    private static readonly Action<Action> _inline = a => a();

    private static DemoLibraryService NewService() =>
        new(_inline, Path.Combine(Path.GetTempPath(), "dvlibfilter_" + Guid.NewGuid().ToString("N") + ".json"));

    private static LibraryTabViewModel NewVm(DemoLibraryService svc) => new(
        svc,
        _ => Task.CompletedTask,
        () => Task.FromResult<IReadOnlyList<string>>([]));

    private static DemoEntry Entry(string map, string players, string? file = null) => new()
    {
        FilePath = "/demos/" + (file ?? map + ".dem"),
        FileName = file ?? map + ".dem",
        Directory = "/demos",
        FileSizeBytes = 100_000_000,
        Modified = new DateTime(2026, 7, 1, 12, 0, 0),
        MapName = map,
        ServerName = "srv",
        Players = players.Split(',').ToList(),
        DurationSeconds = 3000,
        State = DemoIndexState.Indexed
    };

    [Test]
    public async Task MapFilter_MultiSelect_ShowsOnlyCheckedMaps_AndAllWhenNoneChecked()
    {
        using DemoLibraryService svc = NewService();
        LibraryTabViewModel vm = NewVm(svc);
        svc.Entries.Add(Entry("de_dust2", "s1mple,b1t"));
        svc.Entries.Add(Entry("de_mirage", "ZywOo,apEX"));
        svc.Entries.Add(Entry("de_nuke", "device,stavn"));

        // None checked → all maps pass; one filter item per distinct map.
        await Assert.That(vm.FilteredEntries.Count).IsEqualTo(3);
        await Assert.That(vm.MapFilters.Count).IsEqualTo(3);
        await Assert.That(vm.MapFilterSummary).IsEqualTo("Maps");
        await Assert.That(vm.HasActiveFilters).IsFalse();

        // Check dust2 + mirage → only those two demos remain.
        vm.MapFilters.Single(m => m.MapKey == "de_dust2").IsSelected = true;
        vm.MapFilters.Single(m => m.MapKey == "de_mirage").IsSelected = true;

        await Assert.That(vm.FilteredEntries.Count).IsEqualTo(2);
        await Assert.That(vm.FilteredEntries.All(e => e.MapName is "de_dust2" or "de_mirage")).IsTrue();
        await Assert.That(vm.MapFilterSummary).IsEqualTo("Maps (2)");
        await Assert.That(vm.HasActiveFilters).IsTrue();
    }

    [Test]
    public async Task PlayerFilter_ShowsOnlyDemosContainingThePlayer()
    {
        using DemoLibraryService svc = NewService();
        LibraryTabViewModel vm = NewVm(svc);
        svc.Entries.Add(Entry("de_dust2", "s1mple,b1t"));
        svc.Entries.Add(Entry("de_mirage", "ZywOo,apEX"));

        // The player dropdown lists "All players" + every distinct player seen.
        await Assert.That(vm.AvailablePlayers.Contains("s1mple")).IsTrue();
        await Assert.That(vm.AvailablePlayers.Contains("ZywOo")).IsTrue();
        await Assert.That(vm.AvailablePlayers[0]).IsEqualTo("All players");

        vm.SelectedPlayer = "s1mple";

        await Assert.That(vm.FilteredEntries.Count).IsEqualTo(1);
        await Assert.That(vm.FilteredEntries[0].MapName).IsEqualTo("de_dust2");
        await Assert.That(vm.HasActiveFilters).IsTrue();
    }

    [Test]
    public async Task CombinedFilters_MapAndPlayer_Intersect()
    {
        using DemoLibraryService svc = NewService();
        LibraryTabViewModel vm = NewVm(svc);
        svc.Entries.Add(Entry("de_dust2", "s1mple,b1t", "a.dem"));
        svc.Entries.Add(Entry("de_dust2", "ZywOo,apEX", "b.dem")); // same map, no s1mple
        svc.Entries.Add(Entry("de_mirage", "s1mple,jL", "c.dem")); // s1mple, different map

        vm.MapFilters.Single(m => m.MapKey == "de_dust2").IsSelected = true;
        vm.SelectedPlayer = "s1mple";

        // Intersection: only the dust2 demo that also has s1mple.
        await Assert.That(vm.FilteredEntries.Count).IsEqualTo(1);
        await Assert.That(vm.FilteredEntries[0].FileName).IsEqualTo("a.dem");
    }

    [Test]
    public async Task ClearFilters_ResetsMapPlayerAndSearch_InOnePass()
    {
        using DemoLibraryService svc = NewService();
        LibraryTabViewModel vm = NewVm(svc);
        svc.Entries.Add(Entry("de_dust2", "s1mple,b1t"));
        svc.Entries.Add(Entry("de_mirage", "ZywOo,apEX"));

        vm.MapFilters.Single(m => m.MapKey == "de_dust2").IsSelected = true;
        vm.SelectedPlayer = "s1mple";
        vm.SearchText = "dust";
        await Assert.That(vm.HasActiveFilters).IsTrue();

        vm.ClearFiltersCommand.Execute(null);

        await Assert.That(vm.HasActiveFilters).IsFalse();
        await Assert.That(vm.MapFilters.Any(m => m.IsSelected)).IsFalse();
        await Assert.That(vm.SelectedPlayer).IsEqualTo("All players");
        await Assert.That(vm.SearchText).IsEqualTo("");
        await Assert.That(vm.MapFilterSummary).IsEqualTo("Maps");
        await Assert.That(vm.FilteredEntries.Count).IsEqualTo(2);
    }

    [Test]
    public async Task MapFilterSelection_SurvivesReindex_WhenMapSetUnchanged()
    {
        using DemoLibraryService svc = NewService();
        LibraryTabViewModel vm = NewVm(svc);
        svc.Entries.Add(Entry("de_dust2", "s1mple,b1t", "a.dem"));
        svc.Entries.Add(Entry("de_mirage", "ZywOo,apEX", "b.dem"));

        vm.MapFilters.Single(m => m.MapKey == "de_dust2").IsSelected = true;
        await Assert.That(vm.FilteredEntries.Count).IsEqualTo(1);

        // A new demo on an EXISTING map arrives (re-index) — the map set is unchanged, so the dust2 check
        // must persist (chips aren't rebuilt) and the new dust2 demo joins the filtered view.
        svc.Entries.Add(Entry("de_dust2", "NiKo,huNter", "c.dem"));

        await Assert.That(vm.MapFilters.Single(m => m.MapKey == "de_dust2").IsSelected).IsTrue();
        await Assert.That(vm.FilteredEntries.Count).IsEqualTo(2);
        await Assert.That(vm.FilteredEntries.All(e => e.MapName == "de_dust2")).IsTrue();
    }

    /// <summary>
    ///     The virtualized card grid renders CardRows = FilteredEntries chunked by CardColumns: rows
    ///     track the filter, re-chunk on a column-count change, and preserve the filtered order.
    /// </summary>
    [Test]
    public async Task CardRows_ChunkFilteredEntries_AndFollowColumnAndFilterChanges()
    {
        using DemoLibraryService svc = NewService();
        LibraryTabViewModel vm = NewVm(svc);
        for (int i = 0; i < 7; i++)
        {
            svc.Entries.Add(Entry(i % 2 == 0 ? "de_dust2" : "de_mirage", "p" + i, $"d{i}.dem"));
        }

        // Default 4 columns → 7 entries = rows of 4 + 3, in FilteredEntries order.
        await Assert.That(vm.CardRows.Count).IsEqualTo(2);
        await Assert.That(vm.CardRows[0].Items.Count).IsEqualTo(4);
        await Assert.That(vm.CardRows[1].Items.Count).IsEqualTo(3);
        await Assert.That(vm.CardRows.SelectMany(r => r.Items).SequenceEqual(vm.FilteredEntries)).IsTrue();

        // Narrower viewport → 3 columns → re-chunked; clamps below 1 to a single column.
        vm.SetCardColumns(3);
        await Assert.That(vm.CardRows.Count).IsEqualTo(3);
        await Assert.That(vm.CardRows[2].Items.Count).IsEqualTo(1);
        vm.SetCardColumns(0);
        await Assert.That(vm.CardColumns).IsEqualTo(1);
        await Assert.That(vm.CardRows.Count).IsEqualTo(7);

        // Filtering re-chunks to the filtered subset only.
        vm.SetCardColumns(4);
        vm.MapFilters.Single(m => m.MapKey == "de_dust2").IsSelected = true;
        await Assert.That(vm.FilteredEntries.Count).IsEqualTo(4);
        await Assert.That(vm.CardRows.Count).IsEqualTo(1);
        await Assert.That(vm.CardRows[0].Items.Count).IsEqualTo(4);
    }
}
