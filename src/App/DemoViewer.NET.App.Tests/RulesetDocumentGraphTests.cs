#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Modules.RuleWorkbench;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="RulesetDocumentGraph" /> over the SHIPPED corpus in <c>rules/</c>, which is the
///     only place the numbers in docs/rule-graph/design.md §6.7 can be defended.
///     <para>
///         <b>Every assertion here is a regression gate on a measured defect of the graph this
///         model replaces.</b> The authoring graph draws all 18 of the corpus's <c>compute:</c> stats
///         with no edge at all, which in <c>aim_rating</c> means its 17 isolated nodes are its 17
///         board columns; it draws <c>player_stats</c>, the largest ruleset in the repo, as zero
///         nodes; and it carries no back-reference to the YAML, so no node can be jumped to. Each of
///         those is a test below.
///     </para>
///     <para>
///         Pure by construction: composition plus a static projection, so there is no Avalonia app,
///         no demo and no layout, and the class stays in every tier.
///     </para>
/// </summary>
public class RulesetDocumentGraphTests
{
    /// <summary>
    ///     Node-to-caret sync is the cheap half of design.md §6.7 staging item 1 and it needs one
    ///     thing: a position on every node. A node the editor cannot jump to is the defect that
    ///     blocked the canvas from being an editor at all.
    /// </summary>
    [Test]
    public async Task EveryNode_CarriesAPositionTheEditorCanJumpTo()
    {
        foreach (string file in ShippedRulesets())
        {
            RulesetDocumentGraph graph = BuildFor(file);

            RulesetDocumentNode[] placeless =
                [.. graph.Nodes.Where(n => n.Position.Line <= 0 || n.Position.File is null)];

            await Assert.That(placeless.Length).IsEqualTo(0)
                .Because($"{Path.GetFileName(file)} has nodes with no source position: "
                         + string.Join(", ", placeless.Select(n => n.Key.ToString())));
        }
    }

    /// <summary>
    ///     <c>aim_rating</c>'s 17 <c>compute:</c> stats are its board columns, and the authoring
    ///     graph draws every one of them with no edge. Here each one is wired to the siblings its
    ///     formula divides, which is the author's own mental model ("CS% is cs_clean over
    ///     cs_attempts") and the one relationship the old picture refused to draw.
    /// </summary>
    [Test]
    public async Task AimRating_ComputeStats_AreEachWiredToWhatTheyRead()
    {
        RulesetDocumentGraph graph = BuildFor(Ruleset("aim_rating"));

        RulesetDocumentNode[] computes = [.. graph.Nodes.Where(n => n.Kind == RuleNodeKind.Compute)];
        await Assert.That(computes.Length).IsEqualTo(17)
            .Because("the 17 compute: stats of aim_rating.rules.yaml are the 17 columns of its board");

        HashSet<RulesetDocumentNodeKey> reads = [.. graph.Edges.Select(e => e.Target)];
        string[] isolated = [.. computes.Where(n => !reads.Contains(n.Key)).Select(n => n.Id)];

        await Assert.That(isolated.Length).IsEqualTo(0)
            .Because("a compute: lowers to a round-end edge the engine emits no descriptor for, so "
                     + "AuthoringGraph drew all 17 of these unconnected: " + string.Join(", ", isolated));
    }

    /// <summary>
    ///     <c>player_stats</c> is the largest ruleset in the repo, 58 stats over 505 lines, and the
    ///     authoring graph yields 0 nodes for it with no demo loaded. A document-derived model cannot:
    ///     its nodes ARE the document's entries.
    /// </summary>
    [Test]
    public async Task PlayerStats_DrawsItsDocument_RatherThanNothing()
    {
        RulesetDocumentGraph graph = BuildFor(Ruleset("player_stats"));

        RulesetDocumentNode[] own = [.. graph.Nodes.Where(n => n.IsAuthorable)];
        Console.WriteLine($"[player_stats] own={own.Length} with-deps={graph.Nodes.Count} edges={graph.Edges.Count}");

        await Assert.That(own.Length).IsGreaterThanOrEqualTo(50)
            .Because("player_stats declares 58 stats and the authoring graph draws none of them");
        await Assert.That(graph.Edges.Count).IsGreaterThan(0)
            .Because("a 58-stat ruleset whose computes read its counters has value references to draw");
    }

