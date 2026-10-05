#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.Services.Provenance;

/// <summary>
///     The store-backed <see cref="IDemoProvenanceSource" />: the override from <c>teams.json</c> when
///     one names the demo, else <see cref="DemoProvenanceHeuristic" /> over the library row's clan tags and
///     source kind and Team Identity's assignment. Nothing is persisted here; every answer is a lookup
///     over two in-memory indexes, so there is no cache to invalidate.
/// </summary>
public sealed class DemoProvenanceSource : IDemoProvenanceSource, IDisposable
{
    private readonly IExtensionLibrary _library;
    private readonly Action<Action> _post;
    private readonly TeamIdentityService _teams;
    private bool _disposed;

    /// <param name="library">The demo library: clan tags, source kind and the hash-to-path bridge.</param>
    /// <param name="teams">Team Identity: the assignment the default reads, and the override store.</param>
    /// <param name="post">Marshals <see cref="Changed" /> onto the UI thread; defaults to synchronous.</param>
    public DemoProvenanceSource(IExtensionLibrary library, TeamIdentityService teams, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(teams);
        _library = library;
        _teams = teams;
        _post = post ?? (action => action());
        _teams.Changed += OnTeamsChanged;
        _library.Changed += OnLibraryChanged;
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
        _library.Changed -= OnLibraryChanged;
    }

    /// <inheritdoc />
    public string? LabelFor(string sha256)
    {
        if (string.IsNullOrEmpty(sha256))
        {
            return null;
        }

        if (_library.FindBySha256(sha256) is { } entry)
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

            labels[sha] = _library.FindBySha256(sha) is { } entry
                ? Resolve(entry, overrides).Label
                : overrides.FirstOrDefault(o => string.Equals(o.DemoSha256, sha, StringComparison.Ordinal))?.Label;
        }

        return labels;
    }

    /// <inheritdoc />
    public DemoProvenance? Resolve(string demoPath) =>
        _library.Find(demoPath) is { } entry ? Resolve(entry, _teams.ProvenanceOverrides) : null;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, DemoProvenance> ResolveAll(IEnumerable<string> demoPaths)
    {
        ArgumentNullException.ThrowIfNull(demoPaths);
        IReadOnlyList<ProvenanceOverride> overrides = _teams.ProvenanceOverrides;
        Dictionary<string, DemoProvenance> resolved = new(StringComparer.Ordinal);
        foreach (string path in demoPaths)
        {
            if (!resolved.ContainsKey(path) && _library.Find(path) is { } entry)
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
    /// <param name="entry">The library row.</param>
    /// <param name="assignment">Team Identity's assignment, or null when the demo is unknown or unclusterable.</param>
    public static ProvenanceInputs InputsFor(LibraryDemo entry, TeamAssignment? assignment)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new ProvenanceInputs(
            !string.IsNullOrWhiteSpace(entry.CtClan) && !string.IsNullOrWhiteSpace(entry.TClan),
            SourceKindOf(entry),
            assignment?.Source ?? OurSideSource.None,
            assignment?.OpponentTeamId is not null);
    }

    /// <summary>
    ///     The row's effective source kind (<see cref="TeamSourcePolicy.EffectiveKind" />): the kind tier 2
    ///     stored, else the classifier over the cached server name, with a FACEIT server read as FACEIT, so
    ///     no re-index is needed to label an old library.
    /// </summary>
    /// <param name="entry">The library row.</param>
    public static DemoSourceKind SourceKindOf(LibraryDemo entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return TeamSourcePolicy.EffectiveKind(entry.SourceKind, entry.Server);
    }

    private DemoProvenance Resolve(LibraryDemo entry, IReadOnlyList<ProvenanceOverride> overrides)
    {
        string key = DemoKeys.StableKey(entry.FilePath);
        string? pinned = overrides.FirstOrDefault(o => TeamIdentityService.Matches(o, key, entry.Sha256))?.Label;
        string? fallback = DemoProvenanceHeuristic.Default(InputsFor(entry, _teams.GetAssignment(entry.FilePath)));
        return new DemoProvenance(entry.FilePath, entry.Sha256, pinned ?? fallback, fallback,
            pinned is not null ? ProvenanceOrigin.Override
            : fallback is not null ? ProvenanceOrigin.Heuristic
            : ProvenanceOrigin.None);
    }

    private void OnTeamsChanged() => RaiseChanged();

    private void OnLibraryChanged(LibraryChange change) => RaiseChanged();

    private void RaiseChanged() => _post(() => Changed?.Invoke());
}
