#region

using System.Text;
using System.Text.Json;
using Avalonia;

#endregion

namespace DemoViewer.NET.Modules.RuleWorkbench;

/// <summary>
///     A user's arrangement of one ruleset's canvas: where they put each node they moved, keyed by
///     the identity <see cref="RulesetDocumentNodeKey" /> gives, and nothing else.
///     <para>
///         <b>A node this layout does not mention is not missing and is not an error.</b> It is a
///         node nobody has dragged, and the computed MSAGL position is the right answer for it,
///         which is what <see cref="PositionOf" /> says in one line. The same holds for a whole
///         ruleset: one the editor has never opened has no sidecar at all, so auto-layout is its
///         entire answer and nothing has to be migrated to get there.
///     </para>
///     <para>
///         <b>Entries naming nodes that no longer exist are kept, not pruned.</b> Load is a bad
///         moment to prune on: the node set comes from a composition that comes back short whenever
///         the open document does not parse, so pruning against it would delete the arrangement of
///         everything an author was halfway through renaming. An entry nothing looks up costs one
///         line in a file; a discarded arrangement costs the work the sidecar exists to keep.
///     </para>
/// </summary>
public sealed class RulesetNodeLayout
{
    private readonly Dictionary<RulesetDocumentNodeKey, Point> _positions;

    private RulesetNodeLayout(Dictionary<RulesetDocumentNodeKey, Point> positions) => _positions = positions;

    /// <summary>The answer for a ruleset with no sidecar, which is every ruleset until one is dragged.</summary>
    public static RulesetNodeLayout Empty { get; } = new([]);

    /// <summary>How many nodes carry a stored position, including ones the current graph does not draw.</summary>
    public int Count => _positions.Count;

    /// <summary>Every stored position, in no meaningful order.</summary>
    public IReadOnlyDictionary<RulesetDocumentNodeKey, Point> Positions => _positions;

    /// <summary>Builds a layout from the positions a canvas holds. A later duplicate of a key wins.</summary>
    /// <param name="positions">The node-to-position pairs to store.</param>
    /// <returns>The layout, or <see cref="Empty" /> when there is nothing to store.</returns>
    public static RulesetNodeLayout From(IEnumerable<KeyValuePair<RulesetDocumentNodeKey, Point>> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);

        Dictionary<RulesetDocumentNodeKey, Point> map = [];
        foreach ((RulesetDocumentNodeKey key, Point at) in positions)
        {
            map[key] = at;
        }

        return map.Count == 0 ? Empty : new RulesetNodeLayout(map);
    }

    /// <summary>The stored position of <paramref name="key" />, or <c>null</c> when nobody moved it.</summary>
    /// <param name="key">The node to look up.</param>
    /// <returns>The position, or <c>null</c>.</returns>
    public Point? PositionFor(RulesetDocumentNodeKey key) =>
        _positions.TryGetValue(key, out Point at) ? at : null;

    /// <summary>
    ///     The fallback rule itself: a node the user arranged keeps their position, and every other
    ///     node keeps the one layout computed for it.
    /// </summary>
    /// <param name="key">The node being placed.</param>
    /// <param name="computed">Where auto-layout put it.</param>
    /// <returns>The position to draw at.</returns>
    public Point PositionOf(RulesetDocumentNodeKey key, Point computed) => PositionFor(key) ?? computed;

    /// <summary>A copy with <paramref name="key" /> at <paramref name="position" />.</summary>
    /// <param name="key">The node that moved.</param>
    /// <param name="position">Where it now sits.</param>
    /// <returns>The new layout; this one is unchanged.</returns>
    public RulesetNodeLayout With(RulesetDocumentNodeKey key, Point position)
    {
        Dictionary<RulesetDocumentNodeKey, Point> map = new(_positions)
        {
            [key] = position
        };

        return new RulesetNodeLayout(map);
    }

    /// <summary>A copy with <paramref name="key" /> back on auto-layout.</summary>
    /// <param name="key">The node to forget.</param>
    /// <returns>The new layout; this one is unchanged.</returns>
    public RulesetNodeLayout Without(RulesetDocumentNodeKey key)
    {
        if (!_positions.ContainsKey(key))
        {
            return this;
        }

        Dictionary<RulesetDocumentNodeKey, Point> map = new(_positions);
        map.Remove(key);
        return map.Count == 0 ? Empty : new RulesetNodeLayout(map);
    }
}

