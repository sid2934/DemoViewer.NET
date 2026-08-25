#region

using System.Diagnostics;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Export;

/// <summary>
///     The export's wall clock: how long it has been running, which is what a progress bar, a throughput
///     figure and an ETA are made of.
///     <para>
///         <b>Wall time here is the deliverable, not a leak.</b> Design §5.1 bans a clock from the render
///         path because motion must be a function of the injected <c>SceneTime</c>; none of what this
///         measures reaches a layer. It is a separate type so the reference is attributed to a named
///         class under <c>…Pipeline.Export</c> rather than to the compiler-generated state machine
///         <c>RunAsync</c> becomes — which is what keeps <c>BannedApiTests</c>' exemption a namespace
///         rule instead of a carve-out for a generated name.
///     </para>
/// </summary>
internal sealed class ExportClock
{
    private readonly Stopwatch _stopwatch;

    private ExportClock(Stopwatch stopwatch) => _stopwatch = stopwatch;

    /// <summary>Wall time since <see cref="Start" />.</summary>
    public TimeSpan Elapsed => _stopwatch.Elapsed;

    /// <summary>Starts a clock.</summary>
    public static ExportClock Start() => new(Stopwatch.StartNew());
}
