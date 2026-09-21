#region

using DemoViewer.NET.RuleAuthoring;

#endregion

namespace DemoViewer.NET.Modules.RuleWorkbench;

/// <summary>
///     One editable field of a selected node: the YAML key, the value as it stands, and whether the
///     document had the key at all.
/// </summary>
/// <param name="Key">The YAML key under the stat or highlight.</param>
/// <param name="Value">The current value, or empty when the key is absent.</param>
/// <param name="IsPresent">Whether the document has the key today.</param>
public sealed record RulesetNodeField(string Key, string Value, bool IsPresent);

/// <summary>
///     The node editor's write path, and the only one it has.
///     <para>
///         Every gesture lands here and leaves as a <see cref="RulesetDocumentEditor" /> call, so an
///         edit made by dragging a box through the graph and an edit made by typing in the text pane
///         produce the same bytes. §9 decision 4 chose to preserve comments and key order, and a
///         second writer that regenerated instead would make that guarantee depend on which surface
///         the author happened to use.
///     </para>
///     <para>
///         Static and text-in/text-out on purpose: the Workbench already holds the document as one
///         buffer that Save writes, so editing produces a new buffer rather than a new file, and the
///         existing save, dirty-tracking, diagnostics and re-render paths all continue to work
///         without knowing the node editor exists.
///     </para>
/// </summary>
public static class RulesetNodeEditing
{
    /// <summary>
    ///     The keys the editor offers for a stat, in the order the corpus writes them. Deliberately
    ///     NOT every key a stat can carry: the nine kind discriminators (<c>flag:</c>, <c>count:</c>,
    ///     …) decide what a stat IS, and changing one is a different operation from editing a field
    ///     of it. <c>thresholds:</c>, <c>key:</c> and the rest of the kind-specific structure are
    ///     collections rather than scalars and are left to the text pane, which is also where §6.2
    ///     says the expression language belongs.
    /// </summary>
    public static IReadOnlyList<string> StatFieldKeys { get; } =
        ["label", "per", "while", "where", "on", "off"];

    /// <summary>The same for a highlight. <c>when:</c> and <c>title:</c> are what an author changes.</summary>
    public static IReadOnlyList<string> HighlightFieldKeys { get; } =
        ["title", "when", "per", "score", "kind", "group"];

    /// <summary>The keys offered for <paramref name="binding" />, or empty when it is not editable.</summary>
    public static IReadOnlyList<string> KeysFor(RulesetNodeBinding binding) => binding.Kind switch
    {
        RulesetNodeKind.Stat => StatFieldKeys,
        RulesetNodeKind.Highlight => HighlightFieldKeys,
        _ => []
    };

    /// <summary>
    ///     Reads the editable fields of the node <paramref name="binding" /> names out of
    ///     <paramref name="text" />. A key the document does not have comes back present-false with
    ///     an empty value, so the editor can offer to add it.
    /// </summary>
    public static IReadOnlyList<RulesetNodeField> ReadFields(string text, RulesetNodeBinding binding)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!binding.IsEditable)
        {
            return [];
        }

        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlPath at = PathOf(binding);

        List<RulesetNodeField> fields = [];
        foreach (string key in KeysFor(binding))
        {
            string? value = editor.Scalar(at.Key(key));
            fields.Add(new RulesetNodeField(key, value ?? "", value is not null));
        }

        return fields;
    }

    /// <summary>
    ///     Sets one field, or removes it when <paramref name="value" /> is blank and the document
    ///     has the key. Returns the new document text.
    ///     <para>
    ///         Blank meaning "remove" is the behaviour a field editor needs: there is no other
    ///         gesture for taking an optional key back off, and leaving <c>while:</c> set to an
    ///         empty string is a different document from not having it, and a broken one.
    ///     </para>
    /// </summary>
    public static string SetField(string text, RulesetNodeBinding binding, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!binding.IsEditable)
        {
            throw new InvalidOperationException($"{binding.Id} is not editable");
        }

        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlPath at = PathOf(binding);
        bool present = editor.Scalar(at.Key(key)) is not null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return present ? editor.Apply([YamlEdits.RemoveKey(editor.Document, at, key)]).Text : text;
        }

        return editor.Apply([YamlEdits.SetKey(editor.Document, at, key, value.Trim())]).Text;
    }

    /// <summary>Removes the whole stat or highlight the binding names.</summary>
    public static string Delete(string text, RulesetNodeBinding binding)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!binding.IsEditable)
        {
            throw new InvalidOperationException($"{binding.Id} is not editable");
        }

        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        return binding.Kind == RulesetNodeKind.Stat
            ? editor.RemoveStat(binding.Id).Text
            : editor.RemoveHighlight(binding.Id).Text;
    }

    /// <summary>
    ///     Adds a stat under a fresh id and returns the new text plus the id it chose.
    ///     <para>
    ///         A <c>count: kill</c> because it is the smallest stat the resolver accepts, so the new
    ///         node is a real one the graph draws immediately rather than a placeholder that fails
    ///         the check until the author finishes it.
    ///     </para>
    /// </summary>
    public static (string Text, string Id) AddStat(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        string id = UniqueId("new_stat", editor.StatIds, editor.HighlightIds);
        string block = $"{id}:\n  count: kill\n  per: round\n  label: New stat\n";
        return (editor.AddStat(block).Text, id);
    }

    /// <summary>The path of the entry a binding names.</summary>
    public static YamlPath PathOf(RulesetNodeBinding binding) => binding.Kind switch
    {
        RulesetNodeKind.Stat => RulesetDocumentEditor.StatPath(binding.Id),
        RulesetNodeKind.Highlight => RulesetDocumentEditor.HighlightPath(binding.Id),
        _ => throw new InvalidOperationException($"{binding.Id} is not editable")
    };

    // Stats and highlights share one id namespace, so a new id has to clear both.
    private static string UniqueId(string stem, IReadOnlyList<string> stats, IReadOnlyList<string> highlights)
    {
        HashSet<string> taken = new(stats, StringComparer.Ordinal);
        taken.UnionWith(highlights);

        if (!taken.Contains(stem))
        {
            return stem;
        }

        for (int i = 2; ; i++)
        {
            string candidate = $"{stem}_{i}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
