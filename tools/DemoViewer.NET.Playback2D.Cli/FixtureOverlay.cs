#region

using DemoViewer.NET.Playback2D.Core.Overlay;
using DemoViewer.NET.Playback2D.Pipeline.Overlay;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     Loads a <c>.dvoverlay.json</c> overlay fixture for a render that has no app: the Overlay View's
///     stacked positions, drawn by <c>playback2d.overlay</c>. The twin of <see cref="FixtureQuery" />,
///     with the same two entry points: a flag (<c>render --overlay</c>) and the corpus convention
///     (<c>overlays/&lt;name&gt;.dvoverlay.json</c> beside the entry's scene) for <c>golden</c> and
///     <c>bench</c>.
/// </summary>
internal static class FixtureOverlay
{
    /// <summary>The corpus subdirectory holding overlay fixtures, beside <c>scenes/</c> and <c>queries/</c>.</summary>
    public const string CorpusDirectoryName = "overlays";

    /// <summary>
    ///     The document in an overlay fixture, or null when the file is absent or holds no point. Null
    ///     is what keeps <c>playback2d.overlay</c> out of the layer set rather than naming a layer with
    ///     nothing behind it, the same answer <see cref="FixtureQuery.Load" /> gives.
    /// </summary>
    /// <param name="path">Path to the <c>.dvoverlay.json</c> itself.</param>
    public static OverlayDocument? Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return null;
        }

        if (!path.EndsWith(OverlayFixtureStore.SidecarExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUsageException(
                $"--overlay expects a '{OverlayFixtureStore.SidecarExtension}' fixture, got '{path}'.");
        }

        OverlayDocument document = OverlayFixtureStore.ReadFile(path);
        return document.IsEmpty ? null : document;
    }

    /// <summary>The corpus overlay fixture for an entry, or null when the entry ships none.</summary>
    /// <param name="corpusDirectory">The corpus root.</param>
    /// <param name="entryName">The corpus entry name.</param>
    public static OverlayDocument? ForCorpusEntry(string corpusDirectory, string entryName) =>
        Load(Path.Combine(corpusDirectory, CorpusDirectoryName,
            entryName + OverlayFixtureStore.SidecarExtension));
}
