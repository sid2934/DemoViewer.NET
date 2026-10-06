#region

using System.Text;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     An extension's per-demo data: a facet round-trips with its parts and stamp, a read under another
///     schema, fingerprint or demo reads as absent, the index comes back from the file headers, and a demo
///     that leaves the library takes its files along unless another demo has the same content.
/// </summary>
public class ExtensionDemoDataStoreTests
{
    private const string Demo = "/demos/a.dem";
    private const string Other = "/demos/b.dem";

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvextdata_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Seed(DemoCacheStore library, string path, string? sha) =>
        library.Update(path, 10, 20, r => r.Sha256 = sha);

    private static ExtensionDemoDataStore NewStore(string root, DemoCacheStore library, string id = "dev.example.data") =>
        new(id, Path.Combine(root, ExtensionFolders.DataDirectoryName, id), library, a => a(), "1.2.3");

    private static DemoDataWrite Rounds(string text, string fingerprint = "fp-1", int schema = 1) =>
        new("rounds", schema, fingerprint, Encoding.UTF8.GetBytes(text))
        {
            Parts = new Dictionary<string, ReadOnlyMemory<byte>> { ["positions"] = Encoding.UTF8.GetBytes(text + "-positions") },
            Count = 3
        };

    [Test]
    public async Task AFacet_RoundTripsWithItsParts_AndItsStampNeedsNoFile()
    {
        string root = NewRoot();
        try
        {
            DemoCacheStore library = new(null);
            Seed(library, Demo, "AAAA");
            ExtensionDemoDataStore store = NewStore(root, library);
            List<string?> told = [];
            store.Changed += told.Add;

            DemoDataStamp written = store.Write(Demo, Rounds("one"))!;

            DemoDataStamp stamp = store.Stamp(Demo, "rounds")!;
            using (Assert.Multiple())
            {
                await Assert.That(stamp).IsEqualTo(written);
                await Assert.That(stamp.IsCurrent(1, "fp-1")).IsTrue();
                await Assert.That(stamp.Sha256).IsEqualTo("aaaa");
                await Assert.That(stamp.Count).IsEqualTo(3);
                await Assert.That(Encoding.UTF8.GetString(store.Read(Demo, "rounds", 1, "fp-1")!)).IsEqualTo("one");
                await Assert.That(Encoding.UTF8.GetString(store.Read(Demo, "rounds", 1, "fp-1", "positions")!)).IsEqualTo("one-positions");
                await Assert.That(store.Stamps("rounds").Single().DemoPath).IsEqualTo(Demo);
                await Assert.That(store.ReadAny(Demo, "rounds")!.ExtensionVersion).IsEqualTo("1.2.3");
                await Assert.That(File.Exists(Path.Combine(store.Root, "rounds", "aaaa.json.gz"))).IsTrue();
                await Assert.That(told.Count == 1 && told[0] == Demo).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AReadUnderAnotherSchemaFingerprintOrDemo_ReadsAsAbsent()
    {
        string root = NewRoot();
        try
        {
            DemoCacheStore library = new(null);
            Seed(library, Demo, "aaaa");
            Seed(library, Other, "bbbb");
            ExtensionDemoDataStore store = NewStore(root, library);
            store.Write(Demo, Rounds("demo"));
            store.Write(Other, Rounds("other"));
            // Another demo's file under this demo's name: its header names the other content.
            File.Copy(Path.Combine(store.Root, "rounds", "aaaa.json.gz"), Path.Combine(store.Root, "rounds", "bbbb.json.gz"), true);

            using (Assert.Multiple())
            {
                await Assert.That(store.Read(Demo, "rounds", 2, "fp-1")).IsNull();
                await Assert.That(store.Read(Demo, "rounds", 1, "fp-2")).IsNull();
                await Assert.That(store.Read(Other, "rounds", 1, "fp-1")).IsNull().Because("the header names another demo's content");
                await Assert.That(store.ReadAny(Demo, "rounds")!.Fingerprint).IsEqualTo("fp-1");
                await Assert.That(store.Read("/demos/unknown.dem", "rounds", 1, "fp-1")).IsNull();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Stamps_MoveThroughFailedPendingAndARebuild_AndTheContentStaysReadableUntilRewritten()
    {
        DemoCacheStore library = new(null);
        Seed(library, Demo, "aaaa");
        string root = NewRoot();
        try
        {
            ExtensionDemoDataStore store = NewStore(root, library);
            store.Write(Demo, Rounds("one"));

            store.MarkFailed(Demo, "rounds");
            await Assert.That(store.Stamp(Demo, "rounds")!.State).IsEqualTo(DemoDataState.Failed);
            store.ClearFailed(Demo, "rounds");
            await Assert.That(store.Stamp(Demo, "rounds")!.State).IsEqualTo(DemoDataState.Pending);

            store.Write(Demo, Rounds("two"));
            store.Invalidate("rounds");
            DemoDataStamp stale = store.Stamp(Demo, "rounds")!;
            using (Assert.Multiple())
            {
                await Assert.That(stale.Fingerprint).IsNull();
                await Assert.That(stale.IsCurrent(1, "fp-1")).IsFalse();
                await Assert.That(Encoding.UTF8.GetString(store.ReadAny(Demo, "rounds")!.Content)).IsEqualTo("two");
            }

            store.SetCount(Other, "suggestions", 5);
            await Assert.That(store.Stamp(Other, "suggestions")).IsEqualTo(
                new DemoDataStamp(Other, null, "suggestions", 0, null, DemoDataState.Pending, 0, 5));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ALostIndex_IsRebuiltFromTheFileHeaders()
    {
        string root = NewRoot();
        try
        {
            DemoCacheStore library = new(null);
            Seed(library, Demo, "aaaa");
            ExtensionDemoDataStore store = NewStore(root, library);
            store.Write(Demo, Rounds("one"));
            File.Delete(Path.Combine(store.Root, "index.json.gz"));

            ExtensionDemoDataStore reopened = NewStore(root, library);
            DemoDataStamp stamp = reopened.Stamp(Demo, "rounds")!;
            using (Assert.Multiple())
            {
                await Assert.That(stamp.IsCurrent(1, "fp-1")).IsTrue();
                await Assert.That(reopened.Stamps("rounds").Count).IsEqualTo(1).Because("a part file is not a demo of its own");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task OnTheUiThread_AStampReadNeverLoadsTheIndex_ItQueuesTheLoadAndIsToldWhenItIsIn()
    {
        string root = NewRoot();
        try
        {
            DemoCacheStore library = new(null);
            Seed(library, Demo, "AAAA");
            NewStore(root, library).Write(Demo, Rounds("one"));
            List<Action> queued = [];
            string id = "dev.example.data";
            ExtensionDemoDataStore store = new(id, Path.Combine(root, ExtensionFolders.DataDirectoryName, id), library, a => a(), "1.2.3",
                onUiThread: () => true, loadInBackground: queued.Add);
            List<string?> told = [];
            store.Changed += told.Add;

            DemoDataStamp? before = store.Stamp(Demo, "rounds");
            IReadOnlyList<DemoDataStamp> none = store.Stamps("rounds");
            store.Stamp(Demo, "rounds");
            await Assert.That(queued.Count).IsEqualTo(1).Because("one load is queued however often the UI asks");
            queued[0]();

            using (Assert.Multiple())
            {
                await Assert.That(before).IsNull();
                await Assert.That(none).IsEmpty();
                await Assert.That(told).IsEquivalentTo([(string?)null]);
                await Assert.That(store.Stamp(Demo, "rounds")?.IsCurrent(1, "fp-1")).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ADemoWrittenBeforeItsHashWasKnown_MovesToItsHash()
    {
        string root = NewRoot();
        try
        {
            DemoCacheStore library = new(null);
            Seed(library, Demo, null);
            ExtensionDemoDataStore store = NewStore(root, library);
            store.Write(Demo, Rounds("one"));
            await Assert.That(store.Stamp(Demo, "rounds")!.Sha256).IsNull();

            Seed(library, Demo, "cccc");
            using (Assert.Multiple())
            {
                await Assert.That(store.Stamp(Demo, "rounds")!.Sha256).IsEqualTo("cccc");
                await Assert.That(Encoding.UTF8.GetString(store.Read(Demo, "rounds", 1, "fp-1", "positions")!)).IsEqualTo("one-positions");
                await Assert.That(Directory.GetFiles(Path.Combine(store.Root, "rounds")).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal))
                    .IsEquivalentTo(["cccc.json.gz", "cccc.positions.json.gz"]);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ADemoThatLeavesTheLibrary_TakesItsFiles_UnlessAnotherDemoHasTheSameContent()
    {
        string root = NewRoot();
        try
        {
            DemoCacheStore library = new(null);
            Seed(library, Demo, "aaaa");
            Seed(library, "/demos/copy-of-a.dem", "aaaa");
            Seed(library, Other, "bbbb");
            ExtensionDemoDataStore store = NewStore(root, library);
            store.Write(Demo, Rounds("a"));
            store.Write(Other, Rounds("b"));

            library.Remove(Demo);
            library.Remove(Other);

            using (Assert.Multiple())
            {
                await Assert.That(store.Stamp("/demos/copy-of-a.dem", "rounds")).IsNotNull();
                await Assert.That(store.Stamps("rounds").Single().DemoPath).IsEqualTo("/demos/copy-of-a.dem");
                await Assert.That(File.Exists(Path.Combine(store.Root, "rounds", "bbbb.json.gz"))).IsFalse();
                await Assert.That(File.Exists(Path.Combine(store.Root, "rounds", "bbbb.positions.json.gz"))).IsFalse();
            }

            store.Delete("/demos/copy-of-a.dem", "rounds");
            await Assert.That(Directory.GetFiles(Path.Combine(store.Root, "rounds"))).IsEmpty();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AFacetWrittenThroughOnePath_ReadsThroughTheOther_AndFollowsTheBytesWhenAPathIsReplaced()
    {
        string root = NewRoot();
        try
        {
            DemoCacheStore library = new(null);
            Seed(library, Demo, "aaaa");
            Seed(library, "/mnt/a.dem", "aaaa");
            ExtensionDemoDataStore store = NewStore(root, library);
            store.Write(Demo, Rounds("a"));

            await Assert.That(Encoding.UTF8.GetString(store.Read("/mnt/a.dem", "rounds", 1, "fp-1")!)).IsEqualTo("a");

            // The file at the written path is replaced by another match: its data goes with the bytes.
            library.Update(Demo, 11, 21, r => r.Sha256 = "cccc");

            using (Assert.Multiple())
            {
                await Assert.That(store.Stamp(Demo, "rounds")).IsNull();
                await Assert.That(store.Stamps("rounds").Single().DemoPath).IsEqualTo("/mnt/a.dem");
                await Assert.That(Encoding.UTF8.GetString(store.Read("/mnt/a.dem", "rounds", 1, "fp-1")!)).IsEqualTo("a");
            }

            library.Update("/mnt/a.dem", 11, 21, r => r.Sha256 = "dddd");
            using (Assert.Multiple())
            {
                await Assert.That(store.Stamps("rounds")).IsEmpty().Because("no path in the library holds those bytes any more");
                await Assert.That(Directory.GetFiles(Path.Combine(store.Root, "rounds"))).IsEmpty();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task APathWhoseRowLosesItsHash_KeepsItsFacet_ForTheNextHashToFind()
    {
        string root = NewRoot();
        try
        {
            DemoCacheStore library = new(null);
            Seed(library, Demo, "aaaa");
            ExtensionDemoDataStore store = NewStore(root, library);
            store.Write(Demo, Rounds("a"));

            // A moved write time reads as another file until the next hash says otherwise.
            library.Update(Demo, 10, 99, r => r.Sha256 = null);
            await Assert.That(Directory.GetFiles(Path.Combine(store.Root, "rounds"))).IsNotEmpty();

            library.Update(Demo, 10, 99, r => r.Sha256 = "aaaa");
            using (Assert.Multiple())
            {
                await Assert.That(store.Stamp(Demo, "rounds")).IsNotNull();
                await Assert.That(Encoding.UTF8.GetString(store.Read(Demo, "rounds", 1, "fp-1")!)).IsEqualTo("a");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task TwoExtensions_KeepApart_AndAFacetNameCannotLeaveTheFolder()
    {
        string root = NewRoot();
        try
        {
            DemoCacheStore library = new(null);
            Seed(library, Demo, "aaaa");
            ExtensionDemoDataStore one = NewStore(root, library, "dev.example.one");
            ExtensionDemoDataStore two = NewStore(root, library, "dev.example.two");
            one.Write(Demo, Rounds("one"));

            using (Assert.Multiple())
            {
                await Assert.That(two.Stamp(Demo, "rounds")).IsNull();
                await Assert.That(two.Read(Demo, "rounds", 1, "fp-1")).IsNull();
            }

            foreach (string facet in new[] { "../dev.example.two", "..", "a/b", "" })
            {
                await Assert.ThrowsAsync<ArgumentException>(() =>
                {
                    one.Write(Demo, new DemoDataWrite(facet, 1, null, new byte[] { 1 }));
                    return Task.CompletedTask;
                });
            }

            await Assert.ThrowsAsync<ArgumentException>(() =>
            {
                one.Write(Demo, Rounds("x") with
                {
                    Parts = new Dictionary<string, ReadOnlyMemory<byte>> { ["../escape"] = new byte[] { 1 } }
                });
                return Task.CompletedTask;
            });
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task TheBrowserBuild_KeepsNoPerDemoData_AndItsSettingsForTheSession()
    {
        UnavailableDemoData data = UnavailableDemoData.Instance;
        DemoDataStamp? written = data.Write(Demo, Rounds("one"));
        ExtensionSettingsStore settings = new("dev.example.browser", null, a => a());
        settings.Set("flag", true);

        using (Assert.Multiple())
        {
            await Assert.That(data.IsAvailable).IsFalse();
            await Assert.That(written).IsNull();
            await Assert.That(data.Stamp(Demo, "rounds")).IsNull();
            await Assert.That(data.Read(Demo, "rounds", 1, "fp-1")).IsNull();
            await Assert.That(settings.Get("flag", false)).IsTrue().Because("settings live in memory for the session");
        }
    }
}
