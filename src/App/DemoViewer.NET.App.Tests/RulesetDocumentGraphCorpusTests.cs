#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Modules.RuleWorkbench;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The corpus-wide shape of <see cref="RulesetDocumentGraph" /> over every shipped ruleset in
///     <c>rules/</c>, composed in one pass the way <c>AnalysisBench rules check</c> composes it.
///     <para>
///         <b>This is a characterisation gate, and every number in it is a measurement rather than a
///         preference.</b> The model exists because <c>AuthoringGraph</c> draws the wrong picture of
///         the same corpus, so the only defence of the change is the corpus itself: each test below
///         names the authoring graph's number, asserts the document graph's, and prints both, so a
///         red reads as a number that moved rather than as a boolean that flipped.
///     </para>
///     <para>
///         Provenance of the comparison figures, all from docs/rule-graph/design.md §6.7 over this
///         same directory: 240 edges of which 137 leave four lifecycle hub nodes, 230 nodes of which
///         108 have out-degree 0, all 18 <c>compute:</c> stats drawn with no edge, and
///         <c>player_stats</c> drawn as nothing at all. Provenance of the document-graph figures:
///         the <c>[corpus]</c> line each test prints, measured 2026-09-22 against the pinned
///         CS2DemoKit 0.12.0 as 216 nodes, 110 edges, 55 isolated and a largest out-degree of 6.
///     </para>
///     <para>
///         Every ceiling below sits above its measurement on purpose. A gate pinned to the exact
///         number turns red the first time somebody adds a stat to <c>rules/</c>, which trains people
///         to re-baseline it without reading it; these are set to catch a node kind that stopped
///         resolving, not a corpus that grew.
///     </para>
///     <para>
///         Pure by construction: a YAML load, a composition and a static projection, so there is no
///         Avalonia, no demo and no layout, and the class is uncategorised and runs in every tier.
///     </para>
/// </summary>
public class RulesetDocumentGraphCorpusTests
{
    /// <summary>How many offenders a failure names before it stops counting, so a red stays readable.</summary>
    private const int SampleSize = 10;

    /// <summary>
    ///     Node-to-caret sync needs a position on every node, and the document model's claim is that
    ///     it has one for all 216. <c>AuthoringGraphNode</c> carries no back-reference to the YAML at
    ///     all, so against the old model this test could not even be written.
    /// </summary>
    [Test]
    public async Task EveryNode_CarriesASourcePosition()
    {
        Corpus corpus = Corpus.Load();

        // SourcePosition.None is (null, 0, 0), and a named file with a 0 line is the same defect
        // reached another way, so both spellings of "nowhere to jump to" are caught here.
        RulesetDocumentNode[] placeless =
        [
            .. corpus.Graph.Nodes.Where(n => n.Position.File is null || n.Position.Line <= 0)
        ];

        Console.WriteLine($"[corpus] nodes={corpus.Graph.Nodes.Count} placeless={placeless.Length}");

        await Assert.That(placeless.Length).IsEqualTo(0)
            .Because($"{placeless.Length} of {corpus.Graph.Nodes.Count} nodes cannot be jumped to: "
                     + Sample(placeless.Select(n => n.Key.ToString())));
    }

    /// <summary>
    ///     Every edge endpoint is a node the model declares. An edge onto a key with no node is a
    ///     wire into empty canvas, which is the silent-drop failure design.md §1.1 tabulates ten
    ///     sites of in the graph this replaces.
    /// </summary>
    [Test]
    public async Task NoEdge_PointsAtANodeThatDoesNotExist()
    {
        Corpus corpus = Corpus.Load();
        HashSet<RulesetDocumentNodeKey> declared = [.. corpus.Graph.Nodes.Select(n => n.Key)];

        RulesetDocumentEdge[] dangling =
        [
            .. corpus.Graph.Edges.Where(e => !declared.Contains(e.Source) || !declared.Contains(e.Target))
        ];

        Console.WriteLine($"[corpus] edges={corpus.Graph.Edges.Count} dangling={dangling.Length}");

        await Assert.That(corpus.Graph.Edges.Count).IsGreaterThan(0)
            .Because("an endpoint sweep over no edges asserts nothing");
        await Assert.That(dangling.Length).IsEqualTo(0)
            .Because($"{dangling.Length} of {corpus.Graph.Edges.Count} edges name a node the model does "
                     + "not own: "
                     + Sample(dangling.Select(e => $"{e.Source} -> {e.Target} ({e.Reference})")));
    }

