#region

using System.Text.Json;
using DemoViewer.NET.Modules.Library;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

/// <summary>
///     The one-shot migration of <c>library.json</c> + <c>highlights.json</c> into the unified cache
///     .
///     <para>
///         <b>This does not delete or rename anything.</b> <see cref="DemoLibraryService" /> still reads
///         <c>library.json</c> on construction, so moving it aside here would make every user re-index their
///         whole library on the next launch. The legacy files are retired only once their readers have moved
///         over — a later step, deliberately separated from the step that copies the data.
///     </para>
///     <para>
///         <b>Merge, never overwrite.</b> The library indexer dual-writes into this store from its first
///         pass, so records may already exist and may be FRESHER than the legacy data — a re-indexed demo has
///         a real team split where the legacy row has names only. Every field below fills a gap; none
///         clobbers a populated one.
///     </para>
///     <para>
///         <b>Risk is unusually low.</b> The measured <c>highlights.json</c> is 99.4% empty stubs — 346 of
///         348 rows never scanned — so exactly one demo carries a payload worth preserving. And this is a
///         rebuildable cache: anything that fails to migrate is simply re-indexed.
///     </para>
/// </summary>
public static class LegacyCacheMigration
{
    /// <summary>
    ///     Revision of this migration. Bump only to force a re-run against already-migrated caches; the
    ///     merge-don't-overwrite rule makes a re-run safe but pointless.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    ///     Runs the migration if it has not already run against <paramref name="store" />. Safe to call on
    ///     every startup — the marker makes it a no-op after the first.
    /// </summary>
    public static MigrationResult Run(
        DemoCacheStore store,
        string? libraryCachePath,
        string? highlightsCachePath)
    {
        // Gated on an explicit marker, NOT on "does index.json exist" — the dual-write creates that file
        // during the first indexing pass, so an existence check would skip the migration forever and
        // silently drop the legacy data.
        if (store.LegacyMigrationVersion >= CurrentVersion)
        {
            return new MigrationResult(false, 0, 0);
        }

        int fromLibrary = 0;
        int fromHighlights = 0;

        // One batch for the whole pass: consumers re-project wholesale per Changed, so an O(library)
        // migration must coalesce or it is an O(n²) storm on the dispatcher.
        using (store.BeginBatch())
        {
            fromLibrary = MigrateLibrary(store, libraryCachePath);
            fromHighlights = MigrateHighlights(store, highlightsCachePath);
        }

        store.LegacyMigrationVersion = CurrentVersion;
        store.SaveIndex();
        return new MigrationResult(true, fromLibrary, fromHighlights);
    }

    private static int MigrateLibrary(DemoCacheStore store, string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return 0;
        }

        List<DemoLibraryCacheEntry> entries;
        try
        {
            DemoLibraryData? data = JsonSerializer.Deserialize<DemoLibraryData>(File.ReadAllText(path));
            entries = data?.Cache ?? [];
        }
        catch (Exception)
        {
            return 0; // corrupt legacy file — the library rebuilds from disk anyway
        }

        int migrated = 0;
        foreach (DemoLibraryCacheEntry legacy in entries.Where(e => !string.IsNullOrEmpty(e.Path)))
        {
            try
            {
                store.Update(legacy.Path, legacy.Size, legacy.ModifiedTicks, record =>
                {
                    record.Sha256 ??= legacy.Sha256;
                    record.Map ??= legacy.Map;
                    record.Server ??= legacy.Server;
                    record.DemoVersion ??= legacy.DemoVersion;

                    if (!record.Header.IsPresent && (legacy.Map is not null || legacy.Server is not null))
                    {
                        DemoCacheStore.StampHeader(record);
                    }

                    // A record that already has a real parse tier is FRESHER than this legacy row — it has
                    // the team split, tick rate and round boundaries the legacy row never had. Leave it.
                    if (record.Parse.IsPresent)
                    {
                        return;
                    }

                    if (!legacy.FullyIndexed)
                    {
                        return; // nothing beyond identity/header to carry across
                    }

                    record.DurationSeconds = legacy.DurationSeconds;
                    record.RoundCount = legacy.RoundCount;
                    record.CtClan = legacy.CtClan;
                    record.TClan = legacy.Clan;

                    // BOTH-OR-NOTHING, matching ExtractFinalScore's own contract — it returns all-nulls
                    // unless it resolved both sides.
                    //
                    // Real caches contain half-scores anyway: on the reference library 555 rows have a CT
                    // score and only 3 have the T score, which the current extractor cannot produce and
                    // ScoreComputed=true stops it from recomputing. Carrying a half score across would put
                    // "16 – —" on the score plate, which reads as a scoring bug rather than as missing data.
                    // Dropping it lets the completeness model offer a re-index instead.
                    if (legacy.CtScore is int ct && legacy.Score is int t)
                    {
                        record.CtScore = ct;
                        record.TScore = t;
                    }

                    // Names only — the legacy cache never stored teams, slots or steam ids. Team 0 is
                    // honest about that: HasTeamSplit reads false, the index projection falls back to
                    // printing all names so the Library card still fills, and Match Overview's roster cards
                    // can say "team split needs a re-index" rather than showing two empty teams. A
                    // re-index replaces these wholesale.
                    record.Players =
                    [
                        .. (legacy.Players ?? [])
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Select(n => new CachedPlayerInfo
                        {
                            Name = n,
                            Slot = -1,
                            Team = 0
                        })
                    ];

                    DemoCacheStore.StampParse(record);
                });

                migrated++;
            }
            catch (Exception)
            {
                // Rebuildable — one bad row never stops the pass.
            }
        }

