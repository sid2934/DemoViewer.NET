namespace DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;

/// <summary>Where an ffmpeg install was resolved from.</summary>
public enum FfmpegOrigin
{
    /// <summary>No install found.</summary>
    None,

    /// <summary>Resolved from the system <c>PATH</c> — an install the user chose.</summary>
    SystemPath,

    /// <summary>The app-managed drop-in / downloaded install.</summary>
    Managed
}

/// <summary>The result of an ffmpeg lookup.</summary>
/// <param name="Found">True when an ffmpeg binary was located.</param>
/// <param name="Directory">The directory containing it, when found.</param>
/// <param name="Origin">Where it came from.</param>
public readonly record struct FfmpegLocation(bool Found, string? Directory, FfmpegOrigin Origin)
{
    /// <summary>The canonical "nothing here" result.</summary>
    public static FfmpegLocation NotFound { get; } = new(false, null, FfmpegOrigin.None);
}

/// <summary>
///     Locates an ffmpeg binary without launching one.
///     <para>
///         The scan body used to live in the App's <c>Services.Dependencies.FfmpegDependency</c>, which
///         depends on <c>AppPaths.ConfigRoot</c> and therefore cannot be reached from a headless
///         Pipeline consumer (<c>dv2d export</c>, the export job service running before any window
///         exists). It moved here and takes the managed directory as an explicit argument;
///         <c>FfmpegDependency.Locate()</c> is now a delegating shim that supplies
///         <c>FfmpegDependency.ManagedDirectory</c>, so its three existing consumers are unchanged.
///     </para>
///     <para>
///         <b>A filesystem scan, never a process launch.</b> The check runs from UI pre-flights and from
///         an export's start path; spawning <c>ffmpeg -version</c> for a presence question is slower and
///         noisier than stat'ing a handful of directories. Never throws.
///     </para>
/// </summary>
public static class FfmpegLocator
{
    /// <summary>The platform's ffmpeg executable file name.</summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <summary>The platform's ffprobe executable file name.</summary>
    public static string ProbeExecutableName => OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

    /// <summary>
    ///     <c>PATH</c> first (a user-chosen install always wins), then
    ///     <paramref name="managedDirectory" />. Never throws.
    /// </summary>
    /// <param name="managedDirectory">
    ///     The app-managed install directory, or null when the caller has none (a browser head, a test).
    /// </param>
    public static FfmpegLocation Locate(string? managedDirectory)
    {
        if (OperatingSystem.IsBrowser())
        {
            // No filesystem, no processes. Answering "found" here would let a caller build a sink
            // that cannot exist.
            return FfmpegLocation.NotFound;
        }

        string exe = ExecutableName;

        if (FindOnPath(exe) is { } pathDir)
        {
            return new FfmpegLocation(true, pathDir, FfmpegOrigin.SystemPath);
        }

        if (!string.IsNullOrEmpty(managedDirectory) && FileExists(Path.Combine(managedDirectory, exe)))
        {
            return new FfmpegLocation(true, managedDirectory, FfmpegOrigin.Managed);
        }

        return FfmpegLocation.NotFound;
    }

    /// <summary>The first <c>PATH</c> directory containing this executable, or null.</summary>
    /// <param name="exeName">The file name to look for.</param>
    public static string? FindOnPath(string exeName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = dir.Trim();
            if (FileExists(Path.Combine(trimmed, exeName)))
            {
                return trimmed;
            }
        }

        return null;
    }

    // An unparseable PATH entry (illegal characters, a UNC path the machine cannot reach) must not
    // sink the whole scan.
    private static bool FileExists(string candidate)
    {
        try
        {
            return File.Exists(candidate);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
