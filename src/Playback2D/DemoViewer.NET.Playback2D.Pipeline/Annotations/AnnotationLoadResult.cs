#region

using DemoViewer.NET.Playback2D.Core.Annotations;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Annotations;

/// <summary>Where a document was (or would be) persisted.</summary>
public enum AnnotationStoreLocation
{
    /// <summary>Nowhere: the demo directory is not writable and there is no app-data root (WASM).</summary>
    None,

    /// <summary><c>&lt;demo&gt;.dvann.json</c>, beside the demo.</summary>
    DemoSidecar,

    /// <summary><c>&lt;appDataRoot&gt;/annotations/&lt;sha256&gt;.dvann.json</c>.</summary>
    AppData
}

/// <summary>
///     The outcome of a load. Carries the two mismatch flags rather than throwing, because neither is an
///     error the user can act on mid-session and both have a correct degraded behaviour.
/// </summary>
/// <param name="Elements">The loaded elements; empty when there was nothing to load.</param>
/// <param name="Location">Where the document was read from.</param>
/// <param name="Path">The file that was read, or null.</param>
/// <param name="DemoMismatch">
///     The sidecar's demo hash names a different demo. The file is IGNORED and never overwritten: it
///     belongs to someone else's demo that happens to share a path.
/// </param>
/// <param name="ClockMismatch">
///     The sidecar was authored against a different parse. Everything still loads: static elements are
///     unaffected, and the UI warns that time anchors may be off rather than discarding them.
/// </param>
/// <param name="SchemaVersion">The schema version the file declared.</param>
public sealed record AnnotationLoadResult(
    IReadOnlyList<AnnotationElement> Elements,
    AnnotationStoreLocation Location,
    string? Path,
    bool DemoMismatch,
    bool ClockMismatch,
    int SchemaVersion)
{
    /// <summary>Nothing on disk, nothing wrong.</summary>
    /// <param name="location">Where the store would have looked.</param>
    /// <param name="path">Where the store would have looked, or null.</param>
    public static AnnotationLoadResult Empty(AnnotationStoreLocation location, string? path) =>
        new([], location, path, false, false, AnnotationStore.SchemaVersion);
}
