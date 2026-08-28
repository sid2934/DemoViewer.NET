#region

using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.MatchOverview;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Library selection → cached Match Overview render. Selecting a card is a
///     BROWSING gesture: it reads the unified cache and starts nothing. Opening stays on double-click.
///     <para>
///         The parse-free property is the premise of the whole "Match Overview is a cache render" move. One
///         heavy parse is allowed machine-wide, so a preview that parsed would make arrow-keying the library
///         strictly worse than the card grid it replaces — and would queue hundreds of parses for a user
///         merely scrolling.
///     </para>
/// </summary>
[NotInParallel]
public class LibraryPreviewTests
{
    private static string TempLibraryJson() =>
        Path.Combine(Path.GetTempPath(), "dvlib_prev_" + Guid.NewGuid().ToString("N") + ".json");

    private static (MainViewModel Vm, DemoCacheStore Cache, string Root) Shell()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvprev_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        DemoCacheStore cache = new(Path.Combine(root, "cache"));
        MainViewModel vm = new(null, new ModuleRegistry(),
            new DemoLibraryService(null, TempLibraryJson()),
            demoCache: cache);
        return (vm, cache, root);
    }

    private static DemoEntry Entry(string path) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        Directory = Path.GetDirectoryName(path) ?? "",
        FileSizeBytes = 4096,
        Modified = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch
        {
            /* best-effort */
        }
    }

    [Test]
    public async Task SelectingADemo_RendersItsCachedRecord_WithoutParsing()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (MainViewModel vm, DemoCacheStore cache, string root) = Shell();
            try
            {
                const string path = "/demos/cached.dem";
                cache.Update(path, 4096, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, r =>
                {
                    r.Map = "de_nuke";
                    r.Server = "FACEIT";
                    r.TickRate = 64;
                    r.DurationSeconds = 1800;
                    r.Players =
                    [
                        new CachedPlayerInfo
                        {
                            Slot = 0,
                            Name = "s1mple",
                            Team = 3
                        },
                        new CachedPlayerInfo
                        {
                            Slot = 1,
                            Name = "ZywOo",
                            Team = 2
                        }
                    ];
                    DemoCacheStore.StampParse(r);
                });

                vm.LibraryTab.SelectedEntry = Entry(path);

                using (Assert.Multiple())
                {
                    await Assert.That(vm.MatchOverviewTab.Mode).IsEqualTo(OverviewMode.Cached);
                    await Assert.That(vm.MatchOverviewTab.SubjectKey).IsEqualTo(path);
                    await Assert.That(vm.MatchOverviewTab.HasContent).IsTrue();
                    // Nothing was opened, parsed or queued.
                    await Assert.That(vm.HasFile).IsFalse()
                        .Because("selection is browsing — the demo is not open");
                    await Assert.That(vm.Frames).IsEmpty();
                    await Assert.That(vm.IsLoading).IsFalse();
                    // And the stage strip is NOT lit: nothing is doing anything to this demo.
                    await Assert.That(vm.MatchOverviewTab.Progress).IsEqualTo(0);
                    await Assert.That(vm.MatchOverviewTab.ParseStage.IsDone).IsFalse();
                }
            }
            finally
            {
                Cleanup(root);
            }
        });
    }

    /// <summary>
    ///     A demo the library knows but has never indexed still renders — its NOT INDEXED state carries the
    ///     action that fixes it. Falling back to a blank page would make the un-indexed majority of a fresh
    ///     library look broken.
    /// </summary>
    [Test]
    public async Task SelectingAnUnindexedDemo_StillRenders_AndStartsNothing()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (MainViewModel vm, _, string root) = Shell();
            try
            {
                vm.LibraryTab.SelectedEntry = Entry("/demos/never-indexed.dem");

                using (Assert.Multiple())
                {
                    await Assert.That(vm.MatchOverviewTab.Mode).IsEqualTo(OverviewMode.Cached);
                    await Assert.That(vm.MatchOverviewTab.SubjectKey).IsEqualTo("/demos/never-indexed.dem");
                    await Assert.That(vm.HasFile).IsFalse();
                    await Assert.That(vm.IsLoading).IsFalse();
                }
            }
            finally
            {
                Cleanup(root);
            }
        });
    }

    /// <summary>
    ///     Selecting the demo that is ALREADY OPEN must not replace its live page with the cached one — the
    ///     live render is strictly richer, and a selection is not a request to leave it.
    /// </summary>
    [Test]
    [Category("RealDemo")]
    public async Task SelectingTheOpenDemo_LeavesTheLivePageAlone()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            (MainViewModel vm, DemoCacheStore cache, string root) = Shell();
            try
            {
                cache.Update(demo, 1, 2, r =>
                {
                    r.Map = "de_stale_from_cache";
                    DemoCacheStore.StampParse(r);
                });

                await vm.LoadDemoFromPathAsync(demo);
                await Assert.That(vm.MatchOverviewTab.Mode).IsEqualTo(OverviewMode.Live);

                vm.LibraryTab.SelectedEntry = Entry(demo);

                using (Assert.Multiple())
                {
                    await Assert.That(vm.MatchOverviewTab.Mode).IsEqualTo(OverviewMode.Live)
                        .Because("the open demo's live page outranks its own cached record");
                    await Assert.That(vm.MatchOverviewTab.MapDisplay)
                        .IsNotEqualTo("de_stale_from_cache");
                }
            }
            finally
            {
                Cleanup(root);
            }
        });
    }

    /// <summary>
    ///     Previewing another demo while one is open offers the way back, and taking it re-renders the live
    ///     page. The shell re-derives rather than restoring a stash — a stash taken mid-load would be a
    ///     snapshot of a page that was still filling in.
    /// </summary>
    [Test]
    [Category("RealDemo")]
    public async Task PreviewingWhileADemoIsOpen_OffersTheWayBack_AndReturningRestoresTheLivePage()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            (MainViewModel vm, DemoCacheStore cache, string root) = Shell();
            try
            {
                await vm.LoadDemoFromPathAsync(demo);
                string liveMap = vm.MatchOverviewTab.MapDisplay;

                cache.Update("/demos/other.dem", 1, 2, r =>
                {
                    r.Map = "de_other";
                    DemoCacheStore.StampParse(r);
                });
                vm.LibraryTab.SelectedEntry = Entry("/demos/other.dem");

                using (Assert.Multiple())
                {
                    await Assert.That(vm.MatchOverviewTab.Mode).IsEqualTo(OverviewMode.Cached);
                    await Assert.That(vm.MatchOverviewTab.CanReturnToLive).IsTrue();
                    await Assert.That(vm.MatchOverviewTab.LiveDemoName)
                        .IsEqualTo(Path.GetFileName(demo));
                }

                vm.MatchOverviewTab.ReturnToLiveCommand.Execute(null);

                using (Assert.Multiple())
                {
                    await Assert.That(vm.MatchOverviewTab.Mode).IsEqualTo(OverviewMode.Live);
                    await Assert.That(vm.MatchOverviewTab.SubjectKey).IsEqualTo(demo);
                    await Assert.That(vm.MatchOverviewTab.MapDisplay).IsEqualTo(liveMap);
                    await Assert.That(vm.MatchOverviewTab.HasSummary).IsTrue()
                        .Because("the rosters must come back, not just the identity");
                    // The trap the plan flagged: a stale live name would offer "Back to the demo before last".
                    await Assert.That(vm.MatchOverviewTab.CanReturnToLive).IsFalse();
                }
            }
            finally
            {
                Cleanup(root);
            }
        });
    }
}