/// <summary>
///     Reads and writes the <c>&lt;name&gt;.rules.layout.json</c> sidecar that holds a ruleset's node
///     arrangement, per design.md §9 decision 9.
///     <para>
///         <b>The coordinates stay out of the YAML.</b> A ruleset is a documentation-grade file whose
///         value is substantially its prose, <c>rules/aim_rating.rules.yaml</c> opening with about
///         sixty lines of it, and a <c>layout:</c> key would make every drag a document edit that the
///         comment-preserving writer of decision 4 has to splice back in. The sidecar is purely
///         additive instead: it is written beside the ruleset, and its absence means auto-layout.
///     </para>
///     <para>
///         <b>Nothing here throws into the UI.</b> A missing sidecar, an unparseable one, a
///         half-written one, an entry naming a node that no longer exists, and a location that cannot
///         be written all resolve to "this ruleset has no stored arrangement", which is the state
///         every ruleset starts in. <see cref="Save" /> reports failure rather than raising it, which
///         is the case a shipped ruleset under a read-only install directory hits.
///     </para>
///     <para>
///         The sidecar's key spelling belongs to this file rather than to
///         <c>RulesetDocumentNodeKey.ToString</c>: the written form and the read form are one pair
///         here, so changing how a node key DISPLAYS cannot silently repoint every stored position.
///     </para>
/// </summary>
public static class RulesetLayoutStore
{
    /// <summary>What replaces a ruleset's <c>.yaml</c> to name its sidecar.</summary>
    public const string SidecarSuffix = ".layout.json";

    // The format's version, written but not yet branched on. It exists so a later shape change can
    // be read rather than discarded, which is most of why a sidecar is cheap to evolve.
    private const int CurrentVersion = 1;

    private const string VersionProperty = "version";
    private const string NodesProperty = "nodes";
    private const string XProperty = "x";
    private const string YProperty = "y";

    // Canvas coordinates are graph units for a node box, so two decimals is past the point of
    // visible difference. Rounding keeps a dragged node from rewriting its line as
    // 120.00000000000001, and makes two saves of one arrangement byte-identical in a diff.
    private const int Decimals = 2;

    private static readonly string[] _yamlExtensions = [".yaml", ".yml"];

    /// <summary>
    ///     Where <paramref name="rulesetPath" />'s sidecar sits: beside it, named for it. Pure string
    ///     work, so it touches no disk and never throws on a path that does not exist.
    /// </summary>
    /// <param name="rulesetPath">The ruleset's full path, normally a <c>*.rules.yaml</c>.</param>
    /// <returns>The sidecar's full path.</returns>
    public static string SidecarPathFor(string rulesetPath)
    {
        ArgumentNullException.ThrowIfNull(rulesetPath);

        foreach (string extension in _yamlExtensions)
        {
            if (rulesetPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return rulesetPath[..^extension.Length] + SidecarSuffix;
            }
        }

        return rulesetPath + SidecarSuffix;
    }

    /// <summary>
    ///     The arrangement stored for <paramref name="rulesetPath" />, or
    ///     <see cref="RulesetNodeLayout.Empty" /> when there is no sidecar, it cannot be read, or it
    ///     cannot be parsed. All three mean one thing to a canvas: lay this one out.
    /// </summary>
    /// <param name="rulesetPath">The ruleset's full path.</param>
    /// <returns>The layout.</returns>
    public static RulesetNodeLayout Load(string rulesetPath)
    {
        string sidecar = SidecarPathFor(rulesetPath);

        try
        {
            return File.Exists(sidecar) ? Parse(File.ReadAllText(sidecar)) : RulesetNodeLayout.Empty;
        }
        catch (IOException)
        {
            return RulesetNodeLayout.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return RulesetNodeLayout.Empty;
        }
    }

    /// <summary>
    ///     Writes <paramref name="layout" /> beside <paramref name="rulesetPath" />, replacing any
    ///     sidecar already there. A layout with nothing to store REMOVES the sidecar rather than
    ///     writing an empty one, so opening a ruleset and dragging nothing leaves no file behind.
    /// </summary>
    /// <param name="rulesetPath">The ruleset's full path.</param>
    /// <param name="layout">The arrangement to store.</param>
    /// <returns>
    ///     <c>true</c> when the sidecar now matches <paramref name="layout" />. <c>false</c> is the
    ///     answer for a location that cannot be written, which a shipped ruleset under a read-only
    ///     install directory is: the arrangement is lost and the canvas computes one next time.
    /// </returns>
    public static bool Save(string rulesetPath, RulesetNodeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        string sidecar = SidecarPathFor(rulesetPath);

        try
        {
            // A layout with no writable entry is the same state as no layout, and is stored the same
            // way: as the absence of a file. Otherwise a ruleset whose every coordinate came back
            // non-finite would keep a sidecar that stores nothing.
            if (!Writable(layout).Any())
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }

                return true;
            }

