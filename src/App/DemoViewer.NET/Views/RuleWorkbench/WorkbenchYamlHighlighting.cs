#region

using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using CS2DemoKit.Analysis.Catalog;

#endregion

namespace DemoViewer.NET.Views.RuleWorkbench;

/// <summary>
///     A <b>DSL-aware</b> syntax-highlighting definition for the Workbench editor.
///     Generic YAML colouring paints every key the same blue, which hides the rules language's structure;
///     this instead colours by the token's <em>role</em>:
///     <list type="bullet">
///         <item>section keywords (<c>ruleset/stats/highlights/show/for/use</c>) — magenta, the scaffolding;</item>
///         <item>stat kinds (<c>count/sum/tally/compute/flag/…</c>) — bright cyan, the verb that defines a stat;</item>
///         <item>modifiers (<c>per/when/while/where/on/as/…</c>) — blue;</item>
///         <item>literals (<c>each_player/round/match/true/false/null</c>) — teal;</item>
///         <item>catalog <em>event/view</em> names (<c>kill/death/bomb_planted/…</c>) — yellow, the triggers;</item>
///         <item>
///             catalog <em>facets</em> and dotted read-paths (<c>enemy/round.number/player.entity.*/event.*</c>)
///             — light blue, the vocabulary you read;
///         </item>
///         <item>a user's own stat/highlight ids — gold, so declarations stand out from keywords.</item>
///     </list>
///     The event/facet keyword sets are injected from the live <see cref="CatalogRoot" /> so the highlighting
///     tracks the actual authoring vocabulary rather than a hard-coded list.
/// </summary>
internal static class WorkbenchYamlHighlighting
{
    // Keyword groups come BEFORE the generic key/identifier rule so a keyword key (count:, per:) is coloured
    // by its role group, and only NON-keyword keys (a user's stat ids) fall through to the gold Identifier
    // rule. The dotted-path Rule colours read-paths (round.*, player.*, event.*, params.*) anywhere.
    private const string Template =
        """
        <SyntaxDefinition name="YAML-RulesV2" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
        %%COLORS%%
          <RuleSet ignoreCase="false">
            <Span color="Comment"><Begin>\#</Begin></Span>
            <Span color="String" multiline="false"><Begin>"</Begin><End>"</End></Span>
            <Span color="String" multiline="false"><Begin>'</Begin><End>'</End></Span>

            <!-- read-paths first, so a dotted path (round.number, player.entity.pawn.health, event.Attacker,
                 params.min_kills) wins as ONE Path token over its leading segment matching a Literal keyword. -->
            <Rule color="Path">\b(round|match|game|half|player|team|event|params|rule)\.[A-Za-z_][\w.]*</Rule>

            <Keywords color="Section">%%SECTIONS%%</Keywords>
            <Keywords color="Kind">%%KINDS%%</Keywords>
            <Keywords color="Modifier">%%MODIFIERS%%</Keywords>
            <Keywords color="Literal">%%LITERALS%%</Keywords>
            <Keywords color="Event">%%EVENTS%%</Keywords>
            <Keywords color="Facet">%%FACETS%%</Keywords>

            <!-- a user's own declared id (any remaining key) -->
            <Rule color="Identifier">[A-Za-z_][\w-]*(?=\s*:)</Rule>

            <Rule color="Operator">(&amp;&amp;|\|\||[=!&lt;&gt;]=|[&lt;&gt;+*/])</Rule>
            <Rule color="Number">\b\d+(\.\d+)?\b</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;

    // Guarded: the app only touches this from the UI thread, but the test suite exercises
    // Definition/DefinitionFor from parallel test threads — an unguarded Dictionary corrupts.
    private static readonly Dictionary<ThemeVariant, IHighlightingDefinition> _byVariant = new();
    private static readonly object _cacheGate = new();

    // ── role vocabularies (schema-derived; see rules/cs2demokit-rules.schema.json) ────────────────────────
    private static readonly string[] _sections =
        ["ruleset", "for", "use", "params", "stats", "highlights", "show", "define", "summary", "catalog_version"];

    private static readonly string[] _kinds =
        ["count", "sum", "capture", "compute", "tally", "streak", "bucket", "rate", "flag"];

    private static readonly string[] _modifiers =
    [
        "per", "when", "while", "where", "on", "as", "keep", "of", "by", "match", "title", "group", "label",
        "stat", "scoreboard", "tables", "columns", "format", "key", "window", "value", "min", "max", "reset",
        "live", "at_least", "at_most"
    ];

    private static readonly string[] _literals =
        ["each_player", "round", "game", "half", "list", "true", "false", "null"];

    // Each role's foreground is a TOKEN (Syntax<Name>) resolved from the active theme; this table is only the
    // design-time / no-resources FALLBACK (the VS "Dark+" values). Section is bold.
    private static readonly (string Name, string Fallback)[] _roles =
    [
        ("Comment", "#6A9955"), ("Section", "#C586C0"), ("Kind", "#4FC1FF"), ("Modifier", "#569CD6"),
        ("Literal", "#4EC9B0"), ("Event", "#DCDCAA"), ("Facet", "#9CDCFE"), ("Path", "#9CDCFE"),
        ("Identifier", "#D7BA7D"), ("String", "#CE9178"), ("Number", "#B5CEA8"), ("Operator", "#D4D4D4")
    ];

    private static readonly Regex _wordShape = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private static readonly Regex _emptyKeywords =
        new("<Keywords color=\"[^\"]*\"></Keywords>", RegexOptions.Compiled);

    /// <summary>The cached Dark definition (back-compat default).</summary>
    public static IHighlightingDefinition Definition => DefinitionFor(ThemeVariant.Dark);

    /// <summary>
    ///     The cached highlighting definition for a theme VARIANT (T1). Each role colour is resolved from the
    ///     <c>Syntax&lt;Role&gt;</c> token namespace for <paramref name="variant" />, so ANY theme —
    ///     Dark / Light / High-Contrast / E-Girl / a user drop-in — colours the editor with no code change
    ///     here; the RuleWorkbench view re-sets this on <c>ActualThemeVariantChanged</c>. Built once per variant
    ///     (AvaloniaEdit caches the parsed definition; a fresh instance per variant is the refresh).
    /// </summary>
    public static IHighlightingDefinition DefinitionFor(ThemeVariant? variant)
    {
        ThemeVariant v = variant ?? ThemeVariant.Default; // ActualThemeVariant can be null before attach
        lock (_cacheGate)
        {
            if (!_byVariant.TryGetValue(v, out IHighlightingDefinition? def))
            {
                def = Build(SafeCatalog(), v);
                _byVariant[v] = def;
            }

            return def;
        }
    }

    /// <summary>Drops the per-variant cache so a theme edit / drop-in reload rebuilds the definitions (T3).</summary>
    public static void ClearCache()
    {
        lock (_cacheGate)
        {
            _byVariant.Clear();
        }
    }

    // Emits the <Color> block for a variant, resolving each role from its Syntax<Name> token (→ %%COLORS%%).
    private static string ColorBlock(ThemeVariant variant)
    {
        StringBuilder sb = new();
        foreach ((string name, string fallback) in _roles)
        {
            sb.Append("  <Color name=\"").Append(name).Append("\" foreground=\"")
                .Append(ResolveRole(name, variant, fallback)).Append('"');
            if (name == "Section")
            {
                sb.Append(" fontWeight=\"bold\"");
            }

            sb.Append(" />\n");
        }

        return sb.ToString();
    }

    // Resolves the Syntax<Name> brush token for the variant → an "#RRGGBB" string; falls back to the design-time
    // value when the app resources aren't available (tests / design surface).
    private static string ResolveRole(string name, ThemeVariant variant, string fallback)
    {
        if (Application.Current?.TryGetResource("Syntax" + name, variant, out object? o) == true
            && o is ISolidColorBrush b)
        {
            Color c = b.Color;
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        return fallback;
    }

    private static IHighlightingDefinition Build(CatalogRoot? catalog, ThemeVariant variant)
    {
        // Event/view names are the triggers; facets are read-vocabulary. Contexts + entity reads are mostly
        // dotted (round.number, player.entity.pawn.health) → covered by the dotted-path Rule below, not here.
        IEnumerable<string> events = catalog?.Views.Select(v => v.Name) ?? [];
        IEnumerable<string> facets = catalog?.Views.SelectMany(v => v.Facets).Select(f => f.Name) ?? [];

        string xshd = Template
            .Replace("%%COLORS%%", ColorBlock(variant))
            .Replace("%%SECTIONS%%", Words(_sections))
            .Replace("%%KINDS%%", Words(_kinds))
            .Replace("%%MODIFIERS%%", Words(_modifiers))
            .Replace("%%LITERALS%%", Words(_literals))
            .Replace("%%EVENTS%%", Words(events))
            .Replace("%%FACETS%%", Words(facets));

        // Drop any keyword group left empty (e.g. the catalog failed to load) — an empty <Keywords> is invalid.
        xshd = _emptyKeywords.Replace(xshd, string.Empty);

        using StringReader stringReader = new(xshd);
        using XmlReader xmlReader = XmlReader.Create(stringReader);
        return HighlightingLoader.Load(xmlReader, HighlightingManager.Instance);
    }

    /// <summary>Emits <c>&lt;Word&gt;</c> lines for the simple identifiers in <paramref name="terms" /> (deduped, sorted).</summary>
    private static string Words(IEnumerable<string> terms)
    {
        StringBuilder sb = new();
        foreach (string term in terms
                     .Where(t => _wordShape.IsMatch(t))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(t => t, StringComparer.Ordinal))
        {
            sb.Append("<Word>").Append(term).Append("</Word>");
        }

        return sb.ToString();
    }

    private static CatalogRoot? SafeCatalog()
    {
        try
        {
            return CatalogResource.Load();
        }
        catch
        {
            return null;
        }
    }
}
