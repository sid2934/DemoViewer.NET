#region

using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

/// <summary>
///     A pack's mark on one demo: which facet it wrote (<see cref="Id" />, the writing evaluator's id by
///     convention), at which schema, under which fingerprint, and how the write ended. Stamps are the only
///     part of a pack's cache data core reads: the backlog, the Library's fill state and the Match Overview
///     chips all compare stamps and never open <see cref="DemoCacheRecord.Packs" />. The record's stamps are
///     mirrored onto <see cref="DemoCacheIndexEntry.PackStamps" /> so a staleness pass reads no sidecar.
///     <para>
///         <see cref="State" /> is <see cref="DemoAnalysisState.Indexed" /> unless the stamp says otherwise: a
///         stamp is a successful write by default, and <see cref="DemoAnalysisState.Failed" /> is what keeps
///         a demo out of the derived backlog until the user retries it. <see cref="ComputedAtTicks" /> and
///         <see cref="Count" /> are optional (UTC ticks of the write, and the pack's row count, the way
///         <see cref="DemoCacheIndexEntry.HighlightCount" /> rides the index row) and cost nothing when zero.
///     </para>
/// </summary>
/// <param name="Id">The facet, unique across every pack. The evaluator id that writes it, by convention.</param>
/// <param name="Schema">The payload shape the facet was written at. 0 when it was never written.</param>
/// <param name="Fingerprint">Whatever the facet's staleness keys on (a ruleset identity, a walker version), or null.</param>
public sealed record PackStamp(
    string Id,
    int Schema,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Fingerprint)
{
    /// <summary>How the last write ended. Only <see cref="DemoAnalysisState.Indexed" /> can be current.</summary>
    public DemoAnalysisState State { get; init; } = DemoAnalysisState.Indexed;

    /// <summary>When the facet was written (UTC ticks), or 0 when the pack does not record it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ComputedAtTicks { get; init; }

    /// <summary>The pack's row count for the facet, or 0.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Count { get; init; }

    /// <summary>
    ///     Is this stamp current at <paramref name="schema" /> under <paramref name="fingerprint" />? A write
    ///     that failed is never current, whatever it carries.
    /// </summary>
    /// <param name="schema">The facet's schema in force.</param>
    /// <param name="fingerprint">The fingerprint in force, or null when the facet has none.</param>
    public bool IsCurrent(int schema, string? fingerprint) =>
        State == DemoAnalysisState.Indexed
        && Schema == schema
        && string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal);
}
