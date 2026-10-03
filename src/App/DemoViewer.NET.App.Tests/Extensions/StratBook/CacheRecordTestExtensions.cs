#region

using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Field-style reads and writes of the pack's stamps and payload on a record or an index row, for tests
///     that set up or assert one facet at a time. Production code goes through <see cref="StratBookCache" />
///     and the store; these only shorten the fixtures.
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

    // ── Round index ──────────────────────────────────────────────────────────

    public static void StampRoundIndex(this DemoCacheRecord record, string? fingerprint, long computedAt = 0, int rowCount = 0) =>
        record.SetStamp(new PackStamp(RoundIndexEvaluator.EvaluatorId, StratBookCache.RoundIndexSchema, fingerprint)
        {
            ComputedAtTicks = computedAt == 0 ? DateTime.UtcNow.Ticks : computedAt,
            Count = rowCount
        });

    public static DemoAnalysisState RoundIndexState(this DemoCacheRecord record) =>
        record.RoundIndexStamp()?.State ?? DemoAnalysisState.Pending;

    public static DemoAnalysisState RoundIndexState(this DemoCacheIndexEntry entry) =>
        entry.RoundIndexStamp()?.State ?? DemoAnalysisState.Pending;

    public static string? RoundIndexFingerprint(this DemoCacheRecord record) => record.RoundIndexStamp()?.Fingerprint;

    public static string? RoundIndexFingerprint(this DemoCacheIndexEntry entry) => entry.RoundIndexStamp()?.Fingerprint;

    public static int RoundIndexSchema(this DemoCacheRecord record) => record.RoundIndexStamp()?.Schema ?? 0;

    public static int RoundIndexSchema(this DemoCacheIndexEntry entry) => entry.RoundIndexStamp()?.Schema ?? 0;

    public static long RoundIndexComputedAtTicks(this DemoCacheRecord record) => record.RoundIndexStamp()?.ComputedAtTicks ?? 0;

    public static int RoundIndexRowCount(this DemoCacheRecord record) => record.RoundIndexStamp()?.Count ?? 0;

    public static int RoundIndexRowCount(this DemoCacheIndexEntry entry) => entry.RoundIndexStamp()?.Count ?? 0;

    // ── Suggestions ──────────────────────────────────────────────────────────

    public static string? SuggestionsFingerprint(this DemoCacheIndexEntry entry) => entry.SuggestionsStamp()?.Fingerprint;

    // ── Grenades ─────────────────────────────────────────────────────────────

    public static DemoAnalysisState GrenadeState(this DemoCacheRecord record) =>
        record.GrenadesStamp()?.State ?? DemoAnalysisState.Pending;

    public static DemoAnalysisState GrenadeState(this DemoCacheIndexEntry entry) =>
        entry.GrenadesStamp()?.State ?? DemoAnalysisState.Pending;

    public static int GrenadeSchema(this DemoCacheIndexEntry entry) => entry.GrenadesStamp()?.Schema ?? 0;

    public static int GrenadeSchema(this DemoCacheRecord record) => record.GrenadesStamp()?.Schema ?? 0;

    public static int GrenadeCount(this DemoCacheIndexEntry entry) => entry.GrenadesStamp()?.Count ?? 0;

    public static string? GrenadeWalkerVersion(this DemoCacheIndexEntry entry) => entry.GrenadesStamp()?.Fingerprint;

    public static double GrenadeInputCoverage(this DemoCacheRecord record) => Memory.Payload(record)?.GrenadeInputCoverage ?? 0;

    /// <summary>Stamped as walked by the current <see cref="GrenadeWalker.Version" />.</summary>
    public static void StampGrenades(this DemoCacheRecord record, int count = 0) => record.SetGrenades(GrenadeWalker.Version, count);
}