            string? directory = Path.GetDirectoryName(sidecar);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(sidecar, Serialize(layout));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Reads the sidecar's text. Entry-level damage is survivable and is survived: a malformed
    ///     entry costs its own node's position and no other's.
    ///     <para>
    ///         A file whose JSON does not parse at all is NOT recoverable and yields
    ///         <see cref="RulesetNodeLayout.Empty" />, which is the truncation case. That boundary is
    ///         deliberate: guessing at the tail of a half-written file would put nodes somewhere
    ///         nobody placed them, and the cost of not guessing is one auto-layout.
    ///     </para>
    /// </summary>
    /// <param name="json">The sidecar's contents.</param>
    /// <returns>The layout.</returns>
    public static RulesetNodeLayout Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return RulesetNodeLayout.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(NodesProperty, out JsonElement nodes)
                || nodes.ValueKind != JsonValueKind.Object)
            {
                return RulesetNodeLayout.Empty;
            }

            Dictionary<RulesetDocumentNodeKey, Point> map = [];
            foreach (JsonProperty entry in nodes.EnumerateObject())
            {
                // Everything an entry needs is checked before it is taken, so a bad one falls out
                // here instead of failing the read. A property this version does not know about is
                // simply not asked for, which is how an older build reads a newer sidecar.
                if (TryReadKey(entry.Name, out RulesetDocumentNodeKey key)
                    && TryReadCoordinate(entry.Value, XProperty, out double x)
                    && TryReadCoordinate(entry.Value, YProperty, out double y))
                {
                    map[key] = new Point(x, y);
                }
            }

            return RulesetNodeLayout.From(map);
        }
        catch (JsonException)
        {
            return RulesetNodeLayout.Empty;
        }
    }

    /// <summary>
    ///     Renders <paramref name="layout" /> as the sidecar's text, keys in ordinal order so one
    ///     arrangement always produces one file and a version-controlled sidecar does not churn.
    /// </summary>
    /// <param name="layout">The arrangement to render.</param>
    /// <returns>The JSON.</returns>
    public static string Serialize(RulesetNodeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber(VersionProperty, CurrentVersion);
            writer.WriteStartObject(NodesProperty);

            foreach ((string name, Point at) in Writable(layout))
            {
                writer.WriteStartObject(name);
                writer.WriteNumber(XProperty, Math.Round(at.X, Decimals, MidpointRounding.AwayFromZero));
                writer.WriteNumber(YProperty, Math.Round(at.Y, Decimals, MidpointRounding.AwayFromZero));
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    ///     The entries that can be written at all, sorted. A non-finite coordinate is dropped here
    ///     rather than at the writer, which rejects one by throwing, and a node placed at NaN is
    ///     nowhere anyway.
    /// </summary>
    private static IEnumerable<(string Name, Point At)> Writable(RulesetNodeLayout layout) =>
        layout.Positions
            .Select(entry => (Name: FormatKey(entry.Key), At: entry.Value))
            .Where(entry => entry.Name.Length > 0 && double.IsFinite(entry.At.X) && double.IsFinite(entry.At.Y))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal);

    /// <summary>
    ///     The qualified <c>ruleset.id</c> spelling, or empty for a key that cannot round-trip. A
    ///     ruleset id carrying a dot is the only such case, and it cannot arise: that id is what an
    ///     author writes before the dot in a cross-ruleset read, so a dot inside it would be
    ///     ambiguous to the engine long before it reached here.
    /// </summary>
    private static string FormatKey(RulesetDocumentNodeKey key) =>
        string.IsNullOrEmpty(key.Ruleset) || string.IsNullOrEmpty(key.Id)
        || key.Ruleset.Contains('.', StringComparison.Ordinal)
            ? string.Empty
            : key.Ruleset + "." + key.Id;

    /// <summary>Splits a stored key at its first dot, which is the inverse of <see cref="FormatKey" />.</summary>
    private static bool TryReadKey(string name, out RulesetDocumentNodeKey key)
    {
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && dot < name.Length - 1)
        {
            key = new RulesetDocumentNodeKey(name[..dot], name[(dot + 1)..]);
            return true;
        }

        key = default;
        return false;
    }

    /// <summary>
    ///     Reads one coordinate, refusing anything that is not a finite number. <c>TryGetDouble</c>
    ///     is what rejects a value too large to represent, which is the shape an overflowed or
    ///     hand-edited coordinate arrives in.
    /// </summary>
    private static bool TryReadCoordinate(JsonElement entry, string property, out double value)
    {
        value = 0;
        return entry.ValueKind == JsonValueKind.Object
               && entry.TryGetProperty(property, out JsonElement number)
               && number.ValueKind == JsonValueKind.Number
               && number.TryGetDouble(out value)
               && double.IsFinite(value);
    }
}
