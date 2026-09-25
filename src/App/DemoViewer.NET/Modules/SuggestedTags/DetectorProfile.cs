#region

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>A team's edit to one site's region: places added and places taken away.</summary>
/// <param name="Add">Places the region gains.</param>
/// <param name="Remove">Places the region loses.</param>
public sealed record SiteRegionOverride(IReadOnlyList<string> Add, IReadOnlyList<string> Remove);

/// <summary>
///     The parameter profile (suggested-tags.md §3.7): every detector's numbers, the order they run
///     in, and the team's site region overrides, as one JSON document a team owns. Immutable; a
///     change is a new profile, which is what the detector-set fingerprint will hash.
///     <para>
///         Read and written by hand over <see cref="JsonNode" /> rather than a serializer context: the
///         detector sections are open-ended (a value the file names that no detector reads is kept, so
///         a profile written by a newer build survives a round trip) and a missing value falls back to
///         the shipped default, so a profile that names only <c>execute.N</c> is a whole profile.
///     </para>
/// </summary>
public sealed class DetectorProfile
{
    /// <summary>The profile shape version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The key of the overrides block.</summary>
    public const string OverridesKey = "siteRegionOverrides";

    private readonly Dictionary<string, Dictionary<string, double>> _values;

    private DetectorProfile(
        string id,
        IReadOnlyList<string> order,
        Dictionary<string, Dictionary<string, double>> values,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, SiteRegionOverride>> overrides)
    {
        Id = id;
        Order = order;
        _values = values;
        SiteRegionOverrides = overrides;
    }

    /// <summary>
    ///     The shipped profile: every detector's defaults, the fixed order, and the one override the
    ///     measurements asked for (<c>Middle</c> out of de_inferno's A region, §3.2).
    /// </summary>
    public static DetectorProfile Default { get; } = CreateDefault();

    /// <summary>The profile's name.</summary>
    public string Id { get; }

    /// <summary>
    ///     Detector ids in run order. Execute comes first because default and fake read its result; the
    ///     order is data so a future detector can slot in.
    /// </summary>
    public IReadOnlyList<string> Order { get; }

    /// <summary>Map to site place to the team's edit.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, SiteRegionOverride>> SiteRegionOverrides { get; }

    /// <summary>A parameter's value; the shipped default when the profile does not name it.</summary>
    /// <param name="detector">The detector id.</param>
    /// <param name="parameter">The parameter name.</param>
    /// <exception cref="KeyNotFoundException">No detector declares the parameter and the profile does not name it.</exception>
    public double Get(string detector, string parameter)
    {
        if (_values.TryGetValue(detector, out Dictionary<string, double>? section)
            && section.TryGetValue(parameter, out double value))
        {
            return value;
        }

        return ProposalDetection.DefaultOf(detector, parameter)
               ?? throw new KeyNotFoundException($"no parameter {detector}.{parameter}");
    }

    /// <summary>A copy with one parameter changed.</summary>
    /// <param name="detector">The detector id.</param>
    /// <param name="parameter">The parameter name.</param>
    /// <param name="value">The new value.</param>
    public DetectorProfile With(string detector, string parameter, double value)
    {
        Dictionary<string, Dictionary<string, double>> values = Copy(_values);
        if (!values.TryGetValue(detector, out Dictionary<string, double>? section))
        {
            section = new Dictionary<string, double>(StringComparer.Ordinal);
            values[detector] = section;
        }

        section[parameter] = value;
        return new DetectorProfile(Id, Order, values, SiteRegionOverrides);
    }

    /// <summary>A copy with another run order.</summary>
    /// <param name="order">Detector ids in run order.</param>
    public DetectorProfile WithOrder(IReadOnlyList<string> order) =>
        new(Id, [.. order], Copy(_values), SiteRegionOverrides);

    /// <summary>A copy with one site's override replaced; a null edit removes it.</summary>
    /// <param name="map">The map.</param>
    /// <param name="site">The site place.</param>
    /// <param name="edit">The edit.</param>
    public DetectorProfile WithOverride(string map, string site, SiteRegionOverride? edit)
    {
        Dictionary<string, IReadOnlyDictionary<string, SiteRegionOverride>> overrides =
            new(SiteRegionOverrides, StringComparer.Ordinal);
        Dictionary<string, SiteRegionOverride> perMap = overrides.TryGetValue(map, out IReadOnlyDictionary<string, SiteRegionOverride>? existing)
            ? new Dictionary<string, SiteRegionOverride>(existing, StringComparer.Ordinal)
            : new Dictionary<string, SiteRegionOverride>(StringComparer.Ordinal);
        if (edit is null)
        {
            perMap.Remove(site);
        }
        else
        {
            perMap[site] = edit;
        }

        overrides[map] = perMap;
        return new DetectorProfile(Id, Order, Copy(_values), overrides);
    }

