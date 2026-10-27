#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using TUnit.Assertions.Enums;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The one thing <c>StratStore</c>'s crash detection rests on: a commit appends the history line and only
///     then replaces the strat file, so after any commit the strat file is never older than its log, and a log
///     strictly newer than its strat means the second write never happened.
///     <para>
///         <c>StratStoreTests.CrashBetweenTheLogAndTheStratWrite_IsReconciledOnLoad</c> forces both stamps by
///         hand, so it proves the detection and not the order a real commit leaves behind. These cases read the
///         stamps a real commit wrote, and pin the comparison at its boundary: equal stamps are not a crash.
///     </para>
/// </summary>
[NotInParallel]
public class StratStoreWriteOrderTests
{
    private static readonly string[] EveryCommitInOrder =
        ["created: strat >= log", "moved: strat >= log", "moved again: strat >= log", "map changed: strat >= log"];

    // The folder the app hands the store, not a test double, so the order pinned here is the host's own
    // ExtensionFolder. The store takes its path today and takes the folder itself after the migration.
    private static StratStore Store(IExtensionFolder strats) => new(strats.Path);

    private static IExtensionFolder Strats(string root) =>
        new ExtensionStores(StratBookPack.PackId, Path.Combine(root, "config"), Path.Combine(root, "cache"),
            StratBookStores.All).Open(StratBookStores.Strats)!;

    private static string StratFile(IExtensionFolder strats, StratDocument document) =>
        Path.Combine(StratStore.FolderFor(strats.Path, document.Owner, document.Map), document.Id + StratStore.StratExtension);

    private static string LogFile(IExtensionFolder strats, StratDocument document) =>
        Path.Combine(StratStore.FolderFor(strats.Path, document.Owner, document.Map), document.Id + StratStore.HistoryExtension);

    // Named rather than a bool so a failure says which commit inverted the order.
    private static string Order(IExtensionFolder strats, StratDocument document, string stage) =>
        $"{stage}: " + (File.GetLastWriteTimeUtc(StratFile(strats, document)) >= File.GetLastWriteTimeUtc(LogFile(strats, document))
            ? "strat >= log"
            : "LOG AHEAD");

    [Test]
    public async Task EveryCommit_LeavesTheStratFileNoOlderThanItsLog()
    {
        string root = TempRoot();
        try
        {
            IExtensionFolder strats = Strats(root);
            StratStore store = Store(strats);
            StratDocument document = Minimal();
            store.Save(document, [], "created");
            List<string> stamps = [Order(strats, document, "created")];

            (StratDocument moved, PatchOp first) = MoveStep(document, 1, 70);
            store.Save(moved, [first], null);
            stamps.Add(Order(strats, moved, "moved"));

            (StratDocument again, PatchOp second) = MoveStep(moved, 1, 55);
            store.Save(again, [second], null);
            stamps.Add(Order(strats, again, "moved again"));

            // A map change moves both files before it commits, so the order has to survive the move as well.
            PatchOp remap = PatchOp.ReplaceOp("/map", JsonValue.Create("de_mirage"), JsonValue.Create("de_inferno"));
            StratDocument relocated = StratHistory.Apply(again, [remap]);
            store.Save(relocated, [remap], null);
            stamps.Add(Order(strats, relocated, "map changed"));

            await Assert.That(stamps).IsEquivalentTo(EveryCommitInOrder, CollectionOrdering.Matching)
                .Because("the log is appended first, so a strat file older than its log is a commit that never finished");
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task ALogStampedWithItsStrat_IsNotACrashAtStart_AndTheOpenStillRecoversTheCommit()
    {
        string root = TempRoot();
        try
        {
            IExtensionFolder strats = Strats(root);
            StratStore store = Store(strats);
            StratDocument document = Minimal();
            store.Save(document, [], "created");
            store.SaveIndex();
            string file = StratFile(strats, document);
            string revisionOne = File.ReadAllText(file);

            (StratDocument moved, PatchOp op) = MoveStep(document, 1, 70);
            store.Save(moved, [op], null);

            // Revision 1 back on disk, stamped exactly with the log. Within RowSlack, so WrittenAfter stays
            // out of it and the equal stamps are the only thing under test.
            File.WriteAllText(file, revisionOne);
            File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(LogFile(strats, document)));

            StratStore reopened = Store(strats);
            await Assert.That(reopened.Index.Single().Revision).IsEqualTo(1)
                .Because("a log no newer than its strat is not read at start; only a strictly newer one is");

            await Assert.That(reopened.TryLoad(document.Id)!.Revision).IsEqualTo(2)
                .Because("a crash inside the timestamp resolution is still caught when the strat is opened");
            await Assert.That(Order(strats, moved, "recovered")).IsEqualTo("recovered: strat >= log")
                .Because("the writeback restores the order, so the next start trusts its row again");
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task ALogNewerThanItsStrat_IsReadAtStart_AndTheWritebackRestoresTheOrder()
    {
        string root = TempRoot();
        try
        {
            IExtensionFolder strats = Strats(root);
            StratStore store = Store(strats);
            StratDocument document = Minimal();
            store.Save(document, [], "created");
            store.SaveIndex();
            string file = StratFile(strats, document);
            string revisionOne = File.ReadAllText(file);

            (StratDocument moved, PatchOp op) = MoveStep(document, 1, 70);
            store.Save(moved, [op], null);

            File.WriteAllText(file, revisionOne);
            File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(LogFile(strats, document)).AddSeconds(-1));

            StratStore reopened = Store(strats);
            using (Assert.Multiple())
            {
                await Assert.That(reopened.Index.Single().Revision).IsEqualTo(2)
                    .Because("the log is a second ahead of its strat, which is the crash the two stats look for");
                await Assert.That(Order(strats, moved, "recovered")).IsEqualTo("recovered: strat >= log");
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static (StratDocument Next, PatchOp Op) MoveStep(StratDocument document, int step, double to)
    {
        PatchOp op = PatchOp.ReplaceOp($"/steps/{step}/atSeconds", JsonValue.Create(document.Steps[step].AtSeconds), JsonValue.Create(to));
        return (StratHistory.Apply(document, [op]), op);
    }
}
