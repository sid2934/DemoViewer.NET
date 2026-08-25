#region

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;

/// <summary>
///     The middle rung of the ffmpeg ladder: an <b>optional, explicitly consented</b> download of a
///     pinned LGPL build, verified by SHA-256 before a byte of it is extracted.
///     <para>
///         <b>Why LGPL (plan D9).</b> WebM/VP9 — the export default — is present in an LGPL build; H.264
///         is not. Downloading the LGPL variant keeps the redistribution story trivial: the app ships no
///         ffmpeg, fetches a build whose licence it shows the user first, and links that build's source.
///         A user who wants MP4/H.264 installs a GPL ffmpeg themselves and the <c>PATH</c> rung finds it.
///     </para>
///     <para>
///         <b>Every failure degrades, none crashes.</b> A 404 on the pinned asset, a hash mismatch, a
///         declined consent and a cancelled transfer all end with no partial file on disk and a
///         not-found <see cref="FfmpegLocation" /> or a <see cref="FfmpegAcquisitionException" /> whose
///         message is user-facing; the caller then falls through to the GIF floor.
///     </para>
/// </summary>
public static class FfmpegAcquisition
{
    /// <summary>The BtbN build project — source, build scripts and the release the pin points at.</summary>
    public const string SourceUrl = "https://github.com/BtbN/FFmpeg-Builds";

    /// <summary>The licence every offered build carries.</summary>
    public const string LicenseName = "LGPL-2.1";

    /// <summary>
    ///     The dated, immutable release tag the pins below belong to. Re-check it each release
    ///     (plan R5): BtbN re-points its <c>-latest-</c> assets, which is exactly why nothing here uses
    ///     one.
    /// </summary>
    public const string ReleaseTag = "autobuild-2026-08-24-13-10";

    // Digests read from the GitHub release API's own `digest` field for this tag on 2026-08-25 and
    // recorded here verbatim. Verified against the downloaded bytes before extraction, so a re-tag, a
    // CDN fault or a tampered mirror fails closed.
    //
    // WINDOWS ONLY, deliberately. BtbN publishes its Linux builds as .tar.xz, and neither .NET nor this
    // repository has an xz decoder; taking a compression dependency to unpack a binary that every Linux
    // distribution already packages (`apt install ffmpeg`) is the wrong trade. Linux and macOS get
    // install instructions and the GIF floor — see plan deviation 3. The table is data: a Linux row goes
    // in the day an xz decoder earns its place.
    private const string WindowsAsset =
        "ffmpeg-n9.0.1-6-g9d4ca21220-win64-lgpl-9.0.zip";

    private const string WindowsSha256 =
        "c7f6ae32a0c2e36b21091fb0216b905f09521b6ba7d5c9b3a205fbfd76061e73";

    private const long WindowsBytes = 147_008_044;

    /// <summary>
    ///     The offer for this machine, or null when there is nothing pinned for it (macOS, Linux,
    ///     browser, any non-x64 architecture). A null offer is not an error — it means the ladder skips
    ///     straight from <see cref="FfmpegLocator" /> to the GIF floor, and the UI shows install
    ///     instructions instead of a Download button.
    /// </summary>
    /// <param name="targetDirectory">Where the binaries would be written.</param>
    public static FfmpegDownloadOffer? Offer(string targetDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetDirectory);

        if (!OperatingSystem.IsWindows() || RuntimeArchitectureIsNotX64())
        {
            return null;
        }