    /// <summary>
    ///     Reads a profile. Missing detector values take the shipped defaults; a missing order takes the
    ///     shipped order.
    /// </summary>
    /// <param name="json">The profile text.</param>
    /// <exception cref="FormatException">The text is not a profile, or its schema is newer than this build reads.</exception>
    public static DetectorProfile Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return ParseObject(JsonNode.Parse(json) as JsonObject ?? throw new FormatException("a profile is a JSON object"));
        }
        catch (JsonException ex)
        {
            throw new FormatException($"a profile is JSON: {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            // A value of the wrong JSON kind where a number or a name belongs.
            throw new FormatException($"a profile value has the wrong type: {ex.Message}", ex);
        }
    }

    private static DetectorProfile ParseObject(JsonObject root)
    {
        int schema = root["schemaVersion"]?.GetValue<int>() ?? CurrentSchemaVersion;
        if (schema > CurrentSchemaVersion)
        {
            throw new FormatException($"profile schema {schema.ToString(CultureInfo.InvariantCulture)} is newer than this build reads");
        }

        string id = root["id"]?.GetValue<string>() ?? Default.Id;
        IReadOnlyList<string> order = root["order"] is JsonArray orderArray
            ? [.. orderArray.Select(n => n?.GetValue<string>()).OfType<string>()]
            : Default.Order;

        Dictionary<string, Dictionary<string, double>> values = new(StringComparer.Ordinal);
        foreach ((string key, JsonNode? node) in root)
        {
            if (node is not JsonObject section || key == OverridesKey)
            {
                continue;
            }

            Dictionary<string, double> parameters = new(StringComparer.Ordinal);
            foreach ((string name, JsonNode? value) in section)
            {
                if (value is JsonValue number && number.TryGetValue(out double d))
                {
                    parameters[name] = d;
                }
            }

            values[key] = parameters;
        }

        Dictionary<string, IReadOnlyDictionary<string, SiteRegionOverride>> overrides = new(StringComparer.Ordinal);
        if (root[OverridesKey] is JsonObject maps)
        {
            foreach ((string map, JsonNode? sites) in maps)
            {
                if (sites is not JsonObject siteObject)
                {
                    continue;
                }

                Dictionary<string, SiteRegionOverride> perMap = new(StringComparer.Ordinal);
                foreach ((string site, JsonNode? edit) in siteObject)
                {
                    perMap[site] = new SiteRegionOverride(Names(edit?["add"]), Names(edit?["remove"]));
                }

                overrides[map] = perMap;
            }
        }

        return new DetectorProfile(id, order, values, overrides);
    }

    /// <summary>The profile as §3.7 writes it: every detector in order with every value, then the overrides.</summary>
    public string ToJson()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            writer.WriteString("id", Id);
            writer.WriteStartArray("order");
            foreach (string detector in Order)
            {
                writer.WriteStringValue(detector);
            }

            writer.WriteEndArray();

            HashSet<string> written = new(StringComparer.Ordinal);
            foreach (string detector in Order.Concat(_values.Keys.Order(StringComparer.Ordinal)))
            {
                if (!written.Add(detector))
                {
                    continue;
                }

                writer.WriteStartObject(detector);
                foreach ((string name, double value) in Section(detector))
                {
                    writer.WriteNumber(name, value);
                }

                writer.WriteEndObject();
            }

            writer.WriteStartObject(OverridesKey);
            foreach ((string map, IReadOnlyDictionary<string, SiteRegionOverride> sites) in
                     SiteRegionOverrides.OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject(map);
                foreach ((string site, SiteRegionOverride edit) in sites.OrderBy(s => s.Key, StringComparer.Ordinal))
                {
                    writer.WriteStartObject(site);
                    if (edit.Add.Count > 0)
                    {
                        WriteNames(writer, "add", edit.Add);
                    }

                    if (edit.Remove.Count > 0)
                    {
                        WriteNames(writer, "remove", edit.Remove);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // A detector's values: the declared parameters in declaration order, then anything else the
    // profile named, so the file reads like the table in the design.
    private IEnumerable<(string Name, double Value)> Section(string detector)
    {
        _values.TryGetValue(detector, out Dictionary<string, double>? given);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (DetectorParameter parameter in ProposalDetection.ParametersOf(detector))
        {
            seen.Add(parameter.Name);
            yield return (parameter.Name, given is not null && given.TryGetValue(parameter.Name, out double v) ? v : parameter.Default);
        }

        if (given is null)
        {
            yield break;
        }

        foreach ((string name, double value) in given.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!seen.Contains(name))
            {
                yield return (name, value);
            }
        }
    }

    private static void WriteNames(Utf8JsonWriter writer, string key, IReadOnlyList<string> names)
    {
        writer.WriteStartArray(key);
        foreach (string name in names)
        {
            writer.WriteStringValue(name);
        }

        writer.WriteEndArray();
    }

    private static List<string> Names(JsonNode? node) =>
        node is JsonArray array ? [.. array.Select(n => n?.GetValue<string>()).OfType<string>()] : [];

    private static Dictionary<string, Dictionary<string, double>> Copy(Dictionary<string, Dictionary<string, double>> values) =>
        values.ToDictionary(p => p.Key, p => new Dictionary<string, double>(p.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static DetectorProfile CreateDefault()
    {
        Dictionary<string, Dictionary<string, double>> values = new(StringComparer.Ordinal);
        foreach (IProposalDetector detector in ProposalDetection.All)
        {
            values[detector.Id] = detector.Parameters.ToDictionary(p => p.Name, p => p.Default, StringComparer.Ordinal);
        }

        Dictionary<string, IReadOnlyDictionary<string, SiteRegionOverride>> overrides = new(StringComparer.Ordinal)
        {
            ["de_inferno"] = new Dictionary<string, SiteRegionOverride>(StringComparer.Ordinal)
            {
                [SiteRegions.SiteA] = new SiteRegionOverride([], ["Middle"])
            }
        };
        return new DetectorProfile("team-default", [.. ProposalDetection.All.Select(d => d.Id)], values, overrides);
    }
}

/// <summary>
///     The profile on disk: <c>&lt;config&gt;/suggested-tags/profile.json</c>, beside the learned site
///     region tables (suggested-tags.md §3.7). "The shipped default is embedded and written out on
///     first run, the way themes are" — <see cref="Current" /> is that first read, and it writes the
///     shipped profile back out the first time there is nothing to read. A null directory (the
///     browser, tests) keeps the profile in memory only, per §3.8: no tuning view there, and the
///     embedded default is what every build runs with.
/// </summary>
public sealed class ProfileStore
{
    private const string FileName = "profile.json";

    private readonly string? _directory;
    private readonly Lock _gate = new();
    private DetectorProfile? _current;
    private string? _memory;

    /// <param name="directory">The suggested-tags config directory, or null for a session-only store.</param>
    public ProfileStore(string? directory)
    {
        _directory = directory;
    }

    /// <summary>Whether a save outlives the process.</summary>
    public bool IsPersistent => _directory is not null;

    /// <summary>
    ///     The profile in force: read once, cached, and reread only after a <see cref="Save" />. The
    ///     first call writes the shipped default to disk when nothing is there yet, so the file a team
    ///     finds under the config directory is never empty.
    /// </summary>
    public DetectorProfile Current
    {
        get
        {
            lock (_gate)
            {
                return _current ??= LoadOrSeed();
            }
        }
    }

    /// <summary>A profile a tuning save has not yet reached the caller's <see cref="Current" /> read from.</summary>
    public event Action? Changed;

    /// <summary>
    ///     Persists <paramref name="profile" /> as the current one: the tuning view's "Save" (suggested-tags.md
    ///     §3.7), after a candidate has been previewed. Changing a value changes the detector-set
    ///     fingerprint, which is what marks every demo's proposals stale for the evaluator to rebuild.
    /// </summary>
    /// <param name="profile">The profile to keep.</param>
    public void Save(DetectorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string json = profile.ToJson();
        lock (_gate)
        {
            if (_directory is null)
            {
                _memory = json;
            }
            else
            {
                Directory.CreateDirectory(_directory);
                DemoCacheStore.WriteAtomic(Path.Combine(_directory, FileName), json + "\n");
            }

            _current = profile;
        }

        Changed?.Invoke();
    }

    /// <summary>Forgets the cached profile, so the next <see cref="Current" /> rereads the file.</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _current = null;
        }
    }

    private DetectorProfile LoadOrSeed()
    {
        string? json = TryRead();
        if (json is null)
        {
            // Nothing there yet: seed the file with the shipped default, the way EnsureThemesDirectory's
            // callers give a user somewhere to look and something already in it.
            DetectorProfile shipped = DetectorProfile.Default;
            WriteRaw(shipped.ToJson());
            return shipped;
        }

        try
        {
            return DetectorProfile.Parse(json);
        }
        catch (FormatException)
        {
            return DetectorProfile.Default; // a hand-edited file that no longer parses: run on the shipped numbers
        }
    }

    private string? TryRead()
    {
        lock (_gate)
        {
            if (_directory is null)
            {
                return _memory;
            }
        }

        string path = Path.Combine(_directory!, FileName);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteRaw(string json)
    {
        lock (_gate)
        {
            if (_directory is null)
            {
                _memory = json;
                return;
            }
        }

        try
        {
            Directory.CreateDirectory(_directory);
            DemoCacheStore.WriteAtomic(Path.Combine(_directory, FileName), json + "\n");
        }
        catch (IOException)
        {
            // Best-effort seed; the in-memory default still stands for this session.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort seed; the in-memory default still stands for this session.
        }
    }
}
