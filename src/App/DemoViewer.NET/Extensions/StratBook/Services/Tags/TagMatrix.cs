#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Services.Tags;

/// <summary>A pivot axis with the words the Matrix shows for it.</summary>
/// <param name="Display">"Code", "Label · route", "Fact · buy.t".</param>
/// <param name="Axis">The axis <see cref="TagQuery.Pivot" /> groups by.</param>
public sealed record TagMatrixAxis(string Display, PivotAxis Axis)
{
    public static TagMatrixAxis Code { get; } = new("Code", new PivotAxis.Code());
    public static TagMatrixAxis Round { get; } = new("Round", new PivotAxis.Round());
    public static TagMatrixAxis Demo { get; } = new("Demo", new PivotAxis.Demo());

    /// <summary>A label group's axis: a human group reads "Label · g", a parser group "Fact · g".</summary>
    /// <param name="ns">Which label array.</param>
    /// <param name="group">The group; empty is the bare labels.</param>
    public static TagMatrixAxis Label(LabelNamespace ns, string group) =>
        new($"{(ns == LabelNamespace.Fact ? "Fact" : "Label")} · {(group.Length == 0 ? "(bare)" : group)}",
            new PivotAxis.Label(ns, group));
}

/// <summary>
///     The dense face of a <see cref="TagPivot" />: every row against every column, an absent cell as a
///     zero, and totals that count an instance once however many values it has on an axis. The Matrix
///     draws this; <see cref="TagPivot" /> stays sparse because that is what the query layer promises.
/// </summary>
public sealed class TagMatrixTable
{
    private static readonly IReadOnlyList<TagInstanceRef> _none = [];

    /// <param name="pivot">The pivot to read.</param>
    public TagMatrixTable(TagPivot pivot)
    {
        ArgumentNullException.ThrowIfNull(pivot);
        Pivot = pivot;
        RowTotals = [.. pivot.RowKeys.Select(r => Distinct(pivot.ColumnKeys.SelectMany(c => Refs(r, c))))];
        ColumnTotals = [.. pivot.ColumnKeys.Select(c => Distinct(pivot.RowKeys.SelectMany(r => Refs(r, c))))];
        Total = Distinct(pivot.Cells.Values.SelectMany(v => v));
    }

    /// <summary>The pivot the table reads.</summary>
    public TagPivot Pivot { get; }

    public IReadOnlyList<string> RowKeys => Pivot.RowKeys;

    public IReadOnlyList<string> ColumnKeys => Pivot.ColumnKeys;

    /// <summary>Instances in each row, each once; not the sum of the row, which counts a multi-valued instance per value.</summary>
    public IReadOnlyList<int> RowTotals { get; }

    /// <summary>Instances in each column, each once.</summary>
    public IReadOnlyList<int> ColumnTotals { get; }

    /// <summary>Instances in any cell, each once.</summary>
    public int Total { get; }

    /// <summary>The refs of one cell; empty for a cell the sparse pivot left out.</summary>
    /// <param name="row">A row key.</param>
    /// <param name="column">A column key.</param>
    public IReadOnlyList<TagInstanceRef> Refs(string row, string column) =>
        Pivot.Cells.TryGetValue((row, column), out IReadOnlyList<TagInstanceRef>? refs) ? refs : _none;

    /// <summary>The count a cell shows.</summary>
    /// <param name="row">A row key.</param>
    /// <param name="column">A column key.</param>
    public int Count(string row, string column) => Refs(row, column).Count;

    private static int Distinct(IEnumerable<TagInstanceRef> refs) => refs.Select(r => (r.Sha256, r.Id)).Distinct().Count();
}

