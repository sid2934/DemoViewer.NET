#region

using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Headless;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     The <c>--ink</c> flag's half of a <c>.dvann.json</c> sidecar: assert the suffix the caller typed,
///     then hand the file to <see cref="CorpusSidecars.LoadInk" />, which is also what the corpus
///     convention reads.
/// </summary>
internal static class FixtureInk
{
    /// <summary>The corpus subdirectory holding sidecars, beside <c>scenes/</c> and <c>goldens/</c>.</summary>
    public const string CorpusDirectoryName = CorpusSidecars.AnnotationsDirectory;

    /// <summary>
    ///     The session for a sidecar, or null when the file is absent, empty, or holds nothing this build
    ///     can parse. Null is the same answer <c>ExportCommand</c> gives, and for the same reason: a
    ///     caller must be able to keep <c>playback2d.annotations</c> out of the layer set rather than
    ///     name a layer with nothing behind it.
    /// </summary>
    /// <param name="sidecarPath">Path to the <c>.dvann.json</c> itself.</param>
    /// <exception cref="CliUsageException">The file exists but is not a sidecar.</exception>
    public static AnnotationSession? Load(string sidecarPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sidecarPath);

        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        // Asserting the suffix rather than trimming blindly means `--ink some.png` is refused with a
        // clear error instead of silently mishandled.
        if (!sidecarPath.EndsWith(AnnotationStore.SidecarExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUsageException(
                $"--ink expects a '{AnnotationStore.SidecarExtension}' sidecar, got '{sidecarPath}'.");
        }

        return CorpusSidecars.LoadInk(sidecarPath);
    }

    /// <summary>The corpus sidecar for an entry, or null when the entry ships none.</summary>
    /// <param name="corpusDirectory">The corpus root.</param>
    /// <param name="entryName">The corpus entry name.</param>
    public static AnnotationSession? ForCorpusEntry(string corpusDirectory, string entryName) =>
        CorpusSidecars.Ink(corpusDirectory, entryName);
}
