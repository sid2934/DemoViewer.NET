#region

using System.Text.RegularExpressions;
using CS2DemoKit.Analysis.Catalog;

#endregion

namespace DemoViewer.NET.Modules.RuleWorkbench;

/// <summary>
///     One completion candidate surfaced in the Workbench editor: a catalog term or a
///     sibling stat. Kept UI-free (no AvaloniaEdit dependency) so the vocabulary is unit-testable; the
///     View wraps each in an <c>ICompletionData</c> for the AvaloniaEdit completion window.
/// </summary>
/// <param name="Text">The text inserted on accept.</param>
/// <param name="Category">The kind of term (view / facet / context / entity / function / kind / stat).</param>
/// <param name="Detail">A short type/description shown beside the candidate.</param>
public sealed record WorkbenchCompletion(string Text, string Category, string Detail)
{
    /// <summary>Completion priority by category: the terms an author reaches for most sort first.</summary>
    public double Priority => Category switch
    {
        "stat" => 6,
        "view" => 5,
        "literal" => 5,
        "section" => 5,
        "kind" => 4,
        "modifier" => 4,
        "facet" => 3,
        "show" => 3,
        "context" => 2,
        "entity" => 2,
        "function" => 1,
        _ => 0
    };
}

/// <summary>
///     Where the caret sits, so completion can suggest the RIGHT kind of term (type-aware completion:
///     "type suggestions"). Parsed from the current line up to the caret.
/// </summary>
/// <param name="ActiveKey">
///     The mapping key whose value is being typed (e.g. <c>count</c> in <c>count: ki|</c>), or <c>null</c>
///     when the caret is at a key position (a fresh indented line, no <c>:</c> yet).
/// </param>
/// <param name="AtKeyPosition">True when the caret is where a new key is typed (suggest kinds/modifiers).</param>
/// <param name="Block">
///     The enclosing top-level section (v0.6.0 block-scope, GAP-UI-2 cause 3): the last column-0
///     key above the caret ("stats", "show", "highlights", …), the
///     <see cref="WorkbenchCompletionSource.TopLevelBlock" /> sentinel when the caret is itself at
///     column 0, or <c>null</c> when unknown (the line-only overload, legacy behaviour).
/// </param>
public readonly record struct WorkbenchCompletionContext(string? ActiveKey, bool AtKeyPosition, string? Block = null)
{
    /// <summary>The "no context / suggest everything" sentinel (the legacy vocabulary-wide behaviour).</summary>
    public static WorkbenchCompletionContext Any => new(null, false);
}

/// <summary>Builds the Workbench editor's completion vocabulary from the catalog + the edited buffer.</summary>
public static class WorkbenchCompletionSource
{
    /// <summary>Block sentinel: the caret is at column 0, a TOP-LEVEL section key position.</summary>
    public const string TopLevelBlock = "<top>";

    // The closed function set (docs/rules-v2/rules-v2-spec.md) and the stat kinds: stable, not catalog-derived.
    private static readonly string[] _functions = ["min", "max", "abs", "floor", "contains", "startswith"];

    private static readonly string[] _kinds =
        ["count", "sum", "capture", "compute", "tally", "streak", "bucket", "rate", "flag"];

    // The stat/rule/show modifier keys: offered (alongside kinds) when the caret is at a key position.
    private static readonly string[] _modifiers =
    [
        "per", "when", "while", "where", "on", "as", "keep", "of", "by", "match", "title", "group", "label",
        "stat", "reset", "live", "at_least", "at_most", "format"
    ];

    // The document's top-level section keys (the rules authoring guide in the CS2DemoKit repo):
    // offered at a column-0 key position.
    private static readonly string[] _sections = ["ruleset", "for", "use", "stats", "highlights", "show"];

    // show-block container keys. The ENTRY keys (stat/label/group/format/columns…) already live in
    // _modifiers; only the containers are show-specific.
    private static readonly string[] _showKeys = ["scoreboard", "tables", "columns"];

    // Stat ids are direct children of `stats:`, 2-space indent. Matching exactly two leading spaces
    // avoids the 4-space kind keys (count/sum/…) and deeper nesting. (A proper YAML-block parse is a
    // follow-up; the shipped rulesets use 2-space indentation.)
    private static readonly Regex _statIdLine =
        new(@"^ {2}([A-Za-z_][\w]*)\s*:", RegexOptions.Compiled | RegexOptions.Multiline);

    // Fixed-value keys: closed sets that a given key's value must come from (e.g. `per: round|match`).
    private static readonly Dictionary<string, string[]> _valueEnums = new(StringComparer.Ordinal)
    {
        ["per"] = ["round", "match", "game", "half"],
        ["for"] = ["each_player", "match"],
        ["keep"] = ["list"]
    };

