#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Services.Facts;

/// <summary>
///     The Round Facts rows on a demo's cache record (<see cref="DemoCacheRecord.RoundFacts" />) and their
///     stamp, read from the record or its index row with the predicates the backlog is derived from.
/// </summary>
public static class RoundFactsRecords
{
    /// <summary>The stamp's facet: the Round Facts pass id.</summary>
    public const string FacetId = RoundFactsRows.FacetId;

    /// <summary>The rows' shape. Part of the stamp, so a bump re-runs Round Facts alone.</summary>
    public const int Schema = RoundFactsRows.CurrentSchema;

    /// <summary>A demo's round facts rows: a sidecar read served by the store's capacity-1 record cache. Null when none.</summary>
    /// <param name="store">The demo cache.</param>
    /// <param name="demoPath">The demo's path.</param>
    public static RoundFactsRows? RoundFactsOf(this DemoCacheStore store, string demoPath) =>
        store.TryLoadRecord(demoPath)?.RoundFacts;

    /// <summary>
    ///     Writes the rows onto <paramref name="record" /> and stamps them under <paramref name="fingerprint" />
    ///     at the rows' own <see cref="RoundFactsRows.Schema" />, so rows projected at an older shape read as stale.
    /// </summary>
    /// <param name="record">A record the caller is about to save.</param>
    /// <param name="rows">The rows.</param>
    /// <param name="fingerprint">The effective ruleset's identity they were produced under.</param>
    public static void WriteRoundFacts(this DemoCacheRecord record, RoundFactsRows rows, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(rows);
        record.RoundFacts = rows;
        record.SetStamp(new PackStamp(FacetId, rows.Schema, fingerprint));
    }

    /// <summary>The round facts stamp, or null when none were written.</summary>
    /// <param name="entry">The index row.</param>
    public static PackStamp? RoundFactsStamp(this DemoCacheIndexEntry entry) => entry.Stamp(FacetId);

    /// <summary>The round facts stamp, or null when none were written.</summary>
    /// <param name="record">The record.</param>
    public static PackStamp? RoundFactsStamp(this DemoCacheRecord record) => record.Stamp(FacetId);

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
        fingerprint is not null && entry.IsPackCurrent(FacetId, Schema, fingerprint);

    /// <summary>As <see cref="IsRoundFactsCurrent(DemoCacheIndexEntry, string?)" />, on the record.</summary>
    /// <param name="record">The record.</param>
    /// <param name="fingerprint">The effective ruleset's fingerprint, or null when there is none.</param>
    public static bool IsRoundFactsCurrent(this DemoCacheRecord record, string? fingerprint) =>
        fingerprint is not null && record.IsPackCurrent(FacetId, Schema, fingerprint);

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
}
