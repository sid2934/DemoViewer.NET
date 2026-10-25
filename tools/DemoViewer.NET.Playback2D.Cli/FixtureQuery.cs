#region

using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Playback2D.Pipeline.Query;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     The <c>--query</c> flag's half of a <c>.dvquery.json</c> fixture: the Query Canvas's placed
///     tokens, drawn by <c>playback2d.query</c>. The twin of <see cref="FixtureInk" />, and the file
///     itself is read by <see cref="CorpusSidecars.LoadQuery" /> either way it arrives.
/// </summary>
internal static class FixtureQuery
{
    /// <summary>The corpus subdirectory holding query fixtures, beside <c>scenes/</c> and <c>annotations/</c>.</summary>
    public const string CorpusDirectoryName = CorpusSidecars.QueriesDirectory;

    /// <summary>
    ///     The document in a query fixture, or null when the file is absent or holds no token. Null is
    ///     what keeps <c>playback2d.query</c> out of the layer set rather than naming a layer with nothing
    ///     behind it, the same answer <see cref="FixtureInk.Load" /> gives.
    /// </summary>
    /// <param name="path">Path to the <c>.dvquery.json</c> itself.</param>
    /// <exception cref="CliUsageException">The file exists but is not a query fixture.</exception>
    public static QueryCanvasDocument? Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return null;
        }

        if (!path.EndsWith(QueryFixtureStore.SidecarExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUsageException(
                $"--query expects a '{QueryFixtureStore.SidecarExtension}' fixture, got '{path}'.");
        }

        return CorpusSidecars.LoadQuery(path);
    }

    /// <summary>The corpus query fixture for an entry, or null when the entry ships none.</summary>
    /// <param name="corpusDirectory">The corpus root.</param>
    /// <param name="entryName">The corpus entry name.</param>
    public static QueryCanvasDocument? ForCorpusEntry(string corpusDirectory, string entryName) =>
        CorpusSidecars.Query(corpusDirectory, entryName);
}
