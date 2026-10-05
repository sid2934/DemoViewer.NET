#region

using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Field-style reads and writes for tests that set up or assert one facet at a time: the Round Facts rows and
///     stamp on a record, and the pack's per-demo stamps in the in-memory data every store over a library shares
///     (<see cref="MemoryDemoData.For" />). Production code goes through the stores; these only shorten fixtures.
/// </summary>
internal static class CacheRecordTestExtensions
{
    // Record-level payload access needs a store's serializer options, not its files.
    private static readonly DemoCacheStore Memory = new(null);

    // ── Round facts ──────────────────────────────────────────────────────────

    public static RoundFactsRows? RoundFacts(this DemoCacheRecord record) => Memory.RoundFactsOf(record);

    /// <summary>Rows and stamp together; null rows drop both (a null fingerprint drops the stamp, else it stays at schema 0).</summary>
    public static void SetRoundFacts(this DemoCacheRecord record, RoundFactsRows? rows, string? fingerprint = "rf-A")
    {
        if (rows is not null)
        {
            Memory.SetRoundFacts(record, rows, fingerprint!);
            return;
        }

        Memory.UpdatePayload(record, p => p.RoundFacts = null);
        record.PackStamps.RemoveAll(s => s.Id == RoundFactsEvaluator.EvaluatorId);
        if (fingerprint is not null)
        {
            record.SetStamp(new PackStamp(RoundFactsEvaluator.EvaluatorId, 0, fingerprint) { State = DemoAnalysisState.Pending });
        }
    }

    public static void UpdateRoundFacts(this DemoCacheRecord record, Action<RoundFactsRows> mutate) =>
        Memory.UpdatePayload(record, p => mutate(p.RoundFacts!));

    public static string? RoundFactsFingerprint(this DemoCacheRecord record) => record.RoundFactsStamp()?.Fingerprint;

    public static string? RoundFactsFingerprint(this DemoCacheIndexEntry entry) => entry.RoundFactsStamp()?.Fingerprint;

    public static int RoundFactsSchema(this DemoCacheRecord record) => record.RoundFactsStamp()?.Schema ?? 0;

    public static int RoundFactsSchema(this DemoCacheIndexEntry entry) => entry.RoundFactsStamp()?.Schema ?? 0;

    // ── Per-demo data ─────────────────────────────────────────────────────────

    /// <summary>The in-memory per-demo data every store over <paramref name="cache" /> shares in a test.</summary>
    public static MemoryDemoData Data(this DemoCacheStore cache) => MemoryDemoData.For(cache);

    /// <summary>The host's on-disk per-demo data of the Strat Book under <paramref name="cacheRoot" />, over <paramref name="cache" />.</summary>
    public static IExtensionDemoData DiskData(this DemoCacheStore cache, string cacheRoot) =>
        new DemoViewer.NET.Extensions.ExtensionDemoDataStore(StratBookPack.PackId,
            Path.Combine(cacheRoot, DemoViewer.NET.Extensions.ExtensionFolders.DataDirectoryName, StratBookPack.PackId), cache, a => a(), null);

    // ── Round index ──────────────────────────────────────────────────────────

    public static DemoDataStamp? RoundIndexStamp(this DemoCacheStore cache, string path) =>
        cache.Data().Stamp(path, RoundIndexStore.Facet);

    /// <summary>
    ///     A written round index stamp at <paramref name="computedAt" /> (now when 0), over whatever index and positions the
    ///     demo already has.
    /// </summary>
    public static void StampRoundIndex(this DemoCacheStore cache, string path, string? fingerprint, long computedAt = 0, int rowCount = 0)
    {
        MemoryDemoData data = cache.Data();
        data.Put(new DemoDataStamp(path, cache.TryGetIndex(path)?.Sha256, RoundIndexStore.Facet, RoundIndexStore.Schema, fingerprint,
            DemoDataState.Written, computedAt == 0 ? DateTime.UtcNow.Ticks : computedAt, rowCount), data.ReadAny(path, RoundIndexStore.Facet)?.Content ?? [],
            Parts(data, path));
    }

