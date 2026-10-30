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
///         call: one bad file must not take the other twenty with it, but it means a mistyped
///         column name costs the WHOLE ruleset, contributes no graph nodes, and produces exactly the
///         same observable result as rules that ran and matched nothing. A person reading a scoreboard
///         cannot tell those apart.
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
    public static void Report(ILogger logger, BuildResult build, string? subject = null)
    {
        IReadOnlyList<ExcludedRuleset> excluded = build.ExcludedRulesets;
        Remember(subject, [.. excluded.Select(Describe)]);

        if (excluded.Count == 0)
        {
            return;
        }

        foreach (ExcludedRuleset ruleset in excluded)
        {
            AppLog.RulesetExcluded(logger, Name(ruleset), Summarize(ruleset.Diagnostics));
        }
    }

    // What the most recent composition excluded, and WHICH demo it composed. Written by background analysis
    // threads and read on the UI thread, so subject and list must move together in ONE reference: read
    // separately they can pair one demo's subject with another's list.
    private sealed record ExclusionSlot(string Subject, IReadOnlyList<string> Descriptions);

    private static ExclusionSlot? _lastExcluded;

    /// <summary>
    ///     Records what a composition of <paramref name="subject" /> excluded, for the error-report snapshot
    ///     to read. The snapshot cannot compose a BuildResult of its own: building the graph is seconds of
    ///     work and it runs on a button press.
    ///     <para>
    ///         KEYED BY DEMO, because a process composes many and one demo's exclusions must never reach
    ///         another's report, the same rule parse health follows. A caller with no demo to name LEAVES THE
    ///         SLOT ALONE rather than clearing it: clearing would let a bare-run cut, which happens routinely
    ///         after an analysis, throw away the very set the reported demo just produced. Offering nothing
    ///         and destroying what is there are different things.
    ///     </para>
    /// </summary>
    /// <param name="subject">The demo the composition was for, or null/empty from a site that has none.</param>
    /// <param name="descriptions">One line per excluded ruleset; empty when the build dropped nothing.</param>
    internal static void Remember(string? subject, IReadOnlyList<string> descriptions)
    {
        if (subject is { Length: > 0 })
        {
            Volatile.Write(ref _lastExcluded, new ExclusionSlot(subject, descriptions));
        }
    }

    /// <summary>
    ///     What the last composition of this demo excluded, or null when none ran for it. Matched against
    ///     EVERY path the demo is known by, not just the one the caller holds: the cache carries several
    ///     locations for one demo, so a report filed through a second path would otherwise read as unknown.
    /// </summary>
    /// <param name="paths">Every path the demo is known by; the first match wins.</param>
    internal static IReadOnlyList<string>? ExcludedFor(IEnumerable<string> paths)
    {
        if (Volatile.Read(ref _lastExcluded) is not { } last)
        {
            return null;
        }

        return paths.Any(p => p is { Length: > 0 }
                              && string.Equals(last.Subject, p, StringComparison.OrdinalIgnoreCase))
            ? last.Descriptions
            : null;
    }

    /// <summary>Drops the remembered set, so a test can assert the "nothing composed" case.</summary>
    internal static void ForgetForTest() => Volatile.Write(ref _lastExcluded, null);

    /// <summary>The ruleset, its source file when it has one, and its diagnostics, on one line.</summary>
    public static string Describe(ExcludedRuleset ruleset) =>
        $"ruleset '{Name(ruleset)}' was excluded: {Summarize(ruleset.Diagnostics)}";

    // The id alone is not enough to act on: a user overlay can define a ruleset with the same id as
    // a shipped one, so "highlights_clutch was excluded" does not say WHICH file to open.
    private static string Name(ExcludedRuleset ruleset) =>
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
