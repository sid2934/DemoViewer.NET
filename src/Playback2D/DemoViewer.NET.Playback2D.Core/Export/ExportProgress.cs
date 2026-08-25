namespace DemoViewer.NET.Playback2D.Core.Export;

/// <summary>The phases an export moves through. The last three are terminal.</summary>
public enum ExportPhase
{
    /// <summary>Validating the request and building the sink.</summary>
    Preparing,

    /// <summary>The one from-zero tracker replay that reaches the first frame (plan D2).</summary>
    Seeking,

    /// <summary>Frames are being rendered and written.</summary>
    Rendering,

    /// <summary>The renderer is done; the sink is flushing (ffmpeg draining, a GIF being written).</summary>
    Finalizing,

    /// <summary>Finished; the output file exists.</summary>
    Completed,

    /// <summary>Cancelled by the user. The partial output was removed.</summary>
    Cancelled,

    /// <summary>Failed. The status carries the reason.</summary>
    Failed
}

/// <summary>
///     One progress report. <b>Frames-done based, never byte based</b>: an encoder's output size says
///     nothing useful about how much of a render remains.
/// </summary>
/// <param name="Phase">Where the export is.</param>
/// <param name="FramesDone">Frames written so far.</param>
/// <param name="FramesTotal">Frames the request will produce; constant for a run.</param>
/// <param name="FramesPerSecond">Throughput over the run so far; 0 before the first frame.</param>
/// <param name="Elapsed">Wall time since <c>RunAsync</c> was entered.</param>
/// <param name="Eta">Estimated time remaining, or null until at least two frames have been measured.</param>
/// <param name="Detail">Optional human-readable note (the failure message, the current phase's subject).</param>
public readonly record struct ExportProgress(
    ExportPhase Phase,
    int FramesDone,
    int FramesTotal,
    double FramesPerSecond,
    TimeSpan Elapsed,
    TimeSpan? Eta,
    string? Detail)
{
    /// <summary>Completion in [0,1]; 0 when the total is unknown.</summary>
    public double Fraction => FramesTotal > 0 ? Math.Clamp(FramesDone / (double)FramesTotal, 0, 1) : 0;
}
