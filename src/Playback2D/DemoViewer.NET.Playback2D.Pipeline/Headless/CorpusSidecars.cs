#region

using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Overlay;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Overlay;
using DemoViewer.NET.Playback2D.Pipeline.Query;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Headless;

/// <summary>
///     The corpus's sidecar conventions: <c>annotations/</c>, <c>zones/</c>, <c>queries/</c> and
///     <c>overlays/</c> beside <c>scenes/</c>, each file named for the entry it belongs to.
///     <para>
///         By convention rather than by manifest field: the manifest already keys scenes and goldens off
///         the entry name, and a path column that could only ever hold one value is a place for the two
///         to disagree.
///     </para>
///     <para>
///         Every file is read by the production store, so a golden's ink and one the app wrote take one
///         code path. A sidecar a caller named outright is read by the same <c>Load</c> methods; only
///         the suffix check that belongs to a command-line flag sits outside.
///     </para>
/// </summary>
internal static class CorpusSidecars
{
    /// <summary>The subdirectory holding <c>.dvann.json</c> ink sidecars.</summary>
    public const string AnnotationsDirectory = "annotations";

    /// <summary>The subdirectory holding <c>.zones.json</c> overlays.</summary>
    public const string ZonesDirectory = "zones";

    /// <summary>The subdirectory holding <c>.dvquery.json</c> fixtures.</summary>
    public const string QueriesDirectory = "queries";

    /// <summary>The subdirectory holding <c>.dvoverlay.json</c> fixtures.</summary>
    public const string OverlaysDirectory = "overlays";

    /// <summary>The ink sidecar for an entry, or null when the entry ships none.</summary>
    /// <param name="corpusDirectory">The corpus root.</param>
    /// <param name="entryName">The corpus entry name.</param>
    public static AnnotationSession? Ink(string corpusDirectory, string entryName) =>
        LoadInk(Path.Combine(corpusDirectory, AnnotationsDirectory,
            entryName + AnnotationStore.SidecarExtension));

    /// <summary>The zones overlay path for an entry when the file exists, else null.</summary>
    /// <param name="corpusDirectory">The corpus root.</param>
    /// <param name="entryName">The corpus entry name.</param>
    public static string? ZonesOverlay(string corpusDirectory, string entryName)
    {
        string path = Path.Combine(corpusDirectory, ZonesDirectory,
            entryName + ZoneAssetPipeline.OverlaySuffix);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The query fixture for an entry, or null when the entry ships none.</summary>
    /// <param name="corpusDirectory">The corpus root.</param>
    /// <param name="entryName">The corpus entry name.</param>
    public static QueryCanvasDocument? Query(string corpusDirectory, string entryName) =>
        LoadQuery(Path.Combine(corpusDirectory, QueriesDirectory,
            entryName + QueryFixtureStore.SidecarExtension));

    /// <summary>The overlay fixture for an entry, or null when the entry ships none.</summary>
    /// <param name="corpusDirectory">The corpus root.</param>
    /// <param name="entryName">The corpus entry name.</param>
    public static OverlayDocument? Overlay(string corpusDirectory, string entryName) =>
        LoadOverlay(Path.Combine(corpusDirectory, OverlaysDirectory,
            entryName + OverlayFixtureStore.SidecarExtension));

    /// <summary>
    ///     The session in a <c>.dvann.json</c> sidecar, or null when the file is absent, empty, or holds
    ///     nothing this build can parse. Null is what keeps <c>playback2d.annotations</c> out of the
    ///     layer set rather than naming a layer with nothing behind it.
    ///     <para>
    ///         <c>ExportCommand.LoadInkAsync</c> cannot be reused for a render with no demo: it hashes
    ///         the <c>.dem</c> and refuses a sidecar whose recorded SHA does not match. A fixture has no
    ///         <c>.dem</c> to hash, so this asks the store the same question with the identity check
    ///         neutralised. An empty key makes <see cref="AnnotationStore" />'s mismatch branch
    ///         unreachable (<c>actual.Length &gt; 0</c>), and the file is read as a committed corpus
    ///         asset whose provenance is the git history, not a hash.
    ///     </para>
    /// </summary>
    /// <param name="sidecarPath">Path to the <c>.dvann.json</c> itself.</param>
    public static AnnotationSession? LoadInk(string sidecarPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sidecarPath);

        if (!File.Exists(sidecarPath) ||
            !sidecarPath.EndsWith(AnnotationStore.SidecarExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // AnnotationStore.ResolvePath appends the extension, so it is handed the stem.
        string stem = sidecarPath[..^AnnotationStore.SidecarExtension.Length];

        // A fixture is one frame, so the clock identity is only ever used to decide whether to FLAG a
        // mismatch (AnnotationStore records it and reads the elements either way). 64 tick and a zero
        // frame count are the neutral values; nothing downstream reads them for a single-frame render.
        ClockIdentity clock = new(ClockIdentity.DvFrameClock, 64, 0, 0, 0);

        AnnotationLoadResult loaded = new AnnotationStore(null, static _ => "")
            .LoadAsync(stem, clock).GetAwaiter().GetResult();

        if (loaded.Elements.Count == 0)
        {
            return null;
        }

        AnnotationDocument document = new();
        document.Reset(loaded.Elements);
        return new AnnotationSession(document);
    }

    /// <summary>
    ///     The document in a <c>.dvquery.json</c> fixture, or null when the file is absent or holds no
    ///     placed token.
    /// </summary>
    /// <param name="path">Path to the <c>.dvquery.json</c> itself.</param>
    public static QueryCanvasDocument? LoadQuery(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path) ||
            !path.EndsWith(QueryFixtureStore.SidecarExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        QueryCanvasDocument document = QueryFixtureStore.ReadFile(path);
        return document.PlacedCount == 0 ? null : document;
    }

    /// <summary>
    ///     The document in a <c>.dvoverlay.json</c> fixture, or null when the file is absent or holds no
    ///     point.
    /// </summary>
    /// <param name="path">Path to the <c>.dvoverlay.json</c> itself.</param>
    public static OverlayDocument? LoadOverlay(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path) ||
            !path.EndsWith(OverlayFixtureStore.SidecarExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        OverlayDocument document = OverlayFixtureStore.ReadFile(path);
        return document.IsEmpty ? null : document;
    }
}
