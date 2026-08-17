#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Setup;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Covers the first-run wizard's CS2-demos-folder detection (the injected lookup), over a temp-dir
///     <see cref="SettingsService" />: the suggestion surfaces only when a folder is detected, one click adds
///     it, the added/addable state tracks the folder list, Finish persists it, and — when nothing is found —
///     the not-found notice surfaces the searched Steam libraries (or the no-Steam message). Pure-VM (no
///     headless session) so it runs in parallel.
/// </summary>
public class FirstRunWizardCs2DetectionTests
{
    private const string Demos =
        @"C:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\replays";

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvwiz_cs2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Func<Cs2DemosLookup> Found(string path) => () => new Cs2DemosLookup(path, []);

    private static Func<Cs2DemosLookup> NotFound(params string[] searched) =>
        () => new Cs2DemosLookup(null, searched);

    [Test]
    public async Task DetectedFolder_AddsOnce_AndTracksState()
    {
        string dir = NewTempDir();
        try
        {
            FirstRunWizardViewModel vm = new(new SettingsService(dir), Found(Demos));

            await Assert.That(vm.HasDetectedDemosFolder).IsTrue();
            await Assert.That(vm.DetectedDemosFolder).IsEqualTo(Demos);
            await Assert.That(vm.ShowNotFoundNotice).IsFalse();
            await Assert.That(vm.CanAddDetectedFolder).IsTrue().Because("not yet added");
            await Assert.That(vm.IsDetectedFolderAdded).IsFalse();

            vm.AddDetectedFolderCommand.Execute(null);

            await Assert.That(vm.Folders).Contains(Demos);
            await Assert.That(vm.IsDetectedFolderAdded).IsTrue();
            await Assert.That(vm.CanAddDetectedFolder).IsFalse().Because("already added — button hides");

            // Idempotent: a second add does not duplicate.
            vm.AddDetectedFolderCommand.Execute(null);
            await Assert.That(vm.Folders.Count(f => f == Demos)).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task DetectedFolder_AddedThenFinished_PersistsToLibraryFolders()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            FirstRunWizardViewModel vm = new(svc, Found(Demos));

            vm.AddDetectedFolderCommand.Execute(null);
            vm.FinishCommand.Execute(null);

            await Assert.That(svc.Current.Library.Folders).Contains(Demos);
            await Assert.That(svc.Current.FirstRunCompleted).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task AlreadyConfiguredFolder_ShowsAsAdded_NotAddable()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            svc.Write(s => s.Library.Folders = [Demos]); // a re-run where the folder is already configured

            FirstRunWizardViewModel vm = new(svc, Found(Demos));

            await Assert.That(vm.HasDetectedDemosFolder).IsTrue();
            await Assert.That(vm.IsDetectedFolderAdded).IsTrue().Because("seeded from existing config");
            await Assert.That(vm.CanAddDetectedFolder).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task NotFound_WithSearchedLibraries_ShowsNoticeListingThem()
    {
        string dir = NewTempDir();
        try
        {
            FirstRunWizardViewModel vm = new(
                new SettingsService(dir), NotFound(@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"));

            await Assert.That(vm.HasDetectedDemosFolder).IsFalse();
            await Assert.That(vm.ShowNotFoundNotice).IsTrue();
            await Assert.That(vm.HasSearchedDirectories).IsTrue();
            await Assert.That(vm.SearchedDirectories).Contains(@"D:\SteamLibrary");
            await Assert.That(vm.NotFoundMessage).Contains("Searched");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task NotFound_NoSteamInstall_ShowsNoSteamNotice()
    {
        string dir = NewTempDir();
        try
        {
            FirstRunWizardViewModel vm = new(new SettingsService(dir), NotFound());

            await Assert.That(vm.ShowNotFoundNotice).IsTrue();
            await Assert.That(vm.HasSearchedDirectories).IsFalse();
            await Assert.That(vm.NotFoundMessage).Contains("no Steam installation");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
