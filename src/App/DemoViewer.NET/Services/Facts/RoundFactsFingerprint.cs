#region

using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using DemoViewer.NET.Modules.Highlights;
using Microsoft.Extensions.Logging;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Services.Facts;

/// <summary>
///     What decides whether cached round facts are current: <see cref="MergedRulesBuild.RulesetIdentity" />
///     (the effective <c>round_facts</c> composed alone, its source file and the engine version) folded
///     with the payload schema. Editing a threshold re-runs round facts; the highlight scan and the roster
///     parse never notice.
/// </summary>
public static class RoundFactsFingerprint
{
    /// <summary>The ruleset id the evaluator looks for in the effective set.</summary>
    public const string RulesetId = "round_facts";

    /// <summary>Folds the payload schema into a ruleset identity hash.</summary>
    /// <param name="schema">The <see cref="RoundFactsRows.Schema" /> the rows would be written at.</param>
    /// <param name="rulesetIdentity">The <c>round_facts</c> identity from <see cref="MergedRulesBuild.RulesetIdentity" />.</param>
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
///     enabled <c>round_facts</c> (a user override with <c>enabled: false</c>) or it does not compose,
///     otherwise its identity folded with the schema.
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

    /// <summary>
    ///     The effective <c>round_facts</c> ruleset, or null when there is none enabled, the pack that
    ///     owns it being off included.
    /// </summary>
    public RulesetDoc? EffectiveDoc() => Rules.EnabledDoc(RoundFactsFingerprint.RulesetId);

    /// <inheritdoc />
    public string? Fingerprint(int tickRate)
    {
        try
        {
            if (EffectiveDoc() is null)
            {
                return null;
            }

            return RoundFactsFingerprint.Combine(RoundFactsRecords.Schema,
                Rules.RulesetIdentity(RoundFactsFingerprint.RulesetId, tickRate));
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
