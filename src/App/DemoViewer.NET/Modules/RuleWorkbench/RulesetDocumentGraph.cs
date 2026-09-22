#region

using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;

#endregion

namespace DemoViewer.NET.Modules.RuleWorkbench;

/// <summary>
///     The identity of a document-derived node: the ruleset that declares it and the id it is
///     declared under.
///     <para>
///         An id is unique inside one ruleset's shared stat/highlight namespace but NOT across
///         rulesets: the shipped corpus declares <c>enemy_kills_round</c> in
///         <c>highlights_multikill</c> and again in <c>kast</c>. The ruleset is therefore part of
///         the key rather than a decoration on it, which is the same lesson
///         <see cref="ViewModels.GraphNodeKey" /> records for the Analysis graph.
///     </para>
/// </summary>
/// <param name="Ruleset">The declaring ruleset's id (the <c>ruleset:</c> key).</param>
/// <param name="Id">The <c>stats:</c> or <c>highlights:</c> key.</param>
public readonly record struct RulesetDocumentNodeKey(string Ruleset, string Id)
{
    /// <summary>Formats as the qualified read an author would write from another ruleset.</summary>
    /// <returns>The <c>ruleset.id</c> form.</returns>
    public override string ToString() => Ruleset + "." + Id;
}

/// <summary>
///     One node of the document-derived graph: exactly one <c>stats:</c> or <c>highlights:</c> entry,
///     with the back-reference to the YAML that <c>AuthoringGraphNode</c> does not carry.
///     <para>
///         <b>Every node here was written by a human in a file.</b> That is the whole difference from
///         the authoring graph, which draws the engine's scaffolding (the root, <c>MatchLive</c>,
///         <c>RoundActive</c>, every <c>enrich.*</c>, a <c>tally:</c>'s threshold counters) and has
///         nowhere to send an edit for any of it. There is no projected node to hide, so
///         design.md §6.7's authorable-versus-projected problem does not arise;
///         <see cref="IsAuthorable" /> only separates the open document from the rulesets it
///         <c>use:</c>s.
///     </para>
/// </summary>
/// <param name="Key">Identity, stable across renders and across a re-parse of the same document.</param>
/// <param name="Kind">
///     The engine's own node kind. A highlight is <see cref="RuleNodeKind.Highlight" />; a stat
///     carries whichever of <c>flag</c>, <c>count</c>, <c>sum</c>, <c>capture</c>, <c>compute</c>,
///     <c>tally</c>, <c>streak</c>, <c>bucket</c> or <c>rate</c> it resolved to.
/// </param>
/// <param name="ValueType">The stat's value type, or <c>bool</c> for a highlight's <c>when:</c>.</param>
/// <param name="Position">
///     Where the entry starts in its file, which is what a node-to-caret jump moves to.
///     <b>Never <see cref="SourcePosition.None" /> for a node built from a loaded document</b>: the
///     resolver copies the position off the model record, and the model record takes it from the
///     YAML node.
/// </param>
/// <param name="Label">The author's display name (<c>label:</c>, or a highlight's <c>title:</c>), or <c>null</c>.</param>
/// <param name="Events">
///     The trigger's concrete wire events, for the header chips design.md §6.7 puts <c>on:</c> and
///     the gates in. Empty for a <c>compute:</c>, an expression <c>flag:</c>, and a count that rides
///     a sibling flag's rising edge, none of which fire on an event of their own.
/// </param>
/// <param name="CrossRulesetReads">
///     Stat references that resolve outside this node's ruleset, as written. They are NOT edges,
///     because the node they name belongs to another document, and they are the one thing about a
///     node the canvas would otherwise lose without trace.
/// </param>
/// <param name="IsAuthorable">
///     Whether the open document declares this node, and therefore whether an edit can be addressed
///     to it. False for a node contributed by a <c>use:</c> dependency, which is drawn and is read-only.
/// </param>
public sealed record RulesetDocumentNode(
    RulesetDocumentNodeKey Key,
    RuleNodeKind Kind,
    RulesType ValueType,
    SourcePosition Position,
    string? Label,
    IReadOnlyList<string> Events,
    IReadOnlyList<string> CrossRulesetReads,
    bool IsAuthorable)
{
    /// <summary>The <c>stats:</c> or <c>highlights:</c> key, without its ruleset.</summary>
    public string Id => Key.Id;

    /// <summary>Which of the two document sections declares this node.</summary>
    public bool IsHighlight => Kind == RuleNodeKind.Highlight;
}

