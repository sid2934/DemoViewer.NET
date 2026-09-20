#region

using YamlDotNet.RepresentationModel;

#endregion

namespace DemoViewer.NET.RuleAuthoring;

/// <summary>
///     The editable model the node editor writes through: a ruleset held as its own text, with
///     ruleset-shaped operations on top of <see cref="YamlEdits" />.
///     <para>
///         <b>It never regenerates the file.</b> §9 decision 4 chose to preserve comments and key
///         order, and the only way to preserve them in full is not to rewrite what was not edited.
///         <c>rules/kast.rules.yaml</c> opens with sixteen lines explaining how each v1 chain maps
///         onto a v2 stat, and has a trailing comment on most values; a serializer round trip
///         through <c>RulesetDoc</c> would drop every one of them the first time the file was
///         opened in an editor and saved.
///     </para>
///     <para>
///         An instance is immutable. Every operation returns a NEW document, which is what gives
///         undo and redo a value to hold rather than a command to invert.
///     </para>
/// </summary>
public sealed class RulesetDocumentEditor
{
    private static readonly YamlPath _stats = YamlPath.Root.Key("stats");
    private static readonly YamlPath _highlights = YamlPath.Root.Key("highlights");

    private RulesetDocumentEditor(YamlDocumentText document) => Document = document;

    /// <summary>The underlying text and parse.</summary>
    public YamlDocumentText Document { get; }

    /// <summary>The ruleset text as it stands. What a save writes.</summary>
    public string Text => Document.Text;

    /// <summary>The <c>ruleset:</c> id, or <c>null</c> when the document has none.</summary>
    public string? Id => Scalar(YamlPath.Root.Key("ruleset"));

    /// <summary>The stat ids, in source order.</summary>
    public IReadOnlyList<string> StatIds => KeysUnder(_stats);

    /// <summary>The highlight ids, in source order.</summary>
    public IReadOnlyList<string> HighlightIds => KeysUnder(_highlights);

    /// <summary>Opens <paramref name="text" /> for editing. Throws <c>YamlException</c> if it does not parse.</summary>
    public static RulesetDocumentEditor Open(string text) => new(YamlDocumentText.Parse(text));

    /// <summary>The path of one stat's block, for a caller that needs to address inside it.</summary>
    public static YamlPath StatPath(string id) => _stats.Key(id);

    /// <summary>The path of one highlight's block.</summary>
    public static YamlPath HighlightPath(string id) => _highlights.Key(id);

    /// <summary>Applies <paramref name="edits" /> and returns the result. An empty batch returns this instance.</summary>
    public RulesetDocumentEditor Apply(IReadOnlyList<TextEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        return edits.Count == 0 ? this : new RulesetDocumentEditor(Document.With(edits));
    }

    /// <summary>Sets a top-level scalar such as <c>title:</c> or <c>summary:</c>, adding the key when absent.</summary>
    public RulesetDocumentEditor SetDocumentField(string key, string value) =>
        Apply([YamlEdits.SetKey(Document, YamlPath.Root, key, value)]);

    /// <summary>Sets one field of one stat, adding the key when the stat does not have it yet.</summary>
    public RulesetDocumentEditor SetStatField(string statId, string key, string value) =>
        Apply([YamlEdits.SetKey(Document, StatPath(statId), key, value)]);

    /// <summary>Removes one field from one stat.</summary>
    public RulesetDocumentEditor RemoveStatField(string statId, string key) =>
        Apply([YamlEdits.RemoveKey(Document, StatPath(statId), key)]);

    /// <summary>Sets one field of one highlight, adding the key when absent.</summary>
    public RulesetDocumentEditor SetHighlightField(string highlightId, string key, string value) =>
        Apply([YamlEdits.SetKey(Document, HighlightPath(highlightId), key, value)]);

    /// <summary>
    ///     Adds a stat. <paramref name="block" /> is the whole entry at zero indentation, starting
    ///     with <c>id:</c>, and is re-indented to sit under <c>stats:</c>.
    /// </summary>
    public RulesetDocumentEditor AddStat(string block) =>
        Apply([YamlEdits.AddEntry(Document, _stats, block)]);

    /// <summary>Removes a stat, its body, and the comment lines directly above it.</summary>
    public RulesetDocumentEditor RemoveStat(string statId) =>
        Apply([YamlEdits.RemoveKey(Document, _stats, statId)]);

    /// <summary>Adds a highlight, as <see cref="AddStat" /> does for stats.</summary>
    public RulesetDocumentEditor AddHighlight(string block) =>
        Apply([YamlEdits.AddEntry(Document, _highlights, block)]);

    /// <summary>Removes a highlight, its body, and the comment lines directly above it.</summary>
    public RulesetDocumentEditor RemoveHighlight(string highlightId) =>
        Apply([YamlEdits.RemoveKey(Document, _highlights, highlightId)]);

    /// <summary>
    ///     The value of a scalar at <paramref name="path" />, or <c>null</c> when it is absent or
    ///     is not a scalar. For reading back what an edit did without a full reload.
    /// </summary>
    public string? Scalar(YamlPath path) => (Document.Find(path) as YamlScalarNode)?.Value;

    private List<string> KeysUnder(YamlPath path)
    {
        if (Document.Find(path) is not YamlMappingNode map)
        {
            return [];
        }

        List<string> ids = new(map.Children.Count);
        foreach (YamlNode key in map.Children.Keys)
        {
            if (key is YamlScalarNode { Value: { } value })
            {
                ids.Add(value);
            }
        }

        return ids;
    }
}
