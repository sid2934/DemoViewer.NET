#region

using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Services.Strats;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Revisions after a process that never shut down: index.json is written only at shutdown, so a killed
///     Debug run leaves its rows behind the files. Revisions stay strictly increasing, a working copy stamped
///     from a stale row is moved onto the log's head or set aside with a note, never dropped.
/// </summary>
[NotInParallel]
public class StratRevisionTests
{
    private static readonly int[] ThreeRevisions = [1, 2, 3];
    private static readonly int[] FourRevisions = [1, 2, 3, 4];
    private static readonly string[] NameOnly = ["/name"];
    private static readonly double[] BothCopies = [65.0, 70.0];

    private static readonly DateTime Past = DateTime.UtcNow.AddHours(-2);

    private static StratSession Session(StratStore store) =>
        new(store, null, () => false, () => Created)
        {
            AutoSaveDelay = TimeSpan.FromHours(1),
            IdleCommitDelay = TimeSpan.FromHours(1)
        };

    private static string StratFile(string root, StratDocument document) =>
        Path.Combine(StratStore.FolderFor(root, document.Owner, document.Map), document.Id + StratStore.StratExtension);

    private static string LogFile(string root, StratDocument document) =>
        Path.Combine(StratStore.FolderFor(root, document.Owner, document.Map), document.Id + StratStore.HistoryExtension);

    private static PatchOp MoveTime(int step, double to) =>
        PatchOp.ReplaceOp($"/steps/{step}/atSeconds", null, JsonValue.Create(to));

    private static void Commit(StratStore store, Guid id, PatchOp op)
    {
        StratDocument before = store.TryLoad(id)!;
        op.From = StratHistory.ValueAt(StratHistory.ToNode(before), op.Path)?.DeepClone();
        StratSaveResult saved = store.Save(StratHistory.Apply(before, [op]), [op], null);
        if (!saved.Saved)
        {
            throw new InvalidOperationException(saved.Reason);
        }
    }

    // Revision 1 in index.json, revisions 2 and 3 committed by a process that was then killed, and both files
    // stamped no newer than the row, so no startup check sees the difference.
    private static StratDocument KilledAfterTwoCommits(string root)
    {
        StratStore store = new(root, utcNow: SteppingClock(Past));
        StratDocument document = Minimal();
        store.Save(document, [], "created");
        store.SaveIndex();
        DateTime rowStamp = store.Index.Single().ModifiedUtc;

        Commit(store, document.Id, MoveTime(0, 88));
        Commit(store, document.Id, MoveTime(1, 80));

        File.SetLastWriteTimeUtc(LogFile(root, document), rowStamp);
        File.SetLastWriteTimeUtc(StratFile(root, document), rowStamp);
        return document;
    }

    private static int[] Revisions(StratStore store, Guid id) => [.. store.History(id).Select(e => e.Revision)];

