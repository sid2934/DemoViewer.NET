#region

using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Confirms the shared demo-load core extracted from <c>OpenFileAsync</c>: loading via the new
///     path-based entry point (<see cref="MainViewModel.LoadDemoFromPathAsync" />), the demo-library
///     browser's open path, fully populates the shell (frames, file flag, map). The picker's OpenFileAsync
///     routes through the same core, so this also guards that refactor.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ShellLoadPathTests
{
    [Test]
    public async Task LoadDemoFromPath_PopulatesShell()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(null, new ModuleRegistry(),
                new DemoLibraryService(null,
                    Path.Combine(Path.GetTempPath(), "dvlib_test_" + Guid.NewGuid().ToString("N") + ".json")));
            await vm.LoadDemoFromPathAsync(demo);

            await Assert.That(vm.HasFile).IsTrue();
            await Assert.That(vm.Frames.Count).IsGreaterThan(0);
        });
    }

    [Test]
    public async Task LoadDemoFromPath_MissingFile_DoesNotThrow_AndReportsStatus()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(null, new ModuleRegistry(),
                new DemoLibraryService(null,
                    Path.Combine(Path.GetTempPath(), "dvlib_test_" + Guid.NewGuid().ToString("N") + ".json")));
            await vm.LoadDemoFromPathAsync("/no/such/demo-file-xyz.dem");

            await Assert.That(vm.HasFile).IsFalse();
            await Assert.That(vm.StatusText).Contains("not found");
        });
    }
}
