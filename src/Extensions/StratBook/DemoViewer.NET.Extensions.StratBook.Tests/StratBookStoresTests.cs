#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.AppTests.Extensions.StratBook;

/// <summary>
///     <see cref="StratBookStores.All" /> against the real writers: every descriptor's path is
///     exercised by constructing the real store over a temp root and writing one real item, so a renamed
///     root folder (the thing a "delete extension data" action actually keys on) fails this test rather
///     than being caught only by a user's missing file. Stores whose write needs a deep dependency graph
///     (<c>TeamIdentityService</c>, <c>WatchedSituationsService</c>, <c>StratMiningService</c>'s state
///     file, <c>LineupClipService</c>) write at the exact path those types document instead of being
///     constructed in full; <c>palettes/</c> has no writer at all (a user drop-in), so a file is placed by
///     hand. Either way the file lands under the descriptor's own root, which is what the coverage check
///     below verifies.
/// </summary>
[NotInParallel]
public class StratBookStoresTests
{
    private static string TempRoot(string tag) => Path.Combine(Path.GetTempPath(), $"dv-stores-{tag}-{Guid.NewGuid():N}");

    [Test]
    public async Task EveryDescriptor_CoversAtLeastOneRealFile_AndEveryFileIsCovered()
    {
        string configRoot = TempRoot("config");
        string cacheRoot = TempRoot("cache");
        Directory.CreateDirectory(configRoot);
        Directory.CreateDirectory(cacheRoot);
        try
        {
            WriteOneItemPerStore(configRoot, cacheRoot);

            PackDataRemover remover = new(new DemoCacheStore(null), configRoot, cacheRoot);
            ExtensionDataInventory inventory = await remover.InventoryAsync(StratBookStores.All, "fake", "count");

            int coveredFiles = inventory.Items.Sum(i => i.FileCount);
            int realFiles = CountFiles(configRoot) + CountFiles(cacheRoot);

            using (Assert.Multiple())
            {
                foreach (StoreInventoryItem item in inventory.Items)
                {
                    await Assert.That(item.FileCount).IsGreaterThanOrEqualTo(1)
                        .Because($"'{item.Descriptor.Id}' ({string.Join(", ", item.Descriptor.Paths)}) matched no real file");
                }

                await Assert.That(coveredFiles).IsEqualTo(realFiles)
                    .Because("every file the real writers created must be covered by exactly the declared descriptors, no more, no less");
            }
        }
        finally
        {
            Directory.Delete(configRoot, true);
            Directory.Delete(cacheRoot, true);
        }
    }

