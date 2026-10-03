#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Services.Strats;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The strat store (strat-model.md §3.2, §3.8, §3.11): the owner and map folder layout, the derived index
///     rebuilt and reconciled from the folders, corruption that reads as absent and is never overwritten, atomic
///     writes, a failed write reported rather than thrown, the in-memory browser mode, the append-only log with
///     the crash between its two writes reconciled on load, and delete to <c>.trash/</c>.
/// </summary>
[NotInParallel]
public class StratStoreTests
{
    private static readonly int[] SixRevisions = [1, 2, 3, 4, 5, 6];
    private static readonly int[] TwoRevisions = [1, 2];

    private static string StratFile(string root, StratDocument document) =>
        Path.Combine(StratStore.FolderFor(root, document.Owner, document.Map), document.Id + StratStore.StratExtension);

    private static string LogFile(string root, StratDocument document) =>
        Path.Combine(StratStore.FolderFor(root, document.Owner, document.Map), document.Id + StratStore.HistoryExtension);

    private static (StratDocument Next, PatchOp Op) MoveStep(StratDocument document, int step, double to)
    {
        PatchOp op = PatchOp.ReplaceOp($"/steps/{step}/atSeconds", JsonValue.Create(document.Steps[step].AtSeconds), JsonValue.Create(to));
        return (StratHistory.Apply(document, [op]), op);
    }

