#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;

#endregion

namespace DemoViewer.NET.Modules.RuleWorkbench;

/// <summary>
///     Which control a facet row is edited with, which is the only thing about a facet's type a form
///     has to know.
/// </summary>
public enum MatchFacetEditor
{
    /// <summary>A three-state checkbox: unset, <c>true</c>, <c>false</c>.</summary>
    Toggle,

    /// <summary>
    ///     A comparator and a number, which is how the YAML authors <c>ticks_since_spot: "&lt;= 640"</c>.
    /// </summary>
    Number,

    /// <summary>A text or combo box over a string facet (<c>weapon</c>, <c>map</c>).</summary>
    Text,

    /// <summary>
    ///     A type no control here draws. The catalog carries only <c>bool</c>, <c>int</c>, <c>float</c>
    ///     and <c>string</c> facets today, so this is the answer for a type added later: the row is
    ///     still listed and is NOT offered as editable, rather than being drawn as something it is not.
    /// </summary>
    Unsupported
}

/// <summary>
///     The authored right-hand side of one <c>match:</c> entry, in the four shapes the grammar allows.
///     <para>
///         The facet on the left is implicit (it is the map key), so this carries only the test's
///         shape, exactly as <see cref="UnaryTest" /> does. It exists as a separate type because
///         <see cref="UnaryTest" /> carries a <see cref="SourcePosition" /> in every case and a form
///         binds to a value, not to a place in a file.
///     </para>
/// </summary>
public abstract record MatchFacetValue
{
    /// <summary>
    ///     Renders the value as the unary-test text an author writes after the facet's colon.
    ///     <b>This is the test text, not the YAML scalar</b>: a comparison comes back as
    ///     <c>&lt;= 320</c>, and whatever quoting the document needs around it belongs to the writer.
    /// </summary>
    /// <returns>Text that <c>UnaryTestParser.Parse</c> reads back as the same value.</returns>
    public abstract string ToMatchValue();

    /// <summary>
    ///     Projects an engine-parsed unary test onto the form's value model.
    /// </summary>
    /// <param name="test">The parsed test, or <c>null</c> for a facet the author did not bind.</param>
    /// <returns>The value, or <c>null</c> when there is nothing authored.</returns>
    public static MatchFacetValue? From(UnaryTest? test) =>
        test switch
        {
            // A bare literal and an explicit `== x` lower to the identical comparison AST
            // (MatchLowering), so the form keeps one shape for both and renders it the way the
            // corpus writes it, bare.
            LiteralTest literal => new MatchComparison(ComparisonOperator.Equal, literal.RawText),
            ComparisonTest comparison => new MatchComparison(comparison.Operator, comparison.LiteralRawText),
            RangeTest range => new MatchRange(range.Low, range.High),
            InListRefTest listRef => new MatchMembership(listRef.ListRef, []),
            InListLiteralTest listLiteral => new MatchMembership(null, listLiteral.Items),
            _ => null
        };
}

/// <summary>
///     A comparison against one literal: <c>enemy: true</c>, <c>hitgroup: 1</c>,
///     <c>ticks_since_spot: "&lt;= 640"</c>.
/// </summary>
/// <param name="Operator">The comparator, <see cref="ComparisonOperator.Equal" /> for a bare literal.</param>
/// <param name="Literal">
///     The literal's EXACT source text, quotes included. Verbatim because the form's job is to hand
///     back what it was given unless the author changed it, and a string facet's quoting is part of
///     what was given.
/// </param>
public sealed record MatchComparison(ComparisonOperator Operator, string Literal) : MatchFacetValue
{
    /// <inheritdoc />
    public override string ToMatchValue() =>
        Operator == ComparisonOperator.Equal ? Literal : $"{Symbol(Operator)} {Literal}";

