#region

using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Services.DemoCache;
using static DemoViewer.NET.AppTests.DemoLibraryScanTests;
using static DemoViewer.NET.AppTests.LibraryFingerprintTests;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A folder taken out of the library leaves its demos' analysis behind as orphans: the same folder back,
///     or the same bytes found in another one, takes it back without a parse, and only a sweep past the grace
///     period deletes it. Another folder stays registered throughout, since a scan with no reachable root
///     prunes nothing.
/// </summary>
[NotInParallel]
public class LibraryOrphanTests
{
    private const string Facts = ".facts.test";

    [Test]
    public async Task AFolderRemovedAndAddedBack_TakesItsDemoBack_WithoutAParse()
    {
        string keep = NewTempDir();
        string away = NewTempDir();
        try
        {
            Write(keep, "k.dem", Bytes(20));
            byte[] bytes = Bytes(21);
            string demo = Write(away, "m.dem", bytes);
            string sha = DemoContentHash.Compute(bytes);
            using Library library = new(keep, new DemoCacheStore(null));
            DemoLibraryService svc = library.Service;

            await svc.AddFoldersAsync([keep, away]);
            await svc.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => library.Store.TryGetByContentId(sha) is { ContentFingerprint: not null }
                                     && svc.Tier2Backlog().Count == 0 && svc.Entries.Count == 2,
                "both demos indexed");
            library.Store.WriteSibling(demo, Facts, "facts");

            await svc.RemoveFolderAsync(away);
            await WaitForAsync(() => svc.Entries.Count == 1, "the folder's card gone");
            using (Assert.Multiple())
            {
                await Assert.That(library.Store.TryGetIndex(demo)).IsNull();
                await Assert.That(library.Store.TryGetOrphan(sha)?.Tier).IsEqualTo(DemoCacheTier.Parse)
                    .Because("the prune detaches the path and keeps the parse");
            }

            await svc.AddFoldersAsync([away]);
            await WaitForAsync(() => svc.Entries.Count == 2 && svc.Entries.All(e => e.State == DemoIndexState.Indexed),
                "the folder's card back");
            await Task.Delay(150);

            using (Assert.Multiple())
            {
                await Assert.That(library.Parses.GetValueOrDefault(demo)).IsEqualTo(1).Because("nothing is parsed again");
                await Assert.That(library.Reader.FullReads(demo)).IsEqualTo(1);
                await Assert.That(library.Store.TryGetOrphan(sha)).IsNull();
                await Assert.That(library.Store.LocationOf(demo)?.Location.Confirmed).IsTrue();
                await Assert.That(library.Store.TryReadSibling(demo, Facts)).IsEqualTo("facts");
                await Assert.That(svc.Entries.Single(e => e.FilePath == demo).MapName).IsEqualTo("de_test");
            }
        }
        finally
        {
            Cleanup(keep);
            Cleanup(away);
        }
    }

    [Test]
    public async Task TheBytesInANewFolder_TakeTheOrphanBackByFingerprint_WithoutAFullReadOrAParse()
    {
        string keep = NewTempDir();
        string old = NewTempDir();
        string moved = NewTempDir();
        try
        {
            Write(keep, "k.dem", Bytes(30));
            byte[] bytes = Bytes(31);
            string before = Write(old, "m.dem", bytes);
            string after = Write(moved, "renamed.dem", bytes);
            string sha = DemoContentHash.Compute(bytes);
            using Library library = new(keep, new DemoCacheStore(null));
            DemoLibraryService svc = library.Service;

            await svc.AddFoldersAsync([keep, old]);
            await svc.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => library.Store.TryGetByContentId(sha) is { ContentFingerprint: not null }
                                     && svc.Tier2Backlog().Count == 0 && svc.Entries.Count == 2,
                "both demos indexed");
            await svc.RemoveFolderAsync(old);
            await WaitForAsync(() => svc.Entries.Count == 1, "the old folder's card gone");
            await Assert.That(library.Store.TryGetOrphan(sha)).IsNotNull();

            await svc.AddFoldersAsync([moved]);
            await svc.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => svc.Entries.Count == 2 && svc.Entries.All(e => e.State == DemoIndexState.Indexed),
                "the moved demo's card");
            await Task.Delay(150);

            using (Assert.Multiple())
            {
                await Assert.That(library.Parses.GetValueOrDefault(after)).IsEqualTo(0);
                await Assert.That(library.Parses.GetValueOrDefault(before)).IsEqualTo(1);
                await Assert.That(library.Reader.FullReads(after)).IsEqualTo(0);
                await Assert.That(library.Store.LocationOf(after)?.ContentId).IsEqualTo(sha);
                await Assert.That(library.Store.TryGetOrphan(sha)).IsNull();
            }
        }
        finally
        {
            Cleanup(keep);
            Cleanup(old);
            Cleanup(moved);
        }
    }

    [Test]
    public async Task AnOrphanSweptPastTheGracePeriod_IsGone_AndTheFolderBackParsesItAgain()
    {
        string keep = NewTempDir();
        string away = NewTempDir();
        try
        {
            Write(keep, "k.dem", Bytes(40));
            byte[] bytes = Bytes(41);
            string demo = Write(away, "m.dem", bytes);
            string sha = DemoContentHash.Compute(bytes);
            using Library library = new(keep, new DemoCacheStore(null));
            DemoLibraryService svc = library.Service;

            await svc.AddFoldersAsync([keep, away]);
            await svc.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => library.Store.TryGetByContentId(sha) is not null && svc.Tier2Backlog().Count == 0
                                                                                     && svc.Entries.Count == 2,
                "both demos indexed");
            await svc.RemoveFolderAsync(away);
            await WaitForAsync(() => svc.Entries.Count == 1, "the folder's card gone");

            TimeSpan grace = TimeSpan.FromDays(14);
            await Assert.That(library.Store.ExpireOrphans(DateTime.UtcNow, grace)).IsEqualTo(0);
            await Assert.That(library.Store.ExpireOrphans(DateTime.UtcNow + grace + TimeSpan.FromMinutes(1), grace)).IsEqualTo(1);
            await Assert.That(library.Store.HoldsContent(sha)).IsFalse();

            await svc.AddFoldersAsync([away]);
            await WaitForAsync(() => library.Parses.GetValueOrDefault(demo) == 2 && svc.Tier2Backlog().Count == 0,
                "the demo parsed again");
            await Assert.That(library.Store.TryGetByContentId(sha)).IsNotNull();
        }
        finally
        {
            Cleanup(keep);
            Cleanup(away);
        }
    }
}