        return migrated;
    }

    private static int MigrateHighlights(DemoCacheStore store, string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return 0;
        }

        List<LegacyHighlightsRow> rows;
        try
        {
            // Default options on purpose — the retired store wrote PascalCase with no naming policy.
            // LegacyHighlightsModels.cs is the surviving record of that format.
            LegacyHighlightsFile? data =
                JsonSerializer.Deserialize<LegacyHighlightsFile>(File.ReadAllText(path));
            rows = data?.Rows ?? [];
        }
        catch (Exception)
        {
            return 0;
        }

        int migrated = 0;
        foreach (LegacyHighlightsRow legacy in rows.Where(r => !string.IsNullOrEmpty(r.FilePath)))
        {
            // The overwhelming majority of rows are identity stubs that were queued and never scanned
            // (measured: 346 of 348). They carry nothing the library row does not already have, so skipping
            // them keeps the migration honest about what it actually moved.
            if (legacy.ScanState != LegacyHighlightScanState.Indexed && legacy.Events.Count == 0)
            {
                continue;
            }

            try
            {
                store.Update(legacy.FilePath, legacy.FileSize, legacy.ModifiedTicks, record =>
                {
                    record.Sha256 ??= legacy.DemoSha256;
                    record.Map ??= legacy.MapName;

                    // Tier-2 facts the highlights row happened to carry, filled only if the parse tier has
                    // not already produced better ones.
                    if (!record.Parse.IsPresent)
                    {
                        record.TickRate = legacy.TickRate;
                        record.TickCount = legacy.TickCount;
                        record.ServerStartTick = legacy.ServerStartTick;

                        if (legacy.Players.Count > 0)
                        {
                            record.Players =
                            [
                                .. legacy.Players.Select(p => new CachedPlayerInfo
                                {
                                    Slot = p.Slot,
                                    Name = p.Name,
                                    SteamId64 = p.SteamId64,
                                    Team = p.Team
                                })
                            ];
                        }

                        if (legacy.Rounds.Count > 0)
                        {
                            record.Rounds =
                            [
                                .. legacy.Rounds.Select(r => new CachedRound
                                {
                                    Number = r.Number,
                                    StartTickFrameClock = r.StartTickFrameClock
                                })
                            ];
                        }

                        if (legacy.TickRate > 0 || legacy.Players.Count > 0)
                        {
                            DemoCacheStore.StampParse(record);
                        }
                    }

                    if (record.Analysis.IsPresent)
                    {
                        return; // a real analysis run already produced better
                    }

                    record.ProfileName = legacy.ProfileName;
                    record.ConfigFingerprint = legacy.ConfigFingerprint;
                    record.HighlightHashes = new Dictionary<string, string>(legacy.HighlightHashes);
                    record.AnalysisState = legacy.ScanState switch
                    {
                        LegacyHighlightScanState.Indexed => DemoAnalysisState.Indexed,
                        LegacyHighlightScanState.Failed => DemoAnalysisState.Failed,
                        _ => DemoAnalysisState.Pending
                    };

                    record.Highlights =
                    [
                        .. legacy.Events.Select(e => new CachedHighlightEvent
                        {
                            RulesetId = e.RulesetId,
                            HighlightId = e.HighlightId,
                            FrameIndex = e.FrameIndex,
                            Tick = e.Tick,
                            PlayerSlot = e.PlayerSlot,
                            RoundNumber = e.RoundNumber,
                            RenderedTitle = e.RenderedTitle
                        })
                    ];

                    // The legacy scoreboard does not exist — highlights.json never stored one. Tier 3 is
                    // therefore only PARTIALLY present after migration, which the completeness model already
                    // handles: the highlights section fills, the scoreboard still offers Compute full stats.
                    if (record.Highlights.Count > 0 || record.AnalysisState != DemoAnalysisState.Pending)
                    {
                        DemoCacheStore.StampAnalysis(record);
                    }
                });

                migrated++;
            }
            catch (Exception)
            {
                // Rebuildable.
            }
        }

        return migrated;
    }

    /// <summary>Outcome of a migration attempt, for logging and tests.</summary>
    /// <param name="Ran">False when it was skipped (already migrated, or no filesystem).</param>
    /// <param name="FromLibrary">Demos whose record gained data from <c>library.json</c>.</param>
    /// <param name="FromHighlights">Demos whose record gained data from <c>highlights.json</c>.</param>
    public readonly record struct MigrationResult(bool Ran, int FromLibrary, int FromHighlights);
}
