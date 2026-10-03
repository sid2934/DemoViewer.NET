#region

using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     The pack's payload on a demo's cache record (<see cref="DemoCacheRecord.Packs" /> under
///     <see cref="StratBookCache.PackId" />): what rides the record itself. The round index rows, the
///     proposals and the grenade rows live in their own sidecars; their stamps are on the record, not here.
/// </summary>
public sealed class StratBookCachePayload
{
    /// <summary>
    ///     The <c>round_facts</c> ruleset's output: one row per round, two sides each. Stamped by
    ///     <see cref="RoundFactsEvaluator.EvaluatorId" /> with the ruleset identity as fingerprint, so a
    ///     threshold edit re-runs only that evaluator, never the highlight scan.
    /// </summary>
    public RoundFactsRows? RoundFacts { get; set; }

    /// <summary>Share of the demo's commands that decoded when its grenades were walked (the jump-throw source's reach).</summary>
    public double GrenadeInputCoverage { get; set; }
}

/// <summary>
///     The pack's typed view of its share of the demo cache: the payload through <see cref="IPackPayloads" />,
///     and one <see cref="PackStamp" /> per evaluator, read from the record or its index row with the
///     predicates each evaluator's backlog is derived from. Core sees only the stamps.
/// </summary>
public static class StratBookCache
{
    /// <summary>The pack's id, the key of its payload on every record. Equal to <c>StratBookPack.Id</c>.</summary>
    public const string PackId = "net.demoviewer.pack.stratbook";

    /// <summary>The <see cref="StratBookCachePayload.RoundFacts" /> shape. Part of the stamp, so a bump re-runs the evaluator alone.</summary>
    public const int RoundFactsSchema = 1;

    /// <summary>The <c>.dvri.json</c> sidecar shape. Part of the stamp and of the fingerprint, so a bump re-indexes alone.</summary>
    public const int RoundIndexSchema = 1;

    /// <summary>The proposals file's stamp schema; the detector set is the fingerprint.</summary>
    public const int SuggestionsSchema = 1;

    /// <summary>The grenade siblings' shape. A bump re-walks every demo's grenades and nothing else.</summary>
    public const int GrenadeSchema = 1;

    /// <summary>The pack's <see cref="IPackPayloads" /> on <paramref name="store" />.</summary>
    /// <param name="store">The demo cache.</param>
    public static IPackPayloads StratBookPayloads(this DemoCacheStore store) => store.Payloads(PackId);

    /// <summary>The pack's payload on a record the caller holds, or null.</summary>
    /// <param name="store">The demo cache.</param>
    /// <param name="record">The record.</param>
    public static StratBookCachePayload? Payload(this DemoCacheStore store, DemoCacheRecord record) =>
        store.StratBookPayloads().Read<StratBookCachePayload>(record);

    /// <summary>Applies <paramref name="mutate" /> to the pack's payload on <paramref name="record" /> (a fresh one when none) and writes it back onto the record.</summary>
    /// <param name="store">The demo cache.</param>
    /// <param name="record">A record the caller is about to save.</param>
    /// <param name="mutate">The change.</param>
    public static void UpdatePayload(this DemoCacheStore store, DemoCacheRecord record, Action<StratBookCachePayload> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        IPackPayloads payloads = store.StratBookPayloads();
        StratBookCachePayload payload = payloads.Read<StratBookCachePayload>(record) ?? new StratBookCachePayload();
        mutate(payload);
        payloads.Write(record, payload);
    }

    // ── Round facts ──────────────────────────────────────────────────────────

    /// <summary>A demo's record and its round facts rows, or null when the demo is not cached or has no rows.</summary>
    /// <param name="store">The demo cache.</param>
    /// <param name="demoPath">The demo's path.</param>
    public static (DemoCacheRecord Record, RoundFactsRows Rows)? TryLoadWithRoundFacts(this DemoCacheStore store, string demoPath) =>
        store.TryLoadRecord(demoPath) is { } record && store.RoundFactsOf(record) is { } rows ? (record, rows) : null;

    /// <summary>The round facts rows on a record the caller holds, or null when none were written.</summary>
    /// <param name="store">The demo cache.</param>
    /// <param name="record">The record.</param>
    public static RoundFactsRows? RoundFactsOf(this DemoCacheStore store, DemoCacheRecord record) =>
        store.Payload(record)?.RoundFacts;

    /// <summary>A demo's round facts rows: a sidecar read served by the store's capacity-1 record cache. Null when none.</summary>
    /// <param name="store">The demo cache.</param>
    /// <param name="demoPath">The demo's path.</param>
    public static RoundFactsRows? RoundFactsOf(this DemoCacheStore store, string demoPath) =>
        store.StratBookPayloads().Read<StratBookCachePayload>(demoPath)?.RoundFacts;

    /// <summary>
    ///     Writes the rows onto <paramref name="record" /> and stamps them under <paramref name="fingerprint" />
    ///     at the rows' own <see cref="RoundFactsRows.Schema" />, so rows projected at an older shape read as stale.
    /// </summary>
    /// <param name="store">The demo cache.</param>
    /// <param name="record">A record the caller is about to save.</param>
    /// <param name="rows">The rows.</param>
    /// <param name="fingerprint">The effective ruleset's identity they were produced under.</param>
    public static void SetRoundFacts(this DemoCacheStore store, DemoCacheRecord record, RoundFactsRows rows, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(rows);
        store.UpdatePayload(record, p => p.RoundFacts = rows);
        record.SetStamp(new PackStamp(RoundFactsEvaluator.EvaluatorId, rows.Schema, fingerprint));
    }

    /// <summary>The round facts stamp, or null when none were written.</summary>
    /// <param name="entry">The index row.</param>
    public static PackStamp? RoundFactsStamp(this DemoCacheIndexEntry entry) => entry.Stamp(RoundFactsEvaluator.EvaluatorId);

    /// <summary>The round facts stamp, or null when none were written.</summary>
    /// <param name="record">The record.</param>
    public static PackStamp? RoundFactsStamp(this DemoCacheRecord record) => record.Stamp(RoundFactsEvaluator.EvaluatorId);

    /// <summary>True when the row carries round facts at any schema: the input the round index and the suggestions need.</summary>
    /// <param name="entry">The index row.</param>
    public static bool HasRoundFacts(this DemoCacheIndexEntry entry) =>
        entry.RoundFactsStamp() is { Schema: > 0, Fingerprint: not null };

    /// <summary>
    ///     Are the round facts current under <paramref name="fingerprint" />? Rows written at another
    ///     schema or another ruleset identity are stale; a null fingerprint means there is no
    ///     <c>round_facts</c> ruleset to run, so nothing can be current.
    /// </summary>
    /// <param name="entry">The index row.</param>
    /// <param name="fingerprint">The effective ruleset's fingerprint, or null when there is none.</param>
    public static bool IsRoundFactsCurrent(this DemoCacheIndexEntry entry, string? fingerprint) =>
        fingerprint is not null && entry.IsPackCurrent(RoundFactsEvaluator.EvaluatorId, RoundFactsSchema, fingerprint);

    /// <summary>As <see cref="IsRoundFactsCurrent(DemoCacheIndexEntry, string?)" />, on the record.</summary>
    /// <param name="record">The record.</param>
    /// <param name="fingerprint">The effective ruleset's fingerprint, or null when there is none.</param>
    public static bool IsRoundFactsCurrent(this DemoCacheRecord record, string? fingerprint) =>
        fingerprint is not null && record.IsPackCurrent(RoundFactsEvaluator.EvaluatorId, RoundFactsSchema, fingerprint);

    /// <summary>
    ///     Does this demo want the round facts evaluator? Derived, and false whenever there is no ruleset to
    ///     run: the rows are an output of that ruleset, so its absence is "nothing to do", never "everything
    ///     is stale".
    /// </summary>
    /// <param name="entry">The index row.</param>
    /// <param name="fingerprint">The effective ruleset's fingerprint, or null when there is none.</param>
    public static bool NeedsRoundFacts(this DemoCacheIndexEntry entry, string? fingerprint) =>
        fingerprint is not null && !entry.IsRoundFactsCurrent(fingerprint);

    /// <summary>As <see cref="NeedsRoundFacts(DemoCacheIndexEntry, string?)" />, on the record.</summary>
    /// <param name="record">The record.</param>
    /// <param name="fingerprint">The effective ruleset's fingerprint, or null when there is none.</param>
    public static bool NeedsRoundFacts(this DemoCacheRecord record, string? fingerprint) =>
        fingerprint is not null && !record.IsRoundFactsCurrent(fingerprint);

    // ── Round index ──────────────────────────────────────────────────────────

    /// <summary>The round index stamp, or null when the demo was never indexed.</summary>
    /// <param name="entry">The index row.</param>
    public static PackStamp? RoundIndexStamp(this DemoCacheIndexEntry entry) => entry.Stamp(RoundIndexEvaluator.EvaluatorId);

    /// <summary>The round index stamp, or null when the demo was never indexed.</summary>
    /// <param name="record">The record.</param>
    public static PackStamp? RoundIndexStamp(this DemoCacheRecord record) => record.Stamp(RoundIndexEvaluator.EvaluatorId);

    /// <summary>When the demo's <c>.dvri.json</c> was written (UTC ticks), or 0: the Watched Situations watermark.</summary>
    /// <param name="entry">The index row.</param>
    public static long RoundIndexComputedAtTicks(this DemoCacheIndexEntry entry) => entry.RoundIndexStamp()?.ComputedAtTicks ?? 0;

    /// <summary>Is the round index sidecar current under <paramref name="fingerprint" />?</summary>
    /// <param name="entry">The index row.</param>
    /// <param name="fingerprint">The fingerprint in force for the demo's map.</param>
    public static bool IsRoundIndexCurrent(this DemoCacheIndexEntry entry, string fingerprint) =>
        entry.IsPackCurrent(RoundIndexEvaluator.EvaluatorId, RoundIndexSchema, fingerprint);

    /// <summary>As <see cref="IsRoundIndexCurrent(DemoCacheIndexEntry, string)" />, on the record.</summary>
    /// <param name="record">The record.</param>
    /// <param name="fingerprint">The fingerprint in force for the demo's map.</param>
    public static bool IsRoundIndexCurrent(this DemoCacheRecord record, string fingerprint) =>
        record.IsPackCurrent(RoundIndexEvaluator.EvaluatorId, RoundIndexSchema, fingerprint);

    /// <summary>Does this demo want the round index evaluator? Failed is excluded: retry is an explicit user action.</summary>
    /// <param name="entry">The index row.</param>
    /// <param name="fingerprint">The fingerprint in force for the demo's map.</param>
    public static bool NeedsRoundIndex(this DemoCacheIndexEntry entry, string fingerprint) =>
        entry.NeedsPack(RoundIndexEvaluator.EvaluatorId, RoundIndexSchema, fingerprint);

    /// <summary>As <see cref="NeedsRoundIndex(DemoCacheIndexEntry, string)" />, on the record.</summary>
    /// <param name="record">The record.</param>
    /// <param name="fingerprint">The fingerprint in force for the demo's map.</param>
    public static bool NeedsRoundIndex(this DemoCacheRecord record, string fingerprint) =>
        record.NeedsPack(RoundIndexEvaluator.EvaluatorId, RoundIndexSchema, fingerprint);

    // ── Suggested tags ───────────────────────────────────────────────────────

    /// <summary>The proposals stamp, or null when none were built.</summary>
    /// <param name="entry">The index row.</param>
    public static PackStamp? SuggestionsStamp(this DemoCacheIndexEntry entry) => entry.Stamp(SuggestedTagsService.EvaluatorId);

    /// <summary>The proposals stamp, or null when none were built.</summary>
    /// <param name="record">The record.</param>
    public static PackStamp? SuggestionsStamp(this DemoCacheRecord record) => record.Stamp(SuggestedTagsService.EvaluatorId);

    /// <summary>Proposals with no verdict yet, 0 when there are none: the badge sums this over the index.</summary>
    /// <param name="entry">The index row.</param>
    public static int SuggestionCount(this DemoCacheIndexEntry entry) => entry.SuggestionsStamp()?.Count ?? 0;

    /// <summary>Are the proposals current under <paramref name="fingerprint" /> (the detector set)?</summary>
    /// <param name="entry">The index row.</param>
    /// <param name="fingerprint">The detector-set fingerprint in force for the demo's map.</param>
    public static bool IsSuggestionsCurrent(this DemoCacheIndexEntry entry, string fingerprint) =>
        entry.IsPackCurrent(SuggestedTagsService.EvaluatorId, SuggestionsSchema, fingerprint);

    /// <summary>As <see cref="IsSuggestionsCurrent(DemoCacheIndexEntry, string)" />, on the record.</summary>
    /// <param name="record">The record.</param>
    /// <param name="fingerprint">The detector-set fingerprint in force for the demo's map.</param>
    public static bool IsSuggestionsCurrent(this DemoCacheRecord record, string fingerprint) =>
        record.IsPackCurrent(SuggestedTagsService.EvaluatorId, SuggestionsSchema, fingerprint);

    /// <summary>Stamps the proposals file as built under <paramref name="fingerprint" /> with <paramref name="pending" /> open proposals.</summary>
    /// <param name="record">A record the caller is about to save.</param>
    /// <param name="fingerprint">The detector-set fingerprint.</param>
    /// <param name="pending">Proposals with no verdict yet.</param>
    public static void SetSuggestions(this DemoCacheRecord record, string fingerprint, int pending) =>
        record.SetStamp(new PackStamp(SuggestedTagsService.EvaluatorId, SuggestionsSchema, fingerprint) { Count = pending });

    /// <summary>Updates the pending count alone, after a verdict; a demo with no proposals stamp gets a pending one carrying the count.</summary>
    /// <param name="record">A record the caller is about to save.</param>
    /// <param name="pending">Proposals with no verdict yet.</param>
    public static void SetSuggestionCount(this DemoCacheRecord record, int pending) =>
        record.SetStamp((record.SuggestionsStamp()
                         ?? new PackStamp(SuggestedTagsService.EvaluatorId, 0, null) { State = DemoAnalysisState.Pending })
            with { Count = pending });

    // ── Grenades ─────────────────────────────────────────────────────────────

    /// <summary>The grenade walk stamp, or null when the demo was never walked.</summary>
    /// <param name="entry">The index row.</param>
    public static PackStamp? GrenadesStamp(this DemoCacheIndexEntry entry) => entry.Stamp(GrenadeIndexEvaluator.EvaluatorId);

    /// <summary>The grenade walk stamp, or null when the demo was never walked.</summary>
    /// <param name="record">The record.</param>
    public static PackStamp? GrenadesStamp(this DemoCacheRecord record) => record.Stamp(GrenadeIndexEvaluator.EvaluatorId);

    /// <summary>Are the grenade siblings current under <paramref name="walkerVersion" />?</summary>
    /// <param name="entry">The index row.</param>
    /// <param name="walkerVersion">The walker version in force.</param>
    public static bool IsGrenadesCurrent(this DemoCacheIndexEntry entry, string walkerVersion) =>
        entry.IsPackCurrent(GrenadeIndexEvaluator.EvaluatorId, GrenadeSchema, walkerVersion);

    /// <summary>As <see cref="IsGrenadesCurrent(DemoCacheIndexEntry, string)" />, on the record.</summary>
    /// <param name="record">The record.</param>
    /// <param name="walkerVersion">The walker version in force.</param>
    public static bool IsGrenadesCurrent(this DemoCacheRecord record, string walkerVersion) =>
        record.IsPackCurrent(GrenadeIndexEvaluator.EvaluatorId, GrenadeSchema, walkerVersion);

    /// <summary>Does this demo want the grenade walk? Failed is excluded: retry is an explicit user action.</summary>
    /// <param name="entry">The index row.</param>
    /// <param name="walkerVersion">The walker version in force.</param>
    public static bool NeedsGrenades(this DemoCacheIndexEntry entry, string walkerVersion) =>
        entry.NeedsPack(GrenadeIndexEvaluator.EvaluatorId, GrenadeSchema, walkerVersion);

    /// <summary>As <see cref="NeedsGrenades(DemoCacheIndexEntry, string)" />, on the record.</summary>
    /// <param name="record">The record.</param>
    /// <param name="walkerVersion">The walker version in force.</param>
    public static bool NeedsGrenades(this DemoCacheRecord record, string walkerVersion) =>
        record.NeedsPack(GrenadeIndexEvaluator.EvaluatorId, GrenadeSchema, walkerVersion);

    /// <summary>Stamps the grenade siblings as written now by <paramref name="walkerVersion" /> with <paramref name="count" /> rows.</summary>
    /// <param name="record">A record the caller is about to save.</param>
    /// <param name="walkerVersion">The walker version that made the rows.</param>
    /// <param name="count">Rows in the siblings.</param>
    public static void SetGrenades(this DemoCacheRecord record, string walkerVersion, int count) =>
        record.SetStamp(new PackStamp(GrenadeIndexEvaluator.EvaluatorId, GrenadeSchema, walkerVersion)
        {
            ComputedAtTicks = DateTime.UtcNow.Ticks,
            Count = count
        });
}
