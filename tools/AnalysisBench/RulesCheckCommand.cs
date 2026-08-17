#region

using Cs2DemoKit.Analysis;
using Cs2DemoKit.Analysis.Abstractions;
using Cs2DemoKit.Analysis.Building;
using Cs2DemoKit.Analysis.Catalog;
using Cs2DemoKit.Analysis.Graphs;
using Cs2DemoKit.Analysis.Plugins;
using Cs2DemoKit.Analysis.Registry;
using Cs2DemoKit.Analysis.RulesetsV2.Model;
using Cs2DemoKit.Analysis.RulesetsV2.Resolve;
using Cs2DemoKit.Analysis.Yaml;
using Cs2DemoKit.Parser;

#endregion

namespace AnalysisBench;

/// <summary>
///     <c>rules check</c> — the standalone rule-config checker (actionlint is the model).
///     Tier 1: strict load via <see cref="YamlConfigLoader.TryLoadDirectory" /> (attributed
///     <c>file(line,col)</c> errors — a retired-v1 <c>chains:</c> file surfaces here as a loud
///     error). Tier 2: the v2 <c>ruleset:</c> resolve/check pipeline demo-less (reference/type
///     errors with <c>file(line,col)</c>). With <c>--demo</c>: the coverage lints that need a
///     real demo — <see cref="RulesetCoverageDiagnostic" /> view-binding skips plus stats /
///     highlights that never fired. Exit 1 on errors, 0 on warnings-only.
/// </summary>
internal static class RulesCheckCommand
{
    // Demo-less v2 resolution (compiler-plan §4 load-vs-build): the parser's 64/s default and a
    // default source profile. The §8 resolution diagnostics (bad refs, type errors, cycles) are
    // rate- and profile-independent; folding + param binding (which the hash depends on) only
    // matter at the per-demo build, so a demo-less check surfaces every reference/type error.
    private const double DemoLessTickRate = 64.0;
    private const string DemoLessProfileId = "Cs2GotvProfile";

    internal static int Run(string[] positional, Dictionary<string, string> namedArgs, HashSet<string> flags)
    {
        string rulesDir = positional.Length > 0
            ? positional[0]
            : RuleSetLocator.ResolveShippedRulesDirectory();
        if (!Directory.Exists(rulesDir))
        {
            Console.Error.WriteLine($"rules check: directory not found: {rulesDir}");
            return 2;
        }

        Console.WriteLine($"rules check: {rulesDir}");
        int errors = 0, warnings = 0;

        // ── Tier 1: strict load (the loader's own attributed errors) ────────────────
        RuleConfigLoadResult load = YamlConfigLoader.TryLoadDirectory(rulesDir);
        foreach (RuleConfigError err in load.Errors)
        {
            Console.WriteLine($"  error: {err}");
            errors++;
        }

        // ── Tier 2: v2 ruleset resolve/check (demo-less, compiler-plan §8) ───────────
        // The loader already folded v2 mapping/structural diagnostics into tier 1; this runs the
        // resolve → canonicalize → check pipeline the loader does NOT (that is per-demo), surfacing
        // reference/type/cross-read errors with file(line,col).
        errors += CheckV2Rulesets(load);

        // ── Tier 3 (--demo): coverage lints that need a real demo ───────────────────
        if (namedArgs.TryGetValue("--demo", out string? demoPath))
        {
            if (!File.Exists(demoPath))
            {
                Console.Error.WriteLine($"rules check: demo not found: {demoPath}");
                return 2;
            }

            warnings += RunCoverageLints(demoPath, load);
        }

        Console.WriteLine($"rules check: {errors} error(s), {warnings} warning(s) "
                          + $"across {load.LoadedFiles.Count} file(s)");
        return errors > 0 ? 1 : 0;
    }