        return new FfmpegDownloadOffer(
            $"{SourceUrl}/releases/download/{ReleaseTag}/{WindowsAsset}",
            WindowsSha256,
            ReleaseTag,
            SourceUrl,
            LicenseName,
            WindowsBytes,
            targetDirectory);
    }

    /// <summary>
    ///     Downloads, verifies, seeks consent, and extracts <c>ffmpeg</c> + <c>ffprobe</c> into the
    ///     offer's target directory, then re-locates.
    /// </summary>
    /// <param name="offer">What <see cref="Offer" /> returned.</param>
    /// <param name="consent">
    ///     Shown the offer and the <c>LICENSE.txt</c> read out of the downloaded archive; returning
    ///     false aborts and leaves nothing on disk. Called <b>after</b> the hash check, so the licence
    ///     the user reads is the licence inside the bytes that were verified.
    /// </param>
    /// <param name="progress">Download fraction in [0,1]; null to skip reporting.</param>
    /// <param name="http">An injected client (tests supply a fake handler); null builds and disposes one.</param>
    /// <param name="ct">Cancels the transfer and the extraction; leaves no partial file.</param>
    /// <exception cref="FfmpegAcquisitionException">Download failed, hash mismatched, or the archive was wrong.</exception>
    public static async Task<FfmpegLocation> AcquireAsync(
        FfmpegDownloadOffer offer,
        Func<FfmpegDownloadOffer, string, CancellationToken, Task<bool>> consent,
        IProgress<double>? progress,
        HttpClient? http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(consent);

        Directory.CreateDirectory(offer.TargetDirectory);

        // Download to *.part and only ever rename on success: a cancelled or failed acquisition must
        // never leave something that Locate() would then report as a working install.
        string archivePath = Path.Combine(offer.TargetDirectory, "ffmpeg-download.part");
        HttpClient client = http ?? new HttpClient();
        bool ownsClient = http is null;

        try
        {
            string actual = await DownloadAndHashAsync(client, offer, archivePath, progress, ct)
                .ConfigureAwait(false);

            if (!string.Equals(actual, offer.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new FfmpegAcquisitionException(
                    "The downloaded ffmpeg archive did not match its pinned checksum, so it was discarded. " +
                    $"Expected {offer.ArchiveSha256}, got {actual}. Install ffmpeg yourself, or export GIF.");
            }

            string license = ReadLicense(archivePath);
            if (!await consent(offer, license, ct).ConfigureAwait(false))
            {
                return FfmpegLocation.NotFound;
            }

            ct.ThrowIfCancellationRequested();
            Extract(archivePath, offer.TargetDirectory);
        }
        catch (HttpRequestException ex)
        {
            throw new FfmpegAcquisitionException(
                $"Could not download the pinned ffmpeg build ({offer.ReleaseTag}): {ex.Message}. " +
                "Install ffmpeg yourself, or export GIF.", ex);
        }
        finally
        {
            TryDelete(archivePath);
            if (ownsClient)
            {
                client.Dispose();
            }
        }

        return FfmpegLocator.Locate(offer.TargetDirectory);
    }

    private static async Task<string> DownloadAndHashAsync(HttpClient client, FfmpegDownloadOffer offer,
        string archivePath, IProgress<double>? progress, CancellationToken ct)
    {
        using HttpResponseMessage response = await client
            .GetAsync(offer.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? offer.ApproxBytes;
        using SHA256 sha = SHA256.Create();

        await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (FileStream destination = new(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0)
                {
                    progress?.Report(Math.Clamp(done / (double)total, 0, 1));
                }
            }

            sha.TransformFinalBlock([], 0, 0);
        }

        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    // BtbN archives carry a single top-level directory holding bin/, doc/, LICENSE.txt and README.txt.
    private static string ReadLicense(string archivePath)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (!entry.FullName.EndsWith("LICENSE.txt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using StreamReader reader = new(entry.Open());
            return reader.ReadToEnd();
        }

        // No licence file is a packaging surprise, not a reason to install silently: say so and let the
        // consent sheet show it to the user, who can then decline.
        return string.Create(CultureInfo.InvariantCulture,
            $"No LICENSE.txt was found inside the archive. The build is published as {LicenseName}; " +
            $"see {SourceUrl}.");
    }

    private static void Extract(string archivePath, string targetDirectory)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);

        bool extractedFfmpeg = ExtractOne(archive, FfmpegLocator.ExecutableName, targetDirectory);
        ExtractOne(archive, FfmpegLocator.ProbeExecutableName, targetDirectory);

        if (!extractedFfmpeg)
        {
            throw new FfmpegAcquisitionException(
                $"The downloaded archive did not contain {FfmpegLocator.ExecutableName}. Nothing was installed.");
        }
    }

    private static bool ExtractOne(ZipArchive archive, string fileName, string targetDirectory)
    {
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            // Match on the leaf name under bin/ rather than a full path: the top-level directory carries
            // the build's version string, which changes with every pin.
            if (!string.Equals(Path.GetFileName(entry.FullName), fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string final = Path.Combine(targetDirectory, fileName);
            string staging = final + ".part";
            entry.ExtractToFile(staging, true);
            File.Move(staging, final, true);
            return true;
        }

        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover *.part is inert — Locate() looks for ffmpeg[.exe], never for this.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    private static bool RuntimeArchitectureIsNotX64() =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            != System.Runtime.InteropServices.Architecture.X64;
}
