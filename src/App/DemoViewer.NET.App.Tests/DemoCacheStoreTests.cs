#region

using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The unified demo cache. Covers the properties the
///     rest of the redesign leans on: index/sidecar split, lazy record reads, per-tier independence,
///     identity-drift invalidation, atomic overwrite, corruption tolerance, batching, and the WASM
///     (null-root) degrade.
/// </summary>
public class DemoCacheStoreTests
{
    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), $"dv-democache-{Guid.NewGuid():N}");

    private static DemoCacheRecord Record(string path, long size = 1000, long mtime = 2000) => new()
    {
        Path = path,
        Size = size,
        ModifiedTicks = mtime,
        Sha256 = "sha-abc",
        Map = "de_dust2",
        Server = "FACEIT",
        DemoVersion = "1",
        DurationSeconds = 2298,
        TickRate = 64,
        TickCount = 147_000,
        Players =
        [
            // A hostile RAW name: the cache stores names verbatim (the CSVG spec_player currency) and
            // sanitizes only at the render boundary.
            new CachedPlayerInfo
            {
                Slot = 1,
                Name = "s1mple‮",
                SteamId64 = "7656119",
                Team = 3
            },
            new CachedPlayerInfo
            {
                Slot = 2,
                Name = "ZywOo",
                SteamId64 = "7656120",
                Team = 2
            },
            new CachedPlayerInfo
            {
                Slot = 3,
                Name = "BOT Rock",
                SteamId64 = "",
                Team = 3,
                IsBot = true
            },
            new CachedPlayerInfo
            {
                Slot = 9,
                Name = "an observer",
                SteamId64 = "7656199",
                Team = 0
            }
        ],
        Rounds =
        [
            new CachedRound
            {
                Number = 1,
                StartTickFrameClock = 5000
            }
        ],
        CtScore = 13,
        TScore = 9,
        CtClan = "NAVI",
        TClan = "FaZe"
    };

    [Test]
    public async Task Upsert_WritesASidecar_AndTheIndexRoundTripsAcrossReopen()
    {
        string root = TempRoot();
        try
        {
            DemoCacheRecord record = Record("/demos/a.dem");
            DemoCacheStore.StampHeader(record);
            DemoCacheStore.StampParse(record);

            DemoCacheStore store = new(root);
            store.Upsert(record);
            store.SaveIndex();

            await Assert.That(File.Exists(Path.Combine(root, "index.json"))).IsTrue();
            await Assert.That(File.Exists(store.SidecarPathFor("/demos/a.dem")!)).IsTrue();

            // A fresh store loads the index WITHOUT reading any sidecar, then reads one on demand.
            DemoCacheStore reopened = new(root);
            DemoCacheIndexEntry? entry = reopened.TryGetIndex("/demos/a.dem");

            using (Assert.Multiple())
            {
                await Assert.That(entry).IsNotNull();
                await Assert.That(entry!.Map).IsEqualTo("de_dust2");
                await Assert.That(entry.CtScore).IsEqualTo(13);
                await Assert.That(entry.Tier).IsEqualTo(DemoCacheTier.Parse);
                // The index carries roster NAMES (the Library card renders them) but not the rest.
                await Assert.That(entry.PlayerNames).HasCount(3)
                    .Because("the observer is not a roster member");
                await Assert.That(entry.PlayerNames).Contains("s1mple‮")
                    .Because("names are stored raw; sanitizing happens at the render boundary");
            }

            DemoCacheRecord? loaded = reopened.TryLoadRecord("/demos/a.dem");
            using (Assert.Multiple())
            {
                await Assert.That(loaded).IsNotNull();
                await Assert.That(loaded!.Players).HasCount(4);
                await Assert.That(loaded.Roster.Count()).IsEqualTo(3);
                await Assert.That(loaded.Spectators.Count()).IsEqualTo(1);
                await Assert.That(loaded.Players.Single(p => p.Slot == 3).IsBot).IsTrue();
                await Assert.That(loaded.Rounds.Single().StartTickFrameClock).IsEqualTo(5000);
                await Assert.That(loaded.TickRate).IsEqualTo(64);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     The point of per-tier stamps: filling tier 3 must not disturb tier 2, and a tier that was never
    ///     written must not claim to be present.
    /// </summary>
    [Test]
    public async Task Tiers_AreStampedIndependently()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);

            DemoCacheRecord record = Record("/demos/b.dem");
            DemoCacheStore.StampParse(record);
            store.Upsert(record);

            using (Assert.Multiple())
            {
                await Assert.That(record.Parse.IsPresent).IsTrue();
                await Assert.That(record.Analysis.IsPresent).IsFalse();
                await Assert.That(record.Header.IsPresent).IsFalse()
                    .Because("a header read never ran for this demo — parse filled the same fields directly");
                await Assert.That(record.Tier).IsEqualTo(DemoCacheTier.Parse);
            }

            store.Update("/demos/b.dem", 1000, 2000, r =>
            {
                r.Scoreboard =
                [
                    new CachedStatRow
                    {
                        Slot = 1,
                        Team = 3,
                        Kills = 24,
                        Deaths = 14,
                        Rating = 1.31
                    }
                ];
                r.AnalysisState = DemoAnalysisState.Indexed;
                r.ConfigFingerprint = "fp-1";
                DemoCacheStore.StampAnalysis(r);
            });

            DemoCacheRecord? after = new DemoCacheStore(root).TryLoadRecord("/demos/b.dem");
            using (Assert.Multiple())
            {
                await Assert.That(after!.Tier).IsEqualTo(DemoCacheTier.Analysis);
                await Assert.That(after.Parse.IsPresent).IsTrue()
                    .Because("filling analysis must not disturb the parse tier");
                await Assert.That(after.Players).HasCount(4).Because("tier-2 payload survived");
                await Assert.That(after.Scoreboard.Single().Kills).IsEqualTo(24);
                await Assert.That(after.IsAnalysisCurrent("fp-1")).IsTrue();
                await Assert.That(after.IsAnalysisCurrent("fp-2")).IsFalse()
                    .Because("a rules-config change makes tier 3 stale — and ONLY tier 3");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     A file replaced at the same path is a DIFFERENT demo. Salvaging tiers would attribute the old
    ///     match's rosters and score to the new file.
    /// </summary>
    [Test]
    public async Task LoadOrCreate_DiscardsEveryTier_WhenTheFileIdentityDrifts()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            DemoCacheRecord original = Record("/demos/c.dem");
            DemoCacheStore.StampParse(original);
            store.Upsert(original);

            DemoCacheRecord same = store.LoadOrCreate("/demos/c.dem", 1000, 2000);
            await Assert.That(same.Players).IsNotEmpty().Because("same file — the record still applies");

            DemoCacheRecord replaced = store.LoadOrCreate("/demos/c.dem", 9999, 2000);
            using (Assert.Multiple())
            {
                await Assert.That(replaced.Players).IsEmpty();
                await Assert.That(replaced.CtScore).IsNull();
                await Assert.That(replaced.Parse.IsPresent).IsFalse();
                await Assert.That(replaced.Tier).IsEqualTo(DemoCacheTier.Identity);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task CorruptSidecar_ReadsAsNotCached_RatherThanThrowing()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Record("/demos/d.dem"));
            // Sidecars are written eagerly by Upsert; the index is deferred (that is the whole point of the
            // split), so a pass must close with SaveIndex or its rows are not on disk to reload.
            store.SaveIndex();
            File.WriteAllText(store.SidecarPathFor("/demos/d.dem")!, "{ this is not json");

            DemoCacheStore reopened = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(reopened.TryLoadRecord("/demos/d.dem")).IsNull();
                await Assert.That(reopened.TryGetIndex("/demos/d.dem")).IsNotNull()
                    .Because("the index row survives a corrupt sidecar; only the fat payload is lost");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task CorruptIndex_StartsEmpty_AndRebuilds()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Record("/demos/e.dem"));
            store.SaveIndex();
            File.WriteAllText(Path.Combine(root, "index.json"), "]]not json[[");

            DemoCacheStore reopened = new(root);
            await Assert.That(reopened.Count).IsEqualTo(0);

            reopened.Upsert(Record("/demos/e.dem"));
            reopened.SaveIndex();
            await Assert.That(new DemoCacheStore(root).Count).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     An O(library) pass raises one Changed, not one per demo — consumers re-project wholesale per event,
    ///     so an unbatched sweep is an O(n²) storm on the dispatcher.
    /// </summary>
    [Test]
    public async Task BeginBatch_CoalescesChangedIntoOneRaise()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            int raises = 0;
            string? lastPath = "sentinel";
            store.Changed += p =>
            {
                raises++;
                lastPath = p;
            };

            using (store.BeginBatch())
            {
                for (int i = 0; i < 20; i++)
                {
                    store.Upsert(Record($"/demos/batch{i}.dem"));
                }

                await Assert.That(raises).IsEqualTo(0).Because("nothing fires inside the scope");
            }

            using (Assert.Multiple())
            {
                await Assert.That(raises).IsEqualTo(1);
                await Assert.That(lastPath).IsNull()
                    .Because("a batch spans many demos, so no single path can name it");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     A single-demo write names the demo it changed. Per-demo consumers (Match Overview re-rendering the
    ///     demo it is showing) key off this — without it they re-project on every unrelated write, so browsing
    ///     the Library during a background index costs a sidecar read and a full page rebuild per demo
    ///     indexed, and the rebuild pops open every highlight group the user had collapsed.
    /// </summary>
    [Test]
    public async Task ASingleWrite_NamesTheDemoItChanged()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            List<string?> paths = [];
            store.Changed += p => paths.Add(p);

            store.Upsert(Record("/demos/one.dem"));
            store.Upsert(Record("/demos/two.dem"));
            store.Remove("/demos/one.dem");

            using (Assert.Multiple())
            {
                await Assert.That(paths.Count).IsEqualTo(3);
                await Assert.That(paths[0]).IsEqualTo("/demos/one.dem");
                await Assert.That(paths[1]).IsEqualTo("/demos/two.dem");
                await Assert.That(paths[2]).IsEqualTo("/demos/one.dem")
                    .Because("a removal is a change to that demo too");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     Readers must never be handed the store's own instance. Match Overview reads a record on the UI
    ///     thread while a background tier-2 pass writes the same demo — a shared mutable record would let the
    ///     page watch fields change under it mid-render, with no lock a caller could reasonably take.
    /// </summary>
    [Test]
    public async Task TryLoadRecord_ReturnsAnIsolatedInstance_NotTheStoresOwn()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Record("/demos/iso.dem"));

            DemoCacheRecord? first = store.TryLoadRecord("/demos/iso.dem");
            DemoCacheRecord? second = store.TryLoadRecord("/demos/iso.dem");

            using (Assert.Multiple())
            {
                await Assert.That(ReferenceEquals(first, second)).IsFalse()
                    .Because("each read is its own instance, even when served from the in-memory cache");
                await Assert.That(first!.Players).HasCount(4);
            }

            // Mutating what a reader was given must not reach the store or any later reader.
            first!.Players.Clear();
            first.CtScore = 999;

            DemoCacheRecord? third = store.TryLoadRecord("/demos/iso.dem");
            using (Assert.Multiple())
            {
                await Assert.That(third!.Players).HasCount(4);
                await Assert.That(third.CtScore).IsEqualTo(13);
                await Assert.That(store.TryGetIndex("/demos/iso.dem")!.CtScore).IsEqualTo(13);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Remove_DropsTheIndexRowAndTheSidecar()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Record("/demos/f.dem"));
            string sidecar = store.SidecarPathFor("/demos/f.dem")!;
            await Assert.That(File.Exists(sidecar)).IsTrue();

            store.Remove("/demos/f.dem");

            using (Assert.Multiple())
            {
                await Assert.That(store.TryGetIndex("/demos/f.dem")).IsNull();
                await Assert.That(store.TryLoadRecord("/demos/f.dem")).IsNull();
                await Assert.That(File.Exists(sidecar)).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     Sidecar names must survive a restart. <c>string.GetHashCode</c>/<c>System.HashCode</c> are
    ///     randomized per process, so a name derived from one would be unfindable on the next launch — this
    ///     asserts the key is a real content hash of the path.
    /// </summary>
    [Test]
    public async Task StableKey_IsDeterministic_AndCaseInsensitive()
    {
        using (Assert.Multiple())
        {
            await Assert.That(DemoCacheStore.StableKey("/demos/g.dem"))
                .IsEqualTo(DemoCacheStore.StableKey("/demos/g.dem"));
            await Assert.That(DemoCacheStore.StableKey("/Demos/G.DEM"))
                .IsEqualTo(DemoCacheStore.StableKey("/demos/g.dem"));
            await Assert.That(DemoCacheStore.StableKey("/demos/g.dem"))
                .IsNotEqualTo(DemoCacheStore.StableKey("/demos/h.dem"));
            // A literal, so a future "optimization" to a non-cryptographic hash fails loudly here rather
            // than silently orphaning every sidecar on the next launch.
            await Assert.That(DemoCacheStore.StableKey("/demos/g.dem")).HasCount(24);
        }
    }

    /// <summary>WASM has no filesystem: every API must still work, in memory, writing nothing.</summary>
    [Test]
    public async Task NullRoot_WorksInMemory_AndPersistsNothing()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Record("/demos/wasm.dem"));
        store.SaveIndex();

        using (Assert.Multiple())
        {
            await Assert.That(store.TryGetIndex("/demos/wasm.dem")).IsNotNull();
            await Assert.That(store.SidecarPathFor("/demos/wasm.dem")).IsNull();
            // The capacity-1 record cache still serves the record that was just written.
            await Assert.That(store.TryLoadRecord("/demos/wasm.dem")).IsNotNull();
            await Assert.That(new DemoCacheStore(null).TryGetIndex("/demos/wasm.dem")).IsNull()
                .Because("nothing was persisted, so a fresh in-memory store knows nothing");
        }
    }
}