    /// <summary>
    ///     The six comparators of the unary-test grammar, spelled the way <c>UnaryTestParser</c> reads
    ///     them back. A closed set: the parser lexes exactly these, and the form's round-trip test is
    ///     what holds the two spellings together.
    /// </summary>
    private static string Symbol(ComparisonOperator op) =>
        op switch
        {
            ComparisonOperator.Equal => "==",
            ComparisonOperator.NotEqual => "!=",
            ComparisonOperator.Greater => ">",
            ComparisonOperator.GreaterOrEqual => ">=",
            ComparisonOperator.Less => "<",
            ComparisonOperator.LessOrEqual => "<=",
            _ => ""
        };
}

/// <summary>An inclusive integer range: <c>count: [2..5]</c>.</summary>
/// <param name="Low">The inclusive lower bound.</param>
/// <param name="High">The inclusive upper bound.</param>
public sealed record MatchRange(long Low, long High) : MatchFacetValue
{
    /// <inheritdoc />
    public override string ToMatchValue() => $"[{Low}..{High}]";
}

/// <summary>
///     A membership test, against a named <c>define:</c> list (<c>weapon: in rifles</c>) or an inline
///     one (<c>weapon: in [ak47, m4a1]</c>).
/// </summary>
/// <param name="List">The referenced list's name, or <c>null</c> for an inline list.</param>
/// <param name="Items">The inline list's elements as written, empty for a list reference.</param>
public sealed record MatchMembership(string? List, IReadOnlyList<string> Items) : MatchFacetValue
{
    /// <inheritdoc />
    public override string ToMatchValue() =>
        List is not null ? $"in {List}" : $"in [{string.Join(", ", Items)}]";
}

/// <summary>
///     One row of the facet form: a facet the view offers, its type, and what the open document binds
///     it to.
/// </summary>
/// <param name="Name">The facet name the author writes as the <c>match:</c> key.</param>
/// <param name="Type">
///     The facet's type, from <see cref="CatalogScopeAdapter.FacetType" />, which is the same answer
///     the resolver type-checks the test against.
/// </param>
/// <param name="Value">What the document binds this facet to, or <c>null</c> when it binds nothing.</param>
/// <param name="Position">
///     Where the authored key sits, so a row can jump the caret to it the way a node does.
///     <see cref="SourcePosition.None" /> for an unset row, which is nowhere in the file yet.
/// </param>
public sealed record MatchFacetRow(string Name, RulesType Type, MatchFacetValue? Value, SourcePosition Position)
{
    /// <summary>Whether the document binds this facet at all.</summary>
    public bool IsSet => Value is not null;

    /// <summary>The control this row is edited with.</summary>
    public MatchFacetEditor Editor =>
        Type.Kind switch
        {
            RulesTypeKind.Bool => MatchFacetEditor.Toggle,
            RulesTypeKind.Int or RulesTypeKind.Float => MatchFacetEditor.Number,
            RulesTypeKind.String => MatchFacetEditor.Text,
            _ => MatchFacetEditor.Unsupported
        };

    /// <summary>
    ///     The checkbox state for a <see cref="MatchFacetEditor.Toggle" /> row: <c>null</c> when unset
    ///     OR when the test is a shape a checkbox cannot show, which keeps a
    ///     <c>"!= true"</c> from being displayed as if it were <c>true</c>.
    /// </summary>
    public bool? Toggle =>
        Value is MatchComparison { Operator: ComparisonOperator.Equal } comparison
            ? comparison.Literal switch
            {
                "true" => true,
                "false" => false,
                _ => null
            }
            : null;

    /// <summary>The comparator a numeric row shows, <see cref="ComparisonOperator.Equal" /> when unset.</summary>
    public ComparisonOperator Comparator =>
        Value is MatchComparison comparison ? comparison.Operator : ComparisonOperator.Equal;
}

