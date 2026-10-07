#region

using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A path attached to a content row because its fingerprint matched: it shows the row's data, carries no
///     hash for a store to join on, and a full read of it either confirms it or takes it out of the row.
/// </summary>
public class UnconfirmedLocationTests
{
    private const string Sha = "sha-x";
    private const string A = "/nfs/m.dem";
    private const string B = "/smb/m.dem";

    private static DemoCacheRecord Analysed(string path)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = 2000,
            Map = "de_nuke"
        };
        record.SetContentHash(Sha, new DemoContentFingerprint(1000, 64, "head", "tail"));
        record.Scoreboard = [new CachedStatRow { Slot = 1, Team = 3, Kills = 21 }];
        DemoCacheStore.StampParse(record);
        DemoCacheStore.StampAnalysis(record);
        return record;
    }

    private static DemoCacheStore Attached(string? root = null)
    {
        DemoCacheStore store = new(root);
        store.Upsert(Analysed(A));
        store.AttachUnconfirmed(Sha, B, 1000, 3000);
        return store;
    }

    [Test]
    public async Task AnAttachedPath_ShowsTheRowsData_ButNotItsHash()
    {
        DemoCacheStore store = Attached();

        DemoCacheIndexEntry? view = store.TryGetIndex(B);
        using (Assert.Multiple())
        {
            await Assert.That(view?.Map).IsEqualTo("de_nuke");
            await Assert.That(view?.ParseSchema ?? 0).IsGreaterThan(0);
            await Assert.That(view?.ModifiedTicks).IsEqualTo(3000);
            await Assert.That(view?.Sha256).IsNull();
            await Assert.That(view?.ContentFingerprint).IsNull();
            await Assert.That(store.LocationOf(B)).IsEqualTo((Sha, new DemoLocation(B, false, 1000, 3000, store.LocationOf(B)!.Value.Location.LastSeenUtcTicks)));
            await Assert.That(store.DemoKeyOf(B)).IsEqualTo(B);
            await Assert.That(store.SameDemo(A, B)).IsFalse();
            await Assert.That(store.TryGetByContentId(Sha)?.Path).IsEqualTo(A);
            await Assert.That(store.RowsForContentId(Sha).Select(r => r.Path)).IsEquivalentTo([A]);
            await Assert.That(store.TryGetPrimary(B)?.Path).IsEqualTo(B).Because("an unconfirmed path is not the primary's demo");
            await Assert.That(store.TryGetPrimary(B)?.Sha256).IsNull();
            await Assert.That(store.TryGetPrimary(A)?.Path).IsEqualTo(A);
            await Assert.That(store.Contents.Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Attach_RefusesAListedPath_AndAContentIdNoHashedRowCarries()
    {
        DemoCacheStore store = Attached();
        store.Upsert(new DemoCacheRecord { Path = "/nfs/other.dem", Size = 5, ModifiedTicks = 6 });

        using (Assert.Multiple())
        {
            await Assert.That(store.AttachUnconfirmed(Sha, B, 1000, 3000)).IsFalse();
            await Assert.That(store.AttachUnconfirmed(Sha, A, 1000, 2000)).IsFalse();
            await Assert.That(store.AttachUnconfirmed("sha-unknown", "/smb/x.dem", 1, 1)).IsFalse();
            await Assert.That(store.AttachUnconfirmed("p-" + DemoCacheStore.StableKey("/nfs/other.dem"), "/smb/y.dem", 5, 6))
                .IsFalse().Because("a row not hashed yet has no content to match");
        }
    }

    // The store never reads a demo: with no hash in hand, a write at an attached path goes to the row the
    // fingerprint matched, so neither the path nor the row's data is lost to a write.
    [Test]
    public async Task AWriteAtAnAttachedPathNothingHashed_GoesToTheRow_AndLeavesThePathUnconfirmed()
    {
        DemoCacheStore store = Attached();
        store.Remove(A);

        store.UpdateExisting(B, r => r.Map = "de_mirage");

        DemoCacheRecord? atB = store.TryLoadRecord(B);
        using (Assert.Multiple())
        {
            await Assert.That(store.LocationOf(B)).IsEqualTo((Sha, new DemoLocation(B, false, 1000, 3000, store.LocationOf(B)!.Value.Location.LastSeenUtcTicks)));
            await Assert.That(atB?.Map).IsEqualTo("de_mirage");
            await Assert.That(atB?.Scoreboard?.Count).IsEqualTo(1);
            await Assert.That(store.HoldsContent(Sha)).IsTrue();
            await Assert.That(store.IsOrphaned(Sha)).IsFalse();
        }
    }

    // A parse hashed the file before the path was attached: the write settles the path with that hash, without
    // reading the file, and keeps the row's data.
    [Test]
    public async Task AWriteAtTheLastAttachedPath_SettlesItWithAHashAParseTook()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-unconfirmed-{Guid.NewGuid():N}");
        try
        {
            const string b = "/gone/b.dem";
            DateTime written = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            long local = written.ToLocalTime().Ticks;
            DemoCacheStore store = new(root);
            store.Upsert(Analysed(A));
            store.NoteContentRead(b, Sha, null, 1000, written);
            store.AttachUnconfirmed(Sha, b, 1000, local);
            store.Remove(A);
            store.SaveIndex();

            store.UpdateExisting(b, r => r.Server = "from b");

            using (Assert.Multiple())
            {
                await Assert.That(store.LocationOf(b)?.ContentId).IsEqualTo(Sha);
                await Assert.That(store.LocationOf(b)?.Location.Confirmed).IsTrue();
                await Assert.That(store.TryLoadRecord(b)?.Scoreboard.Count).IsEqualTo(1);
                await Assert.That(store.TryLoadRecord(b)?.Server).IsEqualTo("from b");
                await Assert.That(File.Exists(Path.Combine(root, "demos", Sha + DemoCacheStore.RecordSuffix))).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // A parse found other bytes at the attached path: the path leaves the row for a row of its own bytes,
    // confirmed, so data kept per path there survives, and the next write starts from nothing of the old row.
    [Test]
    public async Task AReadOfOtherBytesAtAnAttachedPath_MovesItToARowOfItsOwnBytes()
    {
        DemoCacheStore store = Attached();
        DateTime written = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        store.NoteContentRead(B, "sha-other", null, 1000, written);
        store.UpdateExisting(B, r => r.Server = "from b");

        using (Assert.Multiple())
        {
            await Assert.That(store.LocationOf(B)?.ContentId).IsEqualTo("sha-other");
            await Assert.That(store.LocationOf(B)?.Location.Confirmed).IsTrue();
            await Assert.That(store.TryGetIndex(B)?.Sha256).IsEqualTo("sha-other");
            await Assert.That(store.TryLoadRecord(B)?.Scoreboard).IsEmpty();
            await Assert.That(store.TryLoadRecord(B)?.Server).IsEqualTo("from b");
            await Assert.That(store.TryLoadRecord(A)?.Scoreboard.Count).IsEqualTo(1);
            await Assert.That(store.TryGetByContentId(Sha)?.Locations.Select(l => l.Path)).IsEquivalentTo([A]);
        }
    }

    // A read of an orphan's bytes at a path that had other bytes: the path goes back to the orphan, with every tier.
    [Test]
    public async Task AReadOfAnOrphansBytes_TakesItBack()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Analysed(A));
        store.Detach(A);
        store.Upsert(new DemoCacheRecord { Path = B, Size = 1000, ModifiedTicks = 3000, Sha256 = "sha-old" });

        store.NoteContentRead(B, Sha, null, 1000, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        using (Assert.Multiple())
        {
            await Assert.That(store.IsOrphaned(Sha)).IsFalse();
            await Assert.That(store.TryGetByContentId(Sha)?.Path).IsEqualTo(B);
            await Assert.That(store.TryLoadRecord(B)?.Scoreboard.Count).IsEqualTo(1);
        }
    }

    // An open hashed a demo the library has not: its row takes the hash, so a store joined by hash reaches it.
    [Test]
    public async Task AReadOfAnUnhashedRowsFile_GivesTheRowItsHash()
    {
        DemoCacheStore store = new(null);
        DateTime written = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        store.Update(B, 1000, written.ToLocalTime().Ticks, DemoCacheStore.StampHeader);

        store.NoteContentRead(B, Sha, null, 1000, written);

        using (Assert.Multiple())
        {
            await Assert.That(store.TryGetByContentId(Sha)?.Path).IsEqualTo(B);
            await Assert.That(store.TryLoadRecord(B)?.Header.IsPresent).IsTrue();
            await Assert.That(store.RecentContentRead(B, 1000, written.ToLocalTime().Ticks)?.Sha256).IsEqualTo(Sha);
            await Assert.That(store.RecentContentRead(B, 1001, written.ToLocalTime().Ticks)).IsNull();
        }
    }

    // What an export keys a demo's ink by after its lease parsed it: no second read of the file.
    [Test]
    public async Task ContentIdOf_IsTheHashAReadTook_WhileTheFileIsUnchanged()
    {
        string file = Path.Combine(Path.GetTempPath(), $"dv-content-id-{Guid.NewGuid():N}.dem");
        try
        {
            File.WriteAllBytes(file, new byte[1000]);
            DemoCacheStore store = Attached();
            store.NoteContentRead(file, "sha-read", null, 1000, File.GetLastWriteTimeUtc(file));

            using (Assert.Multiple())
            {
                await Assert.That(store.ContentIdOf(file)).IsEqualTo("sha-read");
                await Assert.That(store.ContentIdOf(A)).IsEqualTo(Sha);
                await Assert.That(store.ContentIdOf(B)).IsNull().Because("only a fingerprint placed it");
            }

            File.WriteAllBytes(file, new byte[1001]);
            await Assert.That(store.ContentIdOf(file)).IsNull().Because("the file changed since it was read");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public async Task AnAgreeingHash_ConfirmsThePath()
    {
        DemoCacheStore store = Attached();

        bool kept = store.ConfirmLocation(B, Sha, 1000);
        store.Update(B, 1000, 3000, r => r.Server = "from b");

        using (Assert.Multiple())
        {
            await Assert.That(kept).IsTrue();
            await Assert.That(store.LocationOf(B)?.Location.Confirmed).IsTrue();
            await Assert.That(store.TryGetIndex(B)?.Sha256).IsEqualTo(Sha);
            await Assert.That(store.DemoKeyOf(B)).IsEqualTo(Sha);
            await Assert.That(store.RowsForContentId(Sha).Select(r => r.Path)).IsEquivalentTo([A, B]);
            await Assert.That(store.TryLoadRecord(B)?.Scoreboard?.Count).IsEqualTo(1);
            await Assert.That(store.TryLoadRecord(A)?.Server).IsEqualTo("from b");
        }
    }

    [Test]
    public async Task AContradictingHash_TakesThePathOut_AndItsNextRecordHoldsNothingOfTheRow()
    {
        DemoCacheStore store = Attached();

        bool kept = store.ConfirmLocation(B, "sha-other", 1000);
        store.Update(B, 1000, 3000, r =>
        {
            r.SetContentHash("sha-other", null);
            r.Map = "de_mirage";
            DemoCacheStore.StampParse(r);
        });

        DemoCacheRecord? atB = store.TryLoadRecord(B);
        using (Assert.Multiple())
        {
            await Assert.That(kept).IsFalse();
            await Assert.That(store.LocationOf(B)?.ContentId).IsEqualTo("sha-other");
            await Assert.That(atB?.Scoreboard).IsEmpty();
            await Assert.That(atB?.Analysis.IsPresent).IsFalse();
            await Assert.That(atB?.Map).IsEqualTo("de_mirage");
            await Assert.That(store.TryLoadRecord(A)?.Scoreboard?.Count).IsEqualTo(1);
            await Assert.That(store.TryGetByContentId(Sha)?.Locations.Select(l => l.Path)).IsEquivalentTo([A]);
        }
    }

    [Test]
    public async Task ASizeThatDisagrees_TakesThePathOutEvenWithTheSameHash()
    {
        DemoCacheStore store = Attached();

        await Assert.That(store.ConfirmLocation(B, Sha, 999)).IsFalse();
        await Assert.That(store.LocationOf(B)).IsNull();
    }

    [Test]
    public async Task AWriteCarryingTheContentId_DoesNotConfirmThePath()
    {
        DemoCacheStore store = Attached();
        DemoCacheRecord record = Analysed(B);
        record.ModifiedTicks = 3000;

        store.Upsert(record);

        await Assert.That(store.LocationOf(B)?.Location.Confirmed).IsFalse();
    }

    [Test]
    public async Task AnAttachedPath_StaysUnconfirmedAcrossASavedIndex()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-unconfirmed-{Guid.NewGuid():N}");
        try
        {
            Attached(root).SaveIndex();

            DemoCacheStore reopened = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(reopened.LocationOf(B)?.Location.Confirmed).IsFalse();
                await Assert.That(reopened.LocationOf(A)?.Location.Confirmed).IsTrue();
                await Assert.That(reopened.TryGetIndex(B)?.Map).IsEqualTo("de_nuke");
                await Assert.That(reopened.TryGetByContentId(Sha)?.Path).IsEqualTo(A);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task AFingerprintIsStoredOnlyThroughAConfirmedPath_OfARowWithoutOne()
    {
        DemoCacheStore store = new(null);
        DemoCacheRecord record = Analysed(A);
        record.ContentFingerprint = null;
        store.Upsert(record);
        store.AttachUnconfirmed(Sha, B, 1000, 3000);
        DemoContentFingerprint fingerprint = new(1000, 64, "h", "t");

        store.SetFingerprint(B, fingerprint);
        DemoContentFingerprint? afterUnconfirmed = store.KnownContents().Single().Fingerprint;
        store.SetFingerprint(A, fingerprint with { Size = 999 });
        DemoContentFingerprint? afterWrongSize = store.KnownContents().Single().Fingerprint;
        store.SetFingerprint(A, fingerprint);

        using (Assert.Multiple())
        {
            await Assert.That(afterUnconfirmed).IsNull();
            await Assert.That(afterWrongSize).IsNull();
            await Assert.That(store.KnownContents().Single().Fingerprint).IsEqualTo(fingerprint);
            await Assert.That(store.TryLoadRecord(A)?.ContentFingerprint).IsEqualTo(fingerprint);
            await Assert.That(store.LocationOf(B)?.Location.Confirmed).IsFalse();
        }
    }
}
