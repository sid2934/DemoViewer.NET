#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.ErrorReports;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The user's analysis-error report: one per demo, kept across edits, carried onto a row a second path
///     of the same bytes joins, and never written over a report a newer app left behind. The report is the
///     only thing in the cache a user typed, so the gates here are the ones that would cost them their words.
/// </summary>
public class ErrorReportStoreTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "dv-error-report");
    private static readonly DateTime First = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 10, 28, 14, 30, 0, DateTimeKind.Utc);

    private static DemoCacheRecord Record(string path, string sha = "sha-report") => new()
    {
        Path = path,
        Size = 100,
        ModifiedTicks = First.Ticks,
        Sha256 = sha
    };

    private static DemoErrorReportSnapshot Snapshot(string path) => new()
    {
        CapturedAtUtcTicks = First.Ticks,
        DemoPath = path
    };

    [Test]
    public async Task ADemoWithNoReport_HasNone()
    {
        DemoCacheStore store = new(null);
        string demo = Path.Combine(Dir, "none.dem");
        store.Upsert(Record(demo));

        using (Assert.Multiple())
        {
            await Assert.That(DemoErrorReportStore.TryLoad(store, demo)).IsNull();
            await Assert.That(DemoErrorReportStore.Exists(store, demo)).IsFalse();
        }
    }

    [Test]
    public async Task ASavedReport_ReadsBackWholeAndCountsAsExisting()
    {
        DemoCacheStore store = new(null);
        string demo = Path.Combine(Dir, "saved.dem");
        store.Upsert(Record(demo));

        DemoErrorReportStore.Save(store, demo, "Round 14 kill is attributed to the wrong player.",
            Snapshot(demo), First);

        DemoErrorReport report = DemoErrorReportStore.TryLoad(store, demo)!;
        using (Assert.Multiple())
        {
            await Assert.That(report.Notes).IsEqualTo("Round 14 kill is attributed to the wrong player.");
            await Assert.That(report.CreatedUtcTicks).IsEqualTo(First.Ticks);
            await Assert.That(report.UpdatedUtcTicks).IsEqualTo(First.Ticks);
            await Assert.That(report.Snapshot.DemoPath).IsEqualTo(demo);
            await Assert.That(report.SubmittedUtcTicks).IsNull();
            await Assert.That(DemoErrorReportStore.Exists(store, demo)).IsTrue();
        }
    }

    [Test]
    public async Task EditingAReport_KeepsItsCreatedDate_AndMovesOnlyTheUpdatedOne()
    {
        DemoCacheStore store = new(null);
        string demo = Path.Combine(Dir, "edited.dem");
        store.Upsert(Record(demo));

        DemoErrorReportStore.Save(store, demo, "first pass", Snapshot(demo), First);
        DemoErrorReportStore.Save(store, demo, "second pass, with more detail", Snapshot(demo), Later);

        DemoErrorReport report = DemoErrorReportStore.TryLoad(store, demo)!;
        using (Assert.Multiple())
        {
            await Assert.That(report.Notes).IsEqualTo("second pass, with more detail");
            await Assert.That(report.CreatedUtcTicks).IsEqualTo(First.Ticks)
                .Because("an edit is the same report, so re-dating it would lose when the user first hit this");
            await Assert.That(report.UpdatedUtcTicks).IsEqualTo(Later.Ticks);
        }
    }

    [Test]
    public async Task SavingAgainstAnUncachedDemo_CreatesTheRecordRatherThanDroppingTheNotes()
    {
        DemoCacheStore store = new(null);
        string demo = Path.Combine(Dir, "uncached.dem");

        DemoErrorReportStore.Save(store, demo, "the demo was never indexed", Snapshot(demo), First);

        await Assert.That(DemoErrorReportStore.TryLoad(store, demo)?.Notes)
            .IsEqualTo("the demo was never indexed")
            .Because("a write that silently dropped the user's words is the one failure this store cannot have");
    }

    [Test]
    public async Task AReportWrittenByANewerApp_IsNeitherShownNorOverwritten()
    {
        DemoCacheStore store = new(null);
        string demo = Path.Combine(Dir, "newer.dem");
        DemoCacheRecord record = Record(demo);
        record.ErrorReport = new DemoErrorReport
        {
            Schema = DemoErrorReport.CurrentSchema + 1,
            Notes = "written by a later build",
            CreatedUtcTicks = First.Ticks,
            UpdatedUtcTicks = First.Ticks
        };
        store.Upsert(record);

        DemoErrorReport? read = DemoErrorReportStore.TryLoad(store, demo);
        DemoErrorReportStore.Save(store, demo, "this app's attempt", Snapshot(demo), Later);

        using (Assert.Multiple())
        {
            await Assert.That(read).IsNull()
                .Because("a payload this app cannot read is not ours to show");
            await Assert.That(store.TryLoadRecord(demo, false)!.ErrorReport!.Notes)
                .IsEqualTo("written by a later build")
                .Because("nor ours to overwrite: the newer app's user still has those words");
        }
    }

    [Test]
    public async Task ASecondPathOfTheSameBytes_CarriesTheReportOnto()
    {
        DemoCacheStore store = new(null);
        string first = Path.Combine(Dir, "original.dem");
        string copy = Path.Combine(Dir, "copy.dem");
        store.Upsert(Record(first));
        DemoErrorReportStore.Save(store, first, "the round boundary is one tick early", Snapshot(first), First);

        // The same bytes at another path: the copy joins the row and must not land on a blank report.
        store.Upsert(Record(copy));

        await Assert.That(DemoErrorReportStore.TryLoad(store, copy)?.Notes)
            .IsEqualTo("the round boundary is one tick early")
            .Because("the report follows the demo's content key, which is what makes it survive a move");
    }

    /// <summary>
    ///     The report is the one thing on the record a pass did not produce, so every pass that writes the
    ///     record has to leave it alone. Covers both shapes production uses: a field write through
    ///     <see cref="DemoCacheStore.UpdateExisting" />, and the reprojection sites that load a record and
    ///     Upsert it back. Upsert only merges through <c>FillMissingFrom</c> when a path JOINS another row,
    ///     so a same-key Upsert of a record that had not been loaded first would drop the report outright.
    /// </summary>
    [Test]
    public async Task NoTierFill_ClearsTheUsersReport()
    {
        DemoCacheStore store = new(null);
        string demo = Path.Combine(Dir, "tierfill.dem");
        store.Upsert(Record(demo, "sha-tierfill"));
        DemoErrorReportStore.Save(store, demo, "deaths read zero for every player", Snapshot(demo), First);

        store.UpdateExisting(demo, r =>
        {
            r.AnalysisState = DemoAnalysisState.Indexed;
            r.ConfigFingerprint = "fp-tierfill";
            DemoCacheStore.StampAnalysis(r);
        });

        string? afterUpdate = DemoErrorReportStore.TryLoad(store, demo)?.Notes;

        // The reprojection shape: RoundFactsEvaluator.ReprojectRow and RoundFactsSource.TryGet both load the
        // record and hand that same instance back to Upsert.
        store.Upsert(store.TryLoadRecord(demo)!);

        using (Assert.Multiple())
        {
            await Assert.That(afterUpdate).IsEqualTo("deaths read zero for every player")
                .Because("filling the analysis tier must not cost the user their words");
            await Assert.That(DemoErrorReportStore.TryLoad(store, demo)?.Notes)
                .IsEqualTo("deaths read zero for every player")
                .Because("a reprojected row keeps the report, which is why those sites load before they write");
        }
    }

    /// <summary>
    ///     The same save against a real cache root rather than the in-memory store, read back through a
    ///     second store over the same directory: this is the only gate on the report actually surviving
    ///     serialisation to the sidecar and back.
    /// </summary>
    [Test]
    public async Task ASavedReport_RoundTripsThroughTheSidecarOnDisk()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-error-report-disk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string demo = Path.Combine(root, "ondisk.dem");
            DemoCacheStore store = new(root);
            store.Upsert(Record(demo, "sha-ondisk"));
            DemoErrorReportStore.Save(store, demo, "round 18 never closes", Snapshot(demo), First);

            DemoErrorReport? read = DemoErrorReportStore.TryLoad(new DemoCacheStore(root), demo);

            using (Assert.Multiple())
            {
                await Assert.That(read).IsNotNull();
                await Assert.That(read!.Notes).IsEqualTo("round 18 never closes");
                await Assert.That(read.CreatedUtcTicks).IsEqualTo(First.Ticks);
                await Assert.That(read.UpdatedUtcTicks).IsEqualTo(First.Ticks);
                await Assert.That(read.Snapshot?.DemoPath).IsEqualTo(demo)
                    .Because("the snapshot is the half that makes a report reproducible");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Test]
    public async Task DeletingAReport_LeavesTheRestOfTheRecordAlone()
    {
        DemoCacheStore store = new(null);
        string demo = Path.Combine(Dir, "deleted.dem");
        DemoCacheRecord record = Record(demo);
        record.Map = "de_mirage";
        store.Upsert(record);
        DemoErrorReportStore.Save(store, demo, "never mind", Snapshot(demo), First);

        DemoErrorReportStore.Delete(store, demo);

        using (Assert.Multiple())
        {
            await Assert.That(DemoErrorReportStore.TryLoad(store, demo)).IsNull();
            await Assert.That(store.TryLoadRecord(demo, false)!.Map).IsEqualTo("de_mirage");
        }
    }
}
