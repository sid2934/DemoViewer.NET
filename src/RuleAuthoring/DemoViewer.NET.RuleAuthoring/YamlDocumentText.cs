#region

using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

#endregion

namespace DemoViewer.NET.RuleAuthoring;

/// <summary>
///     A YAML document held as its original text alongside a parse of it, so a caller can ask where
///     a value is and then splice that span. This is the whole basis of the round trip: comments,
///     key order, indentation and quoting style survive because nothing outside the spliced span is
///     ever rewritten.
///     <para>
///         Anchors and tags survive an unedited save for the same reason, but they are not
///         EDITABLE: an anchored node is shared, so the representation model hands back one node for
///         the definition and for every alias of it. <see cref="YamlEdits" /> refuses them rather
///         than rewriting the wrong span.
///     </para>
/// </summary>
public sealed class YamlDocumentText
{
    private readonly YamlMappingNode? _root;

    private YamlDocumentText(string text, YamlMappingNode? root)
    {
        Text = text;
        _root = root;
    }

    /// <summary>The document text, exactly as loaded or as last produced by an edit.</summary>
    public string Text { get; }

    /// <summary>The root mapping, or <c>null</c> for an empty document or a non-mapping root.</summary>
    public YamlMappingNode? Root => _root;

    /// <summary>Parses <paramref name="text" />. Throws <c>YamlException</c> on malformed YAML.</summary>
    public static YamlDocumentText Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        YamlStream stream = new();
        stream.Load(new StringReader(text));
        YamlMappingNode? root = stream.Documents.Count > 0
            ? stream.Documents[0].RootNode as YamlMappingNode
            : null;
        return new YamlDocumentText(text, root);
    }

    /// <summary>Parses the result of applying <paramref name="edits" />, so the next batch addresses the new text.</summary>
    public YamlDocumentText With(IReadOnlyList<TextEdit> edits) => Parse(TextEditor.Apply(Text, edits));

    /// <summary>
    ///     The node at <paramref name="path" />, or <c>null</c> when any step of it is missing or
    ///     the wrong shape. Missing is a normal answer: an editor asks whether an optional key is
    ///     there before deciding to set or to insert it.
    /// </summary>
    public YamlNode? Find(YamlPath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        YamlNode? current = _root;
        foreach (YamlPathSegment segment in path.Segments)
        {
            if (current is null)
            {
                return null;
            }

            if (segment.Key is { } key)
            {
                if (current is not YamlMappingNode map)
                {
                    return null;
                }

                current = map.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? child)
                    ? child
                    : null;
            }
            else
            {
                if (current is not YamlSequenceNode sequence || segment.Index < 0
                    || segment.Index >= sequence.Children.Count)
                {
                    return null;
                }

                current = sequence.Children[segment.Index];
            }
        }

        return current;
    }

    /// <summary>The key node for <paramref name="path" />'s last segment, when its parent is a mapping.</summary>
    public YamlScalarNode? FindKey(YamlPath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Segments.Count == 0 || path.Segments[^1].Key is not { } key
            || path.Parent is not { } parent || Find(parent) is not YamlMappingNode map)
        {
            return null;
        }

        foreach (YamlNode candidate in map.Children.Keys)
        {
            if (candidate is YamlScalarNode scalar && scalar.Value == key)
            {
                return scalar;
            }
        }

        return null;
    }

    /// <summary>The character span a node occupies in <see cref="Text" />.</summary>
    public (int Start, int Length) SpanOf(YamlNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        int start = (int)node.Start.Index;
        return (start, Math.Max(0, EndOf(node) - start));
    }

    /// <summary>
    ///     The offset just past the last character of <paramref name="node" />.
    ///     <para>
    ///         Not <c>node.End.Index</c>. YamlDotNet's representation model reports a COLLAPSED end
    ///         mark for every collection: a block mapping, a block sequence and a flow sequence all
    ///         come back with <c>End == Start</c>, so the only nodes whose end mark can be believed
    ///         are scalars. The real end is therefore derived: the furthest end of any descendant,
    ///         and for a flow collection the closing bracket past that.
    ///     </para>
    /// </summary>
    public int EndOf(YamlNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        switch (node)
        {
            // A literal or folded scalar's end mark sits PAST its terminating line break, at the
            // start of the following line. Believed as-is it makes every insert after such a value
            // land inside the next sibling, and makes a removal take the line after it as well:
            // both produce a file that still parses, which is the worst kind of wrong.
            case YamlScalarNode { Style: ScalarStyle.Literal or ScalarStyle.Folded } block:
            {
                int end = (int)block.End.Index;
                while (end > (int)block.Start.Index && end - 1 < Text.Length
                       && Text[end - 1] is '\n' or '\r')
                {
                    end--;
                }

                return end;
            }

            case YamlScalarNode scalar:
                return (int)scalar.End.Index;

            case YamlSequenceNode sequence:
            {
                int end = (int)sequence.Start.Index;
                foreach (YamlNode child in sequence.Children)
                {
                    end = Math.Max(end, EndOf(child));
                }

                return sequence.Style == SequenceStyle.Flow ? CloseBracket(end, ']') : end;
            }

            case YamlMappingNode mapping:
            {
                int end = (int)mapping.Start.Index;
                foreach ((YamlNode key, YamlNode value) in mapping.Children)
                {
                    end = Math.Max(end, Math.Max((int)key.End.Index, EndOf(value)));
                }

                return mapping.Style == MappingStyle.Flow ? CloseBracket(end, '}') : end;
            }

            default:
                return (int)node.End.Index;
        }
    }

    // Walks forward past separators and comments to the collection's closing bracket. An unclosed
    // flow collection cannot parse, so reaching the end of the document means the caller handed us a
    // node from a different text.
    private int CloseBracket(int from, char close)
    {
        int i = Math.Clamp(from, 0, Text.Length);
        while (i < Text.Length)
        {
            char c = Text[i];
            if (c == close)
            {
                return i + 1;
            }

            if (c == '#')
            {
                i = StartOfNextLine(i);
                continue;
            }

            i++;
        }

        throw new InvalidOperationException(
            $"no closing '{close}' after offset {from}; the node is not from this document");
    }

    /// <summary>The 0-based offset of the start of the line <paramref name="offset" /> falls on.</summary>
    public int StartOfLine(int offset)
    {
        int i = Math.Clamp(offset, 0, Text.Length);

        // Handed the LF of a CRLF pair, walk onto the CR first. Otherwise the loop below stops
        // immediately and returns the line ENDING's index as if it were a line start, which reads
        // as an empty line and quietly disables anything that walks upward from here.
        if (i > 0 && i < Text.Length && Text[i] == '\n' && Text[i - 1] == '\r')
        {
            i--;
        }

        while (i > 0 && Text[i - 1] is not ('\n' or '\r'))
        {
            i--;
        }

        return i;
    }

    /// <summary>
    ///     The offset just past the line ending that terminates the line <paramref name="offset" />
    ///     falls on, or the document end. CRLF and LF are both one line ending.
    /// </summary>
    public int StartOfNextLine(int offset)
    {
        int i = Math.Clamp(offset, 0, Text.Length);
        while (i < Text.Length && Text[i] is not ('\n' or '\r'))
        {
            i++;
        }

        if (i < Text.Length && Text[i] == '\r')
        {
            i++;
        }

        if (i < Text.Length && Text[i] == '\n')
        {
            i++;
        }

        return i;
    }

    /// <summary>The indentation (leading spaces) of the line <paramref name="offset" /> falls on.</summary>
    public string IndentOf(int offset)
    {
        int start = StartOfLine(offset);
        int i = start;
        while (i < Text.Length && Text[i] == ' ')
        {
            i++;
        }

        return Text[start..i];
    }

    /// <summary>
    ///     The document's line ending, taken from the first one present. A document with none (a
    ///     single line, or empty) reports <c>\n</c>.
    /// </summary>
    public string NewLine
    {
        get
        {
            int i = Text.IndexOf('\n');
            if (i < 0)
            {
                return "\n";
            }

            return i > 0 && Text[i - 1] == '\r' ? "\r\n" : "\n";
        }
    }
}
