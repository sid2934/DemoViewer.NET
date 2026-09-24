#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Tags;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The live session (tag-store.md §3.9): one undo entry per gesture, refreshes outside the history
///     that undo does not roll back, the 200-entry cap, derived rounds, debounced autosave that leaves no
///     file for an untouched demo, and the status line on each host.
/// </summary>
[NotInParallel]
public class TagSessionTests
{
    private const string DemoPath = "/d/match.dem";

    private static readonly List<CachedRound> _rounds =
    [
        new() { Number = 1, StartTickFrameClock = 0 },
        new() { Number = 2, StartTickFrameClock = 10_000 },
        new() { Number = 3, StartTickFrameClock = 20_000 }
    ];

    private static readonly string[] WonAtA = ["won", "A"];
    private static readonly int?[] RoundsThreeAndOne = [3, 1];
    private static readonly string[] OneCode = ["A execute"];

    private static TagSession Session(TagStore? store, bool browser = false) =>
        new(store, _ => _rounds, () => browser, () => Created) { AutoSaveDelay = TimeSpan.FromHours(1) };

    private static async Task<TagSession> Attached(TagStore? store, bool browser = false)
    {
        TagSession session = Session(store, browser);
        await session.AttachAsync(Demo, Clock, DemoPath);
        return session;
    }

    [Test]
    public async Task ACodePressWithItsPanelLabels_IsOneUndoEntry()
    {
        using TagSession session = await Attached(new TagStore(null));
        TagInstance press = Instance("A execute", 12_000, 13_000);
        TagInstance labelled = press.Clone();
        labelled.Labels = [new TagLabel("outcome", "won"), new TagLabel("site", "A")];

        session.Apply(new TagDelta.Batch([new TagDelta.Add(press), new TagDelta.Replace(press.Id, labelled)]));

        using (Assert.Multiple())
        {
            await Assert.That(session.UndoDepth).IsEqualTo(1);
            await Assert.That(session.Document!.Instances.Single().Labels.Count).IsEqualTo(2);
            await Assert.That(session.Document.Instances.Single().Round).IsEqualTo(2)
                .Because("a new instance gets the round containing its start");
        }

        await Assert.That(session.Undo()).IsTrue();
        await Assert.That(session.Document!.Instances).IsEmpty();

        await Assert.That(session.Redo()).IsTrue();
        await Assert.That(session.Document!.Instances.Single().Labels.Select(l => l.Value))
            .IsEquivalentTo(WonAtA);
    }

    [Test]
    public async Task UndoAfterARefresh_RestoresTheHumanFields_AndKeepsTheRefreshedFacts()
    {
        using TagSession session = await Attached(new TagStore(null));
        TagInstance press = Instance("A execute", 12_000, 13_000);
        press.Positions = [new TagPosition { X = 1, Y = 2, LevelMinZ = 0, Tick = 12_500, PlaceSource = "pawn", Place = "Ramp" }];
        session.Apply(new TagDelta.Add(press));

        TagInstance noted = session.Document!.Instances[0].Clone();
        noted.Note = "late smokes";
        noted.Labels = [new TagLabel("outcome", "lost")];
        session.Apply(new TagDelta.Replace(press.Id, noted));

        int beforeRefresh = session.UndoDepth;
        session.ApplyExternal(d =>
        {
            d.Instances[0].Facts = [new TagLabel("side", "T")];
            d.Instances[0].FactsStamp = new TagFactsStamp { Schema = 1, ComputedUtc = Created };
            d.Instances[0].Positions[0].Place = "BombsiteA";
            d.Instances[0].Positions[0].PlaceSource = "zones:abc";
        });

        await Assert.That(session.UndoDepth).IsEqualTo(beforeRefresh).Because("a refresh is not undoable");

        session.Undo();
        TagInstance restored = session.Document!.Instances.Single();
        using (Assert.Multiple())
        {
            await Assert.That(restored.Note).IsNull();
            await Assert.That(restored.Labels).IsEmpty();
            await Assert.That(restored.Facts.Single().Value).IsEqualTo("T");
            await Assert.That(restored.FactsStamp!.Schema).IsEqualTo(1);
            await Assert.That(restored.Positions.Single().Place).IsEqualTo("BombsiteA");
            await Assert.That(restored.Positions.Single().PlaceSource).IsEqualTo("zones:abc");
        }
    }

