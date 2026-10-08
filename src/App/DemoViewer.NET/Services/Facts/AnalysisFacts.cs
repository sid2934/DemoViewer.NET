#region

using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.Facts;

/// <summary>
///     The library's analysis outputs over the demo cache: the highlights on each record, Round Facts, and the
///     stamped rulesets' tables in their sidecars. A ruleset whose owner is off is neither declared nor read, so
///     its stored facts stay on disk for when the owner comes back. Staleness reads the index row only.
/// </summary>
public sealed class AnalysisFacts : IAnalysisFacts, IFactsVisibility
{
    // Every supported demo records at 64 ticks; the row's stamp is compared at that rate, as the backlog does.
    private const int ProbeTickRate = 64;

    private readonly DemoCacheStore _cache;
    private readonly StampedFacts? _facts;

    /// <param name="cache">The demo cache.</param>
    /// <param name="facts">The stamped rulesets; null where the host runs none.</param>
    /// <param name="roundFacts">The Round Facts reader.</param>
    public AnalysisFacts(DemoCacheStore cache, StampedFacts? facts, IRoundFacts roundFacts)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _facts = facts;
        RoundFacts = roundFacts ?? throw new ArgumentNullException(nameof(roundFacts));
    }

    /// <inheritdoc />
    public IReadOnlyList<FactKey> Declared
    {
        get
        {
            if (_facts is null)
            {
                return [];
            }

            List<FactKey> keys = [];
            foreach (StampedFacet facet in _facts.Facets())
            {
                keys.AddRange(facet.Tables.Select(t => new FactKey(facet.RulesetId, t.Name)));
                if (facet.HasScoreboard)
                {
                    keys.Add(new FactKey(facet.RulesetId, FactKey.ScoreboardOutput));
                }
            }

            return keys;
        }
    }

    /// <inheritdoc />
    public IRoundFacts RoundFacts { get; }

    /// <inheritdoc />
    public FactStatus Status(string demoPath, FactKey key)
    {
        ArgumentException.ThrowIfNullOrEmpty(demoPath);
        ArgumentNullException.ThrowIfNull(key);
        if (_facts?.Facet(key.RulesetId) is not { } facet)
        {
            return FactStatus.Absent;
        }

        if (string.Equals(key.Output, FactKey.ScoreboardOutput, StringComparison.Ordinal))
        {
            return facet.HasScoreboard ? FactStatus.NeedsFullAnalysis : FactStatus.Absent;
        }

        if (!facet.Tables.Any(t => string.Equals(t.Name, key.Output, StringComparison.Ordinal))
            || _cache.TryGetIndex(demoPath)?.Stamp(StampedFacts.StampId(key.RulesetId)) is not { } stamp)
        {
            return FactStatus.Absent;
        }

        string? fingerprint = _facts.Fingerprint(key.RulesetId, ProbeTickRate);
        if (stamp.State == DemoAnalysisState.Failed)
        {
            return string.Equals(stamp.Fingerprint, fingerprint, StringComparison.Ordinal) ? FactStatus.Failed : FactStatus.Stale;
        }

        return stamp.IsCurrent(StampedFacts.Schema, fingerprint) ? FactStatus.Current : FactStatus.Stale;
    }

    /// <inheritdoc />
    public bool IsCurrent(string demoPath, FactKey key) => Status(demoPath, key) == FactStatus.Current;

    /// <inheritdoc />
    public FactTable? TryGet(string demoPath, FactKey key)
    {
        if (Status(demoPath, key) is not (FactStatus.Current or FactStatus.Stale))
        {
            return null;
        }

        return _cache.TryReadSiblingBytes(demoPath, StampedFacts.Suffix(key)) is { } bytes
               && FactsCodec.Decode(bytes) is { } table
               && table.Key == key
            ? table
            : null;
    }

    string IFactsVisibility.ShownKey => _facts is null ? "" : string.Join('\n', _facts.Facets().Select(f => f.RulesetId));

    bool IFactsVisibility.Shows(string stampId) =>
        StampedFacts.RulesetOf(stampId) is not { } rulesetId || _facts?.Facet(rulesetId) is not null;

    /// <inheritdoc />
    public IReadOnlyList<LibraryHighlight> Highlights(string demoPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(demoPath);
        if (_cache.TryGetIndex(demoPath) is not { HighlightCount: > 0 })
        {
            return [];
        }

        return _cache.TryLoadRecord(demoPath, false) is { } record
            ?
            [
                .. record.Highlights.Select(h => new LibraryHighlight(h.RulesetId, h.HighlightId, h.Tick, h.PlayerSlot,
                    h.RoundNumber, h.RenderedTitle, h.Score, h.Kind.ToString()))
            ]
            : [];
    }
}

/// <summary>Which facts stamps the library rows show: a stamped ruleset's only while it is on.</summary>
internal interface IFactsVisibility
{
    /// <summary>Changes whenever the set of shown rulesets does; a projection made under another key is stale.</summary>
    string ShownKey { get; }

    /// <summary>False for the facts stamp of a ruleset that is off or no longer contributed; true for every other stamp.</summary>
    /// <param name="stampId">A stamp id on the index row.</param>
    bool Shows(string stampId);
}
