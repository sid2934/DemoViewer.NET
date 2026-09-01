#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Library;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Recent-files store + Library VM recents wiring. Covers the store's most-recent-first
///     ordering, cap enforcement, path de-dup, disk round-trip, and stale-entry pruning; plus the
///     <see cref="LibraryTabViewModel" />'s OpenRecent routing (opens an existing recent through the shared
///     load core, prunes a missing one) and the OpenDemo picker CTA. Pure store / view-model behaviour: no
///     UI thread, no real demo parse (the load core is stubbed with a capturing delegate).
///     <para>
///         Since the settings consolidation the recents persist to the <c>Recents</c> section of the single config
///         file, so the store's storage seam is a temp-dir <see cref="SettingsService" /> (never the real
///         config folder) rather than a standalone JSON path.
///     </para>
/// </summary>
[NotInParallel]
public class RecentFilesTests
{
    private static readonly Action<Action> _inline = a => a();

    // A throwaway config dir → the store persists to <dir>/settings.json's Recents section.
    private static string NewConfigDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvrec_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static RecentFilesStore NewStore(string configDir) => new(new SettingsService(configDir));

    private static string TempFilePath() =>
        Path.Combine(Path.GetTempPath(), "dv_recent_" + Guid.NewGuid().ToString("N") + ".json");

    private static DemoLibraryService NewLibrary() =>
        new(_inline, Path.Combine(Path.GetTempPath(), "dvreclib_" + Guid.NewGuid().ToString("N") + ".json"));

    // ── Store ────────────────────────────────────────────────────────────────

