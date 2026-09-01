#region

using System.Text.Json;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The one-shot legacy migration. The cases here are
///     the ones that would silently damage a real user's library rather than throw: a migration that never
///     runs, one that runs twice, one that clobbers fresher data, and one that blanks the Library grid.
/// </summary>
public class LegacyCacheMigrationTests
{
    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), $"dv-migrate-{Guid.NewGuid():N}");

    private static string WriteLibrary(string dir, params DemoLibraryCacheEntry[] entries)
    {
        string path = Path.Combine(dir, "library.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new DemoLibraryData
        {
            Folders = ["/demos"],
            Cache = [.. entries]
        }));
        return path;
    }

    private static string WriteHighlights(string dir, params LegacyHighlightsRow[] rows)
    {
        string path = Path.Combine(dir, "highlights.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Version = 1,
            Rows = rows
        }));
        return path;
    }

    private static DemoLibraryCacheEntry Indexed(string path) => new()
    {
        Path = path,
        Size = 1000,
        ModifiedTicks = 2000,
        Sha256 = "sha-legacy",
        Map = "de_mirage",
        Server = "Valve CS2 Server",
        Players = ["s1mple", "ZywOo", "ropz"],
        DurationSeconds = 2298,
        RoundCount = 22,
        CtScore = 13,
        Score = 9,
        ScoreComputed = true,
        FullyIndexed = true
    };

    [Test]
    public async Task Migrates_LibraryRows_AndKeepsTheLibraryCardPopulated()
    {
        string dir = TempRoot();
        Directory.CreateDirectory(dir);
        try
        {
            string lib = WriteLibrary(dir, Indexed("/demos/a.dem"));
            DemoCacheStore store = new(Path.Combine(dir, "cache"));

            LegacyCacheMigration.MigrationResult result = LegacyCacheMigration.Run(store, lib, null);

            DemoCacheIndexEntry? entry = store.TryGetIndex("/demos/a.dem");
            DemoCacheRecord? record = store.TryLoadRecord("/demos/a.dem");

            using (Assert.Multiple())
            {
                await Assert.That(result.Ran).IsTrue();
                await Assert.That(result.FromLibrary).IsEqualTo(1);
                await Assert.That(entry).IsNotNull();
                await Assert.That(entry!.Map).IsEqualTo("de_mirage");
                await Assert.That(entry.CtScore).IsEqualTo(13);
                await Assert.That(entry.RoundCount).IsEqualTo(22)
                    .Because("the legacy round count has no boundaries behind it but must not be lost");

                // THE regression this guards: legacy players have no team, so a roster-only projection would
                // return nothing and every already-indexed demo would render "…" as if it were un-indexed.
                await Assert.That(entry.PlayerNames).HasCount(3)
                    .Because("the Library card must still print names for a migrated row");
                await Assert.That(record!.HasTeamSplit).IsFalse()
                    .Because("the legacy cache never stored teams — the record is honest about that");
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    ///     The gate must be an explicit marker, not "does index.json exist": the indexer's dual-write
    ///     creates that file during its first pass, long before any migration.
    /// </summary>
    [Test]
    public async Task DoesNotSkip_WhenTheIndexAlreadyExistsFromDualWrite()
    {
        string dir = TempRoot();
        Directory.CreateDirectory(dir);
        try
        {
            string cacheDir = Path.Combine(dir, "cache");
            string lib = WriteLibrary(dir, Indexed("/demos/legacy.dem"));

            // Simulate the dual-write having already run for a DIFFERENT demo.
            DemoCacheStore seeded = new(cacheDir);
            seeded.Update("/demos/fresh.dem", 10, 20, r => DemoCacheStore.StampParse(r));
            seeded.SaveIndex();
            await Assert.That(File.Exists(Path.Combine(cacheDir, "index.json"))).IsTrue();

            DemoCacheStore store = new(cacheDir);
            LegacyCacheMigration.MigrationResult result = LegacyCacheMigration.Run(store, lib, null);

            using (Assert.Multiple())
            {
                await Assert.That(result.Ran).IsTrue()
                    .Because("an existing index is not evidence the migration has run");
                await Assert.That(store.TryGetIndex("/demos/legacy.dem")).IsNotNull();
                await Assert.That(store.TryGetIndex("/demos/fresh.dem")).IsNotNull()
                    .Because("migration merges — it must not drop what dual-write already produced");
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task IsIdempotent_AndSkipsOnASecondRun()
    {
        string dir = TempRoot();
        Directory.CreateDirectory(dir);
        try
        {
            string cacheDir = Path.Combine(dir, "cache");
            string lib = WriteLibrary(dir, Indexed("/demos/a.dem"));

            DemoCacheStore first = new(cacheDir);
            await Assert.That(LegacyCacheMigration.Run(first, lib, null).Ran).IsTrue();

            // A brand new store over the same directory must read the marker back off disk.
            DemoCacheStore second = new(cacheDir);
            LegacyCacheMigration.MigrationResult again = LegacyCacheMigration.Run(second, lib, null);

            using (Assert.Multiple())
            {
                await Assert.That(second.LegacyMigrationVersion)
                    .IsEqualTo(LegacyCacheMigration.CurrentVersion);
                await Assert.That(again.Ran).IsFalse();
                await Assert.That(second.Count).IsEqualTo(1);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    ///     A re-indexed demo has a real team split, tick rate and round boundaries; the legacy row has names
    ///     only. Migration must never trade the better data for the worse.
    /// </summary>
    [Test]
    public async Task NeverClobbersAFresherParseTier()
    {
        string dir = TempRoot();
        Directory.CreateDirectory(dir);
        try
        {
            string cacheDir = Path.Combine(dir, "cache");
            string lib = WriteLibrary(dir, Indexed("/demos/a.dem"));

            DemoCacheStore store = new(cacheDir);
            store.Update("/demos/a.dem", 1000, 2000, r =>
            {
                r.Players =
                [
                    new CachedPlayerInfo
                    {
                        Slot = 1,
                        Name = "s1mple",
                        Team = 3,
                        SteamId64 = "765"
                    }
                ];
                r.Rounds =
                [
                    new CachedRound
                    {
                        Number = 1,
                        StartTickFrameClock = 500
                    }
                ];
                r.TickRate = 64;
                DemoCacheStore.StampParse(r);
            });

            LegacyCacheMigration.Run(store, lib, null);
            DemoCacheRecord? record = store.TryLoadRecord("/demos/a.dem");

            using (Assert.Multiple())
            {
                await Assert.That(record!.HasTeamSplit).IsTrue()
                    .Because("the fresher parse tier wins over the legacy names-only list");
                await Assert.That(record.Players).HasCount(1);
                await Assert.That(record.TickRate).IsEqualTo(64);
                await Assert.That(record.Rounds).HasCount(1);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    ///     346 of the user's 348 highlight rows are queued-but-never-scanned stubs carrying nothing the
    ///     library row does not already have. Migrating them would inflate the count and imply data moved
    ///     that did not.
    /// </summary>
    [Test]
    public async Task SkipsUnscannedHighlightStubs_ButCarriesRealPayloads()
    {
        string dir = TempRoot();
        Directory.CreateDirectory(dir);
        try
        {
            string hl = WriteHighlights(dir,
                new LegacyHighlightsRow
                {
                    FilePath = "/demos/stub.dem",
                    FileSize = 1,
                    ModifiedTicks = 1,
                    ScanState = LegacyHighlightScanState.Pending
                },
                new LegacyHighlightsRow
                {
                    FilePath = "/demos/real.dem",
                    FileSize = 2,
                    ModifiedTicks = 2,
                    TickRate = 64,
                    ScanState = LegacyHighlightScanState.Indexed,
                    ConfigFingerprint = "fp-legacy",
                    Players =
                    [
                        new LegacyCachedPlayer
                        {
                            Slot = 1,
                            Name = "s1mple",
                            SteamId64 = "765",
                            Team = 3
                        }
                    ],
                    Events =
                    [
                        new LegacyCachedHighlight
                        {
                            RulesetId = "core",
                            HighlightId = "ace",
                            Tick = 54_000,
                            PlayerSlot = 1,
                            RoundNumber = 7,
                            RenderedTitle = "ace"
                        }
                    ]
                });

            DemoCacheStore store = new(Path.Combine(dir, "cache"));
            LegacyCacheMigration.MigrationResult result = LegacyCacheMigration.Run(store, null, hl);

            DemoCacheRecord? real = store.TryLoadRecord("/demos/real.dem");

            using (Assert.Multiple())
            {
                await Assert.That(result.FromHighlights).IsEqualTo(1)
                    .Because("only the scanned row carried anything");
                await Assert.That(store.TryGetIndex("/demos/stub.dem")).IsNull();
                await Assert.That(real).IsNotNull();
                await Assert.That(real!.Highlights).HasCount(1);
                await Assert.That(real.Highlights[0].Tick).IsEqualTo(54_000);
                await Assert.That(real.AnalysisState).IsEqualTo(DemoAnalysisState.Indexed);
                await Assert.That(real.ConfigFingerprint).IsEqualTo("fp-legacy");
                await Assert.That(real.HasTeamSplit).IsTrue()
                    .Because("the highlights row did store teams, unlike the library row");
                // The legacy highlights cache never held a scoreboard, so tier 3 is only partially filled, but
                // the completeness model still offers Compute full stats for the stats half.
                await Assert.That(real.Scoreboard).IsEmpty();
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task MissingOrCorruptLegacyFiles_AreNotAnError()
    {
        string dir = TempRoot();
        Directory.CreateDirectory(dir);
        try
        {
            string bad = Path.Combine(dir, "library.json");
            File.WriteAllText(bad, "{{{ not json");

            DemoCacheStore store = new(Path.Combine(dir, "cache"));
            LegacyCacheMigration.MigrationResult result =
                LegacyCacheMigration.Run(store, bad, Path.Combine(dir, "nope.json"));

            using (Assert.Multiple())
            {
                await Assert.That(result.Ran).IsTrue();
                await Assert.That(result.FromLibrary).IsEqualTo(0);
                await Assert.That(result.FromHighlights).IsEqualTo(0);
                await Assert.That(store.LegacyMigrationVersion)
                    .IsEqualTo(LegacyCacheMigration.CurrentVersion)
                    .Because("a corrupt legacy file is not a reason to retry forever — the cache rebuilds");
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
