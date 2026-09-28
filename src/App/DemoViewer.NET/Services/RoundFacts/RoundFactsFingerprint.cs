#region

using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.RoundFacts;

/// <summary>
///     What decides whether cached round facts are current: <see cref="MergedRulesBuild.RoundFactsIdentity" />
///     (the merged build's highlight fingerprint, the effective <c>round_facts</c> source and the engine
///     version) folded with the payload schema. A highlight edit re-runs round facts in the same pass that
///     re-scans highlights; a <c>round_facts</c> edit leaves the highlight rows current.
/// </summary>
public static class RoundFactsFingerprint
{
    /// <summary>The ruleset id the evaluator looks for in the effective set.</summary>
    public const string RulesetId = "round_facts";

    /// <summary>
    ///     The rulesets minus <c>round_facts</c>, for the open demo's Stats run, where the ruleset would
    ///     put a per-side table in the extras. The background scan runs the merged set.
    /// </summary>
    public static IReadOnlyList<RulesetDoc> WithoutRoundFacts(IReadOnlyList<RulesetDoc> rulesets) =>
    [
        .. rulesets.Where(r => !string.Equals(r.Id, RulesetId, StringComparison.Ordinal))
    ];

    /// <summary>Folds the payload schema into a ruleset identity hash.</summary>
    /// <param name="schema">The <see cref="RoundFactsRows.Schema" /> the rows would be written at.</param>
    /// <param name="rulesetIdentity">The engine's resolved-identity hash of the merged rule set.</param>
    public static string Combine(int schema, string rulesetIdentity)
    {
        ArgumentNullException.ThrowIfNull(rulesetIdentity);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{RulesetId}/{schema}/{rulesetIdentity}"));
        return Convert.ToHexStringLower(hash, 0, 16);
    }
}

/// <summary>
///     The current fingerprint of the effective <c>round_facts</c> ruleset, or null when there is no such
///     ruleset to evaluate. Abstracted so the evaluator's staleness logic is testable without rule files.
/// </summary>
public interface IRoundFactsRulesetIdentity
{
    /// <summary>
    ///     The fingerprint at a demo's tick rate (the engine folds durations to ticks, so there is no
    ///     global value), or null when the effective rules carry no enabled <c>round_facts</c> ruleset
    ///     or it cannot be composed. Null means "nothing to run", never "stale".
    /// </summary>
    string? Fingerprint(int tickRate);
}

/// <summary>
///     The real identity over the <see cref="MergedRulesBuild" />: null when the effective set carries no
///     enabled <c>round_facts</c> (a user override with <c>enabled: false</c>), otherwise the merged
///     set's fingerprint folded with the schema.
/// </summary>
public sealed class RulesRoundFactsRulesetIdentity : IRoundFactsRulesetIdentity
{
    private static ILogger? _diagLog;

    private readonly object _gate = new();
    private bool _reportedFailure;

    /// <summary>An identity over its own read of the shipped-plus-user rules.</summary>
    public RulesRoundFactsRulesetIdentity() : this(new MergedRulesBuild())
    {
    }

    /// <param name="rules">The rules read shared with the highlight harvester.</param>
    public RulesRoundFactsRulesetIdentity(MergedRulesBuild rules)
    {
        Rules = rules;
    }

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger(RoundFactsLog.Category);

    /// <summary>The build the rows come out of.</summary>
    public MergedRulesBuild Rules { get; }

    /// <summary>The effective <c>round_facts</c> ruleset, or null when there is none enabled.</summary>
    public RulesetDoc? EffectiveDoc() => Rules.RoundFactsDoc;

    /// <inheritdoc />
    public string? Fingerprint(int tickRate)
    {
        try
        {
            if (EffectiveDoc() is null)
            {
                return null;
            }

            return RoundFactsFingerprint.Combine(DemoCacheRecord.RoundFactsSchema, Rules.RoundFactsIdentity(tickRate));
        }
        catch (Exception ex)
        {
            // A set that fails composition is one the engine would refuse to run, so the cached rows stay
            // as they are and the Workbench is where the user reads why. Said once, not per demo.
            lock (_gate)
            {
                if (_reportedFailure)
                {
                    return null;
                }

                _reportedFailure = true;
            }

            RoundFactsLog.CompositionFailed(Log, ex);
            return null;
        }
    }

    /// <summary>Drops the cached rule config so the next fingerprint re-reads the rules directories.</summary>
    public void Invalidate()
    {
        Rules.Invalidate();
        lock (_gate)
        {
            _reportedFailure = false;
        }
    }
}
