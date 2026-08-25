namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     <c>dv2d export</c> — the CLI front-end to B4's <c>SceneExportSession</c>.
///     <para>
///         <b>Deferred, deliberately.</b> B4 owns <c>ExportRequest</c>, <c>SceneExportSession</c>,
///         <c>FfmpegFrameSink</c>, <c>ManagedGifSink</c> and <c>FfmpegDependency</c>; none of them are in
///         this build. C1's plan (T11, risk R4) says this is the one task blocked on another track and
///         that it must <b>not</b> be answered with a second encoder path in the CLI — a private ffmpeg
///         wrapper here would be the thing B4 then has to delete, and in the meantime it would produce
///         video that does not match the app's export.
///     </para>
///     <para>
///         So the verb exists, is documented, validates nothing, and exits
///         <see cref="ExitCode.EnvironmentUnavailable" /> with the reason. When B4 lands, the body below
///         becomes: flags → <c>ExportRequest</c> → <c>SceneExportSession.RunAsync</c> with a
///         <c>TrackerFrameSource</c> (already shipped, in Pipeline), a sink chosen by <c>--format</c>, and
///         the provider from <c>--cpu/--gpu</c>; Ctrl+C cancels and disposes the sink so ffmpeg is killed.
///     </para>
/// </summary>
internal static class ExportCommand
{
    /// <summary>The message printed until B4 lands. Asserted by test, so it stays discoverable.</summary>
    public const string UnavailableMessage =
        "export requires the B4 export session (SceneExportSession / FfmpegFrameSink / ManagedGifSink), " +
        "which is not in this build. TrackerFrameSource — the frame source it consumes — already ships in " +
        "DemoViewer.NET.Playback2D.Pipeline.Frames.";

    /// <summary>Runs the command.</summary>
    /// <param name="args">The parsed arguments.</param>
    /// <param name="ct">Cancels the export.</param>
    public static Task<ExitCode> RunAsync(CliArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ct.ThrowIfCancellationRequested();

        throw new BackendUnavailableException(UnavailableMessage);
    }
}
