#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Export;

#endregion

namespace DemoViewer.NET.Services.Export;

/// <summary>
///     App-facing contract for 2D video export. Deliberately shaped like
///     <see cref="LiveSync.IReelJobService" />: one job at a time, started fire-and-forget, progress on a
///     status chip rather than in a multi-minute modal.
///     <para>
///         Everything reusable lives in Pipeline/Core (<c>SceneExportSession</c>, the sinks, the ffmpeg
///         ladder). What is here is the App's half: the refusal policy, the heavy-job gate, and marshalling
///         status to the UI thread.
///     </para>
/// </summary>
public interface IExportJobService
{
    /// <summary>The current job status (Idle when none has run).</summary>
    ExportJobStatus Status { get; }

    /// <summary>Raised on the UI thread on every status change.</summary>
    event EventHandler<ExportJobStatus>? StatusChanged;

    /// <summary>Starts the background job.</summary>
    /// <param name="request">What to render and where to put it.</param>
    /// <exception cref="ExportRefusedException">A LiveSync session or a reel job holds the machine.</exception>
    /// <exception cref="InvalidOperationException">An export is already running.</exception>
    void Start(Scene2DExportRequest request);

    /// <summary>Cancels the running job: kills ffmpeg, removes the partial file, releases the gate.</summary>
    Task CancelAsync();
}

/// <summary>
///     The App-level hand-off: the Core request plus the App-only bits.
/// </summary>
/// <param name="Core">
///     Size, format, layers, camera and the <b>source-relative</b> frame range. Frame 0 of the source is
///     <paramref name="DemoStartFrame" />; the runner re-stamps the range from the built source's own
///     frame count, so the two can never disagree.
/// </param>
/// <param name="OutputPath">Where the encoded file goes.</param>
/// <param name="DemoPath">The demo being exported, for the status text and diagnostics.</param>
/// <param name="DemoStartFrame">
///     First DEMO frame index. Distinct from <c>Core.StartFrame</c> on purpose: an export renders at a
///     fixed timestep, so one output frame is not one demo frame, and conflating the two indices is how a
///     range silently becomes the wrong length.
/// </param>
/// <param name="DemoEndFrame">Last demo frame index, inclusive.</param>
/// <param name="EncoderOverride">
///     <c>auto</c> (the default), <c>software</c>, or an <c>EncoderLadder</c> rung's ffmpeg name — plan
///     P2 D4. It rides the request rather than the runner so two exports in one process can disagree,
///     which is the per-session shape the plan's §7 export node needs.
/// </param>
/// <param name="Quality">
///     <c>draft</c>, <c>standard</c> (the default) or <c>best</c>. A string for the same reason the
///     setting is one: an unknown value degrades to the default rather than throwing.
/// </param>
/// <param name="Ink">
///     The annotation document to burn in, frozen on the UI thread at Start, or null for no ink.
///     <para>
///         <b>On the request, not a mutable field.</b> <c>ExportJobService.RunAsync</c> awaits the heavy-job
///         gate before the runner's setup closure reads the document, so a field the dialog wrote could be
///         replaced by a second Start before the first, still-parked export ever read it. The request is the
///         only object that is one-per-run, so it is the only safe place to carry it.
///     </para>
/// </param>
/// <param name="Palette">
///     The scene colours to render with, resolved on the UI thread at Start, or null to let the setup
///     decide.
///     <para>
///         <b>Here for a harder reason than the ink.</b> The setup is built on the export's pool thread, and
///         it resolved the palette from <c>Application.Current.ActualThemeVariant</c> — a styled property, so
///         <c>AvaloniaObject.VerifyAccess</c> threw <i>"Call from invalid thread"</i> before frame zero. A
///         <c>ScenePalette</c> is a plain record of <c>SKColor</c>, so once resolved it crosses threads
///         freely: the theme is only reachable where it is read, so the read has to happen at Start and
///         travel with the request.
///     </para>
/// </param>
public sealed record Scene2DExportRequest(
    ExportRequest Core,
    string OutputPath,
    string DemoPath,
    int DemoStartFrame = 0,
    int DemoEndFrame = 0,
    string? EncoderOverride = null,
    string? Quality = null,
    AnnotationSession? Ink = null,
    ScenePalette? Palette = null);

/// <summary>A point-in-time export status. The chip and the flyout render from this.</summary>
/// <param name="Phase">Where the export is.</param>
/// <param name="FramesDone">Frames written.</param>
/// <param name="FramesTotal">Frames the request will produce.</param>
/// <param name="FramesPerSecond">Throughput so far.</param>
/// <param name="Elapsed">Wall time since the job started.</param>
/// <param name="OutputPath">The file being written.</param>
/// <param name="Error">The failure or refusal message, when there is one.</param>
/// <param name="Eta">
///     Estimated time remaining, or null before the session can measure one — the figure a user watching a
///     multi-minute render most wants to see.
/// </param>
public readonly record struct ExportJobStatus(
    ExportPhase Phase,
    int FramesDone,
    int FramesTotal,
    double FramesPerSecond,
    TimeSpan Elapsed,
    string? OutputPath,
    string? Error,
    TimeSpan? Eta = null)
{
    /// <summary>The canonical idle status.</summary>
    public static ExportJobStatus Idle { get; } =
        new(ExportPhase.Completed, 0, 0, 0, TimeSpan.Zero, null, null) { IsIdle = true };

    /// <summary>True before any job has run. Distinguishes "nothing happened" from "finished".</summary>
    public bool IsIdle { get; init; }

    /// <summary>True while the job occupies the machine — the chip is visible and the interlocks hold.</summary>
    public bool IsRunning => !IsIdle && Phase is ExportPhase.Preparing or ExportPhase.Seeking
        or ExportPhase.Rendering or ExportPhase.Finalizing;

    /// <summary>Completion in [0,1].</summary>
    public double Fraction => FramesTotal > 0 ? Math.Clamp(FramesDone / (double)FramesTotal, 0, 1) : 0;
}

/// <summary>
///     An export was refused before it started, because something else owns the machine. The message is
///     user-facing copy — the dialog and the chip show it verbatim, which is the whole point of refusing
///     rather than silently queueing.
/// </summary>
public sealed class ExportRefusedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">User-facing reason.</param>
    public ExportRefusedException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    /// <param name="message">User-facing reason.</param>
    /// <param name="innerException">The underlying refusal.</param>
    public ExportRefusedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Parameterless form required by the analyzer's exception-shape rule.</summary>
    public ExportRefusedException() : base("The export was refused.")
    {
    }
}
