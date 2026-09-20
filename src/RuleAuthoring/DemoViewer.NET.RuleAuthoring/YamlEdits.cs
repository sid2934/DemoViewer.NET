#region

using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

#endregion

namespace DemoViewer.NET.RuleAuthoring;

/// <summary>
///     Turns "change this value", "add this key", "delete this entry" into the smallest
///     <see cref="TextEdit" /> that does it. Every operation is expressed against a
///     <see cref="YamlDocumentText" /> and touches only the span it names, which is what keeps
///     comments, key order, indentation and quoting intact (design.md §9 decision 4).
/// </summary>
public static class YamlEdits
{
    /// <summary>
    ///     Replaces the scalar at <paramref name="path" /> with <paramref name="value" />, quoted as
    ///     needed. A trailing comment on the same line survives, because a scalar's span stops at
    ///     the end of the scalar.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing there, or it is not a scalar.</exception>
    public static TextEdit SetScalar(YamlDocumentText doc, YamlPath path, string value)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(path);

        if (doc.Find(path) is not YamlScalarNode scalar)
        {
            throw new InvalidOperationException($"{path} is not a scalar in this document");
        }

        (int start, int length) = doc.SpanOf(scalar);
        return new TextEdit(start, length, Quote(value, scalar.Style), $"set {path}");
    }

    /// <summary>
    ///     Sets <paramref name="key" /> under the mapping at <paramref name="mapping" />, replacing
    ///     the value when the key is there and inserting a new entry after the last one when it is
    ///     not. An inserted entry copies the indentation of its siblings.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="mapping" /> is not a block mapping.</exception>
    public static TextEdit SetKey(YamlDocumentText doc, YamlPath mapping, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(mapping);

        if (doc.Find(mapping.Key(key)) is YamlScalarNode)
        {
            return SetScalar(doc, mapping.Key(key), value);
        }

        if (doc.Find(mapping) is not YamlMappingNode map || map.Style == MappingStyle.Flow)
        {
            throw new InvalidOperationException($"{mapping} is not a block mapping");
        }

        // After the last entry, so an insert never reorders what is already there. The line the last
        // entry ends on may carry a trailing comment, so the insertion point is the START of the
        // next line rather than the end of the value.
        int indentAt = map.Children.Count > 0
            ? (int)map.Children.Keys.Last().Start.Index
            : (int)map.Start.Index;
        string indent = doc.IndentOf(indentAt);
        int at = doc.StartOfNextLine(map.Children.Count > 0 ? doc.EndOf(map) : (int)map.Start.Index);

        string text = $"{indent}{key}: {Quote(value, ScalarStyle.Any)}{doc.NewLine}";
        return new TextEdit(at, 0, text, $"add {mapping.Key(key)}");
    }

    /// <summary>
    ///     Removes the whole <paramref name="key" /> entry from the block mapping at
    ///     <paramref name="mapping" />, including its value however deeply nested, its trailing
    ///     comment, and the run of comment lines directly above it at the same indentation.
    ///     <para>
    ///         Those comment lines go because in every shipped ruleset they describe the entry below
    ///         them. A blank line stops the run, which is how an author separates a section comment
    ///         from an entry comment.
    ///     </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The key is not there, or the parent is not a block mapping.</exception>
    public static TextEdit RemoveKey(YamlDocumentText doc, YamlPath mapping, string key)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(mapping);

        if (doc.Find(mapping) is not YamlMappingNode map || map.Style == MappingStyle.Flow)
        {
            throw new InvalidOperationException($"{mapping} is not a block mapping");
        }

        if (doc.FindKey(mapping.Key(key)) is not { } keyNode
            || doc.Find(mapping.Key(key)) is not { } valueNode)
        {
            throw new InvalidOperationException($"{mapping.Key(key)} is not in this document");
        }

        int from = doc.StartOfLine((int)keyNode.Start.Index);
        int to = doc.StartOfNextLine(doc.EndOf(valueNode));
        from = AbsorbCommentsAbove(doc, from, doc.IndentOf(from).Length);

        return new TextEdit(from, to - from, "", $"remove {mapping.Key(key)}");
    }

    /// <summary>
    ///     Inserts <paramref name="block" /> as a new entry at the end of the block mapping at
    ///     <paramref name="mapping" />. The block is re-indented to the mapping's own level, so a
    ///     caller composes it at zero indentation and does not have to know where it will land.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="mapping" /> is not a block mapping.</exception>
    public static TextEdit AddEntry(YamlDocumentText doc, YamlPath mapping, string block)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(block);

        if (doc.Find(mapping) is not YamlMappingNode map || map.Style == MappingStyle.Flow)
        {
            throw new InvalidOperationException($"{mapping} is not a block mapping");
        }

        int indentAt = map.Children.Count > 0
            ? (int)map.Children.Keys.Last().Start.Index
            : (int)map.Start.Index;
        string indent = doc.IndentOf(indentAt);
        int at = doc.StartOfNextLine(map.Children.Count > 0 ? doc.EndOf(map) : (int)map.Start.Index);

        return new TextEdit(at, 0, Reindent(block, indent, doc.NewLine), $"add entry under {mapping}");
    }

    /// <summary>Appends <paramref name="item" /> to the sequence at <paramref name="sequence" />.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="sequence" /> is not a sequence.</exception>
    public static TextEdit AddSequenceItem(YamlDocumentText doc, YamlPath sequence, string item)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(sequence);

        if (doc.Find(sequence) is not YamlSequenceNode seq)
        {
            throw new InvalidOperationException($"{sequence} is not a sequence");
        }

        string quoted = Quote(item, ScalarStyle.Any);

        if (seq.Style == SequenceStyle.Flow)
        {
            // Inside the brackets, after the last item: `[ a, b ]` -> `[ a, b, c ]`. An empty flow
            // sequence has no last item, so the insertion goes just inside the opening bracket.
            int inner = seq.Children.Count > 0
                ? doc.EndOf(seq.Children[^1])
                : (int)seq.Start.Index + 1;
            string text = seq.Children.Count > 0 ? $", {quoted}" : $" {quoted}";
            return new TextEdit(inner, 0, text, $"append to {sequence}");
        }

        int indentAt = seq.Children.Count > 0 ? (int)seq.Children[0].Start.Index : (int)seq.Start.Index;
        string indent = doc.IndentOf(indentAt);
        int at = doc.StartOfNextLine(doc.EndOf(seq));
        return new TextEdit(at, 0, $"{indent}- {quoted}{doc.NewLine}", $"append to {sequence}");
    }

    /// <summary>
    ///     Quotes <paramref name="value" /> the way this document would.
    ///     <para>
    ///         An EXISTING scalar keeps the style its author chose, so replacing the text of a
    ///         double-quoted title does not turn it plain and rewriting a single-quoted expression
    ///         does not reflow it. That is what lets "set every scalar to the value it already has"
    ///         come back byte-identical, which is the sharpest test of the round trip there is.
    ///     </para>
    ///     <para>
    ///         A NEW value (<see cref="ScalarStyle.Any" />) takes the plainest style its content
    ///         allows, and is quoted when plain would change its meaning: a label of <c>true</c> or
    ///         <c>12</c> is a string the author typed, and YAML would read an unquoted one back as a
    ///         boolean or a number.
    ///     </para>
    /// </summary>
    public static string Quote(string value, ScalarStyle style)
    {
        ArgumentNullException.ThrowIfNull(value);

        return style switch
        {
            ScalarStyle.SingleQuoted when !value.Contains('\'', StringComparison.Ordinal) =>
                $"'{value}'",
            ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted => DoubleQuote(value),
            // An existing PLAIN scalar stays plain when it can. `match: { bullet: true }` is a
            // boolean the author wrote plain, and quoting it on the way back out would both change
            // the file and change what it means.
            ScalarStyle.Plain => CanBePlain(value) ? value : DoubleQuote(value),
            _ => CanBePlain(value) && !LooksNonString(value) ? value : DoubleQuote(value)
        };
    }

    // YAML's real rule for a plain scalar, not a conservative approximation of it: a colon is only a
    // separator when a space follows it or it ends the token, and a hash only starts a comment when
    // a space precedes it. Being stricter than this would re-quote expressions like
    // `event.Weapon == "awp"` that the corpus writes plain, and byte-equality would fail.
    private static bool CanBePlain(string value)
    {
        if (value.Length == 0 || value != value.Trim()
            || value.AsSpan().ContainsAny('\n', '\r', '\t'))
        {
            return false;
        }

        if (value[0] is '-' or '?' or ':' or ',' or '[' or ']' or '{' or '}' or '#' or '&' or '*'
            or '!' or '|' or '>' or '\'' or '"' or '%' or '@' or '`')
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == ':' && (i == value.Length - 1 || value[i + 1] == ' '))
            {
                return false;
            }

            if (value[i] == '#' && i > 0 && value[i - 1] == ' ')
            {
                return false;
            }
        }

        return true;
    }

    // Whether an unquoted form would be read back as something other than a string.
    private static bool LooksNonString(string value) =>
        value is "true" or "false" or "True" or "False" or "TRUE" or "FALSE"
            or "null" or "Null" or "NULL" or "~" or "yes" or "no" or "on" or "off"
        || double.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out _);

    private static string DoubleQuote(string value) =>
        "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal) + "\"";

    // Walks up from `from` over comment lines at the same indentation, stopping at a blank line or
    // at anything that is not a comment.
    private static int AbsorbCommentsAbove(YamlDocumentText doc, int from, int indent)
    {
        int at = from;
        while (at > 0)
        {
            int previous = doc.StartOfLine(at - 1);
            string line = doc.Text[previous..at];
            string trimmed = line.TrimEnd('\r', '\n');
            string content = trimmed.TrimStart(' ');
            if (content.Length == 0 || content[0] != '#' || trimmed.Length - content.Length != indent)
            {
                break;
            }

            at = previous;
        }

        return at;
    }

    // Re-indents a zero-indented block to `indent`, normalising line endings to the document's and
    // guaranteeing exactly one trailing newline. Blank lines stay blank rather than becoming
    // trailing whitespace.
    private static string Reindent(string block, string indent, string newLine)
    {
        string[] lines = block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        System.Text.StringBuilder sb = new();
        foreach (string line in lines)
        {
            if (line.Length == 0 && ReferenceEquals(line, lines[^1]))
            {
                continue;
            }

            sb.Append(line.Length == 0 ? "" : indent + line).Append(newLine);
        }

        return sb.ToString();
    }
}
