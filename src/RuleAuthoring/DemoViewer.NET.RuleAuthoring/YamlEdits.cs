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
    /// <exception cref="NotSupportedException">The scalar carries a construct a splice cannot rewrite.</exception>
    public static TextEdit SetScalar(YamlDocumentText doc, YamlPath path, string value)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(path);

        if (doc.Find(path) is not YamlScalarNode scalar)
        {
            throw new InvalidOperationException($"{path} is not a scalar in this document");
        }

        Refuse(scalar, path);

        // A literal or folded scalar would have to be re-emitted with its own indentation and
        // chomping to stay one. Rewriting it as a quoted string preserves the VALUE and destroys
        // the shape the author chose, so it is refused rather than silently reformatted.
        if (scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded)
        {
            throw new NotSupportedException(
                $"{path} is a block scalar; editing one through a splice is not supported");
        }

        (int start, int length) = doc.SpanOf(scalar);
        return new TextEdit(start, length,
            Quote(value, scalar.Style, InFlow(doc, path), LooksNonString(scalar.Value ?? "")),
            $"set {path}");
    }

    /// <summary>
    ///     Refuses a node this writer cannot splice without changing what the document means.
    ///     <para>
    ///         An anchored node is shared: the representation model hands back ONE node for the
    ///         anchor and for every alias of it, so its span points at the definition wherever the
    ///         alias was used. An edit through an alias would rewrite the definition, and an append
    ///         after one would insert at the wrong place entirely. An explicit tag is dropped by a
    ///         rewrite, which turns <c>!!str 5</c> into the number 5.
    ///     </para>
    ///     <para>
    ///         None of these appear in the shipped corpus. They are refused rather than handled
    ///         because a file that still parses and no longer means what it did is worse than an
    ///         operation the editor says it cannot do.
    ///     </para>
    /// </summary>
    private static void Refuse(YamlNode node, YamlPath path)
    {
        if (!node.Anchor.IsEmpty)
        {
            throw new NotSupportedException(
                $"{path} carries the anchor '{node.Anchor}'; anchors and aliases are not supported");
        }

        if (!node.Tag.IsEmpty)
        {
            throw new NotSupportedException(
                $"{path} carries the explicit tag '{node.Tag}'; tags are not supported");
        }
    }

    // Whether the value at `path` sits inside a flow collection, which changes what plain can hold.
    private static bool InFlow(YamlDocumentText doc, YamlPath path) =>
        path.Parent is { } parent && doc.Find(parent) is YamlMappingNode { Style: MappingStyle.Flow }
            or YamlSequenceNode { Style: SequenceStyle.Flow };

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

        YamlNode? existing = doc.Find(mapping.Key(key));
        if (existing is YamlScalarNode)
        {
            return SetScalar(doc, mapping.Key(key), value);
        }

        // The key is there but holds a collection. Appending a second entry with the same key would
        // write a document YAML rejects as a duplicate key, from a layer that cannot explain why.
        if (existing is not null)
        {
            throw new InvalidOperationException(
                $"{mapping.Key(key)} already holds a {existing.GetType().Name}; "
                + "replacing a collection with a scalar is not supported");
        }

        (int at, string lead, string indent) = AppendPointUnder(doc, mapping);

        string text = $"{lead}{indent}{key}: {Quote(value, ScalarStyle.Any)}{doc.NewLine}";
        return new TextEdit(at, 0, text, $"add {mapping.Key(key)}");
    }

    /// <summary>
    ///     Removes the whole <paramref name="key" /> entry from the block mapping at
    ///     <paramref name="mapping" />: its key line, its value however deeply nested, and every
    ///     comment INSIDE it.
    ///     <para>
    ///         A comment ABOVE the entry stays. It is tempting to take it, since an entry comment
    ///         usually sits right on top of the entry, but the corpus shows why not:
    ///         <c>rules/kast.rules.yaml</c> has
    ///         <c># ── Per-round counters ──</c> directly above <c>kills:</c>, and that line
    ///         describes the whole section. Absorbing it would delete an author's writing to tidy
    ///         up after a deletion, which is the loss §9 decision 2 reversed itself over. An
    ///         orphaned comment is visible and one keystroke to remove; a deleted one is gone.
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

        // An aliased or merged value resolves to the node it points AT, which sits earlier in the
        // file, so the span below would run backwards and the splice would be rejected by the
        // editor with a message about document length that explains nothing.
        Refuse(valueNode, mapping.Key(key));
        if (key == "<<")
        {
            throw new NotSupportedException($"{mapping} uses a merge key; merge keys are not supported");
        }

        int from = doc.StartOfLine((int)keyNode.Start.Index);
        int to = doc.StartOfNextLine(doc.EndOf(valueNode));

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

        (int at, string lead, string indent) = AppendPointUnder(doc, mapping);

        return new TextEdit(at, 0, lead + Reindent(block, indent, doc.NewLine),
            $"add entry under {mapping}");
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

        // An aliased item resolves to the node it points at, which is earlier in the file, so the
        // append would land in the middle of the sequence rather than at its end.
        for (int i = 0; i < seq.Children.Count; i++)
        {
            Refuse(seq.Children[i], sequence.Index(i));
        }

        string quoted = Quote(item, ScalarStyle.Any, seq.Style == SequenceStyle.Flow);

        if (seq.Style == SequenceStyle.Flow)
        {
            // Inside the brackets, after the last item: `[ a, b ]` -> `[ a, b, c ]`. An empty flow
            // sequence has no last item, so the insertion goes just inside the opening bracket.
            int inner = seq.Children.Count > 0
                ? doc.EndOf(seq.Children[^1])
                : (int)seq.Start.Index + 1;
            string text = seq.Children.Count > 0 ? $", {quoted}" : quoted;
            return new TextEdit(inner, 0, text, $"append to {sequence}");
        }

        int indentAt = seq.Children.Count > 0 ? (int)seq.Children[0].Start.Index : (int)seq.Start.Index;
        string indent = doc.IndentOf(indentAt);
        (int at, string lead) = AppendPoint(doc, seq);
        return new TextEdit(at, 0, $"{lead}{indent}- {quoted}{doc.NewLine}", $"append to {sequence}");
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
    /// <param name="value">The text to emit.</param>
    /// <param name="style">The style of the scalar being replaced, or <c>Any</c> for a new one.</param>
    /// <param name="inFlow">
    ///     Whether the scalar sits inside a flow collection. It changes what plain can hold: a comma
    ///     or a bracket is ordinary text in a block context and structure in a flow one, so a weapon
    ///     name of <c>awp, ssg08</c> written plain into <c>match: { … }</c> would silently become two
    ///     more entries.
    /// </param>
    /// <param name="wasNonString">
    ///     Whether the value being REPLACED already read as a boolean, a number or null. It is the
    ///     evidence for what the slot holds: <c>match: { bullet: true }</c> set to <c>false</c> stays
    ///     a plain boolean, while a label of <c>Kills</c> set to <c>true</c> is quoted, because the
    ///     author was writing a string there a moment ago.
    /// </param>
    public static string Quote(string value, ScalarStyle style, bool inFlow = false,
        bool wasNonString = false)
    {
        ArgumentNullException.ThrowIfNull(value);

        bool multiline = value.AsSpan().ContainsAny('\n', '\r');

        return style switch
        {
            // A single-quoted scalar cannot hold a line break without folding it to a space, so a
            // multi-line value has to change style rather than silently lose its newlines.
            ScalarStyle.SingleQuoted when !multiline
                                         && !value.Contains('\'', StringComparison.Ordinal) =>
                $"'{value}'",
            ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted => DoubleQuote(value),
            ScalarStyle.Plain when CanBePlain(value, inFlow)
                                   && (wasNonString || !LooksNonString(value)) => value,
            ScalarStyle.Plain => DoubleQuote(value),
            _ => CanBePlain(value, inFlow) && !LooksNonString(value) ? value : DoubleQuote(value)
        };
    }

    // YAML's real rule for a plain scalar, not a conservative approximation of it: a colon is only a
    // separator when a space follows it or it ends the token, a hash only starts a comment when a
    // space precedes it, and a dash only opens an entry when a space follows it. Being stricter than
    // this would re-quote the `summary:` lines and `event.Weapon == "awp"` expressions the corpus
    // writes plain, and byte-equality would fail.
    private static bool CanBePlain(string value, bool inFlow)
    {
        if (value.Length == 0 || value != value.Trim()
            || value.AsSpan().ContainsAny('\n', '\r', '\t'))
        {
            return false;
        }

        if (value[0] is '?' or ':' or ',' or '[' or ']' or '{' or '}' or '#' or '&' or '*'
                or '!' or '|' or '>' or '\'' or '"' or '%' or '@' or '`'
            || (value[0] == '-' && value.Length > 1 && value[1] == ' '))
        {
            return false;
        }

        // Inside a flow collection these are structure wherever they appear, not only at the front.
        if (inFlow && value.AsSpan().ContainsAny(",[]{}"))
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == ':' && (i == value.Length - 1 || value[i + 1] == ' '
                                    || (inFlow && value[i + 1] is ',' or ']' or '}')))
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

    // Whether an unquoted form would be read back as something other than a string. YAML 1.1's
    // spellings are all here, not just 1.2's, because that is what the editors and language servers
    // a ruleset author has open will apply to it.
    private static bool LooksNonString(string value) =>
        value is "true" or "false" or "True" or "False" or "TRUE" or "FALSE"
            or "null" or "Null" or "NULL" or "~" or ""
            or "yes" or "Yes" or "YES" or "no" or "No" or "NO"
            or "on" or "On" or "ON" or "off" or "Off" or "OFF"
            or "y" or "Y" or "n" or "N"
            or ".inf" or "-.inf" or "+.inf" or ".Inf" or ".INF" or ".nan" or ".NaN" or ".NAN"
        || value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("0o", StringComparison.OrdinalIgnoreCase)
        || double.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out _)
        || DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);

    /// <summary>
    ///     Where a new entry goes under <paramref name="mapping" />, what has to precede it, and at
    ///     what indentation.
    ///     <para>
    ///         It appends AFTER the last entry, so an insert never reorders what is already there,
    ///         and it measures from the start of the following line rather than the end of the last
    ///         value, so a trailing comment is not written through.
    ///     </para>
    ///     <para>
    ///         The third case is a section that has been emptied: removing the only stat leaves
    ///         <c>stats:</c> holding nothing, which parses as a null scalar rather than a mapping.
    ///         Without this the editor could delete a ruleset's last stat and then not be able to
    ///         add one, with no way back through the API.
    ///     </para>
    /// </summary>
    private static (int At, string Lead, string Indent) AppendPointUnder(
        YamlDocumentText doc, YamlPath mapping)
    {
        YamlNode? node = doc.Find(mapping);

        if (node is YamlMappingNode { Style: not MappingStyle.Flow } map)
        {
            int indentAt = map.Children.Count > 0
                ? (int)map.Children.Keys.Last().Start.Index
                : (int)map.Start.Index;
            (int at, string lead) = AppendPoint(doc, map);
            return (at, lead, doc.IndentOf(indentAt));
        }

        if (node is YamlScalarNode { Value: null or "" } && doc.FindKey(mapping) is { } emptyKey)
        {
            int keyStart = (int)emptyKey.Start.Index;
            int at = doc.StartOfNextLine(keyStart);
            bool startsALine = at == 0 || doc.Text[at - 1] is '\n' or '\r';
            return (at, startsALine ? "" : doc.NewLine, doc.IndentOf(keyStart) + "  ");
        }

        throw new InvalidOperationException($"{mapping} is not a block mapping");
    }

    // Where a new entry goes, and what has to precede it.
    //
    // A document that does not end in a newline is the case that bites: the offset after the last
    // line IS the end of the document, so an insert there would be appended to the last line rather
    // than put under it. `count: kill` with no newline would silently become
    // `count: kill  label: X`, which parses as one scalar and is a corrupted file, not an error. So
    // the lead carries the missing newline.
    private static (int At, string Lead) AppendPoint(YamlDocumentText doc, YamlNode collection)
    {
        int at = doc.StartOfNextLine(doc.EndOf(collection));
        bool startsALine = at == 0 || doc.Text[at - 1] is '\n' or '\r';
        return (at, startsALine ? "" : doc.NewLine);
    }

    // Every character YAML treats as a line break or a control has to be escaped, not just the three
    // familiar ones: a raw U+0085, U+2028 or U+2029 inside double quotes is a LINE BREAK to a
    // conforming parser, so leaving one in writes a file that no longer parses.
    private static string DoubleQuote(string value)
    {
        System.Text.StringBuilder sb = new(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\u0085': sb.Append("\\N"); break;
                case '\u2028': sb.Append("\\L"); break;
                case '\u2029': sb.Append("\\P"); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                            $"\\x{(int)c:x2}");
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    // Re-indents a zero-indented block to `indent`, normalising line endings to the document's and
    // guaranteeing exactly one trailing newline. Blank lines stay blank rather than becoming
    // trailing whitespace.
    private static string Reindent(string block, string indent, string newLine)
    {
        string[] lines = block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        System.Text.StringBuilder sb = new();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            // Only the empty segment a trailing newline leaves behind is dropped. Comparing by
            // REFERENCE here matched every blank line in the block, because string.Split hands back
            // the same string.Empty instance for all of them.
            if (line.Length == 0 && i == lines.Length - 1)
            {
                continue;
            }

            sb.Append(line.Length == 0 ? "" : indent + line).Append(newLine);
        }

        return sb.ToString();
    }
}