    /// <summary>
    ///     Runs the loaded v2 <c>ruleset:</c> documents through the demo-less whole-set validation
    ///     (compiler-plan §8) and prints the resulting diagnostics as errors (
    ///     <c>
    ///         file(line,col):
    ///         message [code]
    ///     </c>
    ///     — the §8 contract). Returns the error count.
    ///     <para>
    ///         The recipe itself is <see cref="DemoAnalysis.ValidateRulesets(IReadOnlyList{RulesetDoc})" />
    ///         — the public library entry point — rather than a private copy, so the CLI and a
    ///         consumer's upload-validation endpoint can never disagree about what "valid" means.
    ///         It composes the whole directory (export graph + dependency-ordered resolve), so
    ///         a qualified cross-ruleset read (e.g. player_stats HLTV reading kast.kast_pct)
    ///         resolves against the other loaded rulesets' exports — a per-doc resolve would falsely
    ///         reject it.
    ///     </para>
    /// </summary>
    private static int CheckV2Rulesets(RuleConfigLoadResult load)
    {
        if (load.Rulesets.Count == 0)
        {
            return 0;
        }

        int errors = 0;
        RulesetValidationResult validated = DemoAnalysis.ValidateRulesets(load.Rulesets);
        foreach (RulesetCompositionDiagnostic diagnostic in validated.Diagnostics)
        {
            Console.WriteLine($"  error: {diagnostic}");
            errors++;
        }

        return errors;
    }

    /// <summary>
    ///     Parses + evaluates the demo on the compiled v2 graph, then reports the demo-dependent
    ///     lints: <see cref="RulesetCoverageDiagnostic" /> view-binding skips + stats/highlights
    ///     compiled but never fired (via <see cref="StateEdge.FireCount" /> — the 0.1 always-on
    ///     counters). The v2 rulesets are resolved against the demo's real tick rate + source
    ///     profile and built through the <see cref="RuleChainBuilder" /> seam.
    /// </summary>
    private static int RunCoverageLints(string demoPath, RuleConfigLoadResult load)
    {
        Console.WriteLine($"  coverage: evaluating {Path.GetFileName(demoPath)}…");
        ParsedDemo parsed = DemoParser.Parse(File.ReadAllBytes(demoPath).AsMemory());

        // Resolve every loaded v2 ruleset against the demo's real tick rate + active source
        // profile, then build onto one graph. A ruleset that fails to resolve is skipped here —
        // its errors already surfaced demo-less.
        RuleChainBuilder builder = new(
            EventRegistry.Build(),
            parsed,
            entityProviders: EntityValueProviderRegistry.CreateDefault(),
            perPlayerEntityProviders: PerPlayerEntityValueProviderRegistry.CreateDefault());
        string profileId = builder.Profile.GetType().Name;
        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        // Composed resolve: the whole directory against the demo's tick rate + profile, so a
        // cross-ruleset read resolves against the export graph (a per-doc resolve would drop it).
        List<CheckedRuleset> rulesets =
            [.. RulesetComposition.Compose(load.Rulesets, adapter, parsed.TickRate, profileId).Rulesets];

        BuildResult build = builder.Build(rulesets);
        AnalysisRun run = DemoAnalysis.Evaluate(parsed, build);
        EvaluationResult result = run.Snapshots
                                  ?? throw new InvalidOperationException("snapshot evaluation expected");

        Dictionary<StateNode, int> fireCountByNode = BuildFireCountByNode(build, result);

        int warnings = RunV2CoverageLints(rulesets, build, run, result, fireCountByNode);
        return warnings;
    }

