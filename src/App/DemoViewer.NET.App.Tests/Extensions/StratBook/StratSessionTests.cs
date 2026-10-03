#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Services.Strats;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The live strat (strat-model.md §3.12, overview correction 18): one op per undo entry with <c>from</c> read
///     from the document, a merged drag as one history op, the commit rule (explicit, close, idle), the 200-entry
///     cap, the pending working copy written between commits and re-opened by the next session, the single-writer
///     checkout with the store's status routing, and the status line on each host.
/// </summary>
[NotInParallel]
public class StratSessionTests
{
    private static StratSession Session(StratStore store, bool browser = false) =>
        new(store, null, () => browser, () => Created)
        {
            AutoSaveDelay = TimeSpan.FromHours(1),
            IdleCommitDelay = TimeSpan.FromHours(1)
        };

    private static (StratStore Store, StratDocument Committed) Stored(string? root = null)
    {
        StratStore store = new(root, utcNow: SteppingClock(Created));
        StratDocument document = Minimal();
        StratSaveResult saved = store.Save(document, [], "created");
        if (!saved.Saved)
        {
            throw new InvalidOperationException(saved.Reason);
        }

        return (store, document);
    }

    private static PatchOp MoveTime(int step, double to) =>
        PatchOp.ReplaceOp($"/steps/{step}/atSeconds", null, JsonValue.Create(to));

    [Test]
    public async Task AnEdit_IsOneUndoEntry_WithFromReadFromTheDocument()
    {
        (StratStore store, StratDocument committed) = Stored();
        using StratSession session = Session(store);
        session.Open(committed.Id);

        List<IReadOnlyList<PatchOp>> announced = [];
        session.OpsApplied += announced.Add;

        // The caller's `from` is wrong on purpose: the session reads the displaced value itself.
        session.Apply(PatchOp.ReplaceOp("/name", JsonValue.Create("garbage"), JsonValue.Create("A split")));

        using (Assert.Multiple())
        {
            await Assert.That(session.Document!.Name).IsEqualTo("A split");
            await Assert.That(session.UndoDepth).IsEqualTo(1);
            await Assert.That(session.HasPending).IsTrue();
            await Assert.That(announced.Single().Single().From!.GetValue<string>()).IsEqualTo("A exec");
        }

        await Assert.That(session.Undo()).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(session.Document!.Name).IsEqualTo("A exec");
            await Assert.That(session.CanRedo).IsTrue();
            await Assert.That(session.HasPending).IsFalse().Because("an edit and its undo inside one commit cancel");
            await Assert.That(announced.Count).IsEqualTo(2).Because("an undo reaches the projections too");
        }

