#region

using System.Reflection;
using System.Text;
using System.Text.Json;
using DemoViewer.NET.Playback2D.Core.Zones;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>
///     A team's words over a map's canonical places (strat-model.md §3.7). Pure over the canonical list and one
///     owner's <see cref="CalloutTable" />: steps store canonical names, the UI shows the owner's words, and this
///     is the one place either direction is decided.
/// </summary>
public sealed class CalloutResolver
{
    // Case-boundary splitting gets every shipped name right except a glued "of"; a stop-word rule would split
    // "Roof" too, so the exceptions are listed instead.
    private static readonly Dictionary<string, string> DisplayOverrides = new(StringComparer.Ordinal)
    {
        ["Topof"] = "Top of",
        ["Backof"] = "Back of"
    };

    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _canonical = new(StringComparer.Ordinal);
    private readonly HashSet<string> _canonicalExact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _primary = new(StringComparer.Ordinal);

    /// <param name="canonicalNames">The map's canonical place names.</param>
    /// <param name="table">The owner's aliases for the map; null for none.</param>
    /// <param name="source">Where the canonical list came from, for a status line.</param>
    public CalloutResolver(IEnumerable<string> canonicalNames, CalloutTable? table = null, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(canonicalNames);
        Source = source;
        List<string> names = [];
        foreach (string name in canonicalNames.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            if (_canonicalExact.Add(name))
            {
                names.Add(name);
                _canonical.TryAdd(Fold(name), name);
            }
        }

        CanonicalNames = names;

        // First alias wins on a duplicate; the validator refuses the file that has one, so this only decides
        // what a hand-edited file shows until it is fixed.
        foreach (CalloutAlias alias in table?.Aliases ?? [])
        {
            string key = Fold(alias.Alias);
            if (key.Length == 0 || string.IsNullOrWhiteSpace(alias.Place))
            {
                continue;
            }

            _aliases.TryAdd(key, alias.Place);
            if (alias.Primary)
            {
                _primary.TryAdd(alias.Place, alias.Alias.Trim());
            }
        }
    }

    /// <summary>The canonical names, in the order given, without duplicates.</summary>
    public IReadOnlyList<string> CanonicalNames { get; }

    /// <summary><c>zones:&lt;zonesVersion&gt;</c> or <c>embedded</c> when built by <see cref="For" />; null otherwise.</summary>
    public string? Source { get; }

    /// <summary>
    ///     The resolver for one owner on one map. The canonical list is the map's baked zones when they loaded
    ///     (overview correction 15: <c>ZoneSet.Places</c>, custom zones included), else the embedded list.
    /// </summary>
    /// <param name="map">The map, in the parser's spelling.</param>
    /// <param name="zones">The map's zones from <c>ZoneAssetPipeline</c>, or null.</param>
    /// <param name="table">The owner's aliases for the map.</param>
    public static CalloutResolver For(string map, ZoneSet? zones, CalloutTable? table)
    {
        if (zones is { Places.Count: > 0 })
        {
            return new CalloutResolver(zones.Places.Select(p => p.Name), table, "zones:" + zones.ZonesVersion);
        }

        return new CalloutResolver(CanonicalPlaces.Embedded(map), table, CanonicalPlaces.EmbeddedSource);
    }

    /// <summary>
    ///     The canonical place a word names: a canonical name first, then an alias, both folded. Null for nothing,
    ///     including the empty string the pawn reports before its place is first networked.
    /// </summary>
    /// <param name="text">What the user typed, or a pawn's place.</param>
    public string? Resolve(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string key = Fold(text);
        return _canonical.GetValueOrDefault(key) ?? _aliases.GetValueOrDefault(key);
    }

    /// <summary>Whether a stored place is one of the map's canonical names, exactly as spelled.</summary>
    /// <param name="place">A place from a step.</param>
    public bool IsCanonical(string? place) => place is not null && _canonicalExact.Contains(place);

