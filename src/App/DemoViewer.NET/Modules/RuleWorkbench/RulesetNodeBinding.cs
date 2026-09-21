#region

using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace DemoViewer.NET.Modules.RuleWorkbench;

/// <summary>What a graph node turns out to be in the document behind it.</summary>
public enum RulesetNodeKind
{
    /// <summary>Nothing in the open document declares it: engine scaffolding, an enrichment, a derived counter.</summary>
    NotEditable,

    /// <summary>A <c>stats:</c> entry, addressable by <see cref="RulesetNodeBinding.Id" />.</summary>
    Stat,

    /// <summary>A <c>highlights:</c> entry.</summary>
    Highlight
}

/// <summary>
///     The join between a drawn node and the YAML that declared it, and the reason editing is
///     possible at all.
///     <para>
///         <c>AuthoringGraphNode</c> carries no back-reference to the document (design.md §6.1), so
///         the join has to be made from what it does carry: its name. Measured against the shipped
///         corpus, a stat's node is named exactly its id, which is what makes this work.
///     </para>
///     <para>
///         <b>It is deliberately narrow.</b> A node that does not bind is not editable, and the
///         editor says so rather than guessing. The graph draws plenty that no <c>stats:</c> entry
///         owns: the root, <c>MatchLive</c>, <c>RoundActive</c>, every <c>enrich.*</c>, and a
///         <c>tally:</c> stat's threshold counters, which are named for their buckets
///         (<c>rounds_3k</c>) rather than for the stat that produced them. Offering an edit box for
///         one of those would write a key into a document that has nowhere to put it.
///     </para>
/// </summary>
/// <param name="Kind">What the node is.</param>
/// <param name="Id">The <c>stats:</c> or <c>highlights:</c> key, or empty when nothing binds.</param>
public readonly record struct RulesetNodeBinding(RulesetNodeKind Kind, string Id)
{
    /// <summary>The answer for a node nothing in the document declares.</summary>
    public static RulesetNodeBinding None => new(RulesetNodeKind.NotEditable, "");

    /// <summary>Whether an edit can be addressed through this binding.</summary>
    public bool IsEditable => Kind != RulesetNodeKind.NotEditable;

    /// <summary>
    ///     Builds the name-to-declaration map for one document. A stat binds on its id; a highlight
    ///     binds on its id and on the <c>_chain_</c>-prefixed name the builder gives its chain node.
    /// </summary>
    public static IReadOnlyDictionary<string, RulesetNodeBinding> Map(RulesetDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        Dictionary<string, RulesetNodeBinding> map = new(StringComparer.Ordinal);

        foreach (StatDef stat in doc.Stats)
        {
            map[stat.Id] = new RulesetNodeBinding(RulesetNodeKind.Stat, stat.Id);
        }

        foreach (HighlightDef highlight in doc.Highlights)
        {
            RulesetNodeBinding binding = new(RulesetNodeKind.Highlight, highlight.Id);

            // A stat and a highlight share one id namespace, so a stat already in the map wins:
            // the stat is the thing with editable fields, and overwriting it would point the editor
            // at the wrong section.
            if (!map.ContainsKey(highlight.Id))
            {
                map[highlight.Id] = binding;
            }

            map["_chain_" + highlight.Id] = binding;
        }

        return map;
    }
}
