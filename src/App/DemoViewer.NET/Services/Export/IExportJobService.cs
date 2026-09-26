#region

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

// Scene2DExportRequest itself now lives in Pipeline (Export/Scene2DExportRequest.cs), still in this
// namespace: `dv2d pack` builds one without referencing src/App/*. See the note on that file.

/// <summary>A point-in-time export status. The chip and the flyout render from this.</summary>
/// <param name="Phase">Where the export is.</param>
/// <param name="FramesDone">Frames written.</param>
/// <param name="FramesTotal">Frames the request will produce.</param>
/// <param name="FramesPerSecond">Throughput so far.</param>
/// <param name="Elapsed">Wall time since the job started.</param>
/// <param name="OutputPath">The file being written.</param>
/// <param name="Error">The failure or refusal message, when there is one.</param>
/// <param name="Eta">
///     Estimated time remaining, or null before the session can measure one, the figure a user watching a
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
        new(ExportPhase.Completed, 0, 0, 0, TimeSpan.Zero, null, null)
        {
            IsIdle = true
        };

    /// <summary>True before any job has run. Distinguishes "nothing happened" from "finished".</summary>
    public bool IsIdle { get; init; }

    /// <summary>True while the job occupies the machine: the chip is visible and the interlocks hold.</summary>
    public bool IsRunning => !IsIdle && Phase is ExportPhase.Preparing or ExportPhase.Seeking
        or ExportPhase.Rendering or ExportPhase.Finalizing;

    /// <summary>Completion in [0,1].</summary>
    public double Fraction => FramesTotal > 0 ? Math.Clamp(FramesDone / (double)FramesTotal, 0, 1) : 0;
}

/// <summary>
///     An export was refused before it started, because something else owns the machine. The message is
///     user-facing copy: the dialog and the chip show it verbatim, which is the whole point of refusing
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
