namespace DemoViewer.NET.Services.Provenance;

/// <summary>
///     The read API over demo provenance labels (strat-model.md §5): the one lookup the Strat Record Panel
///     and Search Filters call, keyed by content hash like every user-truth join, plus the path-keyed
///     resolution the Library card renders. Nothing here parses or writes; the override write is
///     <see cref="Teams.TeamIdentityService.SetProvenanceOverride" />.
/// </summary>
public interface IDemoProvenanceSource
{
    /// <summary>The label in force on the demo with this hash, or null when it is unlabeled or unknown.</summary>
    /// <param name="sha256">Lowercase-hex SHA-256 of the demo's bytes.</param>
    string? LabelFor(string sha256);

    /// <summary>The batch form of <see cref="LabelFor" />: one entry per distinct input hash, null for unlabeled or unknown.</summary>
    /// <param name="sha256s">The hashes.</param>
    IReadOnlyDictionary<string, string?> LabelsFor(IEnumerable<string> sha256s);

    /// <summary>The resolved provenance of a demo by path, or null when the cache does not know the demo.</summary>
    /// <param name="demoPath">The demo.</param>
    DemoProvenance? Resolve(string demoPath);

    /// <summary>The batch form of <see cref="Resolve" />, keyed by path; demos the cache does not know are left out.</summary>
    /// <param name="demoPaths">The demos.</param>
    IReadOnlyDictionary<string, DemoProvenance> ResolveAll(IEnumerable<string> demoPaths);

    /// <summary>Raised after anything a label depends on changed: an override, a team assignment, a cache row.</summary>
    event Action? Changed;
}
