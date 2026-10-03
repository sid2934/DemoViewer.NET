#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.ViewModels.Library;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Library's generic hosting of pack contributions (item 22), over a fake <see cref="ILibraryContribution" />
///     so the mechanism is tested apart from the Strat Book's own Team/Provenance contributions (covered by
///     <c>LibraryContributionsTests</c> in the pack's own test folder): filters apply, a badge renders per
///     entry through the batch hook, the gate follows live, a contribution's own <c>Changed</c> re-applies
///     (and only refreshes the badge when it owns it), a filter added live is picked up, and turning off
///     resets the filter to "no selection" without ever touching a contribution the gate says is off.
/// </summary>
[NotInParallel]
public class LibraryContributionTests
{
    private static DemoEntry Entry(string path) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        Directory = "/demos",
        FileSizeBytes = 1000,
        Modified = new DateTime(2026, 3, 1),
        State = DemoIndexState.Indexed
    };

    private static DemoLibraryService NewLibrary(params string[] paths)
    {
        DemoLibraryService lib = new(a => a(), Path.Combine(Path.GetTempPath(), "dvlibcontrib_" + Guid.NewGuid().ToString("N") + ".json"));
        foreach (string p in paths)
        {
            lib.Entries.Add(Entry(p));
        }

        return lib;
    }

    private static LibraryTabViewModel NewVm(DemoLibraryService lib, IReadOnlyList<ILibraryContribution> contributions, Func<string, bool>? isFeatureEnabled = null) =>
        new(lib, _ => Task.CompletedTask, () => Task.FromResult<IReadOnlyList<string>>([]),
            contributions: contributions, isFeatureEnabled: isFeatureEnabled);

    // A filter that keeps only the entry named "keep", a badge that labels every entry with its file name,
    // and a settable FeatureId so a test can drive the host's on/off decision directly. BadgeForCalls and
    // BadgesForCalls distinguish a per-entry read from the full-refresh batch hook.
    private sealed class FakeContribution : ILibraryContribution
    {
        public string? FeatureId { get; set; }
        public LibraryFilter? FilterValue { get; set; }
        public bool HasBadgeValue { get; set; }
        public Func<DemoEntry, LibraryBadge?>? BadgeForFunc { get; set; }
        public IReadOnlyList<string> BadgeLabelsValue { get; set; } = [];
        public string? BadgeResetLabelValue { get; set; }
        public string? BadgeResetTooltipValue { get; set; }
        public int BadgeForCalls { get; private set; }
        public int BadgesForCalls { get; private set; }
        public List<(DemoEntry Entry, string? Label)> SetLabelCalls { get; } = [];

        public event Action? Changed;

        public LibraryFilter? Filter => FilterValue;
        public bool HasBadge => HasBadgeValue;

        public LibraryBadge? BadgeFor(DemoEntry entry)
        {
            BadgeForCalls++;
            return BadgeForFunc?.Invoke(entry);
        }

        public IReadOnlyDictionary<string, LibraryBadge?> BadgesFor(IEnumerable<DemoEntry> entries)
        {
            BadgesForCalls++;
            Dictionary<string, LibraryBadge?> result = new(StringComparer.Ordinal);
            foreach (DemoEntry entry in entries)
            {
                result[entry.FilePath] = BadgeForFunc?.Invoke(entry);
            }

            return result;
        }

        public IReadOnlyList<string> BadgeLabels => BadgeLabelsValue;
        public string? BadgeResetLabel => BadgeResetLabelValue;
        public string? BadgeResetTooltip => BadgeResetTooltipValue;

        public void SetLabel(DemoEntry entry, string? label) => SetLabelCalls.Add((entry, label));

        public void RaiseChanged() => Changed?.Invoke();
    }

    private static LibraryFilter KeepFilter() => new("Keep",
        [new LibraryFilterItem("", "All"), new LibraryFilterItem("keep", "Keep")],
        (entry, key) => key != "keep" || entry.FileName == "keep.dem");

    // Throws from every member except FeatureId: stands in for "the pack's real services", so a test can
    // assert the host never calls into it while its gate is off.
    private sealed class ThrowingContribution : ILibraryContribution
    {
        public string? FeatureId => "pack.fake";

        public event Action? Changed
        {
            add => throw new InvalidOperationException("Changed subscribed while off");
            remove => throw new InvalidOperationException("Changed unsubscribed while off");
        }

        public LibraryFilter? Filter => throw new InvalidOperationException("Filter read while off");
        public bool HasBadge => throw new InvalidOperationException("HasBadge read while off");
        public LibraryBadge? BadgeFor(DemoEntry entry) => throw new InvalidOperationException("BadgeFor called while off");

        public IReadOnlyDictionary<string, LibraryBadge?> BadgesFor(IEnumerable<DemoEntry> entries) =>
            throw new InvalidOperationException("BadgesFor called while off");

        public IReadOnlyList<string> BadgeLabels => throw new InvalidOperationException("BadgeLabels read while off");
        public string? BadgeResetLabel => throw new InvalidOperationException("BadgeResetLabel read while off");
        public string? BadgeResetTooltip => throw new InvalidOperationException("BadgeResetTooltip read while off");
        public void SetLabel(DemoEntry entry, string? label) => throw new InvalidOperationException("SetLabel called while off");
    }

    [Test]
    public async Task Filter_Applies_AndIsSkippedForTheAllChoice()
    {
        DemoLibraryService lib = NewLibrary("/d/keep.dem", "/d/drop.dem");
        FakeContribution c = new() { FeatureId = "pack.fake", FilterValue = KeepFilter() };
        LibraryTabViewModel vm = NewVm(lib, [c], _ => true);

        LibraryFilterViewModel filter = vm.Filters.Single();
        await Assert.That(vm.FilteredEntries.Count).IsEqualTo(2).Because("the \"\" choice applies no predicate");

        filter.Selected = filter.Items.Single(i => i.Key == "keep");
        await Assert.That(vm.FilteredEntries.Select(e => e.FileName)).IsEquivalentTo(["keep.dem"]);
    }

    [Test]
    public async Task Badge_RendersPerEntry_FromTheActiveContribution_AndTheMenuCarriesTheResetRow()
    {
        DemoLibraryService lib = NewLibrary("/d/a.dem", "/d/b.dem");
        FakeContribution c = new()
        {
            FeatureId = "pack.fake",
            HasBadgeValue = true,
            BadgeForFunc = e => new LibraryBadge(e.FileName, "tip:" + e.FileName, e.FileName == "a.dem"),
            BadgeLabelsValue = ["x", "y"],
            BadgeResetLabelValue = "Reset",
            BadgeResetTooltipValue = "Back to automatic"
        };
        LibraryTabViewModel vm = NewVm(lib, [c], _ => true);

        using (Assert.Multiple())
        {
            await Assert.That(vm.HasBadge).IsTrue();
            await Assert.That(vm.BadgeLabels).IsEquivalentTo(["x", "y"]);
            await Assert.That(vm.BadgeResetLabel).IsEqualTo("Reset");
            await Assert.That(vm.BadgeMenuEntries.Select(e => (e.Label, e.IsReset, e.Tooltip))).IsEquivalentTo(
            [
                ("x", false, (string?)null),
                ("y", false, (string?)null),
                ("Reset", true, (string?)"Back to automatic")
            ]);
            await Assert.That(lib.Entries[0].BadgeLabel).IsEqualTo("a.dem");
            await Assert.That(lib.Entries[0].BadgeIsPinned).IsTrue();
            await Assert.That(lib.Entries[1].BadgeLabel).IsEqualTo("b.dem");
            await Assert.That(lib.Entries[1].BadgeIsPinned).IsFalse();
        }

        vm.SetBadgeLabel(lib.Entries[0], "picked");
        using (Assert.Multiple())
        {
            await Assert.That(c.SetLabelCalls.Count).IsEqualTo(1);
            await Assert.That(c.SetLabelCalls[0].Entry).IsEqualTo(lib.Entries[0]);
            await Assert.That(c.SetLabelCalls[0].Label).IsEqualTo("picked");
        }
    }

    [Test]
    public async Task Badge_UsesTheBatchHook_NotOnePerEntry_ForAFullRefresh()
    {
        DemoLibraryService lib = NewLibrary("/d/a.dem", "/d/b.dem", "/d/c.dem");
        FakeContribution c = new()
        {
            FeatureId = "pack.fake",
            HasBadgeValue = true,
            BadgeForFunc = e => new LibraryBadge(e.FileName, null, false)
        };
        LibraryTabViewModel vm = NewVm(lib, [c], _ => true);

        using (Assert.Multiple())
        {
            await Assert.That(c.BadgesForCalls).IsEqualTo(1).Because("one batch call refreshes every entry at construction");
            await Assert.That(c.BadgeForCalls).IsEqualTo(0).Because("the per-entry hook is for a single-entry update, not a full refresh");
            await Assert.That(lib.Entries.Select(e => e.BadgeLabel!)).IsEquivalentTo(["a.dem", "b.dem", "c.dem"]);
        }

        // A library change (OnEntriesChanged, fired by the collection add) also refreshes through the batch hook.
        lib.Entries.Add(Entry("/d/d.dem"));
        await Assert.That(c.BadgesForCalls).IsGreaterThan(1);
        await Assert.That(c.BadgeForCalls).IsEqualTo(0);
    }

    [Test]
    public async Task GateFollowsLive_OffRemovesTheFilterAndClearsTheBadge_OnBringsThemBack()
    {
        DemoLibraryService lib = NewLibrary("/d/keep.dem", "/d/drop.dem");
        FakeContribution c = new()
        {
            FeatureId = "pack.fake",
            FilterValue = KeepFilter(),
            HasBadgeValue = true,
            BadgeForFunc = e => new LibraryBadge(e.FileName, null, false)
        };
        bool on = true;
        LibraryTabViewModel vm = NewVm(lib, [c], _ => on);

        LibraryFilterViewModel filter = vm.Filters.Single();
        filter.Selected = filter.Items.Single(i => i.Key == "keep");
        await Assert.That(vm.FilteredEntries.Count).IsEqualTo(1);

        on = false;
        vm.RefreshContributions();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Filters).IsEmpty();
            await Assert.That(vm.HasBadge).IsFalse();
            await Assert.That(vm.FilteredEntries.Count).IsEqualTo(2)
                .Because("no filter contribution left: every entry passes");
            await Assert.That(lib.Entries[0].BadgeLabel).IsNull().Because("the badge clears when its contribution goes off");
        }

        on = true;
        vm.RefreshContributions();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Filters.Single().Selected.Key).IsEqualTo("")
                .Because("re-enabling starts the filter back at its neutral choice");
            await Assert.That(vm.HasBadge).IsTrue();
            await Assert.That(lib.Entries[0].BadgeLabel).IsEqualTo("keep.dem");
        }
    }

    [Test]
    public async Task Changed_ReApplies_WithoutAGateFlip()
    {
        DemoLibraryService lib = NewLibrary("/d/a.dem", "/d/b.dem");
        FakeContribution c = new() { FeatureId = "pack.fake", FilterValue = KeepFilter() };
        LibraryTabViewModel vm = NewVm(lib, [c], _ => true);

        LibraryFilterViewModel filter = vm.Filters.Single();
        filter.Selected = filter.Items.Single(i => i.Key == "keep");
        await Assert.That(vm.FilteredEntries).IsEmpty().Because("neither demo is named keep.dem yet");

        // The underlying data changed (e.g. a rename); the contribution reports a fresh Filter and raises
        // Changed, with no gate transition at all.
        c.FilterValue = new LibraryFilter("Keep", [new LibraryFilterItem("", "All"), new LibraryFilterItem("keep", "Keep")],
            (entry, key) => key != "keep" || entry.FileName == "a.dem");
        c.RaiseChanged();

        await Assert.That(vm.FilteredEntries.Select(e => e.FileName)).IsEquivalentTo(["a.dem"]);
    }

    [Test]
    public async Task Changed_OnAFilterOnlyContribution_DoesNotRefreshTheBadge()
    {
        DemoLibraryService lib = NewLibrary("/d/a.dem");
        FakeContribution filterOnly = new() { FeatureId = "pack.filter", FilterValue = KeepFilter() };
        FakeContribution badge = new()
        {
            FeatureId = "pack.badge",
            HasBadgeValue = true,
            BadgeForFunc = e => new LibraryBadge(e.FileName, null, false)
        };
        LibraryTabViewModel vm = NewVm(lib, [filterOnly, badge], _ => true);
        int before = badge.BadgesForCalls;
        await Assert.That(before).IsEqualTo(1).Because("construction's own refresh");

        filterOnly.RaiseChanged();

        await Assert.That(badge.BadgesForCalls).IsEqualTo(before)
            .Because("the team filter's own Changed must not re-run the badge contribution's batch hook");
    }

    [Test]
    public async Task Changed_OnTheActiveBadgeContribution_DoesRefreshTheBadge()
    {
        DemoLibraryService lib = NewLibrary("/d/a.dem");
        FakeContribution badge = new()
        {
            FeatureId = "pack.badge",
            HasBadgeValue = true,
            BadgeForFunc = e => new LibraryBadge(e.FileName, null, false)
        };
        LibraryTabViewModel vm = NewVm(lib, [badge], _ => true);
        int before = badge.BadgesForCalls;

        badge.RaiseChanged();

        await Assert.That(badge.BadgesForCalls).IsEqualTo(before + 1);
    }

    [Test]
    public async Task Changed_AddsAFilterViewModel_WhenFilterGoesFromNullToNonNull()
    {
        DemoLibraryService lib = NewLibrary("/d/keep.dem", "/d/drop.dem");
        FakeContribution c = new() { FeatureId = "pack.fake", FilterValue = null };
        LibraryTabViewModel vm = NewVm(lib, [c], _ => true);
        await Assert.That(vm.Filters).IsEmpty().Because("the contribution started with no filter");

        c.FilterValue = KeepFilter();
        c.RaiseChanged();

        using (Assert.Multiple())
        {
            await Assert.That(vm.Filters.Count).IsEqualTo(1)
                .Because("Changed must add a filter VM, not only a gate transition");
            LibraryFilterViewModel filter = vm.Filters.Single();
            filter.Selected = filter.Items.Single(i => i.Key == "keep");
            await Assert.That(vm.FilteredEntries.Select(e => e.FileName)).IsEquivalentTo(["keep.dem"]);
        }
    }

    [Test]
    public async Task NothingIsQueried_WhileTheGateIsOff()
    {
        DemoLibraryService lib = NewLibrary("/d/a.dem");
        ThrowingContribution c = new();

        // Construction, a no-op refresh, and badge/filter reads must all avoid every member but FeatureId.
        LibraryTabViewModel vm = NewVm(lib, [c], _ => false);
        vm.RefreshContributions();

        using (Assert.Multiple())
        {
            await Assert.That(vm.Filters).IsEmpty();
            await Assert.That(vm.HasBadge).IsFalse();
            await Assert.That(vm.BadgeLabels).IsEmpty();
        }
    }
}
