#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Verifies the demo-library tab is wired into the shell as the landing surface: it registers first and
///     is selected on startup, and opening a demo through its VM loads the demo (shared load core) and
///     switches to the Parser tab.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class LibraryShellTests
{
    // An empty, temp-path library service so shell construction never scans the developer's real demo folders.
    private static DemoLibraryService TempLibrary() =>
        new(null, Path.Combine(Path.GetTempPath(), "dvlib_test_" + Guid.NewGuid().ToString("N") + ".json"));

    [Test]
    public async Task LibraryTab_IsFirst_AndSelectedOnStartup()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(null, new ModuleRegistry(), TempLibrary());

            await Assert.That(vm.Tabs[0].TabId).IsEqualTo("builtin.library");
            await Assert.That(vm.SelectedTab?.TabId).IsEqualTo("builtin.library");
        });
    }

    [Test]
    public async Task OpenDemo_FromLibrary_LoadsAndLandsOnMatchOverview()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(null, new ModuleRegistry(), TempLibrary());

            DemoEntry entry = new()
            {
                FilePath = demo,
                FileName = Path.GetFileName(demo),
                Directory = Path.GetDirectoryName(demo) ?? "",
                FileSizeBytes = new FileInfo(demo).Length,
                Modified = File.GetLastWriteTime(demo)
            };

            // P3.2a renamed the card-open command OpenDemoCommand → OpenEntryCommand (OpenDemoCommand is
            // now the file-picker CTA). Semantics unchanged: this still opens the given entry.
            await vm.LibraryTab.OpenEntryCommand.ExecuteAsync(entry);

            // Opening a demo now lands on the Match Overview landing page (the responsive demo-opening surface),
            // which the shared load funnel switches to at the start of every open.
            await Assert.That(vm.SelectedTab?.TabId).IsEqualTo("builtin.matchoverview");
            await Assert.That(vm.HasFile).IsTrue();
            await Assert.That(vm.Frames.Count).IsGreaterThan(0);
            await Assert.That(vm.MatchOverviewTab.HasSummary).IsTrue()
                .Because("the overview summary is filled once the demo is parsed");
        });
    }

    // P3.2a: a real open through the shell's single funnel (LoadDemoFromBytesAsync) records exactly one
    // recent with the demo's path + parsed map. This exercises the actual RecordOpen call in the shared load
    // core (the VM/store unit tests stub the load core, so this is the only end-to-end coverage of it).
    [Test]
    public async Task OpenDemo_ThroughShell_RecordsRecentFile()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            // recents persist to the single config file, so the seam is a throwaway config dir.
            RecentFilesStore store = new(new SettingsService(
                Path.Combine(Path.GetTempPath(), "dv_recent_shell_" + Guid.NewGuid().ToString("N"))));
            MainViewModel vm = new(null, new ModuleRegistry(), TempLibrary(), null, null, store);

            await vm.LoadDemoFromPathAsync(demo); // the real user-facing open funnel

            await Assert.That(vm.HasFile).IsTrue();
            await Assert.That(store.Items.Count).IsEqualTo(1);
            await Assert.That(store.Items[0].Path).IsEqualTo(demo);
            await Assert.That(store.Items[0].MapName).IsNotNull(); // a real demo → map known at open time
            // The Library tab's live projection reflects the recorded open.
            await Assert.That(vm.LibraryTab.RecentFiles.Count).IsEqualTo(1);
            await Assert.That(vm.LibraryTab.RecentFiles[0].Path).IsEqualTo(demo);
        });
    }
}