    /// <summary>
    ///     The <c>use:</c> dependency is drawn and is NOT editable, which is the distinction an edit
    ///     has to respect: an edit is addressed at the file the Workbench has open.
    /// </summary>
    [Test]
    public async Task ADependencysNodes_AreDrawnAndAreNotAuthorable()
    {
        RulesetDocumentGraph graph = BuildFor(Ruleset("player_stats"));

        await Assert.That(graph.Nodes.Any(n => !n.IsAuthorable)).IsTrue()
            .Because("player_stats declares use: [ kast ], whose nodes compose in");
        await Assert.That(graph.Nodes.Where(n => n.IsAuthorable).All(n => n.Key.Ruleset == "player_stats"))
            .IsTrue()
            .Because("only the open document's own entries can be edited");
    }

    /// <summary>
    ///     A qualified read of another ruleset's stat is marked on the node and is not an edge. The
    ///     node it names belongs to a different document, and dropping the read silently is how the
    ///     old canvas lost the relationship in the first place.
    /// </summary>
    [Test]
    public async Task ACrossRulesetRead_MarksItsNode_AndDrawsNoEdge()
    {
        RulesetDocumentGraph graph = BuildFor(Ruleset("player_stats"));

        RulesetDocumentNode[] marked = [.. graph.Nodes.Where(n => n.CrossRulesetReads.Count > 0)];
        Console.WriteLine("[cross-ruleset] "
                          + string.Join(", ", marked.Select(n => $"{n.Key} -> {string.Join("/", n.CrossRulesetReads)}")));

        await Assert.That(marked.Length).IsGreaterThan(0)
            .Because("player_stats reads kast's exported stats through use:");
        await Assert.That(marked.SelectMany(n => n.CrossRulesetReads).All(r => r.Contains('.', StringComparison.Ordinal)))
            .IsTrue()
            .Because("a read that leaves the ruleset is qualified, which is how it was resolved");

        Dictionary<RulesetDocumentNodeKey, string> owners =
            graph.Nodes.ToDictionary(n => n.Key, n => n.Key.Ruleset, EqualityComparer<RulesetDocumentNodeKey>.Default);
        await Assert.That(graph.Edges.All(e => owners[e.Source] == owners[e.Target])).IsTrue()
            .Because("an edge is a sibling read; a cross-ruleset read is a mark on the node");
    }

    /// <summary>
    ///     The corpus-wide shape, which is the claim design.md §6.7 makes and the thing a future
    ///     change to resolution would move. Isolation is what the numbers are about: the authoring
    ///     graph leaves 108 of 230 nodes with out-degree 0 while hanging 137 of 240 edges off four
    ///     lifecycle hubs, and a model that regressed toward either would still pass every test above.
    /// </summary>
    [Test]
    public async Task TheCorpus_IsMostlyConnected_AndHasNoHubs()
    {
        int nodes = 0, edges = 0, isolated = 0, positioned = 0;
        Dictionary<RulesetDocumentNodeKey, int> outDegree = [];

        foreach (string file in ShippedRulesets())
        {
            RulesetDocumentGraph graph = BuildFor(file);

            // The open document only, on both sides of the count. A ruleset that others `use:`
            // composes into every one of their graphs, and counting its nodes once while counting
            // its edges once per dependent would inflate the connectivity this test is about.
            HashSet<RulesetDocumentNodeKey> own = [.. graph.Nodes.Where(n => n.IsAuthorable).Select(n => n.Key)];
            HashSet<RulesetDocumentNodeKey> wired = [];
            foreach (RulesetDocumentEdge edge in graph.Edges.Where(e => own.Contains(e.Target)))
            {
                wired.Add(edge.Source);
                wired.Add(edge.Target);
                outDegree[edge.Source] = outDegree.GetValueOrDefault(edge.Source) + 1;
                edges++;
            }

            foreach (RulesetDocumentNode node in graph.Nodes.Where(n => n.IsAuthorable))
            {
                nodes++;
                if (node.Position.Line > 0)
                {
                    positioned++;
                }

                if (!wired.Contains(node.Key))
                {
                    isolated++;
                }
            }
        }

        int hub = outDegree.Count == 0 ? 0 : outDegree.Values.Max();
        Console.WriteLine($"[document-graph] nodes={nodes} edges={edges} isolated={isolated} "
                          + $"positioned={positioned} largest-out-degree={hub}");

        await Assert.That(positioned).IsEqualTo(nodes)
            .Because("a node with no position cannot be jumped to, and every one of these has one");
        await Assert.That(nodes).IsGreaterThanOrEqualTo(200)
            .Because("the corpus declares over 200 stats and highlights across its 15 documents");
        await Assert.That(edges).IsGreaterThanOrEqualTo(90)
            .Because("the value references AuthoringGraph does not draw are the point of this model");
        await Assert.That(isolated * 100 / nodes).IsLessThan(40)
            .Because("isolated here is no edge in EITHER direction, the state 17 of aim_rating's 51 nodes "
                     + "are drawn in today, and this model has to keep that share well under the 47% of "
                     + "AuthoringGraph nodes that have not even an outgoing one");
        await Assert.That(hub * 100 / Math.Max(edges, 1)).IsLessThan(15)
            .Because("57% of AuthoringGraph's edges leave four lifecycle hubs, which distinguishes nothing");
    }