    // Keys whose value is a triggering event/view (plus its facets). `on:` and the event-taking kinds.
    private static readonly HashSet<string> _eventValuedKeys =
        new(StringComparer.Ordinal)
        {
            "count",
            "flag",
            "streak",
            "on",
            "of",
            "capture"
        };

    // Keys whose value is a read expression: the whole read vocabulary (contexts, entity, functions,
    // facets, sibling stats, events, booleans).
    private static readonly HashSet<string> _expressionValuedKeys =
        new(StringComparer.Ordinal)
        {
            "sum",
            "compute",
            "when",
            "while",
            "where",
            "by",
            "value",
            "min",
            "max",
            "rate"
        };

    // The LAST `<key>:` before the caret: the tolerant value-position anchor (GAP-UI-2).
    private static readonly Regex _keyBeforeCaret = new(@"([A-Za-z_][\w]*)\s*:", RegexOptions.Compiled);

    /// <summary>
    ///     Parses the completion context from the current line up to the caret. <c>count: ki</c> → value of
    ///     <c>count</c>; a fresh indented line (no <c>:</c>) → a key position; anything else →
    ///     <see cref="WorkbenchCompletionContext.Any" />.
    ///     <para>
    ///         GAP-UI-2 fix: the value position anchors on the LAST <c>key:</c> before the caret instead
    ///         of demanding the line END exactly at the partial value, so trailing spaces after a
    ///         completed token, mid-line carets, values containing spaces, and inline maps
    ///         (<c>when: { enemy: tr</c> narrows on the INNER key) all narrow instead of falling
    ///         through to the whole vocabulary. A column-0 prefix still reports <c>Any</c> on purpose:
    ///         the key-position candidate set (kinds + modifiers) describes indented stat bodies, and
    ///         top-level section keys are not part of the completion vocabulary at all.
    ///     </para>
    /// </summary>
    public static WorkbenchCompletionContext ContextFor(string lineBeforeCaret)
    {
        MatchCollection keys = _keyBeforeCaret.Matches(lineBeforeCaret);
        if (keys.Count > 0)
        {
            Match last = keys[^1];
            string tail = lineBeforeCaret[(last.Index + last.Length)..];
            // Still inside this key's value unless an inline-map delimiter has already closed the
            // entry (`{ enemy: true, ` / `{ enemy: true }`): a between-entries caret is not a
            // value position, and guessing a key there would narrow to the WRONG set.
            if (!tail.Contains(',') && !tail.Contains('}'))
            {
                return new WorkbenchCompletionContext(last.Groups[1].Value, false);
            }

            return WorkbenchCompletionContext.Any;
        }

        // `<indent><partial-key>` with no colon yet, the caret is typing a stat-body key.
        if (Regex.IsMatch(lineBeforeCaret, @"^\s+[A-Za-z_]?[\w]*$"))
        {
            return new WorkbenchCompletionContext(null, true);
        }

        return WorkbenchCompletionContext.Any;
    }

    /// <summary>
    ///     Block-scoped overload (v0.6.0, GAP-UI-2 cause 3): resolves the line context AND the
    ///     enclosing top-level section from <paramref name="textBeforeCaret" /> (everything from the
    ///     document start to the caret). A key position inside <c>show:</c> then offers the show
    ///     vocabulary instead of stat kinds, and a partial word at COLUMN 0 offers the section keys
    ///     instead of everything. Value positions keep their line-local key: the nearest key always
    ///     outranks the block.
    /// </summary>
    public static WorkbenchCompletionContext ContextFor(string lineBeforeCaret, string? textBeforeCaret)
    {
        // A non-empty partial word at column 0 is a TOP-LEVEL key position. (An empty column-0 line
        // stays Any: the user may be about to indent into the block above.)
        if (Regex.IsMatch(lineBeforeCaret, @"^[A-Za-z_][\w]*$"))
        {
            return new WorkbenchCompletionContext(null, true, TopLevelBlock);
        }

        return ContextFor(lineBeforeCaret) with
        {
            Block = EnclosingBlock(textBeforeCaret)
        };
    }

    // The last column-0 `key:` on any line ABOVE the caret's line: the enclosing section.
    private static string? EnclosingBlock(string? textBeforeCaret)
    {
        if (string.IsNullOrEmpty(textBeforeCaret))
        {
            return null;
        }

        string[] lines = textBeforeCaret.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? block = null;
        for (int i = 0; i < lines.Length - 1; i++) // exclude the caret's own (partial) line
        {
            Match m = Regex.Match(lines[i], @"^([A-Za-z_][\w]*)\s*:");
            if (m.Success)
            {
                block = m.Groups[1].Value;
            }
        }

        return block;
    }

