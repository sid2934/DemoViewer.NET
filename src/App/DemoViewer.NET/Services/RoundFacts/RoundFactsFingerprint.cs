#region

using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.RoundFacts;

/// <summary>
///     What decides whether cached round facts are current: the resolved identity of the effective
///     <c>round_facts</c> ruleset (user override included) folded with the payload schema. Editing a
///     threshold in the user rules directory changes the identity, so only this evaluator re-runs;
///     the highlight scan and the roster parse never notice.
/// </summary>
public static class RoundFactsFingerprint
{
    /// <summary>The ruleset id the evaluator looks for in the effective set.</summary>
    public const string RulesetId = "round_facts";

    /// <summary>Folds the payload schema into a ruleset identity hash.</summary>
    /// <param name="schema">The <see cref="RoundFactsRows.Schema" /> the rows would be written at.</param>
    /// <param name="rulesetIdentity">The engine's resolved-identity hash of the one ruleset.</param>
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
///     The real identity: the same shipped-plus-user overlay the highlight harvester loads, restricted
///     to the one ruleset and hashed by the engine's own fingerprint replay. Until CS2DemoKit #54 ships
///     the ruleset, the effective set has no <c>round_facts</c> and this answers null.
/// </summary>
public sealed class RulesRoundFactsRulesetIdentity : IRoundFactsRulesetIdentity
{
    private static ILogger? _diagLog;

    private readonly object _gate = new();
    private bool _reportedFailure;
    private RuleConfigLoadResult? _rules;

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger(RoundFactsLog.Category);

    private RuleConfigLoadResult Rules
    {
        get
        {
            lock (_gate)
            {
                if (_rules is null)
                {
                    string shippedDir = RuleSetLocator.ResolveShippedRulesDirectory();
                    string? userDir = OperatingSystem.IsBrowser()
                        ? null
                        : RuleSetLocator.EnsureUserRulesDirectory(shippedDir);
                    _rules = YamlConfigLoader.LoadWithOverlay(shippedDir, userDir);
                }

                return _rules;
            }
        }
    }

    /// <inheritdoc />
    public string? Fingerprint(int tickRate)
    {
        RulesetDoc? doc = Rules.Rulesets.FirstOrDefault(r =>
            r.Enabled && string.Equals(r.Id, RoundFactsFingerprint.RulesetId, StringComparison.Ordinal));
        if (doc is null)
        {
            return null;
        }

        try
        {
            HighlightConfigFingerprint.Result result = HighlightConfigFingerprint.Compute(
                [doc], tickRate, RulesHighlightHarvester.GotvProfileId);
            return RoundFactsFingerprint.Combine(DemoCacheRecord.RoundFactsSchema, result.Fingerprint);
        }
        catch (Exception ex)
        {
            // A ruleset that fails composition is one the engine would refuse to run, so the cached rows
            // stay as they are and the Workbench is where the user reads why. Said once, not per demo.
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
        lock (_gate)
        {
            _rules = null;
            _reportedFailure = false;
        }
    }
}
