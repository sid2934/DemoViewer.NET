#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Services.Tags;

/// <summary>A slice filter on <see cref="TagInstance.Source" />; the typed face of <see cref="TagSources" />.</summary>
public enum TagSource
{
    Human,
    Suggested,
    Import
}

/// <summary>Which label array a predicate or a pivot axis reads.</summary>
public enum LabelNamespace
{
    /// <summary><see cref="TagInstance.Labels" />: what a person said.</summary>
    Human,

    /// <summary><see cref="TagInstance.Facts" />: what the parser derived.</summary>
    Fact,

    /// <summary>Both, as one set.</summary>
    Any
}

/// <summary>
///     Matches an instance that carries at least one label of <paramref name="Group" /> in
///     <paramref name="Namespace" /> whose value is one of <paramref name="Values" />. Ordinal on both, like
///     the index's codes. An empty value set matches nothing: it is a one-of, not a "group present" test.
/// </summary>
public sealed record LabelPredicate(LabelNamespace Namespace, string Group, IReadOnlySet<string> Values);

/// <summary>
///     Which instances a query reads (tag-store.md §3.7). Every clause is an AND; a null clause does not
///     filter. The Matrix, Watched Situations and Search Filters all describe their question as one of these.
/// </summary>
/// <param name="Demos">Content hashes; null is every document the caller passed.</param>
/// <param name="Codes">Codes, ordinal; null is every code.</param>
/// <param name="Where">Label predicates, all of which must hold.</param>
/// <param name="Rounds">Rounds; null is every round. An instance with no round matches no set.</param>
/// <param name="Source">
///     One source only; null is every source but <see cref="TagSource.Import" /> (overview correction 2), so a
///     human tag and an accepted proposal both count and an imported timeline does not unless asked for.
/// </param>
/// <param name="CreatedAfterUtc">Only instances created strictly after this: Watched Situations' "N new" cursor.</param>
public sealed record TagSlice(
    IReadOnlySet<string>? Demos,
    IReadOnlySet<string>? Codes,
    IReadOnlyList<LabelPredicate> Where,
    IReadOnlySet<int>? Rounds,
    TagSource? Source,
    DateTime? CreatedAfterUtc)
{
    /// <summary>No clause set: every instance the default source rule admits.</summary>
    public static TagSlice Everything { get; } = new(null, null, [], null, null, null);
}

/// <summary>
///     What a <see cref="TagQuery.Pivot" /> groups by. An instance that has no key on an axis (no value for
///     a label group, no round) falls in no cell; <see cref="TagQuery.Find" /> with the same slice is the total.
/// </summary>
public abstract record PivotAxis
{
    private PivotAxis()
    {
    }

    /// <summary>One key per code.</summary>
    public sealed record Code : PivotAxis;

    /// <summary>
    ///     One key per distinct value of <paramref name="Group" />. A multi-valued instance (site A and site
    ///     B) counts once under each value, and once only under a value it repeats.
    /// </summary>
    public sealed record Label(LabelNamespace Namespace, string Group) : PivotAxis;

    /// <summary>One key per document, the content hash.</summary>
    public sealed record Demo : PivotAxis;

    /// <summary>One key per round number, invariant text, ordered numerically.</summary>
    public sealed record Round : PivotAxis;
}

/// <summary>
///     One instance, located: enough to seek the open demo or open another and seek, without holding the
///     document. Ticks are the document's frame-clock ticks.
/// </summary>
public sealed record TagInstanceRef(string Sha256, Guid Id, string Code, int FromTick, int ToTick, int? Round);

/// <summary>
///     A two-axis table of instance refs. Sparse: a cell is present only when it holds at least one ref, and
///     every row and column key has at least one present cell. Keys are ordinal except round keys, which are
///     numeric.
/// </summary>
public sealed record TagPivot(
    IReadOnlyList<string> RowKeys,
    IReadOnlyList<string> ColumnKeys,
    IReadOnlyDictionary<(string Row, string Column), IReadOnlyList<TagInstanceRef>> Cells);

/// <summary>
///     The read side of the tag store (tag-store.md §3.7): pure functions over documents the caller loaded,
///     normally through <see cref="TagStore.LoadDocuments" /> off the UI thread. Knows nothing of teams,
///     palettes or the UI; an opponent's demos arrive as a <see cref="TagSlice.Demos" /> set built elsewhere.
///     Results come in document order, then instance order, so a caller that renders them needs no sort to be
///     stable.
/// </summary>
public static class TagQuery
{
    /// <summary>Every instance in the slice.</summary>
    /// <param name="docs">The documents to read.</param>
    /// <param name="slice">The filter.</param>
    public static IReadOnlyList<TagInstanceRef> Find(IEnumerable<TagDocument> docs, TagSlice slice)
    {
        ArgumentNullException.ThrowIfNull(docs);
        ArgumentNullException.ThrowIfNull(slice);
        List<TagInstanceRef> found = [];
        foreach ((TagDocument document, TagInstance instance) in Matching(docs, slice))
        {
            found.Add(RefOf(document, instance));
        }

        return found;
    }