    /// <summary>
    ///     Every edge endpoint is a node of the model. An edge drawn onto a key the canvas does not
    ///     own is a box that never appears, which is how the authoring graph silently dropped rows
    ///     (design.md §1.1 tabulates ten such sites, none of them counted).
    /// </summary>
    [Test]
    public async Task EveryEdge_RunsBetweenNodesTheModelOwns()
    {
        foreach (string file in ShippedRulesets())
        {
            RulesetDocumentGraph graph = BuildFor(file);
            HashSet<RulesetDocumentNodeKey> keys = [.. graph.Nodes.Select(n => n.Key)];

            await Assert.That(graph.Edges.All(e => keys.Contains(e.Source) && keys.Contains(e.Target)))
                .IsTrue()
                .Because($"{Path.GetFileName(file)} draws an edge onto a node it does not own");
            await Assert.That(graph.Edges.Any(e => e.Source.Equals(e.Target))).IsFalse()
                .Because("a self-loop routes through its own box and is dropped, as the MSAGL path also does");
        }
    }

    /// <summary>Nothing composed is nothing drawn, rather than a throw.</summary>
    [Test]
    public async Task NoRulesets_ProjectToAnEmptyGraph()
    {
        RulesetDocumentGraph graph = RulesetDocumentGraph.Build([], "anything");

        await Assert.That(graph.Nodes.Count).IsEqualTo(0);
        await Assert.That(graph.Edges.Count).IsEqualTo(0);
    }

    // ── the corpus, composed the way RenderGraphForOpenFile composes it ──────────────────────────

    /// <summary>
    ///     Composes one shipped ruleset with its transitive <c>use:</c> dependencies and projects it,
    ///     which is exactly what the Workbench does for the open file: one document at a time, with a
    ///     demo-less tick rate and the fallback profile.
    /// </summary>
    private static RulesetDocumentGraph BuildFor(string path)
    {
        Dictionary<string, RulesetDoc> byId = new(StringComparer.Ordinal);
        RulesetDoc? open = null;
        foreach (string candidate in ShippedRulesets())
        {
            if (RulesetDocumentLoader.Load(File.ReadAllText(candidate), candidate).Doc is not { } doc)
            {
                continue;
            }

            byId[doc.Id] = doc;
            if (string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
            {
                open = doc;
            }
        }

        if (open is null)
        {
            throw new InvalidOperationException($"{path} did not load as a ruleset document");
        }

        List<RulesetDoc> docs = [open];
        HashSet<string> seen = new(StringComparer.Ordinal)
        {
            open.Id
        };
        Queue<string> pending = new(open.Use);
        while (pending.Count > 0)
        {
            string id = pending.Dequeue();
            if (seen.Add(id) && byId.TryGetValue(id, out RulesetDoc? dep))
            {
                docs.Add(dep);
                foreach (string next in dep.Use)
                {
                    pending.Enqueue(next);
                }
            }
        }

        RulesetComposition.Result composed = RulesetComposition.Compose(docs,
            CatalogScopeAdapter.From(CatalogResource.Load()), 64.0,
            DemoSourceProfileRegistry.DefaultFallback.GetType().Name);

        if (composed.Rulesets.Count == 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(path)} composed to nothing: "
                                                + string.Join("; ", composed.Diagnostics.Select(d => d.ToString())));
        }

        return RulesetDocumentGraph.Build(composed.Rulesets, open.Id);
    }

    private static string Ruleset(string id) =>
        ShippedRulesets().FirstOrDefault(p =>
            Path.GetFileName(p).Equals(id + ".rules.yaml", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"{id}.rules.yaml is not in the shipped corpus");

    private static IEnumerable<string> ShippedRulesets() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "rules"), "*.rules.yaml").OrderBy(p => p, StringComparer.Ordinal);

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }
}