    /// <summary>Vocabulary-wide candidates (legacy behaviour): no cursor narrowing.</summary>
    public static IReadOnlyList<WorkbenchCompletion> Build(CatalogRoot catalog, string? bufferText) =>
        Build(catalog, bufferText, WorkbenchCompletionContext.Any);

    /// <summary>
    ///     The candidate set for the cursor <paramref name="context" />: the catalog views + facets, context
    ///     paths, entity reads, functions, stat kinds/modifiers, closed value-enums, and the sibling stat ids
    ///     parsed from <paramref name="bufferText" />, then NARROWED to the roles that fit where the caret
    ///     sits (a value after <c>per:</c> offers <c>round/match</c>; after <c>count:</c> the events; a key
    ///     position offers kinds + modifiers). <see cref="WorkbenchCompletionContext.Any" /> returns the whole
    ///     vocabulary. Deduplicated by (Text, Category); the completion window still filters by typed prefix.
    /// </summary>
    public static IReadOnlyList<WorkbenchCompletion> Build(
        CatalogRoot catalog, string? bufferText, WorkbenchCompletionContext context)
    {
        // A key with a closed value-enum (per/for/keep) is answered directly: nothing else fits.
        if (context.ActiveKey is { } key && _valueEnums.TryGetValue(key, out string[]? enumValues))
        {
            return [.. enumValues.Select(v => new WorkbenchCompletion(v, "literal", $"value · {key}"))];
        }

        Dictionary<(string, string), WorkbenchCompletion> byKey = new();

        void Add(string text, string category, string detail)
        {
            if (!string.IsNullOrEmpty(text))
            {
                byKey.TryAdd((text, category), new WorkbenchCompletion(text, category, detail));
            }
        }

        foreach (CatalogView view in catalog.Views)
        {
            Add(view.Name, "view", $"view · {view.Event}");
            foreach (CatalogFacet facet in view.Facets)
            {
                Add(facet.Name, "facet", $"facet · {facet.Type}");
            }
        }

        foreach (CatalogContextRule ctx in catalog.Contexts)
        {
            if (ctx.V2Name is { } v2)
            {
                Add(v2, "context", "context");
            }
        }

        foreach (CatalogProvider provider in catalog.Providers)
        {
            if (provider.V2Name is { } v2)
            {
                Add(v2, "entity", "entity read");
            }
        }

        foreach (string fn in _functions)
        {
            Add(fn, "function", "function");
        }

        foreach (string kind in _kinds)
        {
            Add(kind, "kind", "stat kind");
        }

        foreach (string modifier in _modifiers)
        {
            Add(modifier, "modifier", "modifier");
        }

        foreach (string section in _sections)
        {
            Add(section, "section", "top-level section");
        }

        foreach (string showKey in _showKeys)
        {
            Add(showKey, "show", "show-block key");
        }

        Add("true", "literal", "boolean");
        Add("false", "literal", "boolean");

        if (!string.IsNullOrEmpty(bufferText))
        {
            foreach (Match m in _statIdLine.Matches(bufferText))
            {
                Add(m.Groups[1].Value, "stat", "this ruleset's stat");
            }
        }

        IEnumerable<WorkbenchCompletion> candidates = Narrow(byKey.Values, context);
        return candidates.OrderByDescending(c => c.Priority).ThenBy(c => c.Text, StringComparer.Ordinal).ToList();
    }

    /// <summary>Filters the full vocabulary to the categories that fit the cursor <paramref name="context" />.</summary>
    private static IEnumerable<WorkbenchCompletion> Narrow(
        IEnumerable<WorkbenchCompletion> all, WorkbenchCompletionContext context)
    {
        // A key position: what fits depends on the enclosing BLOCK (v0.6.0). Column 0 → section
        // keys; inside `show:` → the show containers + entry keys; anywhere else (stats /
        // highlights / unknown-indented) → the things that start a stat/rule body.
        if (context.AtKeyPosition)
        {
            return context.Block switch
            {
                TopLevelBlock => all.Where(c => c.Category is "section"),
                "show" => all.Where(c => c.Category is "show" or "modifier"),
                _ => all.Where(c => c.Category is "kind" or "modifier")
            };
        }

        if (context.ActiveKey is not { } key)
        {
            return all; // WorkbenchCompletionContext.Any (or an unrecognised position) → the whole vocabulary
        }

        if (_eventValuedKeys.Contains(key))
        {
            return all.Where(c => c.Category is "view" or "facet");
        }

        if (_expressionValuedKeys.Contains(key))
        {
            return all.Where(c =>
                c.Category is "context" or "entity" or "function" or "facet" or "stat" or "view" or "literal");
        }

        return all; // a key we don't special-case → don't hide anything
    }
}