    [Test]
    public async Task ACommitAfterAKilledProcess_NeverReusesARevision()
    {
        string root = TempRoot();
        try
        {
            StratDocument document = KilledAfterTwoCommits(root);
            StratStore restarted = new(root, utcNow: SteppingClock(Past.AddHours(1)));
            await Assert.That(restarted.Index.Single().Revision).IsEqualTo(1).Because("the stale row is trusted at start");

            using StratSession session = Session(restarted);
            session.Open(document.Id);
            await Assert.That(session.Document!.Revision).IsEqualTo(3);
            session.Apply(PatchOp.ReplaceOp("/name", null, JsonValue.Create("after the restart")));
            StratSaveResult result = session.Commit()!;

            using (Assert.Multiple())
            {
                await Assert.That(result.Revision).IsEqualTo(4);
                await Assert.That(session.Document.Revision).IsEqualTo(4);
                await Assert.That(Revisions(restarted, document.Id)).IsEquivalentTo(FourRevisions);
                await Assert.That(Revisions(restarted, document.Id).Zip(Revisions(restarted, document.Id).Skip(1)).All(p => p.First < p.Second))
                    .IsTrue();
                await Assert.That(restarted.Index.Single().Revision).IsEqualTo(4);
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task Save_TakesTheLogsHighestRevision_OverAStaleRowAndDocument()
    {
        string root = TempRoot();
        try
        {
            StratDocument document = KilledAfterTwoCommits(root);
            StratStore restarted = new(root, utcNow: SteppingClock(Past.AddHours(1)));

            // No Load: the row and the document both say revision 1, only the log knows better.
            StratDocument stale = restarted.Materialize(document.Id, 3)!;
            stale.Revision = 1;
            PatchOp op = PatchOp.ReplaceOp("/name", JsonValue.Create(stale.Name), JsonValue.Create("renamed"));
            StratSaveResult result = restarted.Save(StratHistory.Apply(stale, [op]), [op], null);

            using (Assert.Multiple())
            {
                await Assert.That(result.Revision).IsEqualTo(4);
                await Assert.That(Revisions(restarted, document.Id)).IsEquivalentTo(FourRevisions);
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task AFileWrittenAfterItsRow_IsReReadAtStart()
    {
        string root = TempRoot();
        try
        {
            StratStore store = new(root, utcNow: SteppingClock(Past));
            StratDocument document = Minimal();
            store.Save(document, [], "created");
            store.SaveIndex();
            Commit(store, document.Id, MoveTime(0, 88));
            Commit(store, document.Id, MoveTime(1, 80));

            // The commits land minutes after the row's stamp, and index.json is never written again.
            File.SetLastWriteTimeUtc(StratFile(root, document), Past.AddMinutes(30));
            File.SetLastWriteTimeUtc(LogFile(root, document), Past.AddMinutes(30));

            await Assert.That(new StratStore(root).Index.Single().Revision).IsEqualTo(3);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task AWorkingCopyStampedFromAStaleRow_IsSetAside_NotDropped()
    {
        string root = TempRoot();
        try
        {
            StratDocument document = KilledAfterTwoCommits(root);

            // Revision 3's content plus one edit, stamped with the stale row's revision 1, then a crash. Its
            // diff from revision 1 carries revisions 2 and 3, whose targets the head no longer holds.
            StratStore probe = new(root, utcNow: SteppingClock(Past.AddHours(1)));
            StratDocument working = probe.Materialize(document.Id, 3)!;
            working.Name = "edited before the crash";
            working.Revision = 1;
            File.WriteAllText(StratFile(root, document), StratStore.Serialize(StratStore.WithPending(working, true)));

            StratStore restarted = new(root, utcNow: SteppingClock(Past.AddHours(1)));
            using StratSession session = Session(restarted);
            session.Open(document.Id);

            string[] kept = SetAsideFiles(root, document);
            using (Assert.Multiple())
            {
                await Assert.That(session.Document!.Revision).IsEqualTo(3);
                await Assert.That(session.Document.Steps[1].AtSeconds).IsEqualTo(80);
                await Assert.That(session.HasPending).IsFalse();
                await Assert.That(session.StatusText).Contains("conflict with revision 3");
                await Assert.That(kept.Length).IsEqualTo(1);
                await Assert.That(JsonNode.Parse(File.ReadAllText(kept[0]))!["name"]!.GetValue<string>()).IsEqualTo("edited before the crash");
                await Assert.That(Revisions(restarted, document.Id)).IsEquivalentTo(ThreeRevisions);
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task AWorkingCopyWithEditsTheHeadDidNotTouch_IsMovedOntoTheHead()
    {
        string root = TempRoot();
        try
        {
            StratDocument document = KilledAfterTwoCommits(root);

            // Revision 1 plus a rename; revisions 2 and 3 only moved steps.
            StratDocument working = new StratStore(root).Materialize(document.Id, 1)!;
            working.Name = "renamed at revision 1";
            File.WriteAllText(StratFile(root, document), StratStore.Serialize(StratStore.WithPending(working, true)));

            StratStore restarted = new(root, utcNow: SteppingClock(Past.AddHours(1)));
            using StratSession session = Session(restarted);
            session.Open(document.Id);

            using (Assert.Multiple())
            {
                await Assert.That(session.Document!.Revision).IsEqualTo(3);
                await Assert.That(session.Document.Name).IsEqualTo("renamed at revision 1");
                await Assert.That(session.Document.Steps[0].AtSeconds).IsEqualTo(88);
                await Assert.That(session.Document.Steps[1].AtSeconds).IsEqualTo(80);
                await Assert.That(session.PendingOps.Select(o => o.Path)).IsEquivalentTo(NameOnly);
                await Assert.That(session.StatusText).Contains("recovered edits from revision 1 were moved onto revision 3");
            }

            await Assert.That(session.Commit()!.Revision).IsEqualTo(4);
            await Assert.That(restarted.History(document.Id)[^1].Ops.Select(o => o.Path)).IsEquivalentTo(NameOnly);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    // Revision 1 plus its own move of step 0, which revision 2 moved somewhere else.
    private static void WriteConflictingWorkingCopy(string root, StratDocument document, double stepZero)
    {
        StratDocument working = new StratStore(root).Materialize(document.Id, 1)!;
        working.Steps[0].AtSeconds = stepZero;
        File.WriteAllText(StratFile(root, document), StratStore.Serialize(StratStore.WithPending(working, true)));
    }

    private static string[] SetAsideFiles(string root, StratDocument document) =>
    [
        .. Directory.GetFiles(Path.GetDirectoryName(StratFile(root, document))!, document.Id + ".working-r1*.json").Order(StringComparer.Ordinal)
    ];

    private static double StepZero(string file) => JsonNode.Parse(File.ReadAllText(file))!["steps"]![0]!["atSeconds"]!.GetValue<double>();

    [Test]
    public async Task AWorkingCopyThatConflictsWithTheHead_IsSetAside_WithANote()
    {
        string root = TempRoot();
        try
        {
            StratDocument document = KilledAfterTwoCommits(root);
            WriteConflictingWorkingCopy(root, document, 70);

            StratStore restarted = new(root, utcNow: SteppingClock(Past.AddHours(1)));
            using StratSession session = Session(restarted);
            session.Open(document.Id);

            string[] kept = SetAsideFiles(root, document);
            using (Assert.Multiple())
            {
                await Assert.That(session.Document!.Revision).IsEqualTo(3);
                await Assert.That(session.Document.Steps[0].AtSeconds).IsEqualTo(88);
                await Assert.That(session.HasPending).IsFalse();
                await Assert.That(session.StatusText).Contains("conflict with revision 3");
                await Assert.That(kept.Length).IsEqualTo(1);
                await Assert.That(session.StatusText).Contains(Path.GetFileName(kept[0]));
                await Assert.That(StepZero(kept[0])).IsEqualTo(70);
                await Assert.That(Revisions(restarted, document.Id)).IsEquivalentTo(ThreeRevisions);
                await Assert.That(restarted.Index.Single().Revision).IsEqualTo(3);
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task ASecondConflict_AtTheSameRevisionAndClock_KeepsBothCopies()
    {
        string root = TempRoot();
        try
        {
            StratDocument document = KilledAfterTwoCommits(root);
            WriteConflictingWorkingCopy(root, document, 70);
            new StratStore(root, utcNow: () => Past).TryLoad(document.Id);
            WriteConflictingWorkingCopy(root, document, 65);
            new StratStore(root, utcNow: () => Past).TryLoad(document.Id);

            string[] kept = SetAsideFiles(root, document);
            await Assert.That(kept.Select(StepZero).Order()).IsEquivalentTo(BothCopies);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static void SkipWithoutFileModes()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            throw new SkipTestException("a read-only folder needs Unix file modes and a user they bind");
        }
    }

    private static void ReadOnly(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    private static void Writable(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public async Task AConflictThatCannotBeSetAside_OpensReadOnly_AndNothingIsCommittedOverTheHead()
    {
        SkipWithoutFileModes();
        string root = TempRoot();
        string folder = "";
        try
        {
            StratDocument document = KilledAfterTwoCommits(root);
            WriteConflictingWorkingCopy(root, document, 70);
            folder = Path.GetDirectoryName(StratFile(root, document))!;
            ReadOnly(folder);

            // The log file itself stays writable, so only the store's refusal keeps the stale edits out of it.
            StratStore store = new(root, utcNow: SteppingClock(Past.AddHours(1)));
            StratLoadResult loaded = store.Load(document.Id);
            using (Assert.Multiple())
            {
                await Assert.That(loaded.Document!.Steps[0].AtSeconds).IsEqualTo(70);
                await Assert.That(StratStore.IsPending(loaded.Document)).IsTrue();
                await Assert.That(loaded.Note).Contains("could not be set aside");
                await Assert.That(store.IsUnresolved(document.Id)).IsTrue();
                await Assert.That(store.PendingOps(loaded.Document)).IsEmpty();
            }

            StratSession session = Session(store);
            session.Open(document.Id);
            using (Assert.Multiple())
            {
                await Assert.That(session.HasPending).IsFalse().Because("no recovered ops, so no idle commit");
                await Assert.That(session.RecoveredPending).IsFalse();
                await Assert.That(session.StatusText).Contains("read-only");
            }

            session.Apply(PatchOp.ReplaceOp("/name", null, JsonValue.Create("edited on the conflict")));
            StratSaveResult refused = session.Commit()!;
            await session.FlushAsync();
            session.Close();
            session.Dispose();

            using (Assert.Multiple())
            {
                await Assert.That(refused.Saved).IsFalse();
                await Assert.That(refused.Reason).IsEqualTo(StratStore.UnresolvedReason);
                await Assert.That(Revisions(store, document.Id)).IsEquivalentTo(ThreeRevisions);
                await Assert.That(StepZero(StratFile(root, document))).IsEqualTo(70).Because("the working copy is still on disk");
                await Assert.That(StratStore.IsPending(store.Materialize(document.Id, 3)!)).IsFalse();
                await Assert.That(store.Materialize(document.Id, 3)!.Steps[0].AtSeconds).IsEqualTo(88).Because("the head survives in the log");
            }

            // Writable again: the next open sets the copy aside, and a commit lands on the head.
            Writable(folder);
            using StratSession next = Session(store);
            next.Open(document.Id);
            next.Apply(PatchOp.ReplaceOp("/name", null, JsonValue.Create("on the head")));
            StratSaveResult saved = next.Commit()!;
            string[] kept = SetAsideFiles(root, document);
            using (Assert.Multiple())
            {
                await Assert.That(saved.Revision).IsEqualTo(4);
                await Assert.That(store.Materialize(document.Id, 4)!.Steps[0].AtSeconds).IsEqualTo(88);
                await Assert.That(kept.Length).IsEqualTo(1);
                await Assert.That(StepZero(kept[0])).IsEqualTo(70);
            }
        }
        finally
        {
            if (folder.Length > 0)
            {
                Writable(folder);
            }

            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task AnUnresolvedConflict_IsSetAsideNormally_OnceALoadCanWrite()
    {
        SkipWithoutFileModes();
        string root = TempRoot();
        string folder = "";
        try
        {
            StratDocument document = KilledAfterTwoCommits(root);
            WriteConflictingWorkingCopy(root, document, 70);
            folder = Path.GetDirectoryName(StratFile(root, document))!;
            ReadOnly(folder);
            StratStore store = new(root, utcNow: SteppingClock(Past.AddHours(1)));
            await Assert.That(store.IsUnresolved(document.Id)).IsTrue();
            Writable(folder);

            using StratSession session = Session(store);
            session.Open(document.Id);
            string[] kept = SetAsideFiles(root, document);
            using (Assert.Multiple())
            {
                await Assert.That(store.IsUnresolved(document.Id)).IsFalse();
                await Assert.That(session.Document!.Steps[0].AtSeconds).IsEqualTo(88);
                await Assert.That(session.StatusText).Contains("are kept in");
                await Assert.That(kept.Length).IsEqualTo(1);
                await Assert.That(StepZero(kept[0])).IsEqualTo(70);
            }

            session.Apply(PatchOp.ReplaceOp("/name", null, JsonValue.Create("after the set-aside")));
            await Assert.That(session.Commit()!.Revision).IsEqualTo(4);
            await Assert.That(store.Materialize(document.Id, 4)!.Steps[0].AtSeconds).IsEqualTo(88);
        }
        finally
        {
            if (folder.Length > 0)
            {
                Writable(folder);
            }

            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task ACommitWhoseStratWriteFails_IsSavedOnce_AndAnArrayAddIsNotAppendedTwice()
    {
        SkipWithoutFileModes();
        string root = TempRoot();
        string folder = "";
        try
        {
            StratStore store = new(root, utcNow: SteppingClock(Past));
            StratDocument document = Minimal();
            store.Save(document, [], "created");
            folder = Path.GetDirectoryName(StratFile(root, document))!;

            using StratSession session = Session(store);
            session.Open(document.Id);
            ReadOnly(folder);
            JsonNode step = JsonSerializer.SerializeToNode(Step(3, 70, "A", "peek"), StratJsonContext.Default.StratStep)!;
            session.Apply(PatchOp.AddOp("/steps/-", step));
            StratSaveResult result = session.Commit()!;

            using (Assert.Multiple())
            {
                await Assert.That(result.Saved).IsTrue();
                await Assert.That(result.Revision).IsEqualTo(2);
                await Assert.That(result.Reason).Contains("could not be written");
                await Assert.That(session.HasPending).IsFalse();
                await Assert.That(session.Commit()).IsNull().Because("nothing is left to append again");
                await Assert.That(session.StatusText).Contains("could not be written");
            }

            session.Close();
            Writable(folder);
            StratDocument reloaded = new StratStore(root).TryLoad(document.Id)!;
            using (Assert.Multiple())
            {
                await Assert.That(File.ReadAllLines(LogFile(root, document)).Length).IsEqualTo(2);
                await Assert.That(reloaded.Revision).IsEqualTo(2);
                await Assert.That(reloaded.Steps.Count).IsEqualTo(3);
                await Assert.That(reloaded.Steps.Select(s => s.Id).Distinct().Count()).IsEqualTo(3);
            }
        }
        finally
        {
            if (folder.Length > 0)
            {
                Writable(folder);
            }

            DeleteQuietly(root);
        }
    }
}
