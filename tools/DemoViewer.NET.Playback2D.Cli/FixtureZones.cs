#region

using DemoViewer.NET.Playback2D.Pipeline.Headless;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     The corpus convention for a zones overlay, as the tool names it. The convention itself is
///     <see cref="CorpusSidecars.ZonesOverlay" />.
/// </summary>
internal static class FixtureZones
{
    /// <summary>The corpus subdirectory holding overlays.</summary>
    public const string CorpusDirectoryName = CorpusSidecars.ZonesDirectory;

    /// <summary>The overlay path for an entry when the file exists, else null.</summary>
    /// <param name="corpusDirectory">The corpus root.</param>
    /// <param name="entryName">The corpus entry name.</param>
    public static string? ForCorpusEntry(string corpusDirectory, string entryName) =>
        CorpusSidecars.ZonesOverlay(corpusDirectory, entryName);
}