/// <summary>
///     The Matrix's helpers around <see cref="TagQuery" /> (tag-store.md §3.7): which axes a set of
///     documents offers, which values a label group takes, and the narrowing a caller does before the
///     query when its filter is not a label (the opponent's side in a round, which needs Team Identity and
///     so cannot live in <see cref="TagSlice" />). Pure, like the query layer.
/// </summary>
public static class TagMatrix
{
    /// <summary>
    ///     Every axis the documents can be pivoted on: code, round and demo, then each human label group,
    ///     then each fact group, groups ordinal. The source rule is not applied, so an axis does not vanish
    ///     from the picker when a slice happens to empty it.
    /// </summary>
    /// <param name="docs">The documents.</param>
    public static IReadOnlyList<TagMatrixAxis> AxesOf(IEnumerable<TagDocument> docs)
    {
        ArgumentNullException.ThrowIfNull(docs);
        SortedSet<string> human = new(StringComparer.Ordinal);
        SortedSet<string> facts = new(StringComparer.Ordinal);
        foreach (TagInstance instance in docs.SelectMany(d => d.Instances))
        {
            foreach (TagLabel label in instance.Labels)
            {
                human.Add(label.Group);
            }

            foreach (TagLabel label in instance.Facts)
            {
                facts.Add(label.Group);
            }
        }

        return
        [
            TagMatrixAxis.Code, TagMatrixAxis.Round, TagMatrixAxis.Demo,
            .. human.Select(g => TagMatrixAxis.Label(LabelNamespace.Human, g)),
            .. facts.Select(g => TagMatrixAxis.Label(LabelNamespace.Fact, g))
        ];
    }

    /// <summary>The distinct values one label group takes, ordinal.</summary>
    /// <param name="docs">The documents.</param>
    /// <param name="ns">Which label array; <see cref="LabelNamespace.Any" /> reads both.</param>
    /// <param name="group">The group.</param>
    public static IReadOnlyList<string> ValuesOf(IEnumerable<TagDocument> docs, LabelNamespace ns, string group)
    {
        ArgumentNullException.ThrowIfNull(docs);
        ArgumentNullException.ThrowIfNull(group);
        return
        [
            .. docs.SelectMany(d => d.Instances)
                .SelectMany(i => ns switch
                {
                    LabelNamespace.Human => i.Labels,
                    LabelNamespace.Fact => i.Facts,
                    _ => i.Labels.Concat(i.Facts)
                })
                .Where(l => string.Equals(l.Group, group, StringComparison.Ordinal))
                .Select(l => l.Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     The documents with only the instances <paramref name="keep" /> admits. Shallow: the headers and
    ///     the kept instances are the same objects, so this is for documents loaded for a read, never for a
    ///     document a session holds.
    /// </summary>
    /// <param name="docs">The documents.</param>
    /// <param name="keep">Whether an instance of a document stays.</param>
    public static List<TagDocument> Narrow(IEnumerable<TagDocument> docs, Func<TagDocument, TagInstance, bool> keep)
    {
        ArgumentNullException.ThrowIfNull(docs);
        ArgumentNullException.ThrowIfNull(keep);
        return
        [
            .. docs.Select(d => new TagDocument
            {
                SchemaVersion = d.SchemaVersion,
                Demo = d.Demo,
                Clock = d.Clock,
                Palette = d.Palette,
                Instances = [.. d.Instances.Where(i => keep(d, i))],
                Extra = d.Extra
            })
        ];
    }

    /// <summary>
    ///     A round restriction as typed: "1-12", "13-24", "1, 5, 7-9", or blank for every round. Null when
    ///     the text is blank; an empty set never, because a range that parses to nothing is an error the
    ///     caller shows rather than a filter that silently matches nothing.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="rounds">The rounds, or null for every round.</param>
    /// <returns>False when the text is not a list of positive rounds and ranges.</returns>
    public static bool TryParseRounds(string? text, out IReadOnlySet<int>? rounds)
    {
        rounds = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        HashSet<int> set = [];
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // An en dash is what a person copying a range out of a note will often paste.
            string[] ends = part.Split(['-', '\u2013'], StringSplitOptions.TrimEntries);
            if (ends.Length is < 1 or > 2
                || !int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out int from)
                || from < 1)
            {
                return false;
            }

            int to = from;
            // A match has tens of rounds; a four-digit bound is a typo, not a range to enumerate.
            if (ends.Length == 2 && (!int.TryParse(ends[1], NumberStyles.None, CultureInfo.InvariantCulture, out to) || to < from || to > 999))
            {
                return false;
            }

            for (int r = from; r <= to; r++)
            {
                set.Add(r);
            }
        }

        if (set.Count == 0)
        {
            return false;
        }

        rounds = set;
        return true;
    }
}