    /// <summary>What the UI shows for a place: the owner's primary alias, else the canonical name split into words.</summary>
    /// <param name="place">A canonical place.</param>
    public string Display(string place)
    {
        ArgumentNullException.ThrowIfNull(place);
        return _primary.GetValueOrDefault(place) ?? SplitDisplay(place);
    }

    /// <summary>
    ///     A canonical name split on case boundaries: <c>PalaceInterior</c> to <c>Palace Interior</c>,
    ///     <c>CTSpawn</c> to <c>CT Spawn</c>, <c>TopofMid</c> to <c>Top of Mid</c>. A display helper, not data.
    /// </summary>
    /// <param name="place">A canonical place.</param>
    public static string SplitDisplay(string place)
    {
        ArgumentNullException.ThrowIfNull(place);
        List<string> words = [];
        int start = 0;
        for (int i = 1; i <= place.Length; i++)
        {
            bool boundary = i == place.Length
                            || (char.IsUpper(place[i]) && char.IsLower(place[i - 1]))
                            || (char.IsUpper(place[i]) && char.IsUpper(place[i - 1]) && i + 1 < place.Length && char.IsLower(place[i + 1]))
                            || (char.IsDigit(place[i]) != char.IsDigit(place[i - 1]));
            if (boundary)
            {
                string word = place[start..i];
                words.Add(DisplayOverrides.GetValueOrDefault(word, word));
                start = i;
            }
        }

        return string.Join(' ', words);
    }

    /// <summary>
    ///     The comparison key: trimmed, lower-cased, with whitespace, hyphens and underscores removed. Removing the
    ///     separators rather than collapsing them is what lets <c>top of mid</c> find <c>TopofMid</c>.
    /// </summary>
    /// <param name="text">A name or an alias.</param>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder builder = new(text.Length);
        foreach (char c in text)
        {
            if (!char.IsWhiteSpace(c) && c is not '-' and not '_')
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}

/// <summary>
///     The embedded canonical place lists, one <c>Services/Strats/Callouts/&lt;map&gt;.places.json</c> per shipped
///     map. Generated from the baked zones' <c>env_cs_place</c> names, which the Strat Model measured identical to
///     the pawn's <c>m_szLastPlaceName</c> vocabulary on de_mirage (23 of 23); Place Names From The Pawn replaces
///     them with its own table when it is built.
/// </summary>
public static class CanonicalPlaces
{
    /// <summary>The <see cref="CalloutResolver.Source" /> for a list read from the assembly.</summary>
    public const string EmbeddedSource = "embedded";

    private const string ResourceSuffix = ".places.json";

    private static readonly Lazy<Dictionary<string, IReadOnlyList<string>>> Lists = new(Read);

    /// <summary>The maps with an embedded list, ordinal.</summary>
    public static IReadOnlyList<string> Maps => [.. Lists.Value.Keys.Order(StringComparer.Ordinal)];

    /// <summary>A map's embedded list, or empty for a map with none.</summary>
    /// <param name="map">The map, in the parser's spelling.</param>
    public static IReadOnlyList<string> Embedded(string map) =>
        Lists.Value.GetValueOrDefault(map.ToLowerInvariant()) ?? [];

    private static Dictionary<string, IReadOnlyList<string>> Read()
    {
        Dictionary<string, IReadOnlyList<string>> lists = new(StringComparer.Ordinal);
        Assembly assembly = typeof(CanonicalPlaces).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames()
                     .Where(n => n.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using Stream? stream = assembly.GetManifestResourceStream(resource);
                if (stream is null)
                {
                    continue;
                }

                using JsonDocument document = JsonDocument.Parse(stream);
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("map", out JsonElement map) && map.GetString() is { Length: > 0 } name
                                                                    && root.TryGetProperty("names", out JsonElement names))
                {
                    lists[name.ToLowerInvariant()] = [.. names.EnumerateArray().Select(n => n.GetString()).OfType<string>()];
                }
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
            {
                // A shipped list that fails to read leaves that map with no canonical names: places warn, nothing refuses.
            }
        }

        return lists;
    }
}
