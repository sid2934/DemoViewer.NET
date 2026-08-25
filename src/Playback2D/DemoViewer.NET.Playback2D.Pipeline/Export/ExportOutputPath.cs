namespace DemoViewer.NET.Playback2D.Pipeline.Export;

/// <summary>
///     Prepares the directory an export writes into.
///     <para>
///         Neither rung of the ladder does this for itself: ffmpeg refuses an output path whose parent
///         is missing (<c>Error opening output …: No such file or directory</c>) and ImageSharp's
///         <c>Image.Save(path)</c> throws <see cref="DirectoryNotFoundException" />. Both failures
///         arrive <b>after</b> the whole range has been replayed and rendered, which is minutes of work
///         thrown away for a directory that takes a syscall to make — and it is exactly what
///         <c>dv2d export --out artifacts/playback2d-export/ci-smoke.gif</c> hits on a clean checkout,
///         where <c>artifacts/playback2d-export/</c> does not exist yet.
///     </para>
///     <para>
///         Called from a sink's constructor, so a path that cannot be made fails before the first frame
///         is drawn rather than after the last.
///     </para>
/// </summary>
internal static class ExportOutputPath
{
    /// <summary>Creates the parent directory of <paramref name="outputPath" /> if it is missing.</summary>
    /// <param name="outputPath">The file the sink is about to write. Relative paths are resolved first.</param>
    /// <returns><paramref name="outputPath" />, unchanged, so this can wrap an assignment.</returns>
    public static string EnsureDirectory(string outputPath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));

        // Null only for a root ("C:\"), which is not a file the sink can write anyway — let the open
        // fail with its own message rather than inventing one here.
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return outputPath;
    }
}