    [Test]
    public async Task History_IsCappedAt200Entries()
    {
        using TagSession session = await Attached(new TagStore(null));
        for (int i = 0; i < TagSession.MaxHistoryEntries + 25; i++)
        {
            session.Apply(new TagDelta.Add(Instance("Default", i, i + 1)));
        }

        await Assert.That(session.UndoDepth).IsEqualTo(TagSession.MaxHistoryEntries);

        while (session.Undo())
        {
        }

        await Assert.That(session.Document!.Instances.Count).IsEqualTo(25)
            .Because("the oldest entries fell off the history; what they added stays");
    }

    [Test]
    public async Task ABackwardsSpan_IsRefused_BeforeAnythingChanges()
    {
        using TagSession session = await Attached(new TagStore(null));
        TagInstance good = Instance("A execute", 1, 2);
        TagInstance bad = Instance("B execute", 50, 10);

        Assert.Throws<ArgumentException>(() => session.Apply(new TagDelta.Batch([new TagDelta.Add(good), new TagDelta.Add(bad)])));
        await Assert.That(session.Document!.Instances).IsEmpty();
        await Assert.That(session.UndoDepth).IsEqualTo(0);
    }

    [Test]
    public async Task Attach_DerivesRoundsForInstancesWithoutOne()
    {
        TagStore store = new(null);
        store.Save(Document(Sha, Instance("A execute", 21_000, 22_000), Instance("Default", 5, 6)));

        using TagSession session = await Attached(store);

        await Assert.That(session.Document!.Instances.Select(i => i.Round)).IsEquivalentTo(RoundsThreeAndOne);
        await Assert.That(session.UndoDepth).IsEqualTo(0);
    }

