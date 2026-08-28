#region

using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     Loads a <c>.dvann.json</c> sidecar for a render that has <b>no demo</b>: a fixture, a corpus
///     entry, a bench run.
///     <para>
///         <c>ExportCommand.LoadInkAsync</c> cannot be reused here because it is built on the demo's own
///         identity: it hashes the <c>.dem</c> and refuses a sidecar whose recorded SHA does not match.
///         A fixture has no <c>.dem</c> to hash, so this asks the store the same question with the
///         identity check neutralised. An empty key makes <c>AnnotationStore</c>'s mismatch branch
///         unreachable (<c>actual.Length &gt; 0</c>), and the file is read as a committed corpus asset
///         whose provenance is the git history, not a hash.
///     </para>
///     <para>
///         Everything else goes through the production store: the same DTO, the same tolerant reader,
///         the same <c>ToElement</c> fence that drops an element this build cannot parse. A sidecar the
///         app wrote and one the corpus ships are read by one code path.
///     </para>
/// </summary>
internal static class FixtureInk
{
    /// <summary>The corpus subdirectory holding sidecars, beside <c>scenes/</c> and <c>goldens/</c>.</summary>
    public const string CorpusDirectoryName = "annotations";

    /// <summary>
    ///     The session for a sidecar, or null when the file is absent, empty, or holds nothing this build
    ///     can parse. Null is the same answer <c>ExportCommand</c> gives, and for the same reason: a
    ///     caller must be able to keep <c>playback2d.annotations</c> out of the layer set rather than
    ///     name a layer with nothing behind it.
    /// </summary>
    /// <param name="sidecarPath">Path to the <c>.dvann.json</c> itself.</param>
    public static AnnotationSession? Load(string sidecarPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sidecarPath);

        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        // AnnotationStore.ResolvePath appends the extension, so it is handed the stem. Asserting the
        // suffix rather than trimming blindly means `--ink some.png` is refused with a clear error
        // instead of silently mishandled.
        if (!sidecarPath.EndsWith(AnnotationStore.SidecarExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUsageException(
                $"--ink expects a '{AnnotationStore.SidecarExtension}' sidecar, got '{sidecarPath}'.");
        }

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
    ///     The corpus sidecar for an entry (<c>annotations/&lt;name&gt;.dvann.json</c>) or null when
    ///     the entry ships none. By convention rather than by manifest field: the manifest already keys
    ///     scenes and goldens off the entry name, and a fourth path column that could only ever hold one
    ///     value is a place for the two to disagree.
    /// </summary>
    /// <param name="corpusDirectory">The corpus root.</param>
    /// <param name="entryName">The corpus entry name.</param>
    public static AnnotationSession? ForCorpusEntry(string corpusDirectory, string entryName) =>
        Load(Path.Combine(corpusDirectory, CorpusDirectoryName,
            entryName + AnnotationStore.SidecarExtension));
}