    public static DemoDataState RoundIndexState(this DemoCacheStore cache, string path) =>
        cache.RoundIndexStamp(path)?.State ?? DemoDataState.Pending;

    public static string? RoundIndexFingerprint(this DemoCacheStore cache, string path) => cache.RoundIndexStamp(path)?.Fingerprint;

    public static int RoundIndexSchema(this DemoCacheStore cache, string path) => cache.RoundIndexStamp(path)?.Schema ?? 0;

    public static long RoundIndexComputedAtTicks(this DemoCacheStore cache, string path) => cache.RoundIndexStamp(path)?.WrittenAtTicks ?? 0;

    public static int RoundIndexRowCount(this DemoCacheStore cache, string path) => cache.RoundIndexStamp(path)?.Count ?? 0;

    /// <summary>Puts <paramref name="document" /> as a demo's index, keeping its positions and its stamp when it has them.</summary>
    public static void Write(this RoundIndexStore store, string path, RoundIndexDocument document)
    {
        MemoryDemoData data = (MemoryDemoData)store.Data;
        DemoDataStamp stamp = data.Stamp(path, RoundIndexStore.Facet)
                              ?? new DemoDataStamp(path, null, RoundIndexStore.Facet, RoundIndexStore.Schema, document.Fingerprint,
                                  DemoDataState.Written, DateTime.UtcNow.Ticks, document.RowCount);
        data.Put(stamp with { Schema = RoundIndexStore.Schema }, System.Text.Encoding.UTF8.GetBytes(document.Serialize()), Parts(data, path));
    }

    /// <summary>Puts <paramref name="positions" /> as a demo's positions, keeping its index and its stamp when it has them.</summary>
    public static void WritePositions(this RoundIndexStore store, string path, RoundPositionsDocument positions)
    {
        MemoryDemoData data = (MemoryDemoData)store.Data;
        DemoDataStamp stamp = data.Stamp(path, RoundIndexStore.Facet)
                              ?? new DemoDataStamp(path, null, RoundIndexStore.Facet, RoundIndexStore.Schema, positions.Fingerprint,
                                  DemoDataState.Written, DateTime.UtcNow.Ticks, 0);
        data.Put(stamp with { Schema = RoundIndexStore.Schema }, data.ReadAny(path, RoundIndexStore.Facet)?.Content ?? [],
            new Dictionary<string, byte[]> { [RoundIndexStore.PositionsPart] = positions.SerializeUtf8() });
    }

    private static Dictionary<string, byte[]>? Parts(MemoryDemoData data, string path) =>
        data.ReadAny(path, RoundIndexStore.Facet, RoundIndexStore.PositionsPart) is { } positions
            ? new Dictionary<string, byte[]> { [RoundIndexStore.PositionsPart] = positions.Content }
            : null;

    // ── Suggestions ──────────────────────────────────────────────────────────

    public static DemoDataStamp? SuggestionsStamp(this DemoCacheStore cache, string path) => cache.Data().Stamp(path, ProposalStore.Facet);

    public static string? SuggestionsFingerprint(this DemoCacheStore cache, string path) => cache.SuggestionsStamp(path)?.Fingerprint;

    public static int SuggestionCount(this DemoCacheStore cache, string path) => cache.SuggestionsStamp(path)?.Count ?? 0;

    public static void SetSuggestionCount(this DemoCacheStore cache, string path, int pending) =>
        cache.Data().SetCount(path, ProposalStore.Facet, pending);

    // ── Grenades ─────────────────────────────────────────────────────────────

    public static GrenadeStore Grenades(this DemoCacheStore cache) => new(cache.Data());

    public static DemoDataStamp? GrenadesStamp(this DemoCacheStore cache, string path) => cache.Data().Stamp(path, GrenadeStore.Facet);

    public static DemoDataState GrenadeState(this DemoCacheStore cache, string path) =>
        cache.GrenadesStamp(path)?.State ?? DemoDataState.Pending;

    /// <summary>Writes <paramref name="rows" /> as the demo's grenades, stamped as walked by the current walker.</summary>
    public static void WriteGrenades(this DemoCacheStore cache, string path, GrenadeDocument rows) => cache.Grenades().Write(path, rows);
}