/// <summary>
///     One value reference between two nodes of the same ruleset, running the way the value does:
///     from the node that is read to the node that reads it.
///     <para>
///         These are the wires design.md §6.7 argues the canvas should draw and does not. They come
///         from <see cref="CheckedStat.DeclaredReads" />, which the resolver precomputes from every
///         reference in the node's checked ASTs, so a <c>compute:</c> formula's operands are edges
///         here although the engine emits no <c>GraphEdgeDescriptor</c> for them and the authoring
///         graph therefore draws all 18 of the corpus's computes with no edge at all.
///     </para>
/// </summary>
/// <param name="Source">The node that is read.</param>
/// <param name="Target">The node whose <c>compute:</c>, <c>when:</c>, <c>where:</c> or <c>while:</c> reads it.</param>
/// <param name="Reference">The read path as the author wrote it, which is the edge's label.</param>
public sealed record RulesetDocumentEdge(
    RulesetDocumentNodeKey Source,
    RulesetDocumentNodeKey Target,
    string Reference);

/// <summary>
///     The node/edge model the Rule Workbench canvas is built from: a pure, static projection of the
///     composed <see cref="CheckedRuleset" /> set, with no layout, no view models and no Avalonia.
///     <para>
///         <b>This replaces <c>AuthoringGraph</c> as the canvas's source, and the reason is
///         measured.</b> Over <c>rules/</c> the authoring graph puts 137 of 240 edges on four
///         lifecycle hub nodes, leaves 108 of 230 nodes with out-degree 0, and draws every
///         <c>compute:</c> stat unconnected, which in <c>aim_rating</c> means the 17 isolated nodes
///         ARE the 17 board columns the file exists to produce. The checked model carries the
///         author's own relationships instead, and a <see cref="SourcePosition" /> on every node.
///     </para>
///     <para>
///         <b>One dependency the checked model cannot express.</b> A <c>count:</c> that triggers on a
///         sibling <c>flag:</c> resolves to a flag source the resolver keeps to itself: the stat
///         comes back with empty <see cref="CheckedStat.ConcreteEvents" /> and nothing naming the
///         flag, so that wire is not drawn. Recovering it needs an engine change, not a parse of the
///         YAML behind the model's back.
///     </para>
/// </summary>
/// <param name="Nodes">Every declared stat and highlight of every composed ruleset, in document order.</param>
/// <param name="Edges">Every resolving sibling read, in the order the nodes declare them.</param>
public sealed record RulesetDocumentGraph(
    IReadOnlyList<RulesetDocumentNode> Nodes,
    IReadOnlyList<RulesetDocumentEdge> Edges)
{
    /// <summary>The answer for a composition that produced nothing, which is what a failed check leaves.</summary>
    public static RulesetDocumentGraph Empty { get; } = new([], []);

    /// <summary>
    ///     Projects the composed rulesets into nodes and edges.
    /// </summary>
    /// <param name="rulesets">
    ///     The composition's <c>Rulesets</c>, which is the open document plus its transitive
    ///     <c>use:</c> dependencies as the Workbench composes them.
    /// </param>
    /// <param name="openRulesetId">
    ///     The id of the document being edited, whose nodes are authorable. <c>null</c> marks nothing
    ///     authorable, which is the honest answer when no file is open.
    /// </param>
    /// <returns>The model.</returns>
    public static RulesetDocumentGraph Build(IReadOnlyList<CheckedRuleset> rulesets, string? openRulesetId)
    {
        ArgumentNullException.ThrowIfNull(rulesets);

        List<RulesetDocumentNode> nodes = [];
        List<RulesetDocumentEdge> edges = [];

        foreach (CheckedRuleset ruleset in rulesets)
        {
            string rulesetId = ruleset.Id.Id;
            bool authorable = string.Equals(rulesetId, openRulesetId, StringComparison.Ordinal);
            Dictionary<string, RulesetDocumentNodeKey> index = IndexOf(ruleset);

            foreach (CheckedStat stat in ruleset.Stats)
            {
                RulesetDocumentNodeKey key = new(rulesetId, stat.StatId);
                List<string> foreign = [];
                Collect(key, stat.DeclaredReads, index,
                    [stat.TriggerCondition, stat.ValueSelector, stat.WhileGate], edges, foreign);

                nodes.Add(new RulesetDocumentNode(key, stat.Kind, stat.ValueType, stat.Position, stat.Label,
                    stat.ConcreteEvents, foreign, authorable));
            }

            foreach (CheckedHighlight highlight in ruleset.Highlights)
            {
                RulesetDocumentNodeKey key = new(rulesetId, highlight.HighlightId);
                List<string> foreign = [];
                Collect(key, highlight.DeclaredReads, index, [highlight.When], edges, foreign);

                // A highlight fires on its when: conjunction rather than on a trigger of its own, so
                // it has no events to chip. Its title: is the closest thing it has to a label.
                nodes.Add(new RulesetDocumentNode(key, RuleNodeKind.Highlight, highlight.When.ResultType,
                    highlight.Position, highlight.Title, [], foreign, authorable));
            }
        }

        return new RulesetDocumentGraph(nodes, edges);
    }

    /// <summary>
    ///     The ruleset's own id namespace, as <c>StatReferenceCycleDetector</c> builds it: every stat
    ///     id, every highlight id, and every highlight's automatic <c>&lt;id&gt;.count</c>, which
    ///     resolves to the highlight because the canvas draws no separate node for it.
    /// </summary>
    private static Dictionary<string, RulesetDocumentNodeKey> IndexOf(CheckedRuleset ruleset)
    {
        Dictionary<string, RulesetDocumentNodeKey> index = new(StringComparer.Ordinal);

        foreach (CheckedStat stat in ruleset.Stats)
        {
            index.TryAdd(stat.StatId, new RulesetDocumentNodeKey(ruleset.Id.Id, stat.StatId));
        }

        foreach (CheckedHighlight highlight in ruleset.Highlights)
        {
            RulesetDocumentNodeKey key = new(ruleset.Id.Id, highlight.HighlightId);
            index.TryAdd(highlight.HighlightId, key);
            index.TryAdd(highlight.CountNodeId, key);
        }

        return index;
    }

    /// <summary>
    ///     Turns one node's declared reads into its edges and its cross-ruleset marks, classifying
    ///     each read against the checked expressions it came from.
    /// </summary>
    private static void Collect(RulesetDocumentNodeKey owner, IReadOnlyList<string> declaredReads,
        Dictionary<string, RulesetDocumentNodeKey> index, ReadOnlySpan<CheckedExpression?> expressions,
        List<RulesetDocumentEdge> edges, List<string> crossRuleset)
    {
        Dictionary<string, ResolvedReference> resolutions = new(StringComparer.Ordinal);
        foreach (CheckedExpression? expression in expressions)
        {
            if (expression is null)
            {
                continue;
            }

            foreach (ResolvedReference reference in expression.References)
            {
                resolutions.TryAdd(reference.Path, reference);
            }
        }

        foreach (string read in declaredReads)
        {
            if (resolutions.TryGetValue(read, out ResolvedReference? resolution))
            {
                // The engine already decided what this path names, so nothing is re-derived from the
                // text. A non-stat read is a catalog, event or param read, which design.md §6.7
                // renders as a header chip rather than as a wire.
                if (resolution is not { IsStatReference: true, StatPath: { } statPath })
                {
                    continue;
                }

                if (TryResolve(statPath, owner.Ruleset, index, out RulesetDocumentNodeKey target))
                {
                    Connect(owner, target, read, edges);
                }
                else if (!crossRuleset.Contains(statPath, StringComparer.Ordinal))
                {
                    crossRuleset.Add(statPath);
                }

                continue;
            }

            // A read the three ASTs do not carry: a bucket: key expression is collected into
            // DeclaredReads and then kept only as its rendered key part. Resolve it structurally
            // against the id namespace, and stay silent when it does not hit, since a miss here is
            // an ordinary catalog read and not a reference to another document.
            if (TryResolvePrefix(read, owner.Ruleset, index, out RulesetDocumentNodeKey inferred))
            {
                Connect(owner, inferred, read, edges);
            }
        }
    }

    /// <summary>Adds the edge unless it would be a self-loop, which a canvas draws through its own box.</summary>
    private static void Connect(RulesetDocumentNodeKey owner, RulesetDocumentNodeKey target, string read,
        List<RulesetDocumentEdge> edges)
    {
        if (!target.Equals(owner))
        {
            edges.Add(new RulesetDocumentEdge(target, owner, read));
        }
    }

    /// <summary>
    ///     Resolves a stat path against one ruleset's id namespace, accepting the self-qualified
    ///     <c>ruleset.id</c> spelling an author may write for a sibling.
    /// </summary>
    private static bool TryResolve(string statPath, string rulesetId,
        Dictionary<string, RulesetDocumentNodeKey> index, out RulesetDocumentNodeKey key)
    {
        if (index.TryGetValue(statPath, out key))
        {
            return true;
        }

        string qualifier = rulesetId + ".";
        return statPath.StartsWith(qualifier, StringComparison.Ordinal)
               && index.TryGetValue(statPath[qualifier.Length..], out key);
    }

    /// <summary>
    ///     Resolves the longest dotted prefix of a read that names a local node, which is how a
    ///     pseudo-member such as <c>&lt;id&gt;.count</c> or <c>&lt;id&gt;.set</c> hangs off its stat.
    /// </summary>
    private static bool TryResolvePrefix(string read, string rulesetId,
        Dictionary<string, RulesetDocumentNodeKey> index, out RulesetDocumentNodeKey key)
    {
        for (int end = read.Length; end > 0; end = read.LastIndexOf('.', end - 1))
        {
            if (TryResolve(read[..end], rulesetId, index, out key))
            {
                return true;
            }
        }

        key = default;
        return false;
    }
}
