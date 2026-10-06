#region

using System.Text;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Services.Tags;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services.DemoCache;
using static DemoViewer.NET.AppTests.DemoLibraryScanTests;
using static DemoViewer.NET.AppTests.LibraryFingerprintTests;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Everything kept about a demo outlives its folder leaving the library, inside the grace period: its
///     tags and the strats they name, its annotations, an extension's per-demo data and its round facts. The
///     folder coming back finds all of it again, with no parse.
/// </summary>
[NotInParallel]
public class FolderRemovalKeepsDataTests
{
    private const string FactsSuffix = ".facts.test";

    [Test]
    public async Task AFolderRemovedAndAddedBack_KeepsTagsStratsAnnotationsExtensionDataAndRoundFacts()
    {
        string keep = NewTempDir();
        string away = NewTempDir();
        string config = NewTempDir();
        try
        {
            Write(keep, "k.dem", Bytes(50));
            byte[] bytes = Bytes(51);
            string demo = Write(away, "m.dem", bytes);
            string sha = DemoContentHash.Compute(bytes);
            using Library library = new(config, new DemoCacheStore(Path.Combine(config, "cache")));
            DemoLibraryService svc = library.Service;
            DemoCacheStore cache = library.Store;

            await svc.AddFoldersAsync([keep, away]);
            await svc.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => cache.TryGetByContentId(sha) is not null && svc.Tier2Backlog().Count == 0
                                                                             && svc.Entries.Count == 2,
                "both demos indexed");

            ClockIdentity clock = new(ClockIdentity.DvFrameClock, 64, 1000, 0, 0);
            DemoIdentity identity = new(sha, "m.dem", bytes.Length);
            TagStore tags = new(Path.Combine(config, "tags"));
            TagDocument document = TagDocument.Create(identity, clock);
            document.Instances.Add(new TagInstance
            {
                Id = Guid.NewGuid(), Code = "A execute", FromTick = 1, ToTick = 2,
                Labels = [new TagLabel(TagStore.StratGroup, "a-split")]
            });
            await Assert.That(tags.Save(document)).IsTrue();
            tags.SaveIndex();

            AnnotationStore annotations = new(Path.Combine(config, "annotations"), _ => sha);
            AnnotationElement stroke = new(Guid.NewGuid(), AnnotationKind.Freehand, AnnotationStyle.Default,
                new SpaceRef.World(0), TimeEnvelope.Static, [new InkPoint(0, 0, 0.5f), new InkPoint(40, 10, 0.5f)], null);
            await Assert.That(await annotations.SaveAsync(demo, identity, clock, [stroke])).IsTrue();

            ExtensionDemoDataStore data = new("dev.example.data", Path.Combine(config, "ext"), cache, a => a(), "1.0.0");
            data.Write(demo, new DemoDataWrite("rounds", 1, "fp", Encoding.UTF8.GetBytes("rounds of m")));
            cache.WriteSibling(demo, FactsSuffix, "facts of m");

            await svc.RemoveFolderAsync(away);
            await WaitForAsync(() => svc.Entries.Count == 1, "the folder's card gone");
            await Assert.That(cache.TryGetIndex(demo)).IsNull();

            // A reopened store sees only what is on disk.
            TagStore tagsAfter = new(Path.Combine(config, "tags"));
            ExtensionDemoDataStore dataAfter = new("dev.example.data", Path.Combine(config, "ext"), cache, a => a(), "1.0.0");
            using (Assert.Multiple())
            {
                await Assert.That(tagsAfter.TryLoad(sha)?.Instances.Single().Labels.Single().Value).IsEqualTo("a-split");
                await Assert.That(tagsAfter.Index.Single().StratIds).IsEquivalentTo(["a-split"]);
                await Assert.That((await new AnnotationStore(Path.Combine(config, "annotations"), _ => sha).LoadAsync(demo, clock))
                    .Elements).HasCount(1);
                await Assert.That(cache.TryGetOrphan(sha)).IsNotNull();
                await Assert.That(Directory.GetFiles(Path.Combine(config, "ext", "demo-data", "rounds"))).IsNotEmpty();
            }

            await svc.AddFoldersAsync([away]);
            await WaitForAsync(() => svc.Entries.Count == 2 && svc.Entries.All(e => e.State == DemoIndexState.Indexed),
                "the folder's card back");
            await Task.Delay(150);

            using (Assert.Multiple())
            {
                await Assert.That(library.Parses.GetValueOrDefault(demo)).IsEqualTo(1);
                await Assert.That(Encoding.UTF8.GetString(dataAfter.Read(demo, "rounds", 1, "fp")!)).IsEqualTo("rounds of m");
                await Assert.That(Encoding.UTF8.GetString(data.Read(demo, "rounds", 1, "fp")!)).IsEqualTo("rounds of m");
                await Assert.That(cache.TryReadSibling(demo, FactsSuffix)).IsEqualTo("facts of m");
                await Assert.That(new TagStore(Path.Combine(config, "tags")).TryLoad(sha)).IsNotNull();
            }
        }
        finally
        {
            Cleanup(keep);
            Cleanup(away);
            Cleanup(config);
        }
    }
}