    [Test]
    public async Task NoDescriptor_MatchesSettingsJsonOrTheCacheIndexOrARecordSidecar()
    {
        string configRoot = TempRoot("core-config");
        string cacheRoot = TempRoot("core-cache");
        Directory.CreateDirectory(configRoot);
        Directory.CreateDirectory(Path.Combine(cacheRoot, "demos"));
        await File.WriteAllTextAsync(Path.Combine(configRoot, "settings.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(cacheRoot, "index.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(cacheRoot, "demos", "abc.json.gz"), "record sidecar");
        try
        {
            PackDataRemover remover = new(new DemoCacheStore(null), configRoot, cacheRoot);
            ExtensionDataInventory inventory = await remover.InventoryAsync(StratBookStores.All, "fake", "count");

            await Assert.That(inventory.TotalBytes).IsEqualTo(0)
                .Because("core's own files must never match a pack descriptor");
        }
        finally
        {
            Directory.Delete(configRoot, true);
            Directory.Delete(cacheRoot, true);
        }
    }

    private static int CountFiles(string root) =>
        Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count() : 0;

    private static void WriteOneItemPerStore(string configRoot, string cacheRoot)
    {
        // strats/: real API.
        new StratStore(Path.Combine(configRoot, "strats")).Create(StratOwner.Me(), "de_dust2", "T", "default", "test");

        // tags/: the store's own path, written directly rather than through a live TagSession.
        TagStore tags = new(Path.Combine(configRoot, "tags"));
        string tagSidecar = tags.PathFor("a" + new string('b', 63))!;
        Directory.CreateDirectory(Path.GetDirectoryName(tagSidecar)!);
        File.WriteAllText(tagSidecar, "{}");

        // palettes/: a user drop-in; TagPaletteStore only reads this directory, nothing in the app writes to it.
        Directory.CreateDirectory(Path.Combine(configRoot, "palettes"));
        File.WriteAllText(Path.Combine(configRoot, "palettes", "custom" + TagPaletteStore.FileExtension), "{}");

        // suggested-tags/: real API (the tuned detector profile).
        new ProfileStore(Path.Combine(configRoot, "suggested-tags")).Save(DetectorProfile.Default);

        // lineup-clips/: LineupClipService needs a built GrenadeIndex to render anything; one placed file
        // is enough to prove the directory name.
        Directory.CreateDirectory(Path.Combine(configRoot, "lineup-clips"));
        File.WriteAllText(Path.Combine(configRoot, "lineup-clips", "sample.gif"), "gif bytes");

        // teams.json: TeamIdentityService needs a DemoCacheStore/RoundFacts/queue graph; its own doc
        // comment fixes this path (beside settings.json).
        File.WriteAllText(Path.Combine(configRoot, "teams.json"), "{}");

        // watched-situations.json: WatchedSituationsService.FileName is public; same heavy-graph reason.
        File.WriteAllText(Path.Combine(configRoot, WatchedSituationsService.FileName), "{}");

        // veto-history.json: real API.
        new VetoHistoryStore(configRoot).Add(new VetoEntry { OpponentTeamId = Guid.NewGuid(), Order = 0, Map = "de_dust2" });

        // dossier-notes.json: real API.
        new DossierNotesStore(configRoot).AddNote(Guid.NewGuid(), "a note");

        // strat-mining.json: StratMiningService's user-truth file; its own doc comment fixes this path.
        File.WriteAllText(Path.Combine(configRoot, "strat-mining.json"), "{}");

        // round-index/ and suggestions/: the old layout, written by older builds.
        Directory.CreateDirectory(Path.Combine(cacheRoot, "round-index"));
        File.WriteAllText(Path.Combine(cacheRoot, "round-index", "a.dvri.json"), "{}");
        Directory.CreateDirectory(Path.Combine(cacheRoot, "suggestions"));
        File.WriteAllText(Path.Combine(cacheRoot, "suggestions", "a.json"), "{}");

        // strat-mining/: StratMiningService's derived cache; its own doc comment fixes this path.
        Directory.CreateDirectory(Path.Combine(cacheRoot, "strat-mining"));
        File.WriteAllText(Path.Combine(cacheRoot, "strat-mining", "detected.json"), "{}");

        // team-index.json: TeamIdentityService's derived cache; its own doc comment fixes this path.
        File.WriteAllText(Path.Combine(cacheRoot, "team-index.json"), "{}");

        // grenade-lineups.json.gz: the old layout's file, in the format the lineup store reads.
        new GrenadeLineupStore(cacheRoot).Save();

        // grenades-v3.attempts.json and demos/*.grenades*: the old layout, written by older builds.
        File.WriteAllText(Path.Combine(cacheRoot, "grenades-v3.attempts.json"), "{}");
        string demos = Path.Combine(cacheRoot, "demos");
        Directory.CreateDirectory(demos);
        File.WriteAllText(Path.Combine(demos, "a.grenades.json.gz"), "g1");
        File.WriteAllText(Path.Combine(demos, "a.grenades.paths.json.gz"), "g2");
        File.WriteAllText(Path.Combine(demos, "a" + GrenadeThrowLog.Suffix), "g3");
        File.WriteAllText(Path.Combine(demos, "a.grenades.json"), "g4");
        File.WriteAllText(Path.Combine(demos, "a.grenades.paths.json"), "g5");
    }
}
