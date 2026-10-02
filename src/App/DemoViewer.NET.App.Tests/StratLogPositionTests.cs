#region

using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Services.Strats;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Logs that already hold a revision twice, from builds that numbered commits off a stale index row:
///     recovery is anchored by position in the file, never by number. A committed file is never rewritten to an
///     older state, a working copy on an ambiguous revision is set aside, and every recovery leaves a note.
/// </summary>
[NotInParallel]
public class StratLogPositionTests
{
    // A duplicate run not at the tail, and the owner's shape.
    private static readonly int[] NotAtTail = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 9, 10];
    private static readonly int[] OwnerShape = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 9, 10, 11, 12];

    private static readonly string[] NotesOnly = ["/notes"];

    private static readonly DateTime Past = DateTime.UtcNow.AddHours(-3);

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

    private static string[] SetAsideFiles(string root, StratDocument document) =>
        Directory.GetFiles(Path.GetDirectoryName(StratFile(root, document))!, document.Id + ".working-r*.json");

    /// <summary>
    ///     Writes a log with the given revisions, one rename and one step move per line so every position has its
    ///     own state, and returns those states in file order. No strat file is left; the caller writes one.
    /// </summary>
    private static List<StratDocument> WriteLog(string root, int[] revisions)
    {
        StratStore store = new(root, utcNow: SteppingClock(Past));
        StratDocument created = Minimal();
        store.Save(created, [], "created");
        string log = LogFile(root, created);
        List<string> lines = [File.ReadAllLines(log).Single()];
        List<StratDocument> states = [store.Materialize(created.Id, 1)!];
        for (int i = 1; i < revisions.Length; i++)
        {
            StratDocument before = states[^1];
            int step = i % 2;
            double to = step == 0 ? 90 - i : 60 - i;
            PatchOp[] ops =
            [
                PatchOp.ReplaceOp($"/steps/{step}/atSeconds", JsonValue.Create(before.Steps[step].AtSeconds), JsonValue.Create(to)),
                PatchOp.ReplaceOp("/name", JsonValue.Create(before.Name), JsonValue.Create($"line {i}"))
            ];
            HistoryEntry entry = new() { Revision = revisions[i], AtUtc = Past.AddMinutes(10 + i), Summary = $"line {i}", Ops = [.. ops] };
            StratDocument after = StratHistory.Apply(before, ops);
            after.Revision = entry.Revision;
            after.ModifiedUtc = entry.AtUtc;
            states.Add(after);
            lines.Add(JsonSerializer.Serialize(entry, StratHistoryJsonContext.Default.HistoryEntry));
        }

        File.WriteAllLines(log, lines);
        return states;
    }

    private static void WriteStrat(string root, StratDocument document) =>
        File.WriteAllText(StratFile(root, document), StratStore.Serialize(document));

    private static string Committed(StratDocument document) => StratStore.Serialize(StratStore.WithPending(document, false));

    [Test]
    [MethodDataSource(nameof(Shapes))]
    public async Task ACommittedFileAtTheTail_IsLeftAlone_WithNoNote(int[] shape)
    {
        string root = TempRoot();
        try
        {
            List<StratDocument> states = WriteLog(root, shape);
            WriteStrat(root, states[^1]);
            string before = File.ReadAllText(StratFile(root, states[^1]));

            StratStore store = new(root);
            StratDocument loaded = store.TryLoad(states[^1].Id)!;
            using (Assert.Multiple())
            {
                await Assert.That(StratStore.Serialize(loaded)).IsEqualTo(before);
                await Assert.That(File.ReadAllText(StratFile(root, states[^1]))).IsEqualTo(before);
                await Assert.That(store.TakeNote(states[^1].Id)).IsNull();
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    [MethodDataSource(nameof(Shapes))]
    public async Task ACommittedFileOneLineBehind_GetsOnlyTheLinesAfterItsPosition(int[] shape)
    {
        string root = TempRoot();
        try
        {
            // A crash between the last line and its strat write: the file is the line before, whose revision
            // the log also holds earlier. Replaying by number would apply the earlier lines again.
            List<StratDocument> states = WriteLog(root, shape);
            WriteStrat(root, states[^2]);

            StratStore store = new(root);
            StratDocument loaded = store.TryLoad(states[^1].Id)!;
            using (Assert.Multiple())
            {
                await Assert.That(Committed(loaded)).IsEqualTo(Committed(states[^1]));
                await Assert.That(File.ReadAllText(StratFile(root, states[^1]))).IsEqualTo(Committed(states[^1]));
                await Assert.That(store.TakeNote(states[^1].Id)).Contains("re-applied");
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task ACommittedFileAtAnEarlierDuplicate_IsAnchoredThere_NotAtTheLaterOne()
    {
        string root = TempRoot();
        try
        {
            // The file is the first 10. Everything after that position is applied, ending at the tail.
            List<StratDocument> states = WriteLog(root, NotAtTail);
            WriteStrat(root, states[9]);

            StratStore store = new(root);
            StratDocument loaded = store.TryLoad(states[^1].Id)!;
            using (Assert.Multiple())
            {
                await Assert.That(Committed(loaded)).IsEqualTo(Committed(states[^1]));
                await Assert.That(store.TakeNote(states[^1].Id)).Contains("3 committed change(s) were re-applied");
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task ACommittedFileThatMatchesNoLine_IsLeftAlone_WithANote()
    {
        string root = TempRoot();
        try
        {
            List<StratDocument> states = WriteLog(root, NotAtTail);
            StratDocument stray = states[10].Clone();
            stray.Name = "edited by hand";
            stray.Revision = 10;
            WriteStrat(root, stray);
            string before = File.ReadAllText(StratFile(root, stray));

            StratStore store = new(root);
            store.TryLoad(stray.Id);
            using (Assert.Multiple())
            {
                await Assert.That(File.ReadAllText(StratFile(root, stray))).IsEqualTo(before);
                await Assert.That(store.TakeNote(stray.Id)).Contains("matches no line");
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    [MethodDataSource(nameof(Shapes))]
    public async Task AWorkingCopyOnTheTail_OpensWithItsEdit_AndCommitsAboveTheHighest(int[] shape)
    {
        string root = TempRoot();
        try
        {
            List<StratDocument> states = WriteLog(root, shape);
            StratDocument working = states[^1].Clone();
            working.Notes = "uncommitted";
            WriteStrat(root, StratStore.WithPending(working, true));

            StratStore store = new(root, utcNow: SteppingClock(Past.AddHours(1)));
            bool unique = shape.Count(r => r == shape[^1]) == 1;
            using StratSession session = Session(store);
            session.Open(working.Id);

            if (unique)
            {
                using (Assert.Multiple())
                {
                    await Assert.That(session.Document!.Notes).IsEqualTo("uncommitted");
                    await Assert.That(session.PendingOps.Select(o => o.Path)).IsEquivalentTo(NotesOnly);
                    await Assert.That(session.RecoveryNote).IsNull();
                }

                await Assert.That(session.Commit()!.Revision).IsEqualTo(shape.Max() + 1);
                return;
            }

            // The tail's revision is held twice, so the base is ambiguous: set aside, the head opens.
            using (Assert.Multiple())
            {
                await Assert.That(Committed(session.Document!)).IsEqualTo(Committed(states[^1]));
                await Assert.That(File.ReadAllText(StratFile(root, working))).IsEqualTo(Committed(states[^1]));
                await Assert.That(session.RecoveryNote).Contains("more than once");
                await Assert.That(SetAsideFiles(root, working).Length).IsEqualTo(1);
                await Assert.That(JsonNode.Parse(File.ReadAllText(SetAsideFiles(root, working)[0]))!["notes"]!.GetValue<string>())
                    .IsEqualTo("uncommitted");
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    [MethodDataSource(nameof(Shapes))]
    public async Task AWorkingCopyOnARepeatedRevision_IsSetAside_AndTheFileBecomesTheHead(int[] shape)
    {
        string root = TempRoot();
        try
        {
            // Stamped 9, which both shapes hold twice: the first 9 plus an edit.
            List<StratDocument> states = WriteLog(root, shape);
            StratDocument working = states[8].Clone();
            working.Notes = "on the first 9";
            WriteStrat(root, StratStore.WithPending(working, true));

            StratStore store = new(root);
            StratDocument loaded = store.TryLoad(working.Id)!;
            string[] kept = SetAsideFiles(root, working);
            using (Assert.Multiple())
            {
                await Assert.That(Committed(loaded)).IsEqualTo(Committed(states[^1]));
                await Assert.That(File.ReadAllText(StratFile(root, working))).IsEqualTo(Committed(states[^1]));
                await Assert.That(store.TakeNote(working.Id)).Contains("more than once");
                await Assert.That(kept.Length).IsEqualTo(1);
                await Assert.That(JsonNode.Parse(File.ReadAllText(kept[0]))!["notes"]!.GetValue<string>()).IsEqualTo("on the first 9");
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task AnEditWhoseValueTheHeadHoldsAtAShiftedIndex_IsSetAside_NotDropped()
    {
        string root = TempRoot();
        try
        {
            StratStore store = new(root, utcNow: SteppingClock(Past));
            StratDocument document = Minimal();
            store.Save(document, [], "created");

            // Revision 2 inserts a step at the front at 1:40, so step 0 on the head is the new step.
            JsonNode inserted = JsonSerializer.SerializeToNode(Step(9, 100, "A", "peek"), StratJsonContext.Default.StratStep)!;
            PatchOp insert = PatchOp.AddOp("/steps/0", inserted);
            StratDocument revisionOne = store.Materialize(document.Id, 1)!;
            store.Save(StratHistory.Apply(revisionOne, [insert]), [insert], null);

            // The working copy, on revision 1, moved its own step 0 to 1:40: the head's value at that path.
            StratDocument working = revisionOne.Clone();
            working.Steps[0].AtSeconds = 100;
            WriteStrat(root, StratStore.WithPending(working, true));

            StratStore restarted = new(root);
            StratDocument loaded = restarted.TryLoad(document.Id)!;
            string[] kept = SetAsideFiles(root, document);
            using (Assert.Multiple())
            {
                await Assert.That(loaded.Revision).IsEqualTo(2);
                await Assert.That(loaded.Steps.Count).IsEqualTo(3);
                await Assert.That(loaded.Steps[1].AtSeconds).IsEqualTo(90).Because("the working copy's move was not applied");
                await Assert.That(restarted.TakeNote(document.Id)).Contains("conflict with revision 2");
                await Assert.That(kept.Length).IsEqualTo(1);
                await Assert.That(JsonNode.Parse(File.ReadAllText(kept[0]))!["steps"]![0]!["atSeconds"]!.GetValue<double>()).IsEqualTo(100);
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task TheOwnersShape_ReplaysInFileOrder_AndTheHistoryPaneShowsEveryLine()
    {
        string root = TempRoot();
        try
        {
            List<StratDocument> states = WriteLog(root, OwnerShape);
            WriteStrat(root, states[^1]);
            StratStore store = new(root);
            IReadOnlyList<StratHistoryRow> rows = StratHistoryPane.Rows(store.History(states[^1].Id));

            using (Assert.Multiple())
            {
                await Assert.That(Committed(store.Materialize(states[^1].Id, 12)!)).IsEqualTo(Committed(states[^1]));
                await Assert.That(rows.Count).IsEqualTo(OwnerShape.Length);
                await Assert.That(rows.Select(r => r.Revision)).IsEquivalentTo(Enumerable.Reverse(OwnerShape));
                await Assert.That(rows.Take(OwnerShape.Length - 1).All(r => r.Changes.Count == 2)).IsTrue();
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    public static IEnumerable<Func<int[]>> Shapes()
    {
        yield return () => NotAtTail;
        yield return () => OwnerShape;
    }
}