        await Assert.That(session.Redo()).IsTrue();
        await Assert.That(session.Document!.Name).IsEqualTo("A split");
    }

    [Test]
    public async Task SeveralOps_AreOneUndoEntry_AndAppendsNameTheirIndex()
    {
        (StratStore store, StratDocument committed) = Stored();
        using StratSession session = Session(store);
        session.Open(committed.Id);
        StratStep step = Step(3, 70, "A", "peek", "TRamp", "Connector");
        JsonNode node = System.Text.Json.JsonSerializer.SerializeToNode(step, StratJsonContext.Default.StratStep)!;

        List<IReadOnlyList<PatchOp>> announced = [];
        session.OpsApplied += announced.Add;
        session.Apply([PatchOp.AddOp("/steps/-", node), PatchOp.ReplaceOp("/notes", null, JsonValue.Create("peek added"))]);

        using (Assert.Multiple())
        {
            await Assert.That(session.UndoDepth).IsEqualTo(1);
            await Assert.That(session.Document!.Steps.Count).IsEqualTo(3);
            await Assert.That(announced.Single()[0].Path).IsEqualTo("/steps/2")
                .Because("the log names the concrete index, so the inverse knows what to remove");
        }

        session.Undo();
        using (Assert.Multiple())
        {
            await Assert.That(session.Document!.Steps.Count).IsEqualTo(2);
            await Assert.That(session.Document.Notes).IsNull();
        }
    }

    [Test]
    public async Task AnOpThatDoesNotApply_Throws_AndLeavesTheDocumentAsItWas()
    {
        (StratStore store, StratDocument committed) = Stored();
        using StratSession session = Session(store);
        session.Open(committed.Id);

        Assert.Throws<InvalidOperationException>(() => session.Apply([
            PatchOp.ReplaceOp("/name", null, JsonValue.Create("changed")),
            PatchOp.RemoveOp("/steps/9/note", null)
        ]));
        Assert.Throws<InvalidOperationException>(() => session.Apply(PatchOp.ReplaceOp("/revision", null, JsonValue.Create(99))));

        using (Assert.Multiple())
        {
            await Assert.That(session.Document!.Name).IsEqualTo("A exec");
            await Assert.That(session.UndoDepth).IsEqualTo(0);
            await Assert.That(session.HasPending).IsFalse();
        }
    }

    [Test]
    public async Task ADrag_OfFortyReplaces_CommitsAsOneHistoryOp()
    {
        (StratStore store, StratDocument committed) = Stored();
        using StratSession session = Session(store);
        session.Open(committed.Id);

        for (int i = 1; i <= 40; i++)
        {
            session.Apply(MoveTime(1, 82 - i * 0.1));
        }

        StratSaveResult? result = session.Commit();
        HistoryEntry entry = store.History(committed.Id)[^1];

        using (Assert.Multiple())
        {
            await Assert.That(result!.Saved).IsTrue();
            await Assert.That(session.Document!.Revision).IsEqualTo(2);
            await Assert.That(session.HasPending).IsFalse();
            await Assert.That(entry.Ops.Count).IsEqualTo(1);
            await Assert.That(entry.Ops[0].From!.GetValue<double>()).IsEqualTo(82);
            await Assert.That(entry.Ops[0].Value!.GetValue<double>()).IsEqualTo(78).Within(1e-9);
            await Assert.That(session.UndoDepth).IsEqualTo(40).Because("the log merges; the undo stack keeps each gesture");
        }
    }

    [Test]
    public async Task TheUndoStack_IsCappedAt200()
    {
        (StratStore store, StratDocument committed) = Stored();
        using StratSession session = Session(store);
        session.Open(committed.Id);

        for (int i = 0; i < StratSession.MaxHistoryEntries + 25; i++)
        {
            session.Apply(PatchOp.ReplaceOp("/name", null, JsonValue.Create($"name {i}")));
        }

        await Assert.That(session.UndoDepth).IsEqualTo(StratSession.MaxHistoryEntries);
    }

    [Test]
    public async Task ARefusedCommit_KeepsTheEditsPending_AndSaysWhy()
    {
        (StratStore store, StratDocument committed) = Stored();
        using StratSession session = Session(store);
        session.Open(committed.Id);

        // The clock may not run backwards along the steps: the validator refuses the commit.
        session.Apply(MoveTime(1, 100));
        StratSaveResult? refused = session.Commit();

        using (Assert.Multiple())
        {
            await Assert.That(refused!.Saved).IsFalse();
            await Assert.That(session.HasPending).IsTrue();
            await Assert.That(session.StatusText).StartsWith("strat could not be saved");
            await Assert.That(store.History(committed.Id).Count).IsEqualTo(1);
        }

        session.Apply(MoveTime(1, 80));
        await Assert.That(session.Commit()!.Saved).IsTrue();
        await Assert.That(store.History(committed.Id)[^1].Ops.Single().Value!.GetValue<double>()).IsEqualTo(80);
    }

    [Test]
    public async Task TheIdleTimer_Commits()
    {
        (StratStore store, StratDocument committed) = Stored();
        using StratSession session = Session(store);
        session.IdleCommitDelay = TimeSpan.FromMilliseconds(30);
        session.Open(committed.Id);

        session.Apply(PatchOp.ReplaceOp("/name", null, JsonValue.Create("idle")));
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (session.CommitCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        using (Assert.Multiple())
        {
            await Assert.That(session.CommitCount).IsEqualTo(1);
            await Assert.That(store.TryLoad(committed.Id)!.Name).IsEqualTo("idle");
            await Assert.That(store.TryLoad(committed.Id)!.Revision).IsEqualTo(2);
        }
    }

    [Test]
    public async Task TheIdleTimer_RestartsOnEveryEdit()
    {
        (StratStore store, StratDocument committed) = Stored();
        using StratSession session = Session(store);
        session.IdleCommitDelay = TimeSpan.FromMilliseconds(300);
        session.Open(committed.Id);

        for (int i = 0; i < 5; i++)
        {
            session.Apply(PatchOp.ReplaceOp("/name", null, JsonValue.Create($"n{i}")));
            await Task.Delay(100);
        }

        await Assert.That(session.CommitCount).IsEqualTo(0).Because("no 300 ms passed without an edit");
    }

    [Test]
    public async Task Close_Commits_AndReleasesTheCheckOut()
    {
        (StratStore store, StratDocument committed) = Stored();
        StratSession session = Session(store);
        session.Open(committed.Id);
        session.Apply(PatchOp.ReplaceOp("/name", null, JsonValue.Create("closed")));
        await Assert.That(store.SessionFor(committed.Id)).IsSameReferenceAs(session);

        session.Close();

        using (Assert.Multiple())
        {
            await Assert.That(session.Document).IsNull();
            await Assert.That(store.SessionFor(committed.Id)).IsNull();
            await Assert.That(store.TryLoad(committed.Id)!.Name).IsEqualTo("closed");
            await Assert.That(store.TryLoad(committed.Id)!.Revision).IsEqualTo(2);
        }

        session.Dispose();
    }

    [Test]
    public async Task ASecondSession_OnTheSameStrat_IsRefused()
    {
        (StratStore store, StratDocument committed) = Stored();
        using StratSession first = Session(store);
        using StratSession second = Session(store);
        first.Open(committed.Id);

        Assert.Throws<InvalidOperationException>(() => second.Open(committed.Id));
        await Assert.That(store.SessionFor(committed.Id)).IsSameReferenceAs(first);
    }

    [Test]
    public async Task SetStatus_OnTheStore_RoutesThroughTheOpenSession()
    {
        (StratStore store, StratDocument committed) = Stored();
        using StratSession session = Session(store);
        session.Open(committed.Id);

        await Assert.That(store.SetStatus(committed.Id, StratStatus.Active)).IsTrue();

        using (Assert.Multiple())
        {
            await Assert.That(session.Document!.Status).IsEqualTo("Active");
            await Assert.That(session.CanUndo).IsTrue().Because("the routed change is one of the session's edits");
            await Assert.That(store.History(committed.Id).Count).IsEqualTo(1).Because("it commits by the session's rule");
        }
    }

    [Test]
    public async Task TheWorkingCopy_CarriesPending_AtThePreviousRevision()
    {
        string root = TempRoot();
        try
        {
            (StratStore store, StratDocument committed) = Stored(root);
            using StratSession session = Session(store);
            session.Open(committed.Id);
            session.Apply(PatchOp.ReplaceOp("/name", null, JsonValue.Create("working")));
            await session.FlushAsync();

            string file = Path.Combine(StratStore.FolderFor(root, committed.Owner, committed.Map), committed.Id + StratStore.StratExtension);
            JsonNode onDisk = JsonNode.Parse(File.ReadAllText(file))!;

            using (Assert.Multiple())
            {
                await Assert.That(onDisk["pending"]!.GetValue<bool>()).IsTrue();
                await Assert.That(onDisk["revision"]!.GetValue<int>()).IsEqualTo(1);
                await Assert.That(onDisk["name"]!.GetValue<string>()).IsEqualTo("working");
                await Assert.That(store.History(committed.Id).Count).IsEqualTo(1).Because("the log moves only at a commit");
            }

            session.Commit();
            JsonNode afterCommit = JsonNode.Parse(File.ReadAllText(file))!;
            using (Assert.Multiple())
            {
                await Assert.That(afterCommit["pending"]).IsNull();
                await Assert.That(afterCommit["revision"]!.GetValue<int>()).IsEqualTo(2);
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task APendingFile_ReopensWithItsEditsUncommitted_AndTheNextCommitWritesThem()
    {
        string root = TempRoot();
        try
        {
            (StratStore store, StratDocument committed) = Stored(root);
            StratSession crashed = Session(store);
            crashed.Open(committed.Id);
            crashed.Apply(PatchOp.ReplaceOp("/name", null, JsonValue.Create("after the crash")));
            crashed.Apply(MoveTime(1, 80));
            await crashed.FlushAsync();

            // A crash: no commit, no close. A new process reads the folder.
            StratStore reopened = new(root);
            using StratSession session = Session(reopened);
            session.Open(committed.Id);

            using (Assert.Multiple())
            {
                await Assert.That(session.RecoveredPending).IsTrue();
                await Assert.That(session.HasPending).IsTrue();
                await Assert.That(session.Document!.Name).IsEqualTo("after the crash");
                await Assert.That(session.Document.Extra?.ContainsKey(StratStore.PendingField) ?? false).IsFalse()
                    .Because("the marker is the file's, never the document's");
                await Assert.That(session.StatusText).StartsWith("recovered edits");
            }

            await Assert.That(session.Commit()!.Saved).IsTrue();
            HistoryEntry entry = reopened.History(committed.Id)[^1];
            using (Assert.Multiple())
            {
                await Assert.That(entry.Revision).IsEqualTo(2);
                await Assert.That(entry.Ops.Select(o => o.Path)).IsEquivalentTo(["/name", "/steps/1/atSeconds"]);
                await Assert.That(entry.Ops.Single(o => o.Path == "/name").From!.GetValue<string>()).IsEqualTo("A exec");
                await Assert.That(reopened.Materialize(committed.Id, 2)!.Name).IsEqualTo("after the crash");
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task APendingFileBehindItsLog_OpensAsTheLogSays_WithNoInsertAppliedTwice()
    {
        string root = TempRoot();
        try
        {
            (StratStore store, StratDocument committed) = Stored(root);
            string file = Path.Combine(StratStore.FolderFor(root, committed.Owner, committed.Map), committed.Id + StratStore.StratExtension);
            StratSession session = Session(store);
            session.Open(committed.Id);
            JsonNode step = System.Text.Json.JsonSerializer.SerializeToNode(Step(3, 70, "A", "peek"), StratJsonContext.Default.StratStep)!;
            session.Apply(PatchOp.AddOp("/steps/-", step));
            await session.FlushAsync();
            string workingCopy = File.ReadAllText(file);

            // The commit's line lands; its strat write is lost, leaving the pending working copy in place.
            session.Commit();
            session.Dispose();
            File.WriteAllText(file, workingCopy);

            StratDocument reopened = new StratStore(root).TryLoad(committed.Id)!;
            using (Assert.Multiple())
            {
                await Assert.That(reopened.Revision).IsEqualTo(2);
                await Assert.That(reopened.Steps.Count).IsEqualTo(3);
                await Assert.That(StratStore.IsPending(reopened)).IsFalse();
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task TheStatusLine_OnEachHost()
    {
        (StratStore memory, StratDocument committed) = Stored();
        using StratSession browser = Session(memory, browser: true);
        browser.Open(committed.Id);
        await Assert.That(browser.StatusText).IsEqualTo("session only: this browser tab forgets strats when it reloads");

        string root = TempRoot();
        try
        {
            (StratStore store, StratDocument onDisk) = Stored(root);
            using StratSession desktop = Session(store);
            await Assert.That(desktop.StatusText).IsEqualTo("no strat open");
            desktop.Open(onDisk.Id);
            await Assert.That(desktop.StatusText).IsEqualTo("revision 1 · saved");
            desktop.Apply(PatchOp.ReplaceOp("/name", null, JsonValue.Create("x")));
            await Assert.That(desktop.StatusText).StartsWith("revision 1 · edits commit");
        }
        finally
        {
            DeleteQuietly(root);
        }
    }
}
