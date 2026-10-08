#region

using DemoViewer.NET.Modules;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Match Overview showing a cached demo re-renders when that demo's record is written through another of
///     its paths, and still ignores a write to another demo.
/// </summary>
[NotInParallel]
public class MatchOverviewContentIdentityTests
{
    private static DemoCacheRecord Parsed(string path, string? sha, int ctScore) => new()
    {
        Path = path,
        Size = 10,
        ModifiedTicks = 20,
        Sha256 = sha,
        Map = "de_dust2",
        Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 },
        CtScore = ctScore,
        TScore = 9
    };

    [Test]
    public async Task AWriteThroughAnotherPathOfTheShownDemo_RefreshesThePage_AndAnotherDemosWriteDoesNot()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            DemoCacheStore store = new(null);
            store.Upsert(Parsed("/nfs/a.dem", "sha-a", 13));
            store.Upsert(Parsed("/smb/a.dem", "sha-a", 13));
            store.Upsert(Parsed("/nfs/b.dem", "sha-b", 7));
            MainViewModel vm = new(null, new ModuleRegistry(), TestLibraries.Empty(), demoCache: store);
            try
            {
                vm.MatchOverviewTab.SetCachedRecord(store.TryLoadRecord("/smb/a.dem")!);
                await Assert.That(vm.MatchOverviewTab.CtTeamScoreDisplay).IsEqualTo("13");

                store.UpdateExisting("/nfs/a.dem", r => r.CtScore = 16);
                await Assert.That(vm.MatchOverviewTab.CtTeamScoreDisplay).IsEqualTo("16");
                await Assert.That(vm.MatchOverviewTab.SubjectKey).IsEqualTo("/smb/a.dem")
                    .Because("the page stays on the path it was shown by");

                store.UpdateExisting("/nfs/b.dem", r => r.CtScore = 1);
                await Assert.That(vm.MatchOverviewTab.CtTeamScoreDisplay).IsEqualTo("16");
            }
            finally
            {
                vm.Dispose();
            }
        });
    }
}
