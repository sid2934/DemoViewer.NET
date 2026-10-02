#region

using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Services.Strats;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Revisions after a process that never shut down: index.json is written only at shutdown, so a killed
///     Debug run leaves its rows behind the files. Revisions stay strictly increasing, a working copy stamped
///     from a stale row is moved onto the log's head or set aside with a note, and a log that already holds
///     reused revisions still replays and takes the next commit above its highest.
/// </summary>
[NotInParallel]
public class StratRevisionTests
{
    private static readonly int[] ThreeRevisions = [1, 2, 3];
    private static readonly int[] FourRevisions = [1, 2, 3, 4];
    private static readonly int[] NewestFour = [12, 11, 10, 9];
    private static readonly string[] NameOnly = ["/name"];

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
    // stamped no newer than the row, so no startup check sees the difference: the owner's state on 2026-10-02.
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
    public async Task AWorkingCopyStampedFromAStaleRow_IsMovedOntoTheHead_NotDropped()
    {
        string root = TempRoot();
        try
        {
            StratDocument document = KilledAfterTwoCommits(root);

            // What the old build's autosave wrote after the restart: revision 3's content plus one edit,
            // stamped with the row's revision 1, then a crash.
            StratStore probe = new(root, utcNow: SteppingClock(Past.AddHours(1)));
            StratDocument working = probe.Materialize(document.Id, 3)!;
            working.Name = "edited before the crash";
            working.Revision = 1;
            File.WriteAllText(StratFile(root, document), StratStore.Serialize(StratStore.WithPending(working, true)));

            StratStore restarted = new(root, utcNow: SteppingClock(Past.AddHours(1)));
            using StratSession session = Session(restarted);
            session.Open(document.Id);

            using (Assert.Multiple())
            {
                await Assert.That(session.Document!.Revision).IsEqualTo(3);
                await Assert.That(session.Document.Name).IsEqualTo("edited before the crash");
                await Assert.That(session.Document.Steps[1].AtSeconds).IsEqualTo(80);
                await Assert.That(session.RecoveredPending).IsTrue();
                await Assert.That(session.PendingOps.Select(o => o.Path)).IsEquivalentTo(NameOnly);
                await Assert.That(session.StatusText).Contains("recovered edits from revision 1 were moved onto revision 3");
            }

            await Assert.That(session.Commit()!.Revision).IsEqualTo(4);
            HistoryEntry last = restarted.History(document.Id)[^1];
            using (Assert.Multiple())
            {
                await Assert.That(Revisions(restarted, document.Id)).IsEquivalentTo(FourRevisions);
                await Assert.That(last.Ops.Select(o => o.Path)).IsEquivalentTo(NameOnly);
                await Assert.That(restarted.Materialize(document.Id, 4)!.Steps[0].AtSeconds).IsEqualTo(88);
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task AWorkingCopyThatConflictsWithTheHead_IsSetAside_WithANote()
    {
        string root = TempRoot();
        try
        {
            StratDocument document = KilledAfterTwoCommits(root);

            // Revision 1 plus its own move of step 0, which revision 2 moved somewhere else.
            StratStore probe = new(root);
            StratDocument working = probe.Materialize(document.Id, 1)!;
            working.Steps[0].AtSeconds = 70;
            File.WriteAllText(StratFile(root, document), StratStore.Serialize(StratStore.WithPending(working, true)));

            StratStore restarted = new(root, utcNow: SteppingClock(Past.AddHours(1)));
            using StratSession session = Session(restarted);
            session.Open(document.Id);

            string setAside = Path.Combine(Path.GetDirectoryName(StratFile(root, document))!, document.Id + ".working-r1.json");
            using (Assert.Multiple())
            {
                await Assert.That(session.Document!.Revision).IsEqualTo(3);
                await Assert.That(session.Document.Steps[0].AtSeconds).IsEqualTo(88);
                await Assert.That(session.HasPending).IsFalse();
                await Assert.That(session.StatusText).Contains("conflict with revision 3");
                await Assert.That(session.StatusText).Contains(Path.GetFileName(setAside));
                await Assert.That(File.Exists(setAside)).IsTrue();
                await Assert.That(JsonNode.Parse(File.ReadAllText(setAside))!["steps"]![0]!["atSeconds"]!.GetValue<double>()).IsEqualTo(70);
                await Assert.That(Revisions(restarted, document.Id)).IsEquivalentTo(ThreeRevisions);
                await Assert.That(restarted.Index.Single().Revision).IsEqualTo(3);
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    // The owner's log shape: 1..8, 9, 10, 11, then 9, 10, 11 again and 12, each a real edit, in file order.
    private static StratDocument WriteDoubledLog(string root)
    {
        StratStore store = new(root, utcNow: SteppingClock(Past));
        StratDocument document = Minimal();
        store.Save(document, [], "created");
        string log = LogFile(root, document);
        List<string> lines = [File.ReadAllLines(log).Single()];
        StratDocument current = store.Materialize(document.Id, 1)!;
        int[] revisions = [2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 9, 10, 11, 12];
        for (int i = 0; i < revisions.Length; i++)
        {
            PatchOp op = PatchOp.ReplaceOp($"/steps/{i % 2}/atSeconds", JsonValue.Create(current.Steps[i % 2].AtSeconds),
                JsonValue.Create(80.0 - i));
            current = StratHistory.Apply(current, [op]);
            HistoryEntry entry = new() { Revision = revisions[i], AtUtc = Past.AddMinutes(10 + i), Summary = "move " + i, Ops = [op] };
            lines.Add(JsonSerializer.Serialize(entry, StratHistoryJsonContext.Default.HistoryEntry));
        }

        current.Revision = 12;
        current.ModifiedUtc = Past.AddMinutes(10 + revisions.Length - 1);
        File.WriteAllLines(log, lines);
        File.WriteAllText(StratFile(root, document), StratStore.Serialize(current));
        return current;
    }

    [Test]
    public async Task ALogWithReusedRevisions_StillReplays_AndTheNextCommitIsAboveItsHighest()
    {
        string root = TempRoot();
        try
        {
            StratDocument expected = WriteDoubledLog(root);
            StratStore store = new(root, utcNow: SteppingClock(Past.AddHours(1)));
            IReadOnlyList<HistoryEntry> log = store.History(expected.Id);
            IReadOnlyList<StratHistoryRow> rows = StratHistoryPane.Rows(log);

            using (Assert.Multiple())
            {
                await Assert.That(store.TryLoad(expected.Id)!.Revision).IsEqualTo(12);
                await Assert.That(StratStore.Serialize(store.Materialize(expected.Id, 12)!)).IsEqualTo(StratStore.Serialize(expected))
                    .Because("file order is the replay order, whatever the numbers say");
                await Assert.That(rows.Count).IsEqualTo(15);
                await Assert.That(rows.Select(r => r.Revision).Take(4)).IsEquivalentTo(NewestFour);
                await Assert.That(rows.Take(14).All(r => r.Changes.Count == 1)).IsTrue();
            }

            using StratSession session = Session(store);
            session.Open(expected.Id);
            session.Apply(MoveTime(0, 75));
            await Assert.That(session.Commit()!.Revision).IsEqualTo(13);
            await Assert.That(store.Materialize(expected.Id, 13)!.Steps[0].AtSeconds).IsEqualTo(75);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }
}