    /// <summary>
    ///     Isolation is the pathology the model was built to fix, so it is the number to gate on.
    ///     <b>Isolated here means no edge in EITHER direction</b>, which is stricter than the 47% of
    ///     authoring-graph nodes that merely have out-degree 0, and the measured share is still 25%,
    ///     55 of 216.
    /// </summary>
    [Test]
    public async Task IsolatedNodes_StayWellBelowTheAuthoringGraphsShare()
    {
        Corpus corpus = Corpus.Load();

        HashSet<RulesetDocumentNodeKey> wired = [];
        foreach (RulesetDocumentEdge edge in corpus.Graph.Edges)
        {
            wired.Add(edge.Source);
            wired.Add(edge.Target);
        }

        RulesetDocumentNode[] isolated = [.. corpus.Graph.Nodes.Where(n => !wired.Contains(n.Key))];
        int share = isolated.Length * 100 / corpus.Graph.Nodes.Count;

        Console.WriteLine($"[corpus] isolated={isolated.Length}/{corpus.Graph.Nodes.Count} ({share}%) "
                          + $"wired={wired.Count}");

        await Assert.That(corpus.Graph.Nodes.Count).IsGreaterThanOrEqualTo(200)
            .Because($"a share over only {corpus.Graph.Nodes.Count} nodes is not the corpus; the fifteen "
                     + "shipped rulesets declare 216 stats and highlights between them");
        await Assert.That(share).IsLessThanOrEqualTo(30)
            .Because($"{isolated.Length} of {corpus.Graph.Nodes.Count} nodes ({share}%) are drawn with no "
                     + "wire at all, against a measured 25% and the authoring graph's 108 of 230 with not "
                     + "even an outgoing one: " + Sample(isolated.Select(n => n.Key.ToString())));
    }

    /// <summary>
    ///     No node is a hub. 137 of the authoring graph's 240 edges leave four lifecycle nodes, which
    ///     is a picture of the engine's scaffolding rather than of the author's rules, and a single
    ///     node sourcing a fifth of the corpus's edges is that pathology returning under a new name.
    ///     <para>
    ///         The four-node share is asserted beside the single-node one because the defect being
    ///         gated was never ONE hub: it was 57% of the edges leaving four lifecycle nodes between
    ///         them, a shape no per-node ceiling of a fifth would have caught. The document graph's
    ///         busiest four carry 14%.
    ///     </para>
    /// </summary>
    [Test]
    public async Task NoNode_SourcesAFifthOfTheCorpusEdges()
    {
        Corpus corpus = Corpus.Load();

        Dictionary<RulesetDocumentNodeKey, int> outDegree = [];
        foreach (RulesetDocumentEdge edge in corpus.Graph.Edges)
        {
            outDegree[edge.Source] = outDegree.GetValueOrDefault(edge.Source) + 1;
        }

        KeyValuePair<RulesetDocumentNodeKey, int>[] busiest =
            [.. outDegree.OrderByDescending(pair => pair.Value).Take(4)];
        int edges = corpus.Graph.Edges.Count;
        int top = busiest.Length == 0 ? 0 : busiest[0].Value * 100 / Math.Max(edges, 1);
        int four = busiest.Sum(pair => pair.Value) * 100 / Math.Max(edges, 1);
        string named = Sample(busiest.Select(pair => $"{pair.Key}={pair.Value}"));

        Console.WriteLine($"[corpus] edges={edges} top-out-degree={top}% top-four={four}% {named}");

        await Assert.That(edges).IsGreaterThanOrEqualTo(90)
            .Because($"a hub share over only {edges} edges measures nothing; the corpus draws 110 value "
                     + "references and the authoring graph draws none of them");
        await Assert.That(top).IsLessThan(20)
            .Because($"{busiest[0].Key} sources {busiest[0].Value} of {edges} edges ({top}%), against a "
                     + "measured 6 of 110 and the four authoring-graph lifecycle hubs that carry 57% of "
                     + "its edges between them");
        await Assert.That(four).IsLessThan(25)
            .Because($"the four busiest nodes source {four}% of {edges} edges, against a measured 14% and "
                     + "the 137 of 240 four lifecycle nodes carry in the graph this replaces: " + named);
    }