    [Test]
    public async Task Create_WritesTheStratAndItsLogUnderOwnerAndMap_AndAnIndexRow()
    {
        string root = TempRoot();
        try
        {
            StratStore store = new(root);
            StratDocument team = store.Create(Team, "DE_MIRAGE", "T", "execute", "A exec");
            StratDocument mine = store.Create(StratOwner.Me(), "de_nuke", "CT", "setup", "2-1-2");

            StratIndexEntry row = store.Index.Single(e => e.Id == team.Id);
            using (Assert.Multiple())
            {
                await Assert.That(File.Exists(Path.Combine(root, "team-" + TeamId, "de_mirage", team.Id + ".dvstrat.json"))).IsTrue();
                await Assert.That(File.ReadAllLines(Path.Combine(root, "team-" + TeamId, "de_mirage", team.Id + ".history.jsonl")).Length).IsEqualTo(1);
                await Assert.That(File.Exists(Path.Combine(root, "me", "de_nuke", mine.Id + ".dvstrat.json"))).IsTrue();
                await Assert.That(team.Revision).IsEqualTo(1);
                await Assert.That(team.Slots.Select(s => s.Slot)).IsEquivalentTo(StratVocabulary.Slots);
                await Assert.That(row.Map).IsEqualTo("de_mirage").Because("the map is the parser's lower-case spelling");
                await Assert.That(row.Owner).IsEqualTo(Team);
                await Assert.That(row.Revision).IsEqualTo(1);
                await Assert.That(File.Exists(Path.Combine(root, "index.json"))).IsFalse()
                    .Because("index.json is deferred to SaveIndex, the DemoCacheStore rule");
            }

            store.SaveIndex();
            StratStore reopened = new(root);
            await Assert.That(reopened.Index.Count).IsEqualTo(2);
            await Assert.That(reopened.TryLoad(team.Id)!.Name).IsEqualTo("A exec");
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task AFreshStore_WithNothingSaved_CreatesNoDirectory()
    {
        string root = TempRoot();
        _ = new StratStore(root);
        await Assert.That(Directory.Exists(root)).IsFalse();
    }

    [Test]
    public async Task MissingOrCorruptIndex_IsRebuiltFromTheFolders()
    {
        string root = TempRoot();
        try
        {
            StratStore store = new(root);
            StratDocument a = store.Create(Team, "de_mirage", "T", "execute", "A exec");
            StratDocument b = store.Create(StratOwner.Me(), "de_inferno", "T", "default", "default");

            // Missing: never saved.
            StratStore rebuilt = new(root);
            await Assert.That(rebuilt.Index.Select(e => e.Id)).IsEquivalentTo(new[] { a.Id, b.Id }.Order());

            // Corrupt.
            File.WriteAllText(Path.Combine(root, "index.json"), "{ not json");
            StratStore fromCorrupt = new(root);
            await Assert.That(fromCorrupt.Index.Select(e => e.Id)).IsEquivalentTo(new[] { a.Id, b.Id }.Order());
            await Assert.That(File.ReadAllText(Path.Combine(root, "index.json"))).Contains(a.Id.ToString());
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task Index_IsReconciledAgainstTheListing_AtStart()
    {
        string root = TempRoot();
        try
        {
            StratStore store = new(root);
            StratDocument kept = store.Create(Team, "de_mirage", "T", "execute", "kept");
            StratDocument gone = store.Create(Team, "de_mirage", "T", "execute", "gone");
            store.SaveIndex();

            // Removed by hand, both files: a log left alone would read as a first commit that crashed.
            File.Delete(StratFile(root, gone));
            File.Delete(LogFile(root, gone));

            // A strat copied in by hand after the index was saved: the listing finds it.
            StratDocument late = Minimal(owner: StratOwner.Me(), map: "de_nuke");
            late.Revision = 1;
            Directory.CreateDirectory(Path.GetDirectoryName(StratFile(root, late))!);
            File.WriteAllText(StratFile(root, late), StratStore.Serialize(late));

            StratStore reopened = new(root);
            await Assert.That(reopened.Index.Select(e => e.Id)).IsEquivalentTo(new[] { kept.Id, late.Id }.Order());
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task CorruptStrat_LoadsAsUnreadableWithAnIssue_AndIsNeverOverwritten()
    {
        string root = TempRoot();
        try
        {
            StratStore store = new(root);
            StratDocument document = store.Create(Team, "de_mirage", "T", "execute", "A exec");
            string file = StratFile(root, document);
            File.WriteAllText(file, "{ \"schemaVersion\": 1, \"id\": ");

            StratStore reopened = new(root);
            StratLoadResult loaded = reopened.Load(document.Id);
            using (Assert.Multiple())
            {
                await Assert.That(loaded.Document).IsNull();
                await Assert.That(loaded.IsUnreadable).IsTrue();
                await Assert.That(loaded.Issues.Count).IsEqualTo(1);
                await Assert.That(reopened.Index.Any(e => e.Id == document.Id)).IsFalse();
            }

            StratSaveResult save = reopened.Save(Minimal(document.Id), [], "created");
            await Assert.That(save.Saved).IsFalse();
            await Assert.That(File.ReadAllText(file)).IsEqualTo("{ \"schemaVersion\": 1, \"id\": ")
                .Because("a hand-broken file is somebody's strat; a fresh revision 1 must not replace it");
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task Save_BumpsRevision_AppendsOneLine_AndLeavesNoTempFile()
    {
        string root = TempRoot();
        try
        {
            StratStore store = new(root, utcNow: SteppingClock(Created));
            StratDocument document = Minimal();
            await Assert.That(store.Save(document, [], "created").Revision).IsEqualTo(1);

            (StratDocument moved, PatchOp op) = MoveStep(document, 1, 76);
            StratSaveResult result = store.Save(moved, [op], "molotov moved from 1:22 to 1:16");

            string folder = Path.GetDirectoryName(StratFile(root, document))!;
            IReadOnlyList<HistoryEntry> log = store.History(document.Id);
            using (Assert.Multiple())
            {
                await Assert.That(result.Saved).IsTrue();
                await Assert.That(result.Revision).IsEqualTo(2);
                await Assert.That(moved.Revision).IsEqualTo(2).Because("the caller's document is stamped to match the file");
                await Assert.That(moved.ModifiedUtc).IsEqualTo(Created.AddMinutes(1));
                await Assert.That(log.Count).IsEqualTo(2);
                await Assert.That(log[0].Ops.Single().Path).IsEqualTo("").Because("revision 1 is the whole document");
                await Assert.That(log[1].Ops.Single().From!.GetValue<double>()).IsEqualTo(82);
                await Assert.That(log[1].Summary).IsEqualTo("molotov moved from 1:22 to 1:16");
                await Assert.That(store.TryLoad(document.Id)!.Steps[1].AtSeconds).IsEqualTo(76);
                await Assert.That(Directory.EnumerateFiles(folder, "*.tmp", SearchOption.AllDirectories).Any()).IsFalse();
                await Assert.That(Directory.EnumerateFiles(folder, ".strat-*", SearchOption.AllDirectories).Any()).IsFalse();
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task History_IsAppendedNeverRewritten()
    {
        string root = TempRoot();
        try
        {
            StratStore store = new(root);
            StratDocument document = Minimal();
            store.Save(document, [], "created");
            string log = LogFile(root, document);

            string previous = File.ReadAllText(log);
            for (int i = 0; i < 5; i++)
            {
                (StratDocument next, PatchOp op) = MoveStep(document, 1, 80 - i);
                await Assert.That(store.Save(next, [op], null).Saved).IsTrue();
                document = next;

                string now = File.ReadAllText(log);
                await Assert.That(now.Length).IsGreaterThan(previous.Length);
                await Assert.That(now.StartsWith(previous, StringComparison.Ordinal)).IsTrue()
                    .Because("every earlier byte of the log is untouched by a later commit");
                previous = now;
            }

            await Assert.That(store.History(document.Id).Select(e => e.Revision)).IsEquivalentTo(SixRevisions);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task CrashBetweenTheLogAndTheStratWrite_IsReconciledOnLoad()
    {
        string root = TempRoot();
        try
        {
            StratStore store = new(root);
            StratDocument document = Minimal();
            store.Save(document, [], "created");
            store.SaveIndex();
            string file = StratFile(root, document);
            string revisionOne = File.ReadAllText(file);

            (StratDocument moved, PatchOp op) = MoveStep(document, 1, 70);
            store.Save(moved, [op], null);

            // The crash: the log has revision 2, the strat file never got past revision 1, and it is older.
            File.WriteAllText(file, revisionOne);
            File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(LogFile(root, document)).AddSeconds(-5));

            StratStore reopened = new(root);
            await Assert.That(reopened.Index.Single().Revision).IsEqualTo(2)
                .Because("a log newer than its strat is read at start, so the index is not left a revision behind");

            StratDocument loaded = reopened.TryLoad(document.Id)!;
            using (Assert.Multiple())
            {
                await Assert.That(loaded.Revision).IsEqualTo(2);
                await Assert.That(loaded.Steps[1].AtSeconds).IsEqualTo(70);
                await Assert.That(StratStore.Serialize(loaded)).IsEqualTo(File.ReadAllText(file))
                    .Because("the reconciled strat is written back");
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task ATornLastLine_IsSkipped_AndTheNextCommitStartsOnItsOwnLine()
    {
        string root = TempRoot();
        try
        {
            StratStore store = new(root);
            StratDocument document = Minimal();
            store.Save(document, [], "created");
            File.AppendAllText(LogFile(root, document), "{\"revision\":2,\"atUtc\":\"2026-09-");

            (StratDocument moved, PatchOp op) = MoveStep(document, 1, 70);
            await Assert.That(store.Save(moved, [op], null).Saved).IsTrue();

            IReadOnlyList<HistoryEntry> log = new StratStore(root).History(document.Id);
            await Assert.That(log.Select(e => e.Revision)).IsEquivalentTo(TwoRevisions);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task Save_OnARootThatCannotBeWritten_ReportsAFailure_AndDoesNotThrow()
    {
        // A file where the root folder should be: every directory create under it fails, on every OS.
        string blocker = TempRoot();
        File.WriteAllText(blocker, "not a folder");
        try
        {
            StratStore store = new(Path.Combine(blocker, "strats"));
            StratSaveResult result = store.Save(Minimal(), [], "created");
            using (Assert.Multiple())
            {
                await Assert.That(result.Saved).IsFalse();
                await Assert.That(result.Reason).IsNotNull();
                await Assert.That(store.Index.Count).IsEqualTo(0);
                await Assert.That(store.SaveBook(new StratBook { Owner = Team })).IsFalse();
            }
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Test]
    public async Task Save_ARefusedStrat_WritesNothing()
    {
        string root = TempRoot();
        try
        {
            StratStore store = new(root);
            StratDocument document = Minimal();
            document.Steps[1].Verb = "teleport";

            StratSaveResult result = store.Save(document, [], "created");
            using (Assert.Multiple())
            {
                await Assert.That(result.Saved).IsFalse();
                await Assert.That(result.Reason).Contains("teleport");
                await Assert.That(Directory.Exists(root)).IsFalse();
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task NullRoot_HoldsManyStrats_WithHistory_InMemory()
    {
        StratStore store = new(null);
        List<StratDocument> documents = [];
        for (int i = 0; i < 50; i++)
        {
            documents.Add(store.Create(i % 2 == 0 ? Team : StratOwner.Me(), i % 3 == 0 ? "de_nuke" : "de_mirage", "T", "default", $"strat {i}"));
        }

        StratDocument first = store.TryLoad(documents[0].Id)!;
        PatchOp op = PatchOp.ReplaceOp("/name", JsonValue.Create(first.Name), JsonValue.Create("renamed"));
        store.Save(StratHistory.Apply(first, [op]), [op], "renamed");

        using (Assert.Multiple())
        {
            await Assert.That(store.IsPersistent).IsFalse();
            await Assert.That(store.Index.Count).IsEqualTo(50);
            await Assert.That(store.TryLoad(documents[0].Id)!.Name).IsEqualTo("renamed");
            await Assert.That(store.History(documents[0].Id).Count).IsEqualTo(2);
            await Assert.That(store.Materialize(documents[0].Id, 1)!.Name).IsEqualTo("strat 0");
            await Assert.That(store.Query(Team, "de_nuke", null, null).Count).IsEqualTo(9);
        }

        await Assert.That(store.Delete(documents[1].Id)).IsTrue();
        await Assert.That(store.TryLoad(documents[1].Id)).IsNull();
        await Assert.That(store.Index.Count).IsEqualTo(49);
    }

    [Test]
    public async Task Delete_MovesBothFilesToTrash_AndTheStratStaysGone()
    {
        string root = TempRoot();
        try
        {
            List<Guid?> changed = [];
            StratStore store = new(root);
            store.Changed += id => changed.Add(id);
            StratDocument document = store.Create(Team, "de_mirage", "T", "execute", "A exec");
            string trash = Path.Combine(StratStore.FolderFor(root, Team, "de_mirage"), ".trash");

            await Assert.That(store.Delete(document.Id)).IsTrue();
            store.SaveIndex();

            using (Assert.Multiple())
            {
                await Assert.That(File.Exists(StratFile(root, document))).IsFalse();
                await Assert.That(File.Exists(Path.Combine(trash, document.Id + ".dvstrat.json"))).IsTrue();
                await Assert.That(File.Exists(Path.Combine(trash, document.Id + ".history.jsonl"))).IsTrue();
                await Assert.That(store.TryLoad(document.Id)).IsNull();
                await Assert.That(store.Index.Count).IsEqualTo(0);
                await Assert.That(changed.Last()).IsEqualTo(document.Id);
                await Assert.That(new StratStore(root).Index.Count).IsEqualTo(0).Because("the trash is never listed");
                await Assert.That(store.Delete(document.Id)).IsFalse();
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task SetStatus_IsAOneOpCommit()
    {
        StratStore store = new(null);
        StratDocument document = store.Create(Team, "de_mirage", "T", "execute", "A exec");

        await Assert.That(store.SetStatus(document.Id, StratStatus.Archived)).IsTrue();

        HistoryEntry entry = store.History(document.Id)[^1];
        using (Assert.Multiple())
        {
            await Assert.That(entry.Revision).IsEqualTo(2);
            await Assert.That(entry.Ops.Single().Path).IsEqualTo("/status");
            await Assert.That(entry.Summary).IsEqualTo("status Theory → Archived");
            await Assert.That(store.Query(null, null, null, StratStatus.Archived).Single().Id).IsEqualTo(document.Id);
            await Assert.That(store.SetStatus(Guid.NewGuid(), StratStatus.Active)).IsFalse();
        }
    }

    [Test]
    public async Task AMapChange_MovesTheStratAndItsLog()
    {
        string root = TempRoot();
        try
        {
            StratStore store = new(root);
            StratDocument document = Minimal();
            store.Save(document, [], "created");
            string oldFile = StratFile(root, document);

            PatchOp op = PatchOp.ReplaceOp("/map", JsonValue.Create("de_mirage"), JsonValue.Create("de_inferno"));
            StratDocument moved = StratHistory.Apply(document, [op]);
            await Assert.That(store.Save(moved, [op], null).Saved).IsTrue();

            using (Assert.Multiple())
            {
                await Assert.That(File.Exists(oldFile)).IsFalse();
                await Assert.That(File.Exists(StratFile(root, moved))).IsTrue();
                await Assert.That(File.ReadAllLines(LogFile(root, moved)).Length).IsEqualTo(2);
                await Assert.That(new StratStore(root).TryLoad(document.Id)!.Map).IsEqualTo("de_inferno");
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task BookAndCallouts_RoundTrip_AndADuplicateAliasIsRefused()
    {
        string root = TempRoot();
        try
        {
            StratStore store = new(root);
            StratBook book = store.LoadBook(Team);
            book.SlotDefaults["e1"] = new Dictionary<string, string> { ["A"] = "76561198000000001" };
            await Assert.That(store.SaveBook(book)).IsTrue();

            CalloutTable table = store.LoadCallouts(Team, "de_mirage");
            table.Aliases.Add(new CalloutAlias { Alias = "palace", Place = "PalaceInterior", Primary = true });
            await Assert.That(store.SaveCallouts(Team, table)).IsTrue();

            CalloutTable duplicate = store.LoadCallouts(Team, "de_mirage");
            duplicate.Aliases.Add(new CalloutAlias { Alias = "Palace", Place = "PalaceAlley" });

            StratStore reopened = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(File.Exists(Path.Combine(root, "team-" + TeamId, "book.json"))).IsTrue();
                await Assert.That(File.Exists(Path.Combine(root, "team-" + TeamId, "de_mirage", "callouts.json"))).IsTrue();
                await Assert.That(reopened.LoadBook(Team).SlotDefaults["e1"]["A"]).IsEqualTo("76561198000000001");
                await Assert.That(reopened.LoadCallouts(Team, "de_mirage").Aliases.Single().Primary).IsTrue();
                await Assert.That(store.SaveCallouts(Team, duplicate)).IsFalse();
                await Assert.That(reopened.LoadCallouts(StratOwner.Me(), "de_mirage").Aliases.Count).IsEqualTo(0);
                await Assert.That(reopened.Index.Count).IsEqualTo(0).Because("books and callouts are not strats");
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }
}