    [Test]
    public async Task RecordOpen_Persists_AndReloadsFromDisk()
    {
        string dir = NewConfigDir();
        try
        {
            RecentFilesStore store = NewStore(dir);
            store.RecordOpen("/demos/a.dem", "de_dust2");
            store.RecordOpen("/demos/b.dem", "de_mirage");

            // Recents land in the single consolidated config file, not a standalone recent-files.json.
            await Assert.That(File.Exists(Path.Combine(dir, "settings.json"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "recent-files.json"))).IsFalse();

            // A fresh store over the same config dir restores the list, most-recent-first, with maps intact.
            RecentFilesStore reloaded = NewStore(dir);
            await Assert.That(reloaded.Items.Count).IsEqualTo(2);
            await Assert.That(reloaded.Items[0].Path).IsEqualTo("/demos/b.dem");
            await Assert.That(reloaded.Items[0].MapName).IsEqualTo("de_mirage");
            await Assert.That(reloaded.Items[1].Path).IsEqualTo("/demos/a.dem");
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Test]
    public async Task RecordOpen_EnforcesCap_KeepingMostRecentN()
    {
        string dir = NewConfigDir();
        try
        {
            RecentFilesStore store = NewStore(dir);
            // Open more than the cap; each open is a distinct path.
            for (int i = 0; i < RecentFilesStore.MaxRecent + 3; i++)
            {
                store.RecordOpen($"/demos/d{i}.dem", null);
            }

            await Assert.That(store.Items.Count).IsEqualTo(RecentFilesStore.MaxRecent);
            // Most-recent-first: the last opened is at the front; the 3 oldest fell off the tail.
            int newest = RecentFilesStore.MaxRecent + 2;
            await Assert.That(store.Items[0].Path).IsEqualTo($"/demos/d{newest}.dem");
            await Assert.That(store.Items.Any(r => r.Path == "/demos/d0.dem")).IsFalse();
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Test]
    public async Task RecordOpen_DeDupesByPath_MovingToFront()
    {
        string dir = NewConfigDir();
        try
        {
            RecentFilesStore store = NewStore(dir);
            store.RecordOpen("/demos/a.dem", null);
            store.RecordOpen("/demos/b.dem", null);
            store.RecordOpen("/demos/a.dem", "de_nuke"); // re-open a → moves to front, no duplicate, map updated

            await Assert.That(store.Items.Count).IsEqualTo(2);
            await Assert.That(store.Items[0].Path).IsEqualTo("/demos/a.dem");
            await Assert.That(store.Items[0].MapName).IsEqualTo("de_nuke");
            await Assert.That(store.Items[1].Path).IsEqualTo("/demos/b.dem");

            // De-dup is case-insensitive (matches the library indexer's path keying).
            store.RecordOpen("/DEMOS/A.dem", null);
            await Assert.That(store.Items.Count).IsEqualTo(2);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Test]
    public async Task Remove_PrunesEntry_AndReportsWhetherPresent()
    {
        string dir = NewConfigDir();
        try
        {
            RecentFilesStore store = NewStore(dir);
            store.RecordOpen("/demos/a.dem", null);
            store.RecordOpen("/demos/b.dem", null);

            await Assert.That(store.Remove("/demos/a.dem")).IsTrue();
            await Assert.That(store.Items.Count).IsEqualTo(1);
            await Assert.That(store.Items[0].Path).IsEqualTo("/demos/b.dem");

            await Assert.That(store.Remove("/demos/missing.dem")).IsFalse(); // not present → no-op
            await Assert.That(store.Items.Count).IsEqualTo(1);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    // ── Library VM recents wiring ──────────────────────────────────────────────

    [Test]
    public async Task Vm_RecentFiles_ProjectExistence_AndLiveUpdate()
    {
        string dir = NewConfigDir();
        string realDemo = TempFilePath(); // any real file: OpenRecent only File.Exists-checks it
        File.WriteAllText(realDemo, "x");
        try
        {
            RecentFilesStore store = NewStore(dir);
            store.RecordOpen(realDemo, "de_dust2");
            store.RecordOpen("/demos/gone.dem", null); // never existed on disk

            using DemoLibraryService lib = NewLibrary();
            LibraryTabViewModel vm = NewVm(lib, store, _ => Task.CompletedTask);

            // Built at construction, most-recent-first, with Exists reflecting the filesystem.
            await Assert.That(vm.HasRecentFiles).IsTrue();
            await Assert.That(vm.RecentFiles.Count).IsEqualTo(2);
            await Assert.That(vm.RecentFiles[0].Path).IsEqualTo("/demos/gone.dem");
            await Assert.That(vm.RecentFiles[0].Exists).IsFalse();
            await Assert.That(vm.RecentFiles[1].Path).IsEqualTo(realDemo);
            await Assert.That(vm.RecentFiles[1].Exists).IsTrue();
            await Assert.That(vm.RecentFiles[1].FileName).IsEqualTo(Path.GetFileName(realDemo));

            // A later open live-updates the projection (store Changed → RefreshRecentFiles).
            store.RecordOpen("/demos/c.dem", null);
            await Assert.That(vm.RecentFiles.Count).IsEqualTo(3);
            await Assert.That(vm.RecentFiles[0].Path).IsEqualTo("/demos/c.dem");
        }
        finally
        {
            TryDeleteDir(dir);
            TryDelete(realDemo);
        }
    }

    [Test]
    public async Task Vm_OpenRecent_OpensExisting_ThroughLoadCore()
    {
        string dir = NewConfigDir();
        string realDemo = TempFilePath();
        File.WriteAllText(realDemo, "x");
        try
        {
            RecentFilesStore store = NewStore(dir);
            store.RecordOpen(realDemo, "de_dust2");

            string? openedPath = null;
            using DemoLibraryService lib = NewLibrary();
            LibraryTabViewModel vm = NewVm(lib, store, p =>
            {
                openedPath = p;
                return Task.CompletedTask;
            });

            await vm.OpenRecentCommand.ExecuteAsync(vm.RecentFiles[0]);

            // Routed to the shared load core with the path; the funnel owns the landing tab
            // (Match Overview: the Library no longer pre-switches tabs itself).
            await Assert.That(openedPath).IsEqualTo(realDemo);
        }
        finally
        {
            TryDeleteDir(dir);
            TryDelete(realDemo);
        }
    }

    [Test]
    public async Task Vm_OpenRecent_PrunesMissing_WithoutOpening()
    {
        string dir = NewConfigDir();
        try
        {
            RecentFilesStore store = NewStore(dir);
            store.RecordOpen("/demos/gone.dem", null);

            string? openedPath = null;
            using DemoLibraryService lib = NewLibrary();
            LibraryTabViewModel vm = NewVm(lib, store, p =>
            {
                openedPath = p;
                return Task.CompletedTask;
            });

            RecentFileItem missing = vm.RecentFiles[0];
            await Assert.That(missing.Exists).IsFalse();

            await vm.OpenRecentCommand.ExecuteAsync(missing);

            await Assert.That(openedPath).IsNull(); // never attempted a load
            await Assert.That(store.Items.Count).IsEqualTo(0); // pruned from the store
            await Assert.That(vm.RecentFiles.Count).IsEqualTo(0); // projection live-refreshed
            await Assert.That(vm.HasRecentFiles).IsFalse();
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Test]
    public async Task Vm_OpenDemo_InvokesSharedPicker()
    {
        string dir = NewConfigDir();
        try
        {
            RecentFilesStore store = NewStore(dir);
            bool pickerInvoked = false;
            using DemoLibraryService lib = NewLibrary();
            LibraryTabViewModel vm = new(
                lib,
                _ => Task.CompletedTask,
                () => Task.FromResult<IReadOnlyList<string>>([]),
                () =>
                {
                    pickerInvoked = true;
                    return Task.CompletedTask;
                },
                store);

            await vm.OpenDemoCommand.ExecuteAsync(null);

            await Assert.That(pickerInvoked).IsTrue(); // the CTA routes through the shell's shared file picker
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Test]
    public async Task Vm_OpenPath_OpensDemFile_ThroughLoadCore_AndIgnoresOthers()
    {
        string? openedPath = null;
        using DemoLibraryService lib = NewLibrary();
        LibraryTabViewModel vm = new(
            lib,
            p =>
            {
                openedPath = p;
                return Task.CompletedTask;
            },
            () => Task.FromResult<IReadOnlyList<string>>([]));

        // A dropped .dem routes to the SAME shared load core (the drag-drop path the view forwards here);
        // the funnel owns the landing tab.
        await vm.OpenPathCommand.ExecuteAsync("/demos/dropped.dem");
        await Assert.That(openedPath).IsEqualTo("/demos/dropped.dem");

        // A non-.dem drop is a no-op (never loads).
        openedPath = null;
        await vm.OpenPathCommand.ExecuteAsync("/demos/notes.txt");
        await Assert.That(openedPath).IsNull();

        // A null/blank path is a no-op too.
        await vm.OpenPathCommand.ExecuteAsync(null);
        await Assert.That(openedPath).IsNull();
    }

    private static LibraryTabViewModel NewVm(
        DemoLibraryService lib, RecentFilesStore store, Func<string, Task> openDemo) =>
        new(
            lib,
            openDemo,
            () => Task.FromResult<IReadOnlyList<string>>([]),
            () => Task.CompletedTask,
            store);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