    /// <summary>
    ///     The v2 half of the <c>--demo</c> coverage lints (compiler-plan §6 obligation 7): every
    ///     <see cref="RulesetCoverageDiagnostic" /> view-binding skip on the demo's profile, plus a
    ///     never-fired warning for each compiled stat / highlight (a coverage-skipped node resolves
    ///     to no graph node and is silently skipped here — its skip diagnostic already reported it).
    ///     A highlight "fires" on its timeline rising edge; a stat fires when its edges fire.
    /// </summary>
    private static int RunV2CoverageLints(
        IReadOnlyList<CheckedRuleset> rulesets, BuildResult build, AnalysisRun run,
        EvaluationResult result, Dictionary<StateNode, int> fireCountByNode)
    {
        int warnings = 0;

        foreach (RulesetCoverageDiagnostic coverage in build.RulesetCoverage ?? [])
        {
            Console.WriteLine($"  warning: {coverage}");
            warnings++;
        }

        foreach (CheckedRuleset rs in rulesets)
        {
            foreach (CheckedStat stat in rs.Stats)
            {
                int? fires = ResolveV2NodeFireCount($"{rs.Id.Id}.{stat.StatId}", result, fireCountByNode);
                if (fires == 0)
                {
                    Console.WriteLine(
                        $"  warning: ruleset '{rs.Id.Id}' stat '{stat.StatId}': fired 0 times in this demo "
                        + "— its trigger/conditions never matched");
                    warnings++;
                }
            }

            foreach (CheckedHighlight highlight in rs.Highlights)
            {
                // The highlight's timeline chain is named _chain_<highlightId> (compiler-plan §6
                // obligation 2), so its rising-edge count is the timeline count for that name.
                if (run.Timeline.CountFor($"_chain_{highlight.HighlightId}") == 0)
                {
                    Console.WriteLine(
                        $"  warning: ruleset '{rs.Id.Id}' highlight '{highlight.HighlightId}': never fired in this demo "
                        + "— its when: condition was never satisfied");
                    warnings++;
                }
            }
        }

        return warnings;
    }

    /// <summary>
    ///     Resolves a v2 stat's summed fire count across every materialized player (all v2 rulesets
    ///     are per-player) via its qualified <c>{ruleset}.{stat}</c> node-map key
    ///     (compiler-plan §6 obligation 8). Null when the node never materialized (e.g. a coverage
    ///     skip), so the caller can distinguish "never fired" from "not built".
    /// </summary>
    private static int? ResolveV2NodeFireCount(
        string qualifiedId, EvaluationResult result, Dictionary<StateNode, int> fireCountByNode)
    {
        int total = 0;
        bool resolved = false;
        foreach (PerPlayerNodeTemplate.MaterializedPlayer player in result.MaterializedPlayers)
        {
            StateNode? node = player.NodesByRuleId?.GetValueOrDefault(qualifiedId);
            if (node is not null)
            {
                resolved = true;
                total += fireCountByNode.GetValueOrDefault(node);
            }
        }

        return resolved ? total : null;
    }

    /// <summary>
    ///     <see cref="StateEdge.FireCount" /> summed per written node, over game-scoped trigger
    ///     edges + every materialized player's edges. Shared by the coverage lints and the
    ///     <c>--test</c> fixture runner — the two demo-backed consumers must agree on what
    ///     "fired" means (the third copy lives in the app's badge layer; the engine's semantic
    ///     core is the planned consolidation point).
    /// </summary>
    private static Dictionary<StateNode, int> BuildFireCountByNode(BuildResult build, EvaluationResult result)
    {
        Dictionary<StateNode, int> fireCountByNode = new(ReferenceEqualityComparer.Instance);
        foreach (StateEdge edge in build.EdgeBacking?.Values ?? Enumerable.Empty<StateEdge>())
        {
            if (edge.WrittenNode is { } node)
            {
                fireCountByNode[node] = fireCountByNode.GetValueOrDefault(node) + edge.FireCount;
            }
        }

        foreach (PerPlayerNodeTemplate.MaterializedPlayer player in result.MaterializedPlayers)
        {
            foreach (StateEdge edge in player.Edges)
            {
                if (edge.WrittenNode is { } node)
                {
                    fireCountByNode[node] = fireCountByNode.GetValueOrDefault(node) + edge.FireCount;
                }
            }
        }

        return fireCountByNode;
    }
}
