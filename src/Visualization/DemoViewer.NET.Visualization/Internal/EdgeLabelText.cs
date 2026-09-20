namespace DemoViewer.NET.Visualization.Internal;

/// <summary>
///     The one place that decides what text an edge label carries and how wide it is. The layout
///     pass, the renderer and the headless metrics all go through here, because three independent
///     copies of the same format string is how they drift.
///     <para>
///         <b>A predicate does not ride the edge.</b> The label used to be
///         <c>"{Label}  [{ConditionLabel}]"</c>, and on the shipped Analysis graph that is not a
///         label, it is a paragraph: the predicates measured off the fifteen shipped rulesets run to
///         434 characters, which at <see cref="CharWidth" /> places a rect fifteen times the width of
///         the 180-unit node it connects. The overlap metrics never caught it because the layout
///         obligingly separated nodes far enough to fit the text; the graph reads as sparse and
///         enormous rather than crowded. So the condition collapses to a three-character chip and the
///         renderer reveals the predicate on hover (issue #4).
///     </para>
/// </summary>
internal static class EdgeLabelText
{
    /// <summary>
    ///     Mono-calibrated character width in logical units. Deliberately an estimate rather than a
    ///     <c>FormattedText</c> measurement: the headless metrics path has no Avalonia font manager,
    ///     so real measurement is unavailable exactly where the gate runs.
    /// </summary>
    internal const double CharWidth = 6;

    /// <summary>Label rect height in logical units, mono-calibrated like <see cref="CharWidth" />.</summary>
    internal const double LabelHeight = 14;

    /// <summary>
    ///     What a collapsed condition draws as. Three characters, so its contribution to the label
    ///     rect is bounded no matter what the author wrote.
    /// </summary>
    internal const string Chip = "[…]";

    /// <summary>
    ///     A predicate that fits within this many characters stays on the edge, because the chip
    ///     exists to bound a label's width and a short predicate does not need bounding. 30 is one
    ///     default node width at <see cref="CharWidth" />, which makes "a label may be as wide as the
    ///     node it connects" the rule rather than a number picked by eye. It matters: 92 of the 244
    ///     predicates on the shipped graph are things like <c>rising edge</c>, <c>active</c> and
    ///     <c>value &gt;= 2</c>, and collapsing those would hide the useful ones to bound the rest.
    /// </summary>
    private const int InlineBudget = 30;

    private const string Gap = "  ";

    /// <summary>
    ///     The text drawn on the edge: the event name, then the predicate if it fits in
    ///     <see cref="InlineBudget" /> and a chip if it does not.
    /// </summary>
    internal static string Collapsed(IGraphEdge edge)
    {
        if (!HasCondition(edge))
        {
            return edge.Label;
        }

        string full = Expanded(edge);
        return full.Length <= InlineBudget
            ? full
            : edge.Label.Length == 0 ? Chip : edge.Label + Gap + Chip;
    }

    /// <summary>The full text, chip expanded to the predicate. Render-only: layout never reserves it.</summary>
    internal static string Expanded(IGraphEdge edge)
    {
        if (!HasCondition(edge))
        {
            return edge.Label;
        }

        string predicate = "[" + edge.ConditionLabel + "]";
        return edge.Label.Length == 0 ? predicate : edge.Label + Gap + predicate;
    }

    internal static bool HasCondition(IGraphEdge edge) => !string.IsNullOrEmpty(edge.ConditionLabel);

    /// <summary>Whether this edge's predicate is actually hidden behind a chip, so a reveal has work to do.</summary>
    internal static bool IsCollapsed(IGraphEdge edge) =>
        HasCondition(edge) && Expanded(edge).Length > InlineBudget;

    /// <summary>Width of the collapsed label rect, in logical units.</summary>
    internal static double Width(IGraphEdge edge) => Collapsed(edge).Length * CharWidth;
}