    [Test]
    public async Task AnUntouchedDemo_LeavesNoFile()
    {
        string root = TempRoot();
        try
        {
            TagStore store = new(root);
            TagSession session = await Attached(store);
            await session.FlushAsync();
            session.Dispose();

            await Assert.That(File.Exists(TagStore.SidecarPathFor(root, Sha))).IsFalse();
            await Assert.That(session.SaveCount).IsEqualTo(0);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task RemovingTheLastInstance_OfAStoredDocument_IsWritten()
    {
        TagStore store = new(null);
        TagInstance only = Instance("A execute", 1, 2);
        store.Save(Document(Sha, only));

        using TagSession session = await Attached(store);
        session.Apply(new TagDelta.Remove(only.Id));
        await session.FlushAsync();

        await Assert.That(store.TryLoad(Sha)!.Instances).IsEmpty()
            .Because("clearing your tags has to stick");
    }

    [Test]
    public async Task Autosave_CoalescesABurst_AndDoesNotRewriteWhatIsSaved()
    {
        string root = TempRoot();
        try
        {
            TagStore store = new(root);
            using TagSession session = await Attached(store);
            session.AutoSaveDelay = TimeSpan.FromMilliseconds(40);
            for (int i = 0; i < 5; i++)
            {
                session.Apply(new TagDelta.Add(Instance("Default", i, i + 1)));
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (session.SaveCount == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            await Task.Delay(200);
            await session.FlushAsync(); // nothing new since the save: stands down

            using (Assert.Multiple())
            {
                await Assert.That(session.SaveCount).IsEqualTo(1);
                await Assert.That(store.TryLoad(Sha)!.Instances.Count).IsEqualTo(5);
                await Assert.That(session.StatusText).IsEqualTo("saving to " + TagStore.SidecarPathFor(root, Sha));
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task Detach_FlushesAndWritesTheIndex()
    {
        string root = TempRoot();
        try
        {
            TagStore store = new(root);
            TagSession session = await Attached(store);
            session.Apply(new TagDelta.Add(Instance("A execute", 1, 2)));
            session.Detach();

            await Assert.That(new TagStore(root).Index.Single().Codes).IsEquivalentTo(OneCode);
            await Assert.That(File.Exists(Path.Combine(root, "index.json"))).IsTrue();

            // Released: another session can take the document now.
            using TagSession next = await Attached(store);
            await Assert.That(next.Document!.Instances.Count).IsEqualTo(1);
            session.Dispose();
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task StatusLine_OnTheBrowser_SaysTheTabForgets()
    {
        using TagSession session = await Attached(new TagStore(null), browser: true);
        await Assert.That(session.StatusText).IsEqualTo("session only: this browser tab forgets tags when it reloads");
    }

    [Test]
    public async Task StatusLine_WithoutAStore_SaysNothingIsSaved()
    {
        using TagSession session = await Attached(null);
        session.Apply(new TagDelta.Add(Instance("A execute", 1, 2)));
        await session.FlushAsync();

        await Assert.That(session.StatusText).IsEqualTo("session only: tags are not saved");
        await Assert.That(session.SaveCount).IsEqualTo(0);
    }

    [Test]
    public async Task StatusLine_ReportsAFailedSave()
    {
        string blocker = TempRoot();
        File.WriteAllText(blocker, "not a directory");
        try
        {
            using TagSession session = await Attached(new TagStore(blocker));
            session.Apply(new TagDelta.Add(Instance("A execute", 1, 2)));
            await session.FlushAsync();

            await Assert.That(session.StatusText).IsEqualTo("tags could not be saved: session only");
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Test]
    public async Task StatusLine_WarnsOnAClockMismatch_AndRefusesAForeignFile()
    {
        string root = TempRoot();
        try
        {
            TagStore store = new(root);
            TagDocument stored = Document(Sha, Instance("A execute", 1, 2));
            stored.Clock.FrameCount = 1;
            store.Save(stored);

            using (TagSession session = await Attached(store))
            {
                await Assert.That(session.ClockMismatch).IsTrue();
                await Assert.That(session.StatusText).StartsWith("loaded from a different parse: tag spans may be off · saving to ");
            }

            File.WriteAllText(TagStore.SidecarPathFor(root, Sha),
                File.ReadAllText(TagStore.SidecarPathFor(root, Sha)).Replace(Sha, new string('e', 64), StringComparison.Ordinal));
            using TagSession foreign = await Attached(new TagStore(root));
            foreign.Apply(new TagDelta.Add(Instance("Default", 1, 2)));
            await foreign.FlushAsync();

            await Assert.That(foreign.DemoMismatch).IsTrue();
            await Assert.That(foreign.StatusText)
                .IsEqualTo("an existing tag file belongs to a different demo: it will not be touched");
            await Assert.That(File.ReadAllText(TagStore.SidecarPathFor(root, Sha))).DoesNotContain("Default");
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task RoundAt_IsTheLatestStartAtOrBeforeTheTick()
    {
        using (Assert.Multiple())
        {
            await Assert.That(TagSession.RoundAt(_rounds, 0)).IsEqualTo(1);
            await Assert.That(TagSession.RoundAt(_rounds, 9_999)).IsEqualTo(1);
            await Assert.That(TagSession.RoundAt(_rounds, 10_000)).IsEqualTo(2);
            await Assert.That(TagSession.RoundAt(_rounds, 99_999)).IsEqualTo(3);
            await Assert.That(TagSession.RoundAt([new CachedRound { Number = 1, StartTickFrameClock = 500 }], 10)).IsNull();
            await Assert.That(TagSession.RoundAt(null, 10)).IsNull();
        }
    }
}
