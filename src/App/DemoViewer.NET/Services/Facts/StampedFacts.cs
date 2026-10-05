#region

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.Facts;

/// <summary>One stamped ruleset whose outputs are stored as library facts, as the merged rules read it now.</summary>
/// <param name="Ruleset">The stamped ruleset, with its owner.</param>
/// <param name="Doc">The effective document: a user's same-id override when there is one.</param>
public sealed record StampedFacet(StampedRuleset Ruleset, RulesetDoc Doc)
{
    /// <summary>The ruleset's id.</summary>
    public string RulesetId => Ruleset.RulesetId;

    /// <summary>The tables its <c>show: tables:</c> declares.</summary>
    public IReadOnlyList<TableDef> Tables => Doc.Show?.Tables ?? [];

    /// <summary>True when it declares a scoreboard, which only a full analysis projects.</summary>
    public bool HasScoreboard => Doc.Show?.Scoreboard is { Count: > 0 };
}

/// <summary>
///     The stamped rulesets whose outputs are library facts, and where those facts live: one sibling sidecar
///     per demo per output, and one <c>facts:&lt;ruleset&gt;</c> stamp per demo on the record, mirrored onto
///     the index row so staleness never opens a sidecar. <c>round_facts</c> is not one of them: its rows have
///     their own typed store.
/// </summary>
public sealed class StampedFacts
{
    /// <summary>The storage shape; part of every fingerprint, so a bump re-writes every demo's facts.</summary>
    public const int Schema = 1;

    private const string StampPrefix = "facts:";

    private readonly ConditionalWeakTable<RulesetDoc, Dictionary<int, string?>> _fingerprints = new();

    /// <param name="rules">The merged rules the facts come out of.</param>
    public StampedFacts(MergedRulesBuild rules)
    {
        Rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    /// <summary>The merged rules.</summary>
    public MergedRulesBuild Rules { get; }

    /// <summary>The facets on now: every stamped ruleset but <c>round_facts</c> whose owner is on and whose doc is enabled.</summary>
    public IReadOnlyList<StampedFacet> Facets()
    {
        List<StampedFacet> facets = [];
        foreach (StampedRuleset ruleset in Rules.StampedRulesets)
        {
            if (string.Equals(ruleset.RulesetId, RoundFactsFingerprint.RulesetId, StringComparison.Ordinal))
            {
                continue;
            }

            if (Rules.EnabledDoc(ruleset.RulesetId) is { } doc)
            {
                facets.Add(new StampedFacet(ruleset, doc));
            }
        }

        return facets;
    }

    /// <summary>The facet with this ruleset id, or null when it is not on.</summary>
    /// <param name="rulesetId">The ruleset's id.</param>
    public StampedFacet? Facet(string rulesetId) =>
        Facets().FirstOrDefault(f => string.Equals(f.RulesetId, rulesetId, StringComparison.Ordinal));

    /// <summary>The stamp id a ruleset's facts are written under.</summary>
    /// <param name="rulesetId">The ruleset's id.</param>
    public static string StampId(string rulesetId) => StampPrefix + rulesetId;

    /// <summary>The ruleset a stamp id names, or null when it is not a facts stamp.</summary>
    /// <param name="stampId">A stamp id.</param>
    public static string? RulesetOf(string stampId) =>
        stampId.StartsWith(StampPrefix, StringComparison.Ordinal) ? stampId[StampPrefix.Length..] : null;

    /// <summary>The sibling suffix one output is stored under.</summary>
    /// <param name="key">The output.</param>
    public static string Suffix(FactKey key) => $".facts.{key.RulesetId}.{key.Output}.json.gz";

    /// <summary>
    ///     The ruleset's identity folded with <see cref="Schema" />, or null when it is not on or does not compose
    ///     alone. Null means "nothing to run", never "stale".
    /// </summary>
    /// <param name="rulesetId">The ruleset's id.</param>
    /// <param name="tickRate">The demo's tick rate; the engine folds durations to ticks.</param>
    public string? Fingerprint(string rulesetId, int tickRate)
    {
        if (Rules.EnabledDoc(rulesetId) is not { } doc)
        {
            return null;
        }

        Dictionary<int, string?> byRate = _fingerprints.GetValue(doc, _ => []);
        lock (byRate)
        {
            if (byRate.TryGetValue(tickRate, out string? known))
            {
                return known;
            }
        }

        string? fingerprint;
        try
        {
            fingerprint = Combine(rulesetId, Rules.RulesetIdentity(rulesetId, tickRate));
        }
        catch (Exception)
        {
            fingerprint = null;
        }

        lock (byRate)
        {
            byRate[tickRate] = fingerprint;
        }

        return fingerprint;
    }

    /// <summary>
    ///     True when the demo's row holds no settled write of the ruleset's facts under
    ///     <paramref name="fingerprint" />. A failed write under the same fingerprint is settled: it is retried
    ///     once the ruleset changes, never on every visit.
    /// </summary>
    /// <param name="entry">The demo's index row.</param>
    /// <param name="rulesetId">The ruleset's id.</param>
    /// <param name="fingerprint">The fingerprint in force.</param>
    public static bool NeedsWrite(DemoCacheIndexEntry entry, string rulesetId, string fingerprint) =>
        entry.Stamp(StampId(rulesetId)) is not { Schema: Schema } stamp
        || !string.Equals(stamp.Fingerprint, fingerprint, StringComparison.Ordinal)
        || stamp.State is not (DemoAnalysisState.Indexed or DemoAnalysisState.Failed);

    private static string Combine(string rulesetId, string identity)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{rulesetId}/{Schema}/{identity}"));
        return Convert.ToHexStringLower(hash, 0, 16);
    }
}
