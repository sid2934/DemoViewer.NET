#region

using System.Diagnostics.CodeAnalysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.ViewModels.Diagnostics;

/// <summary>
///     Reports rulesets that v2 composition dropped from a <see cref="BuildResult" />.
///     <para>
///         Exists because the failure mode it covers is silent and total. From CS2DemoKit 0.9.2,
///         composition validates cross-references up front: a ruleset naming a scoreboard or table
///         that does not resolve, or an unsupported <c>per:</c> dimension, is excluded from the
///         composed set rather than loaded and left partly broken. Tolerant composition is the right
///         call — one bad file must not take the other twenty with it — but it means a mistyped
///         column name costs the WHOLE ruleset, contributes no graph nodes, and produces exactly the
///         same observable result as rules that ran and matched nothing. Nobody reading a scoreboard
///         can tell those apart.
///     </para>
///     <para>
///         Every <c>DemoAnalysis.Build</c> call site in the app routes through here so the report
///         cannot be present on one surface and missing on another. Deliberately best-effort and
///         non-throwing: this is a diagnostic, and it must never be the reason an analysis run fails.
///     </para>
/// </summary>
internal static class RulesetExclusionReport
{
    /// <summary>
    ///     Logs one Warning row per excluded ruleset. No-ops on the common path where every supplied
    ///     document composed.
    /// </summary>
    [SuppressMessage("Performance", "CA1873:Avoid potentially expensive logging")]
    public static void Report(ILogger logger, BuildResult build)
    {
        IReadOnlyList<ExcludedRuleset> excluded = build.ExcludedRulesets;
        if (excluded.Count == 0)
        {
            return;
        }

        foreach (ExcludedRuleset ruleset in excluded)
        {
            AppLog.RulesetExcluded(logger, Describe(ruleset), Summarize(ruleset.Diagnostics));
        }
    }

    // The id alone is not enough to act on: a user overlay can define a ruleset with the same id as
    // a shipped one, so "highlights_clutch was excluded" does not say WHICH file to open.
    private static string Describe(ExcludedRuleset ruleset) =>
        string.IsNullOrWhiteSpace(ruleset.SourceFile)
            ? ruleset.Id
            : $"{ruleset.Id}' ('{ruleset.SourceFile}";

    // Diagnostics carry a source position; include it, because "unresolvable reference" without a
    // line number is a search rather than a fix. Capped so a document that fails structural
    // validation in fifty places cannot flood the Diagnostics tab with one row per mistake.
    private static string Summarize(IReadOnlyList<RulesetCompositionDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return "no diagnostic was attached";
        }

        const int Cap = 5;
        IEnumerable<string> lines = diagnostics
            .Take(Cap)
            .Select(d => d.Position is { } pos ? $"[{d.Code}] {d.Message} ({pos})" : $"[{d.Code}] {d.Message}");
        string body = string.Join("; ", lines);
        return diagnostics.Count > Cap
            ? $"{body}; +{diagnostics.Count - Cap} more"
            : body;
    }
}
