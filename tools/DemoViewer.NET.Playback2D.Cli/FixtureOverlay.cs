#region

using DemoViewer.NET.Playback2D.Overlay;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Playback2D.Pipeline.Overlay;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     The <c>--overlay</c> flag's half of a <c>.dvoverlay.json</c> fixture: the Overlay View's stacked
///     positions, drawn by <c>playback2d.overlay</c>. The twin of <see cref="FixtureQuery" />, and the
///     file itself is read by <see cref="CorpusSidecars.LoadOverlay" /> either way it arrives.
/// </summary>
internal static class FixtureOverlay
{
    /// <summary>The corpus subdirectory holding overlay fixtures, beside <c>scenes/</c> and <c>queries/</c>.</summary>
    public const string CorpusDirectoryName = CorpusSidecars.OverlaysDirectory;

    /// <summary>
    ///     The document in an overlay fixture, or null when the file is absent or holds no point. Null
    ///     is what keeps <c>playback2d.overlay</c> out of the layer set rather than naming a layer with
    ///     nothing behind it, the same answer <see cref="FixtureQuery.Load" /> gives.
    /// </summary>
    /// <param name="path">Path to the <c>.dvoverlay.json</c> itself.</param>
    /// <exception cref="CliUsageException">The file exists but is not an overlay fixture.</exception>
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

        return CorpusSidecars.LoadOverlay(path);
    }

    /// <summary>The corpus overlay fixture for an entry, or null when the entry ships none.</summary>
    /// <param name="corpusDirectory">The corpus root.</param>
    /// <param name="entryName">The corpus entry name.</param>
    public static OverlayDocument? ForCorpusEntry(string corpusDirectory, string entryName) =>
        CorpusSidecars.Overlay(corpusDirectory, entryName);
}