/// <summary>
///     The <c>match:</c> block of one node, as a typed form scoped to the view the node fires on:
///     every facet that view offers, with its type and its authored value.
///     <para>
///         <b>The rows are the view's facets and nothing else.</b> That is the whole point, and it is
///         what the text pane structurally cannot do: <c>WorkbenchCompletionSource.Build</c> iterates
///         every view and offers all 83 facets whatever the stat fires on, so it cannot tell an author
///         that <c>counter_strafe_good</c> is legal on <c>shot</c> and meaningless on <c>kill</c>. The
///         catalog carries 18 views and 83 facets, mean 4.6 per view, and 88 of the 172 stats in
///         <c>rules/</c> declare a <c>match:</c> block.
///     </para>
///     <para>
///         Derived from <see cref="CatalogResource" /> rather than from a list of facet names kept
///         here, so a facet added to the engine's catalog appears in the form on the next package bump
///         with no edit to this file. The types come from
///         <see cref="CatalogScopeAdapter.FacetType" /> and the values from the engine's own
///         <see cref="UnaryTest" /> parse, so nothing about a <c>match:</c> block is re-derived from
///         its text.
///     </para>
/// </summary>
/// <param name="View">The view the node fires on, empty when it has none.</param>
/// <param name="IsKnownView">
///     Whether <paramref name="View" /> names a catalog view. It distinguishes the four grenade views
///     that really do offer no facets from a view name the catalog does not have, which have the same
///     empty <paramref name="Rows" /> and mean opposite things.
/// </param>
/// <param name="Rows">One row per facet of the view, in the catalog's own order.</param>
/// <param name="Unknown">
///     Authored keys with no facet on this view, kept rather than dropped. The resolver already
///     reports each one as an <c>UnknownFacet</c> diagnostic, so a form that silently omitted them
///     would be editing a block whose broken half it does not show.
/// </param>
public sealed record MatchFacetForm(
    string View,
    bool IsKnownView,
    IReadOnlyList<MatchFacetRow> Rows,
    IReadOnlyList<MatchBinding> Unknown)
{
    /// <summary>The form for a node that fires on no view, which is what a <c>compute:</c> is.</summary>
    public static MatchFacetForm None { get; } = new("", false, [], []);

    /// <summary>The rows the document actually binds, in the catalog's order: the node face's content.</summary>
    public IEnumerable<MatchFacetRow> Set => Rows.Where(r => r.IsSet);

    /// <summary>
    ///     Builds the form for one view from the catalog and the node's authored <c>match:</c> bindings.
    /// </summary>
    /// <param name="catalog">The embedded catalog, normally <see cref="CatalogResource.Load" />.</param>
    /// <param name="view">
    ///     The view the node fires on, which is <c>CheckedStat.ResolvedView</c>: the engine's own answer,
    ///     already resolved through <c>on:</c>, a kind argument such as <c>count: kill</c>, and any
    ///     <c>define:</c> in between.
    /// </param>
    /// <param name="match">
    ///     The node's <c>TriggerDef.Match</c>, or <c>null</c> for a node with no <c>match:</c> block.
    /// </param>
    /// <returns>The form.</returns>
    public static MatchFacetForm For(CatalogRoot catalog, string? view, IReadOnlyList<MatchBinding>? match)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        IReadOnlyList<MatchBinding> bindings = match ?? [];
        CatalogView? scope = view is null
            ? null
            : catalog.Views.FirstOrDefault(v => string.Equals(v.Name, view, StringComparison.Ordinal));

        if (scope is null)
        {
            return new MatchFacetForm(view ?? "", false, [], bindings);
        }

        // First binding wins on a repeated key, which is what the resolver does after reporting a
        // DuplicateMatchKey: the form shows the entry the engine is going to use.
        Dictionary<string, MatchBinding> authored = new(StringComparer.Ordinal);
        foreach (MatchBinding binding in bindings)
        {
            authored.TryAdd(binding.Key, binding);
        }

        List<MatchFacetRow> rows = [];
        HashSet<string> facets = new(StringComparer.Ordinal);
        foreach (CatalogFacet facet in scope.Facets)
        {
            facets.Add(facet.Name);
            MatchBinding? bound = authored.GetValueOrDefault(facet.Name);
            rows.Add(new MatchFacetRow(facet.Name, CatalogScopeAdapter.FacetType(facet),
                MatchFacetValue.From(bound?.Test), bound?.Position ?? SourcePosition.None));
        }

        return new MatchFacetForm(scope.Name, true, rows,
            [.. bindings.Where(b => !facets.Contains(b.Key))]);
    }
}
