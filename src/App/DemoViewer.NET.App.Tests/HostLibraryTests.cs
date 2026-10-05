#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The library as extensions read it: the rows are the cache's index, an unchanged row is one instance,
///     lookups and pages read memory only, a change names its kind the way the store's event names its path,
///     the detail comes from the record, and a row from an older index carries no sides rather than empty ones.
/// </summary>
public class HostLibraryTests
{
    private const string Sha = "aa11";

    internal static DemoCacheRecord Parsed(string path, long ticks = 638000000000000000, string? sha = Sha)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 100,
            ModifiedTicks = ticks,
            Sha256 = sha,
            Map = "de_mirage",
            Server = "Valve CS2 Server",
            SourceKind = "Valve",
            DurationSeconds = 2400,
            TickRate = 64,
            TickCount = 153600,
            ServerStartTick = 10,
            CtScore = 13,
            TScore = 9,
            CtClan = "Blue",
            TClan = "Red",
            Players =
            [
                new CachedPlayerInfo { Slot = 1, Name = "b-ct", SteamId64 = "76561198000000002", Team = 3 },
                new CachedPlayerInfo { Slot = 2, Name = "a-ct", SteamId64 = "76561198000000001", Team = 3 },
                new CachedPlayerInfo { Slot = 3, Name = "a-ct again", SteamId64 = "76561198000000001", Team = 3 },
                new CachedPlayerInfo { Slot = 4, Name = "coach", SteamId64 = "76561198000000009", Team = 3, IsCoach = true },
                new CachedPlayerInfo { Slot = 5, Name = "bot", SteamId64 = "0", Team = 2, IsBot = true },
                new CachedPlayerInfo { Slot = 6, Name = "no id", SteamId64 = "0", Team = 2 },
                new CachedPlayerInfo { Slot = 7, Name = "t", SteamId64 = "76561198000000003", Team = 2 },
                new CachedPlayerInfo { Slot = 8, Name = "caster", SteamId64 = "76561198000000004", Team = 1 }
            ],
            Rounds = [new CachedRound { Number = 1, StartTickFrameClock = 100 }, new CachedRound { Number = 2, StartTickFrameClock = 5000 }]
        };
        DemoCacheStore.StampHeader(record);
        DemoCacheStore.StampParse(record);
        return record;
    }

    private static (DemoCacheStore Store, HostLibrary Library) Make()
    {
        DemoCacheStore store = new(null);
        return (store, new HostLibrary(store, null, () => false));
    }

    [Test]
    public async Task Demos_IsTheIndex_AndAnUnchangedRowIsTheSameInstance()
    {
        (DemoCacheStore store, HostLibrary library) = Make();
        store.Upsert(Parsed("/d/one.dem"));
        store.Upsert(Parsed("/d/two.dem", sha: "bb22"));

        IReadOnlyList<LibraryDemo> first = library.Demos;
        await Assert.That(first.Select(d => d.FilePath).Order()).IsEquivalentTo(store.Index.Select(e => e.Path).Order());
        await Assert.That(ReferenceEquals(library.Demos, first)).IsTrue();

        LibraryDemo one = library.Find("/D/ONE.dem")!;
        await Assert.That(one.Sha256).IsEqualTo(Sha);
        await Assert.That(one.Server).IsEqualTo("Valve CS2 Server");
        await Assert.That(one.SourceKind).IsEqualTo("Valve");
        await Assert.That(one.CtScore).IsEqualTo(13);
        await Assert.That(one.TClan).IsEqualTo("Red");
        await Assert.That(one.RoundCount).IsEqualTo(2);
        await Assert.That(one.State).IsEqualTo(LibraryDemoState.Parsed);
        await Assert.That(one.Modified.Ticks).IsEqualTo(638000000000000000);
        await Assert.That(ReferenceEquals(one, library.Find("/d/one.dem"))).IsTrue();
        await Assert.That(ReferenceEquals(one, library.FindBySha256(Sha))).IsTrue();

        store.Upsert(Parsed("/d/two.dem", sha: "cc33"));
        await Assert.That(ReferenceEquals(library.Demos, first)).IsFalse();
        await Assert.That(ReferenceEquals(library.Find("/d/one.dem"), one)).IsTrue();
    }

    [Test]
    public async Task Sides_AreHumanNonCoachPlayersWithAnId_SortedAndDeduplicated()
    {
        (DemoCacheStore store, HostLibrary library) = Make();
        store.Upsert(Parsed("/d/one.dem"));

        LibraryDemo demo = library.Find("/d/one.dem")!;
        await Assert.That(demo.CtPlayers!.Select(p => p.SteamId64)).IsEquivalentTo([76561198000000001UL, 76561198000000002UL]);
        await Assert.That(demo.CtPlayers![0].Name).IsEqualTo("a-ct");
        await Assert.That(demo.TPlayers!.Select(p => p.Name)).IsEquivalentTo(["t"]);
    }

    [Test]
    public async Task ARowFromAnOlderIndex_HasNoSides_AndARecordReadRefreshesIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-hostlib-" + Guid.NewGuid().ToString("N"));
        try
        {
            DemoCacheStore writer = new(root);
            writer.Upsert(Parsed("/d/old.dem"));
            writer.SaveIndex();
            string indexPath = Path.Combine(root, "index.json");
            JsonObject index = JsonNode.Parse(File.ReadAllText(indexPath))!.AsObject();
            index["Version"] = 2;
            foreach (JsonNode? row in index["Entries"]!.AsArray())
            {
                row!.AsObject().Remove("CtPlayers");
                row.AsObject().Remove("TPlayers");
            }

            File.WriteAllText(indexPath, index.ToJsonString());

            DemoCacheStore store = new(root);
            HostLibrary library = new(store, null, () => false);
            LibraryDemo before = library.Find("/d/old.dem")!;
            await Assert.That(before.CtPlayers).IsNull();
            await Assert.That(before.State).IsEqualTo(LibraryDemoState.Parsed);

            await Assert.That(store.RefreshIndexRow(store.TryLoadRecord("/d/old.dem", false)!)).IsTrue();
            await Assert.That(library.Find("/d/old.dem")!.CtPlayers!.Count).IsEqualTo(2);
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
    public async Task Changed_NamesTheKind_AndANullPathForABatch()
    {
        (DemoCacheStore store, HostLibrary library) = Make();
        List<LibraryChange> changes = [];
        library.Changed += changes.Add;

        store.Upsert(Parsed("/d/one.dem"));
        store.UpdateExisting("/d/one.dem", r => r.CtScore = 14);
        store.UpdateExisting("/d/one.dem", r => r.SetStamp(new PackStamp("roundfacts", 1, "fp")));
        using (store.BeginBatch())
        {
            store.Upsert(Parsed("/d/two.dem", sha: "bb"));
            store.Upsert(Parsed("/d/three.dem", sha: "cc"));
        }

        store.Remove("/d/one.dem");

        await Assert.That(changes).IsEquivalentTo([
            new LibraryChange("/d/one.dem", LibraryChangeKind.Added),
            new LibraryChange("/d/one.dem", LibraryChangeKind.Updated),
            new LibraryChange("/d/one.dem", LibraryChangeKind.FactsUpdated),
            new LibraryChange(null, LibraryChangeKind.Updated),
            new LibraryChange("/d/one.dem", LibraryChangeKind.Removed)
        ]);
        await Assert.That(library.Find("/d/two.dem")!.Fact("roundfacts")).IsNull();
    }

    [Test]
    public async Task Facts_MirrorTheRowsStamps()
    {
        (DemoCacheStore store, HostLibrary library) = Make();
        DemoCacheRecord record = Parsed("/d/one.dem");
        record.SetStamp(new PackStamp("roundfacts", 1, "fp") { Count = 22 });
        record.SetStamp(new PackStamp("other", 2, null) { State = DemoAnalysisState.Failed });
        store.Upsert(record);

        LibraryDemo demo = library.Find("/d/one.dem")!;
        await Assert.That(demo.Fact("roundfacts")).IsEqualTo(new LibraryFactState("roundfacts", 1, "fp", DemoDataState.Written, 22));
        await Assert.That(demo.Fact("roundfacts")!.IsCurrent(1, "fp")).IsTrue();
        await Assert.That(demo.Fact("other")!.State).IsEqualTo(DemoDataState.Failed);
        await Assert.That(demo.Fact("other")!.IsWritten).IsFalse();
    }

    [Test]
    public async Task Query_FiltersSortsAndPages()
    {
        (DemoCacheStore store, HostLibrary library) = Make();
        for (int i = 0; i < 5; i++)
        {
            DemoCacheRecord record = Parsed($"/d/{i}.dem", 638000000000000000 + i, "h" + i);
            record.Map = i % 2 == 0 ? "de_mirage" : "de_nuke";
            store.Upsert(record);
        }

        LibraryPage page = library.Query(new LibraryQuery { Map = "DE_MIRAGE", Skip = 1, Take = 1 });
        await Assert.That(page.Total).IsEqualTo(3);
        await Assert.That(page.Demos.Select(d => d.FilePath)).IsEquivalentTo(["/d/2.dem"]);

        LibraryPage oldest = library.Query(new LibraryQuery { Sort = LibrarySort.OldestFirst, Take = 2 });
        await Assert.That(oldest.Demos.Select(d => d.FilePath)).IsEquivalentTo(["/d/0.dem", "/d/1.dem"]);

        await Assert.That(library.Query(new LibraryQuery { SteamId64 = 76561198000000003 }).Total).IsEqualTo(5);
        await Assert.That(library.Query(new LibraryQuery { Clan = "blue", PlayerName = "A-CT" }).Total).IsEqualTo(5);
        await Assert.That(library.Query(new LibraryQuery { HasFact = "roundfacts" }).Total).IsEqualTo(0);
        await Assert.That(library.Query(new LibraryQuery { Skip = 9 }).Demos.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetDetail_ReadsTheRecord_OffTheUiThreadBeforeReturning()
    {
        (DemoCacheStore store, HostLibrary library) = Make();
        store.Upsert(Parsed("/d/one.dem"));

        Task<LibraryDemoDetail?> read = library.GetDetailAsync("/d/one.dem", CancellationToken.None);
        await Assert.That(read.IsCompleted).IsTrue();
        LibraryDemoDetail detail = (await read)!;
        await Assert.That(detail.TickRate).IsEqualTo(64);
        await Assert.That(detail.ServerStartTick).IsEqualTo(10);
        await Assert.That(detail.Players.Count).IsEqualTo(8);
        await Assert.That(detail.Players.Single(p => p.Slot == 5)).IsEqualTo(new LibraryPlayer(5, "bot", 0, 2, true, false));
        await Assert.That(detail.Rounds).IsEquivalentTo([new LibraryRound(1, 100), new LibraryRound(2, 5000)]);
        await Assert.That(ReferenceEquals(detail.Demo, library.Find("/d/one.dem"))).IsTrue();
        await Assert.That(await library.GetDetailAsync("/d/missing.dem", CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task AnExtensionsHandlerThatThrows_DoesNotStopItsNextHandler()
    {
        (DemoCacheStore store, HostLibrary library) = Make();
        ExtensionLibraryView view = new(library, ExtensionGuard.Standalone(new LibraryTestExtension()));
        int reached = 0;
        view.Changed += _ => throw new InvalidOperationException("boom");
        view.Changed += _ => reached++;

        store.Upsert(Parsed("/d/one.dem"));

        await Assert.That(reached).IsEqualTo(1);
    }

    internal sealed class LibraryTestExtension : IExtension
    {
        public string Id => "test.library";
        public string FeatureId => "test.library";
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }
}
