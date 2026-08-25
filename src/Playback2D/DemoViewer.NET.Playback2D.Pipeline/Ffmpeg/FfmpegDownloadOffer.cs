namespace DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;

/// <summary>
///     One pinned, hash-verified ffmpeg build the app may offer to download, and where it would land.
///     <para>
///         Everything a consent sheet needs is here except the licence text itself, which is read out of
///         the downloaded archive rather than vendored into this repository — a copy of a licence that
///         drifts from the binary it covers is worse than no copy.
///     </para>
/// </summary>
/// <param name="Url">The immutable release-asset URL. Never a <c>-latest-</c> asset (plan R5).</param>
/// <param name="ArchiveSha256">Lower-case hex SHA-256 of the archive, verified before extraction.</param>
/// <param name="ReleaseTag">The dated BtbN release tag the asset belongs to.</param>
/// <param name="SourceUrl">Where the build's source and build scripts live.</param>
/// <param name="LicenseName">The licence the build carries — always <c>LGPL-2.1</c> here (plan D9).</param>
/// <param name="ApproxBytes">Archive size, for the progress UI and the "this is a big download" warning.</param>
/// <param name="TargetDirectory">Where <c>ffmpeg</c> and <c>ffprobe</c> will be written.</param>
public sealed record FfmpegDownloadOffer(
    string Url,
    string ArchiveSha256,
    string ReleaseTag,
    string SourceUrl,
    string LicenseName,
    long ApproxBytes,
    string TargetDirectory);

/// <summary>
///     A managed ffmpeg download failed in a way the user should be told about — a 404 on the pinned
///     asset, a SHA-256 mismatch, or an archive missing the binaries it should contain. Every one of
///     these degrades to the GIF floor rather than crashing, so the message is user-facing copy.
/// </summary>
public sealed class FfmpegAcquisitionException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">User-facing explanation.</param>
    public FfmpegAcquisitionException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    /// <param name="message">User-facing explanation.</param>
    /// <param name="innerException">The underlying failure.</param>
    public FfmpegAcquisitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Parameterless form required by the analyzer's exception-shape rule.</summary>
    public FfmpegAcquisitionException() : base("The managed ffmpeg download failed.")
    {
    }
}
