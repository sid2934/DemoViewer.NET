#region

using DemoViewer.NET.Playback2D.Pipeline.Assets;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     The corpus convention for a zones overlay: <c>zones/&lt;name&gt;.zones.json</c> beside the
///     entry's scene, the twin of <see cref="FixtureInk" />'s <c>annotations/</c>. By convention rather
///     than by manifest field for the same reason: the manifest already keys everything off the entry
///     name, and a column that could only ever hold one value is a place for the two to disagree.
///     <para>
///         The file itself is read by the production <see cref="ZoneAssetPipeline" />, so a golden's
///         overlay and one the app reads from <c>&lt;config&gt;/zones/</c> take one code path.
///     </para>
/// </summary>
internal static class FixtureZones
{
    /// <summary>The corpus subdirectory holding overlays.</summary>
    public const string CorpusDirectoryName = "zones";

    /// <summary>The overlay path for an entry when the file exists, else null.</summary>
    /// <param name="corpusDirectory">The corpus root.</param>
    /// <param name="entryName">The corpus entry name.</param>
    public static string? ForCorpusEntry(string corpusDirectory, string entryName)
    {
        string path = Path.Combine(corpusDirectory, CorpusDirectoryName,
            entryName + ZoneAssetPipeline.OverlaySuffix);
        return File.Exists(path) ? path : null;
    }
}
