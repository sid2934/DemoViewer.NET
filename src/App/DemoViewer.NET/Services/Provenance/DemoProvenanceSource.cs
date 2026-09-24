#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.Services.Provenance;

/// <summary>
///     The store-backed <see cref="IDemoProvenanceSource" />: the override from <c>teams.json</c> when
///     one names the demo, else <see cref="DemoProvenanceHeuristic" /> over the cache row's clan tags and
///     source kind and Team Identity's assignment. Nothing is persisted here; every answer is a lookup
///     over two in-memory indexes, so there is no cache to invalidate.
/// </summary>
public sealed class DemoProvenanceSource : IDemoProvenanceSource, IDisposable
{
    private readonly DemoCacheStore _demoCache;
    private readonly Action<Action> _post;
    private readonly TeamIdentityService _teams;
    private bool _disposed;

    /// <param name="demoCache">The unified demo cache: clan tags, source kind and the hash-to-path bridge.</param>
    /// <param name="teams">Team Identity: the assignment the default reads, and the override store.</param>
    /// <param name="post">Marshals <see cref="Changed" /> onto the UI thread; defaults to synchronous.</param>
    public DemoProvenanceSource(DemoCacheStore demoCache, TeamIdentityService teams, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        ArgumentNullException.ThrowIfNull(teams);
        _demoCache = demoCache;
        _teams = teams;
        _post = post ?? (action => action());
        _teams.Changed += OnTeamsChanged;
        _demoCache.Changed += OnCacheChanged;
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _teams.Changed -= OnTeamsChanged;
        _demoCache.Changed -= OnCacheChanged;
    }

    /// <inheritdoc />
    public string? LabelFor(string sha256)
    {
        if (string.IsNullOrEmpty(sha256))
        {
            return null;
        }

        if (_demoCache.TryGetIndexBySha256(sha256) is { } entry)
        {
            return Resolve(entry, _teams.ProvenanceOverrides).Label;
        }

        // A pin outlives the demo leaving the library: the hash still names it, and a tag document
        // joined on that hash still wants the answer the user gave.
        return _teams.ProvenanceOverrides.FirstOrDefault(o => string.Equals(o.DemoSha256, sha256, StringComparison.Ordinal))?.Label;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> LabelsFor(IEnumerable<string> sha256s)
    {
        ArgumentNullException.ThrowIfNull(sha256s);
        IReadOnlyList<ProvenanceOverride> overrides = _teams.ProvenanceOverrides;
        Dictionary<string, string?> labels = new(StringComparer.Ordinal);
        foreach (string sha in sha256s)
        {
            if (string.IsNullOrEmpty(sha) || labels.ContainsKey(sha))
            {
                continue;
            }

            labels[sha] = _demoCache.TryGetIndexBySha256(sha) is { } entry
                ? Resolve(entry, overrides).Label
                : overrides.FirstOrDefault(o => string.Equals(o.DemoSha256, sha, StringComparison.Ordinal))?.Label;
        }

        return labels;
    }

    /// <inheritdoc />
    public DemoProvenance? Resolve(string demoPath) =>
        _demoCache.TryGetIndex(demoPath) is { } entry ? Resolve(entry, _teams.ProvenanceOverrides) : null;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, DemoProvenance> ResolveAll(IEnumerable<string> demoPaths)
    {
        ArgumentNullException.ThrowIfNull(demoPaths);
        IReadOnlyList<ProvenanceOverride> overrides = _teams.ProvenanceOverrides;
        Dictionary<string, DemoProvenance> resolved = new(StringComparer.Ordinal);
        foreach (string path in demoPaths)
        {
            if (!resolved.ContainsKey(path) && _demoCache.TryGetIndex(path) is { } entry)
            {
                resolved[path] = Resolve(entry, overrides);
            }
        }

        return resolved;
    }

    /// <summary>
    ///     The heuristic's inputs for a cache row and its assignment. Public so a test can pin what the
    ///     source reads without a service.
    /// </summary>
    /// <param name="entry">The index row.</param>
    /// <param name="assignment">Team Identity's assignment, or null when the demo is unknown or unclusterable.</param>
    public static ProvenanceInputs InputsFor(DemoCacheIndexEntry entry, TeamAssignment? assignment)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new ProvenanceInputs(
            !string.IsNullOrWhiteSpace(entry.CtClan) && !string.IsNullOrWhiteSpace(entry.TClan),
            SourceKindOf(entry),
            assignment?.Source ?? OurSideSource.None,
            assignment?.OpponentTeamId is not null);
    }

    /// <summary>
    ///     The engine classifier's verdict on the row: the kind tier 2 stored, else, for a row written
    ///     before the field existed, the classifier over the cached server name alone (its own fallback
    ///     when the client name is absent), so no re-index is needed to label an old library.
    /// </summary>
    /// <param name="entry">The index row.</param>
    public static DemoSourceKind SourceKindOf(DemoCacheIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Enum.TryParse(entry.SourceKind, out DemoSourceKind kind)
            ? kind
            : DemoSourceClassifier.Classify(entry.Server ?? "", "", "", 0).SourceKind;
    }

    private DemoProvenance Resolve(DemoCacheIndexEntry entry, IReadOnlyList<ProvenanceOverride> overrides)
    {
        string key = DemoCacheStore.StableKey(entry.Path);
        string? pinned = overrides.FirstOrDefault(o => TeamIdentityService.Matches(o, key, entry.Sha256))?.Label;
        string? fallback = DemoProvenanceHeuristic.Default(InputsFor(entry, _teams.GetAssignment(entry.Path)));
        return new DemoProvenance(entry.Path, entry.Sha256, pinned ?? fallback, fallback,
            pinned is not null ? ProvenanceOrigin.Override
            : fallback is not null ? ProvenanceOrigin.Heuristic
            : ProvenanceOrigin.None);
    }

    private void OnTeamsChanged() => RaiseChanged();

    private void OnCacheChanged(string? path) => RaiseChanged();

    private void RaiseChanged() => _post(() => Changed?.Invoke());
}
