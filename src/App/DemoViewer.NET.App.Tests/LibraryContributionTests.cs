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
///     entry, the gate follows live, a contribution's own <c>Changed</c> re-applies, and turning off resets
///     the filter to "no selection" without ever touching a contribution the gate says is off.
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
    // and a settable FeatureId so a test can drive the host's on/off decision directly.
    private sealed class FakeContribution : ILibraryContribution
    {
        public string? FeatureId { get; set; }
        public LibraryFilter? FilterValue { get; set; }
        public bool HasBadgeValue { get; set; }
        public Func<DemoEntry, LibraryBadge?>? BadgeForFunc { get; set; }
        public IReadOnlyList<string> BadgeLabelsValue { get; set; } = [];
        public string? BadgeResetLabelValue { get; set; }
        public List<(DemoEntry Entry, string? Label)> SetLabelCalls { get; } = [];

        public event Action? Changed;

        public LibraryFilter? Filter => FilterValue;
        public bool HasBadge => HasBadgeValue;
        public LibraryBadge? BadgeFor(DemoEntry entry) => BadgeForFunc?.Invoke(entry);
        public IReadOnlyList<string> BadgeLabels => BadgeLabelsValue;
        public string? BadgeResetLabel => BadgeResetLabelValue;

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
        public IReadOnlyList<string> BadgeLabels => throw new InvalidOperationException("BadgeLabels read while off");
        public string? BadgeResetLabel => throw new InvalidOperationException("BadgeResetLabel read while off");
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
    public async Task Badge_RendersPerEntry_FromTheActiveContribution()
    {
        DemoLibraryService lib = NewLibrary("/d/a.dem", "/d/b.dem");
        FakeContribution c = new()
        {
            FeatureId = "pack.fake",
            HasBadgeValue = true,
            BadgeForFunc = e => new LibraryBadge(e.FileName, "tip:" + e.FileName, e.FileName == "a.dem"),
            BadgeLabelsValue = ["x", "y"],
            BadgeResetLabelValue = "Reset"
        };
        LibraryTabViewModel vm = NewVm(lib, [c], _ => true);

        using (Assert.Multiple())
        {
            await Assert.That(vm.HasProvenance).IsTrue();
            await Assert.That(vm.BadgeLabels).IsEquivalentTo(["x", "y"]);
            await Assert.That(vm.BadgeResetLabel).IsEqualTo("Reset");
            await Assert.That(vm.BadgeMenuEntries).IsEquivalentTo(["x", "y", "Reset"]);
            await Assert.That(lib.Entries[0].BadgeLabel).IsEqualTo("a.dem");
            await Assert.That(lib.Entries[0].BadgeIsPinned).IsTrue();
            await Assert.That(lib.Entries[1].BadgeLabel).IsEqualTo("b.dem");
            await Assert.That(lib.Entries[1].BadgeIsPinned).IsFalse();
        }

        vm.SetProvenance(lib.Entries[0], "picked");
        using (Assert.Multiple())
        {
            await Assert.That(c.SetLabelCalls.Count).IsEqualTo(1);
            await Assert.That(c.SetLabelCalls[0].Entry).IsEqualTo(lib.Entries[0]);
            await Assert.That(c.SetLabelCalls[0].Label).IsEqualTo("picked");
        }
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
            await Assert.That(vm.HasTeamFilter).IsFalse();
            await Assert.That(vm.HasProvenance).IsFalse();
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
            await Assert.That(vm.HasProvenance).IsTrue();
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
            await Assert.That(vm.HasTeamFilter).IsFalse();
            await Assert.That(vm.HasProvenance).IsFalse();
            await Assert.That(vm.BadgeLabels).IsEmpty();
        }
    }
}