    /// <summary>
    ///     The slice as a table. Swapping the axes is calling it again with them exchanged; every cell already
    ///     holds the refs that open its clips.
    /// </summary>
    /// <param name="docs">The documents to read.</param>
    /// <param name="slice">The filter, applied before grouping.</param>
    /// <param name="rows">The row axis.</param>
    /// <param name="columns">The column axis.</param>
    public static TagPivot Pivot(IEnumerable<TagDocument> docs, TagSlice slice, PivotAxis rows, PivotAxis columns)
    {
        ArgumentNullException.ThrowIfNull(docs);
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(columns);

        Dictionary<(string Row, string Column), List<TagInstanceRef>> cells = [];
        HashSet<string> rowKeys = new(StringComparer.Ordinal);
        HashSet<string> columnKeys = new(StringComparer.Ordinal);
        foreach ((TagDocument document, TagInstance instance) in Matching(docs, slice))
        {
            IReadOnlyList<string> rowValues = KeysOf(rows, document, instance);
            if (rowValues.Count == 0)
            {
                continue;
            }

            IReadOnlyList<string> columnValues = KeysOf(columns, document, instance);
            if (columnValues.Count == 0)
            {
                continue;
            }

            TagInstanceRef found = RefOf(document, instance);
            foreach (string row in rowValues)
            {
                foreach (string column in columnValues)
                {
                    if (!cells.TryGetValue((row, column), out List<TagInstanceRef>? cell))
                    {
                        cells[(row, column)] = cell = [];
                    }

                    cell.Add(found);
                    rowKeys.Add(row);
                    columnKeys.Add(column);
                }
            }
        }

        return new TagPivot(
            Ordered(rowKeys, rows),
            Ordered(columnKeys, columns),
            cells.ToDictionary(c => c.Key, c => (IReadOnlyList<TagInstanceRef>)c.Value));
    }

    /// <summary>
    ///     The instances whose span contains <paramref name="tick" />, bounds inclusive: what Label Mode offers
    ///     at the playhead. No slice and no source rule: on the open demo every instance is selectable.
    /// </summary>
    /// <param name="doc">The open demo's document.</param>
    /// <param name="tick">A frame-clock tick.</param>
    public static IReadOnlyList<TagInstanceRef> At(TagDocument doc, int tick)
    {
        ArgumentNullException.ThrowIfNull(doc);
        List<TagInstanceRef> found = [];
        foreach (TagInstance instance in doc.Instances)
        {
            if (instance.FromTick <= tick && tick <= instance.ToTick)
            {
                found.Add(RefOf(doc, instance));
            }
        }

        return found;
    }

    private static IEnumerable<(TagDocument Document, TagInstance Instance)> Matching(IEnumerable<TagDocument> docs, TagSlice slice)
    {
        foreach (TagDocument document in docs)
        {
            if (slice.Demos is not null && !slice.Demos.Contains(document.Demo.Sha256))
            {
                continue;
            }

            foreach (TagInstance instance in document.Instances)
            {
                if (Admits(slice, instance))
                {
                    yield return (document, instance);
                }
            }
        }
    }

    private static bool Admits(TagSlice slice, TagInstance instance)
    {
        if (!SourceAdmits(slice.Source, instance.Source))
        {
            return false;
        }

        if (slice.Codes is not null && !slice.Codes.Contains(instance.Code))
        {
            return false;
        }

        if (slice.Rounds is not null && (instance.Round is not { } round || !slice.Rounds.Contains(round)))
        {
            return false;
        }

        if (slice.CreatedAfterUtc is { } cursor && instance.CreatedUtc <= cursor)
        {
            return false;
        }

        foreach (LabelPredicate predicate in slice.Where)
        {
            if (!LabelsIn(instance, predicate.Namespace).Any(l =>
                    string.Equals(l.Group, predicate.Group, StringComparison.Ordinal) && predicate.Values.Contains(l.Value)))
            {
                return false;
            }
        }

        return true;
    }

    // The stored source is a string so an unknown value survives a round trip. The default admits such a
    // value (it is not "import"); naming a source admits only its exact spelling.
    private static bool SourceAdmits(TagSource? wanted, string source) => wanted switch
    {
        null => !string.Equals(source, TagSources.Import, StringComparison.Ordinal),
        TagSource.Human => string.Equals(source, TagSources.Human, StringComparison.Ordinal),
        TagSource.Suggested => string.Equals(source, TagSources.Suggested, StringComparison.Ordinal),
        TagSource.Import => string.Equals(source, TagSources.Import, StringComparison.Ordinal),
        _ => false
    };

    private static IEnumerable<TagLabel> LabelsIn(TagInstance instance, LabelNamespace ns) => ns switch
    {
        LabelNamespace.Human => instance.Labels,
        LabelNamespace.Fact => instance.Facts,
        _ => instance.Labels.Concat(instance.Facts)
    };

    private static IReadOnlyList<string> KeysOf(PivotAxis axis, TagDocument document, TagInstance instance) => axis switch
    {
        PivotAxis.Code => [instance.Code],
        PivotAxis.Demo => [document.Demo.Sha256],
        PivotAxis.Round => instance.Round is { } round ? [round.ToString(CultureInfo.InvariantCulture)] : [],
        // Distinct so a value the instance repeats (or that both namespaces carry, under Any) counts once.
        PivotAxis.Label label =>
        [
            .. LabelsIn(instance, label.Namespace)
                .Where(l => string.Equals(l.Group, label.Group, StringComparison.Ordinal))
                .Select(l => l.Value)
                .Distinct(StringComparer.Ordinal)
        ],
        _ => []
    };

    private static TagInstanceRef RefOf(TagDocument document, TagInstance instance) =>
        new(document.Demo.Sha256, instance.Id, instance.Code, instance.FromTick, instance.ToTick, instance.Round);

    private static List<string> Ordered(HashSet<string> keys, PivotAxis axis) => axis is PivotAxis.Round
        ? [.. keys.OrderBy(k => int.Parse(k, CultureInfo.InvariantCulture))]
        : [.. keys.Order(StringComparer.Ordinal)];
}