    /// <summary>
    ///     Every shipped ruleset draws something. The authoring graph yields zero nodes for
    ///     <c>player_stats</c>, the largest file in the repo at 58 stats, because its nodes come from
    ///     a demo-driven build rather than from the document; a document-derived model cannot, since
    ///     its nodes ARE the document's entries.
    /// </summary>
    [Test]
    public async Task EveryShippedRuleset_DrawsANonEmptyNodeSet()
    {
        Corpus corpus = Corpus.Load();

        Dictionary<string, int> byRuleset = corpus.Graph.Nodes
            .GroupBy(n => n.Key.Ruleset, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        Console.WriteLine("[corpus] " + string.Join(", ",
            corpus.DocumentIds.Select(id => $"{id}={byRuleset.GetValueOrDefault(id)}")));

        string[] empty = [.. corpus.DocumentIds.Where(id => byRuleset.GetValueOrDefault(id) == 0)];

        await Assert.That(corpus.DocumentIds.Count).IsGreaterThanOrEqualTo(15)
            .Because($"only {corpus.DocumentIds.Count} rulesets loaded from rules/, so the sweep below "
                     + "would pass by covering nothing");
        await Assert.That(empty.Length).IsEqualTo(0)
            .Because($"{empty.Length} of {corpus.DocumentIds.Count} shipped rulesets draw no node at all: "
                     + Sample(empty));
        await Assert.That(byRuleset.GetValueOrDefault("player_stats")).IsGreaterThanOrEqualTo(50)
            .Because($"player_stats declares 58 stats and draws {byRuleset.GetValueOrDefault("player_stats")} "
                     + "nodes, against the authoring graph's 0");
    }

    /// <summary>Joins the first <see cref="SampleSize" /> offenders and counts the rest.</summary>
    private static string Sample(IEnumerable<string> offenders)
    {
        string[] all = [.. offenders];
        return all.Length <= SampleSize
            ? string.Join(", ", all)
            : string.Join(", ", all.Take(SampleSize)) + $", and {all.Length - SampleSize} more";
    }

    // ── the corpus, composed once ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The whole of <c>rules/</c> loaded, composed and projected, which is the unit every test
    ///     above measures.
    ///     <para>
    ///         Composed as ONE set rather than file by file, for the same reason
    ///         <c>AnalysisBench rules check</c> composes it that way: a qualified cross-ruleset read
    ///         resolves against the other loaded rulesets' exports, and a per-document composition
    ///         would reject it. It also means a ruleset that several others <c>use:</c> is counted
    ///         once, so the shares above are shares of the corpus rather than of the sum of fifteen
    ///         overlapping graphs.
    ///     </para>
    /// </summary>
    /// <param name="Graph">The projection under test.</param>
    /// <param name="DocumentIds">Every <c>ruleset:</c> id that loaded, which is what the graph has to cover.</param>
    private sealed record Corpus(RulesetDocumentGraph Graph, IReadOnlyList<string> DocumentIds)
    {
        /// <summary>Loads and projects the shipped corpus, skipping the test when the repo is not on disk.</summary>
        /// <returns>The composed model.</returns>
        internal static Corpus Load()
        {
            string root = DemoTestHelper.FindRepoRoot()
                          ?? throw new SkipTestException("repo root not found from the test bin");

            RuleConfigLoadResult loaded = YamlConfigLoader.TryLoadDirectory(Path.Combine(root, "rules"));
            if (!loaded.Success)
            {
                throw new InvalidOperationException("rules/ did not load: "
                                                    + string.Join("; ", loaded.Errors.Select(e => e.ToString())));
            }

            // Demo-less, exactly as the Workbench composes for an open file with no demo loaded: the
            // 64-tick default and the vanilla GOTV profile a demo-less RuleChainBuilder falls back to.
            RulesetComposition.Result composed = RulesetComposition.Compose(
                loaded.Rulesets,
                CatalogScopeAdapter.From(CatalogResource.Load()),
                64.0,
                DemoSourceProfileRegistry.DefaultFallback.GetType().Name);

            // A ruleset that fails to check is dropped from Rulesets rather than reported here, and a
            // dropped ruleset would silently make every share above a share of a smaller corpus.
            if (composed.Rulesets.Count != loaded.Rulesets.Count)
            {
                throw new InvalidOperationException(
                    $"{loaded.Rulesets.Count} rulesets loaded but {composed.Rulesets.Count} composed: "
                    + string.Join("; ", composed.Diagnostics.Select(d => d.ToString())));
            }

            // No open document, so nothing is authorable. The properties gated above are about the
            // shape of the whole corpus, which the authorable/read-only split does not change.
            return new Corpus(
                RulesetDocumentGraph.Build(composed.Rulesets, openRulesetId: null),
                [.. loaded.Rulesets.Select(d => d.Id)]);
        }
    }
}
