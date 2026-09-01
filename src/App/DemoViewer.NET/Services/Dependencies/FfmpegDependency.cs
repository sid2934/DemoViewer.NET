#region

using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;

#endregion

namespace DemoViewer.NET.Services.Dependencies;

/// <summary>
///     Locates the ffmpeg the reel pipeline needs. DemoViewer never invokes ffmpeg itself; it is
///     a runtime requirement of the CSVG capture/concat path, but CSVG resolves it from
///     <c>PATH</c> (or an explicit <c>Ffmpeg:BinaryDirectory</c>), so the app can BOTH detect it
///     up front (reel-dialog pre-flight, instead of a raw failure after CS2 launches) and honor a
///     user-populated drop-in folder under the app-data root that <c>CsvgWebHost</c> then points
///     CSVG at.
///     <para>
///         Resolution order mirrors CSVG's own: a system <c>PATH</c> install wins (the user chose
///         it); the drop-in copy under <c>&lt;config&gt;/tools/ffmpeg</c> is the no-PATH-edits
///         fallback the pre-flight instructions describe: the user copies <c>ffmpeg.exe</c> +
///         <c>ffprobe.exe</c> there themselves. WASM has no filesystem and no reel path: always
///         not found.
///     </para>
/// </summary>
public static class FfmpegDependency
{
    /// <summary>
    ///     The drop-in directory (<c>&lt;config&gt;/tools/ffmpeg</c>), or null on WASM.
    ///     <c>CsvgWebHost.ProjectSettings</c> reads the same path when projecting CSVG's config.
    /// </summary>
    public static string? ManagedDirectory =>
        AppPaths.ConfigRoot is { } root ? Path.Combine(root, "tools", "ffmpeg") : null;

    /// <summary>
    ///     Locates ffmpeg (and implicitly ffprobe, installed alongside). Never throws.
    ///     <para>
    ///         <b>
    ///             The scan itself lives in
    ///             <see cref="DemoViewer.NET.Playback2D.Pipeline.Ffmpeg.FfmpegLocator" />
    ///         </b>
    ///         (B4 D14): the
    ///         2D-export path needs the same resolution headlessly, where <see cref="AppPaths" /> does
    ///         not exist. This method stays as the App-facing shim so
    ///         <see cref="FfmpegStatus" />/<see cref="FfmpegSource" /> and this namespace are unchanged
    ///         for the reel dialog, <c>App.axaml.cs</c> and <c>CsvgWebHost</c>.
    ///     </para>
    /// </summary>
    public static FfmpegStatus Locate()
    {
        FfmpegLocation located = FfmpegLocator.Locate(ManagedDirectory);
        return new FfmpegStatus(located.Found, located.Directory, located.Origin switch
        {
            FfmpegOrigin.SystemPath => FfmpegSource.SystemPath,
            FfmpegOrigin.Managed => FfmpegSource.Managed,
            _ => FfmpegSource.None
        });
    }
}

/// <summary>Result of an ffmpeg lookup.</summary>
/// <param name="Found">True when an ffmpeg binary was located.</param>
/// <param name="Directory">The directory containing it, when found.</param>
/// <param name="Source">Where it came from (PATH vs the app-managed install).</param>
public readonly record struct FfmpegStatus(bool Found, string? Directory, FfmpegSource Source);

/// <summary>How an ffmpeg install was resolved.</summary>
public enum FfmpegSource
{
    /// <summary>No install found.</summary>
    None,

    /// <summary>Resolved from the system <c>PATH</c> (CSVG's default resolution).</summary>
    SystemPath,

    /// <summary>The app-managed install under the app-data root (in-app download).</summary>
    Managed
}
