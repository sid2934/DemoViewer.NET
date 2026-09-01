namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     The process exit codes (C1 decision 7). The split that matters is <see cref="GateFailure" />
///     versus everything else: CI treats 4 as "the change is bad" and every other non-zero code as
///     "the run is broken", so a missing asset root can never be mistaken for a pixel regression.
/// </summary>
internal enum ExitCode
{
    /// <summary>Everything worked.</summary>
    Success = 0,

    /// <summary>Bad or unknown arguments, including an unknown option, which is never a no-op.</summary>
    Usage = 1,

    /// <summary>A required input is absent: demo, fixture, corpus, asset root, ffmpeg.</summary>
    InputMissing = 2,

    /// <summary>Decode, render or encode threw.</summary>
    RuntimeFailure = 3,

    /// <summary>A gate failed: a golden mismatched, or a measured budget was exceeded.</summary>
    GateFailure = 4,

    /// <summary>Ctrl+C.</summary>
    Cancelled = 5,

    /// <summary>The requested environment is unavailable (e.g. <c>--gpu</c> with a failed probe).</summary>
    EnvironmentUnavailable = 6
}

/// <summary>Conversion helpers for <see cref="ExitCode" />.</summary>
internal static class ExitCodeExtensions
{
    /// <summary>The integer the process returns.</summary>
    /// <param name="code">The code to convert.</param>
    public static int ToInt(this ExitCode code) => (int)code;
}
