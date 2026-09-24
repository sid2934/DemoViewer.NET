#region

using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services.Tags;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The tag store (tag-store.md §3.5): the index and sidecar split under the config root, lazy reads,
///     corruption that reads as absent and is never overwritten, the index rebuilt and reconciled from
///     <c>demos/</c>, atomic writes, a failed write reported rather than thrown, the in-memory browser mode,
///     and <c>Update</c> routed to a checked-out session.
/// </summary>
[NotInParallel]
public class TagStoreTests
{
    private static readonly string[] TwoCodes = ["A execute", "Default"];
    private static readonly string[] OneStrat = ["a-split"];

    private static string ShaOf(int n) => n.ToString("x64", System.Globalization.CultureInfo.InvariantCulture);

    [Test]
    public async Task Save_WritesTheSidecarUnderDemos_AndTheIndexRowWithCodesAndStratIds()
    {
        string root = TempRoot();
        try
        {
            TagStore store = new(root);
            TagInstance a = Instance("A execute", 100, 200, (TagStore.StratGroup, "a-split"));
            TagInstance b = Instance("Default", 300, 400, (TagStore.StratGroup, "a-split"), ("outcome", "won"));
            TagInstance c = Instance("A execute", 500, 600);

            await Assert.That(store.Save(Document(Sha, a, b, c))).IsTrue();

            TagIndexEntry row = store.Index.Single();
            using (Assert.Multiple())
            {
                await Assert.That(File.Exists(Path.Combine(root, "demos", Sha + ".dvtag.json"))).IsTrue();
                await Assert.That(row.Sha256).IsEqualTo(Sha);
                await Assert.That(row.InstanceCount).IsEqualTo(3);
                await Assert.That(row.Codes).IsEquivalentTo(TwoCodes);
                await Assert.That(row.StratIds).IsEquivalentTo(OneStrat);
                await Assert.That(row.TickRate).IsEqualTo(64);
                await Assert.That(File.Exists(Path.Combine(root, "index.json"))).IsFalse()
                    .Because("index.json is deferred to SaveIndex, the DemoCacheStore rule");
            }

            store.SaveIndex();
            TagStore reopened = new(root);
            await Assert.That(reopened.Index.Single().InstanceCount).IsEqualTo(3);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task AFreshStore_WithNothingTagged_CreatesNoDirectory()
    {
        string root = TempRoot();
        _ = new TagStore(root);
        await Assert.That(Directory.Exists(root)).IsFalse();
    }

    [Test]
    public async Task Sidecars_AreReadLazily_NotAtStart()
    {
        string root = TempRoot();
        try
        {
            TagStore store = new(root);
            store.Save(Document(Sha, Instance("A execute", 1, 2)));
            store.SaveIndex();

            // Corrupt the sidecar behind the index's back: a store that read sidecars at start would
            // drop the row, one that reads lazily keeps it and finds out only when asked.
            File.WriteAllText(TagStore.SidecarPathFor(root, Sha), "{ not json");
            TagStore reopened = new(root);

            await Assert.That(reopened.Index.Count).IsEqualTo(1);
            await Assert.That(reopened.TryLoad(Sha)).IsNull();
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task CorruptSidecar_ReadsAsNull_AndIsNeverOverwritten()
    {
        string root = TempRoot();
        try
        {
            string path = TagStore.SidecarPathFor(root, Sha);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ \"schemaVersion\": 1, \"instances\": [");

            TagStore store = new(root);
            TagLoadResult result = store.Load(Sha, Clock);
            TagDocument fresh = store.LoadOrCreate(Demo, Clock);
            fresh.Instances.Add(Instance("A execute", 1, 2));

            using (Assert.Multiple())
            {
                await Assert.That(result.Document).IsNull();
                await Assert.That(result.Unreadable).IsTrue();
                await Assert.That(store.TryLoad(Sha)).IsNull();
                await Assert.That(store.Save(fresh)).IsFalse()
                    .Because("a file someone may have hand-edited is not replaced by what this session typed");
                await Assert.That(File.ReadAllText(path)).IsEqualTo("{ \"schemaVersion\": 1, \"instances\": [");
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task CorruptIndex_IsRebuiltFromTheSidecars()
    {
        string root = TempRoot();
        try
        {
            TagStore store = new(root);
            store.Save(Document(ShaOf(1), Instance("A execute", 1, 2)));
            store.Save(Document(ShaOf(2), Instance("B execute", 1, 2), Instance("Default", 3, 4)));
            store.SaveIndex();
            File.WriteAllText(Path.Combine(root, "index.json"), "][");

            TagStore reopened = new(root);

            using (Assert.Multiple())
            {
                await Assert.That(reopened.Index.Count).IsEqualTo(2);
                await Assert.That(reopened.Index.Single(e => e.Sha256 == ShaOf(2)).InstanceCount).IsEqualTo(2);
                await Assert.That(File.ReadAllText(Path.Combine(root, "index.json"))).Contains(ShaOf(2));
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task IndexMissingARow_IsReconciledFromTheListing()
    {
        string root = TempRoot();
        try
        {
            TagStore store = new(root);
            store.Save(Document(ShaOf(1), Instance("A execute", 1, 2)));
            store.SaveIndex();

            // A crash after a sidecar write and before SaveIndex: the file exists, the index never heard.
            store.Save(Document(ShaOf(2), Instance("B execute", 1, 2)));
            File.Delete(TagStore.SidecarPathFor(root, ShaOf(1)));

            TagStore reopened = new(root);
            await Assert.That(reopened.Index.Select(e => e.Sha256)).IsEquivalentTo(new[] { ShaOf(2) });
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task AtomicOverwrite_LeavesNoTempFile()
    {
        string root = TempRoot();
        try
        {
            TagStore store = new(root);
            TagDocument document = Document(Sha, Instance("A execute", 1, 2));
            store.Save(document);
            document.Instances.Add(Instance("Default", 3, 4));
            await Assert.That(store.Save(document)).IsTrue();

            string[] files = Directory.GetFiles(Path.Combine(root, "demos"));
            await Assert.That(files.Length).IsEqualTo(1);
            await Assert.That(store.TryLoad(Sha)!.Instances.Count).IsEqualTo(2);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task Save_OnAnUnwritableRoot_ReturnsFalse_AndDoesNotThrow()
    {
        // A FILE where the root directory should be: every create under it fails, on every OS.
        string blocker = TempRoot();
        File.WriteAllText(blocker, "not a directory");
        try
        {
            TagStore store = new(blocker);
            bool saved = store.Save(Document(Sha, Instance("A execute", 1, 2)));

            await Assert.That(saved).IsFalse();
            await Assert.That(store.Index.Count).IsEqualTo(0);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Test]
    public async Task NullRoot_HoldsManyDocuments_InMemory()
    {
        TagStore store = new(null);
        for (int i = 1; i <= 5; i++)
        {
            store.Save(Document(ShaOf(i), Instance("A execute", i, i + 10)));
        }

        using (Assert.Multiple())
        {
            await Assert.That(store.IsPersistent).IsFalse();
            await Assert.That(store.PathFor(ShaOf(1))).IsNull();
            await Assert.That(store.Index.Count).IsEqualTo(5);
            await Assert.That(store.LoadDocuments().Count).IsEqualTo(5)
                .Because("the second document must not evict the first (the _memoryRecords lesson)");
            await Assert.That(store.TryLoad(ShaOf(1))!.Instances[0].FromTick).IsEqualTo(1);
        }
    }

    [Test]
    public async Task LoadDocuments_FiltersOnTheIndexRow()
    {
        TagStore store = new(null);
        store.Save(Document(ShaOf(1), Instance("A execute", 1, 2)));
        store.Save(Document(ShaOf(2), Instance("B execute", 1, 2)));

        List<TagDocument> documents = store.LoadDocuments(e => e.Codes.Contains("B execute"));
        await Assert.That(documents.Single().Demo.Sha256).IsEqualTo(ShaOf(2));
    }

    [Test]
    public async Task Update_WithoutASession_IsAReadModifyWrite()
    {
        TagStore store = new(null);
        store.Save(Document(Sha, Instance("A execute", 1, 2)));

        store.Update(Sha, d => d.Instances[0].Facts = [new TagLabel("side", "T")]);
        store.Update(ShaOf(9), d => d.Instances.Clear()); // nothing stored: nothing to refresh

        await Assert.That(store.TryLoad(Sha)!.Instances[0].Facts.Single().Value).IsEqualTo("T");
        await Assert.That(store.Contains(ShaOf(9))).IsFalse();
    }

    [Test]
    public async Task Update_RoutesToTheCheckedOutSession_AndNotToDisk()
    {
        string root = TempRoot();
        try
        {
            TagStore store = new(root);
            store.Save(Document(Sha, Instance("A execute", 1, 2)));
            DateTime before = File.GetLastWriteTimeUtc(TagStore.SidecarPathFor(root, Sha));

            using TagSession session = new(store);
            session.AutoSaveDelay = TimeSpan.FromHours(1);
            await session.AttachAsync(Demo, Clock, "/d/match.dem");
            int version = session.Version;

            store.Update(Sha, d => d.Instances[0].Facts = [new TagLabel("side", "CT")]);

            using (Assert.Multiple())
            {
                await Assert.That(session.Document!.Instances[0].Facts.Single().Value).IsEqualTo("CT");
                await Assert.That(session.Version).IsGreaterThan(version);
                await Assert.That(session.UndoDepth).IsEqualTo(0).Because("a refresh is not the user's edit");
                await Assert.That(store.TryLoad(Sha)!.Instances[0].Facts).IsEmpty()
                    .Because("the session is the single writer; the file changes on its next autosave");
                await Assert.That(File.GetLastWriteTimeUtc(TagStore.SidecarPathFor(root, Sha))).IsEqualTo(before);
            }

            session.Detach();
            await Assert.That(store.TryLoad(Sha)!.Instances[0].Facts.Single().Value).IsEqualTo("CT");
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task CheckOut_IsExclusivePerProcess_AndReleasedOnDispose()
    {
        TagStore store = new(null);
        using TagSession first = new(store);
        using TagSession second = new(store);

        IDisposable held = store.CheckOut(Sha, first);
        Assert.Throws<InvalidOperationException>(() => store.CheckOut(Sha, second));

        held.Dispose();
        using IDisposable taken = store.CheckOut(Sha, second);
        await Assert.That(taken).IsNotNull();
    }

    [Test]
    public async Task Changed_NamesTheHash_AndABatchFiresOnceWithNull()
    {
        TagStore store = new(null);
        List<string?> raised = [];
        store.Changed += raised.Add;

        store.Save(Document(ShaOf(1), Instance("A execute", 1, 2)));
        using (store.BeginBatch())
        {
            store.Save(Document(ShaOf(2), Instance("A execute", 1, 2)));
            store.Save(Document(ShaOf(3), Instance("A execute", 1, 2)));
        }

        await Assert.That(raised).IsEquivalentTo(new[] { ShaOf(1), null });
    }

    [Test]
    public async Task Delete_RemovesTheFileAndTheRow()
    {
        string root = TempRoot();
        try
        {
            TagStore store = new(root);
            store.Save(Document(Sha, Instance("A execute", 1, 2)));

            await Assert.That(store.Delete(Sha)).IsTrue();
            await Assert.That(File.Exists(TagStore.SidecarPathFor(root, Sha))).IsFalse();
            await Assert.That(store.Index).IsEmpty();
            await Assert.That(store.Delete(Sha)).IsFalse();
        }
        finally
        {
            DeleteQuietly(root);
        }
    }
}

/// <summary>
///     Identity rules shared with the annotation sidecar: a body that names another demo is ignored and
///     never overwritten; a clock mismatch loads and says so; an all-zero clock is unknown and never warns.
/// </summary>
[NotInParallel]
public class TagStoreIdentityTests
{
    private const string Other = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    [Test]
    public async Task HashMismatchInTheBody_IsIgnored_AndNotOverwritten()
    {
        string root = TempRoot();
        try
        {
            TagStore writer = new(root);
            writer.Save(Document(Other, Instance("A execute", 1, 2)));

            // Someone copied another demo's sidecar under this demo's name.
            string foreign = File.ReadAllText(TagStore.SidecarPathFor(root, Other));
            string path = TagStore.SidecarPathFor(root, Sha);
            File.WriteAllText(path, foreign);

            TagStore store = new(root);
            TagLoadResult result = store.Load(Sha, Clock);
            TagDocument fresh = store.LoadOrCreate(Demo, Clock);
            fresh.Instances.Add(Instance("Default", 1, 2));

            using (Assert.Multiple())
            {
                await Assert.That(result.DemoMismatch).IsTrue();
                await Assert.That(result.Document).IsNull();
                await Assert.That(fresh.Instances.Single().Code).IsEqualTo("Default");
                await Assert.That(store.Save(fresh)).IsFalse();
                await Assert.That(File.ReadAllText(path)).IsEqualTo(foreign);
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task ClockMismatch_LoadsWithTheFlag()
    {
        TagStore store = new(null);
        store.Save(Document(Sha, Instance("A execute", 1, 2)));

        TagLoadResult result = store.Load(Sha, Clock with { FrameCount = 1000 });

        await Assert.That(result.ClockMismatch).IsTrue();
        await Assert.That(result.Document!.Instances.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AllZeroClock_NeverWarns()
    {
        TagStore store = new(null);
        TagDocument document = Document(Sha, Instance("A execute", 1, 2));
        document.Clock = TagClockHeader.From(ClockIdentity.Unknown);
        store.Save(document);

        await Assert.That(store.Load(Sha, Clock).ClockMismatch).IsFalse();
        await Assert.That(store.Load(Sha, ClockIdentity.Unknown).ClockMismatch).IsFalse();
    }

    [Test]
    public async Task UppercaseHash_IsTheSameDocument()
    {
        TagStore store = new(null);
        store.Save(Document(Sha.ToUpperInvariant(), Instance("A execute", 1, 2)));

        await Assert.That(store.TryLoad(Sha)).IsNotNull();
        await Assert.That(store.Index.Single().Sha256).IsEqualTo(Sha);
    }
}
