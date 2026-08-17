#region

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DemoViewer.NET.Debugging;

#endregion

namespace DemoViewer.NET.Services;

/// <summary>
///     The persisted projection of a <see cref="GraphBreakpoint" /> — its identity (node name, or the
///     edge source/dest/label/condition-label 4-tuple), its rule-expression condition, and its enabled
///     flag. Transient fields are deliberately not serialized: the runtime <see cref="GraphBreakpoint.Id" />
///     re-mints on load, <see cref="GraphBreakpoint.HitIndices" /> recompute from the new evaluation, and
///     <see cref="GraphBreakpoint.HitCount" /> is session-only (persistent counts are a later phase).
/// </summary>
public sealed record PersistedGraphBreakpoint(
    GraphBreakpointTarget TargetKind,
    string? NodeName,
    string? EdgeSource,
    string? EdgeDest,
    string? EdgeLabel,
    string? EdgeConditionLabel,
    string? Condition,
    bool Enabled)
{
    /// <summary>Captures the persisted fields of a live breakpoint.</summary>
    public static PersistedGraphBreakpoint From(GraphBreakpoint bp) => new(
        bp.TargetKind, bp.NodeName, bp.EdgeSource, bp.EdgeDest,
        bp.EdgeLabel, bp.EdgeConditionLabel, bp.Condition, bp.Enabled);

    /// <summary>Reconstructs a live breakpoint (fresh id; hits recomputed by the host after load).</summary>
    public GraphBreakpoint ToBreakpoint() => new()
    {
        TargetKind = TargetKind,
        NodeName = NodeName,
        EdgeSource = EdgeSource,
        EdgeDest = EdgeDest,
        EdgeLabel = EdgeLabel,
        EdgeConditionLabel = EdgeConditionLabel,
        Condition = Condition,
        Enabled = Enabled
    };
}

/// <summary>
///     Best-effort, <em>per-demo</em> disk persistence for Analysis-graph breakpoints.
///     Mirrors <see cref="BookmarkStore" />: there's no filesystem in the WASM/browser sandbox, so every
///     method short-circuits when <see cref="OperatingSystem.IsBrowser" /> is true (breakpoints stay
///     in-memory there). A runtime check — not a <c>#if BROWSER</c> define — because the same assembly is
///     shared by the desktop and browser hosts.
///     <para>
///         Desktop persists to <c>%AppData%/DemoViewer.NET/GraphBreakpoints.json</c>: a map of
///         <em>demo content key</em> (lowercase hex SHA-256 of the <c>.dem</c> bytes) → the breakpoints set
///         on that demo. Keying on content rather than path means a renamed or re-downloaded-identical demo
///         restores the same breakpoints, and two different demos never collide.
///     </para>
/// </summary>
public sealed class GraphBreakpointStore
{
    // One options instance for BOTH read and write so the string-enum encoding stays symmetric — a
    // converter on only one side would write a file the other side can't parse (→ silent empty load).
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    private readonly string? _path;

    /// <summary>Initializes a new <see cref="GraphBreakpointStore" /> instance.</summary>
    public GraphBreakpointStore()
    {
        if (OperatingSystem.IsBrowser())
        {
            return; // no filesystem on WASM
        }

        _path = AppPaths.GraphBreakpointsFile;
    }

    /// <summary>The lowercase hex SHA-256 of a demo's bytes — its stable content key.</summary>
    public static string ComputeDemoKey(ReadOnlySpan<byte> demoBytes) =>
        Convert.ToHexStringLower(SHA256.HashData(demoBytes));

    /// <summary>The breakpoints persisted for <paramref name="demoKey" />, or empty if none / unavailable.</summary>
    public IReadOnlyList<PersistedGraphBreakpoint> Load(string demoKey) =>
        ReadAll().TryGetValue(demoKey, out List<PersistedGraphBreakpoint>? list) ? list : [];

    /// <summary>
    ///     Persists <paramref name="breakpoints" /> under <paramref name="demoKey" />, leaving every other
    ///     demo's entry on disk untouched. An empty set removes the key entirely, so demos that never had a
    ///     breakpoint don't accumulate. No-op on WASM or on I/O failure (persistence is best-effort).
    /// </summary>
    public void Save(string demoKey, IReadOnlyList<GraphBreakpoint> breakpoints)
    {
        if (_path is null)
        {
            return;
        }

        Dictionary<string, List<PersistedGraphBreakpoint>> merged = Merge(
            ReadAll(), demoKey, breakpoints.Select(PersistedGraphBreakpoint.From).ToList());

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(merged, _options));
        }
        catch
        {
            // Persistence is best-effort; never crash the app on a write failure.
        }
    }

    /// <summary>
    ///     Pure merge: a new map equal to <paramref name="existing" /> with <paramref name="demoKey" />
    ///     set to <paramref name="entries" /> — or removed when <paramref name="entries" /> is empty.
    ///     Neither argument is mutated. Isolated as a static so the per-key replace/remove logic (the
    ///     bug-prone part of "don't clobber other demos") is deterministic and unit-testable.
    /// </summary>
    public static Dictionary<string, List<PersistedGraphBreakpoint>> Merge(
        IReadOnlyDictionary<string, List<PersistedGraphBreakpoint>> existing,
        string demoKey,
        IReadOnlyList<PersistedGraphBreakpoint> entries)
    {
        Dictionary<string, List<PersistedGraphBreakpoint>> result = new(StringComparer.Ordinal);
        foreach ((string key, List<PersistedGraphBreakpoint> value) in existing)
        {
            if (key != demoKey)
            {
                result[key] = value;
            }
        }

        if (entries.Count > 0)
        {
            result[demoKey] = entries.ToList();
        }

        return result;
    }

    private Dictionary<string, List<PersistedGraphBreakpoint>> ReadAll()
    {
        if (_path is null || !File.Exists(_path))
        {
            return new Dictionary<string, List<PersistedGraphBreakpoint>>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, List<PersistedGraphBreakpoint>>>(
                File.ReadAllText(_path), _options) ?? new Dictionary<string, List<PersistedGraphBreakpoint>>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, List<PersistedGraphBreakpoint>>(StringComparer.Ordinal); // best-effort restore
        }
    }
}
