#region

using DemoViewer.NET.Playback2D.Core.Compositing;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Benchmarking;

/// <summary>What a <see cref="PerfRow" /> measures.</summary>
public enum PerfRowKind
{
    /// <summary>A whole pipeline stage. Stages partition the frame.</summary>
    Stage = 0,

    /// <summary>One layer's half of a frame. Nested inside the advance and render stages.</summary>
    Layer = 1
}

/// <summary>
///     One measured thing over a run: a stage, or one layer's phase.
/// </summary>
/// <param name="Name">The stage name, or the layer id.</param>
/// <param name="Kind">Stage or layer.</param>
/// <param name="Phase">Which half of the frame, for a layer row; null for a stage row.</param>
/// <param name="Samples">Frames contributing to the distribution (the ring's live window).</param>
/// <param name="Times">Per-frame distribution, nearest-rank, in milliseconds.</param>
/// <param name="TotalMs">Summed cost over the sampled frames.</param>
/// <param name="SharePct">
///     <see cref="TotalMs" /> as a percentage of the run's frame total. Layer rows are nested inside
///     the advance and render stages, so layer shares and stage shares do not sum to 200 %.
/// </param>
/// <param name="CacheReplayed">Picture-cache hits. Render rows only.</param>
/// <param name="CacheRecorded">Picture-cache misses: a re-record. Render rows only.</param>
/// <param name="CacheUncached">
///     Draws with no cache in the path at all (<see cref="LayerCacheHint.Dynamic" />, or caching off).
///     Counted apart from misses so a Dynamic layer does not read as a permanent cache failure.
/// </param>
public sealed record PerfRow(
    string Name,
    PerfRowKind Kind,
    LayerPhase? Phase,
    int Samples,
    FrameTimeStats Times,
    double TotalMs,
    double SharePct,
    long CacheReplayed,
    long CacheRecorded,
    long CacheUncached)
{
    /// <summary>Hits over hits + misses, or null when the picture cache was never in this row's path.</summary>
    public double? CacheHitRate =>
        CacheReplayed + CacheRecorded > 0
            ? (double)CacheReplayed / (CacheReplayed + CacheRecorded)
            : null;

    /// <summary>The row's label as it appears in a report: <c>id</c>, or <c>id (render)</c>.</summary>
    public string Label => Kind == PerfRowKind.Stage || Phase is null
        ? Name
        : $"{Name} ({(Phase == LayerPhase.Advance ? "advance" : "render")})";
}

/// <summary>
///     One run's per-stage and per-layer breakdown (plan <c>P1-perf-instrumentation</c> §4). Built by
///     <see cref="ScenePerfRecorder.Snapshot" /> after the measured window; pure data from there on.
/// </summary>
/// <param name="Frames">Frames closed during the capture.</param>
/// <param name="FrameTotalMs">Summed frame time over those frames.</param>
/// <param name="Frame">
///     Per-frame distribution of the sum of the captured stages. Under <c>bench</c> that is source +
///     advance + render; under <c>export</c> it also includes read-back and the encoder handoff.
/// </param>
/// <param name="Stages">Stage rows, in pipeline order.</param>
/// <param name="Layers">Layer rows, in compositor draw order, advance before render.</param>
public sealed record PerfReport(
    int Frames,
    double FrameTotalMs,
    FrameTimeStats Frame,
    IReadOnlyList<PerfRow> Stages,
    IReadOnlyList<PerfRow> Layers)
{
    /// <summary>An empty report: what a run with capture off would produce.</summary>
    public static PerfReport Empty { get; } = new(0, 0, default, [], []);

    /// <summary>
    ///     The uncapped render-only ceiling: <c>1000 / p50</c> of the render stage. This is the number
    ///     "how fast could this scene possibly draw" asks for, and it is the same number under
    ///     <c>bench</c> (which never encodes) and <c>export --no-encode</c> (which encodes nothing):
    ///     that equality is the cross-check that the two harnesses measure one renderer.
    /// </summary>
    public double MaxRenderFps => Fps(Find("render", PerfRowKind.Stage)?.Times.P50Ms ?? 0);

    /// <summary>The whole captured frame's ceiling: <c>1000 / p50</c> of <see cref="Frame" />.</summary>
    public double MaxFrameFps => Fps(Frame.P50Ms);

    /// <summary>Mean frame time over the capture, in milliseconds.</summary>
    public double MeanFrameMs => Frames > 0 ? FrameTotalMs / Frames : 0;

    /// <summary>
    ///     Stages and layers together, slowest total first: the "what should I go and look at" list.
    ///     Stage rows and the layer rows nested inside them both appear; a stage that is dominated by one
    ///     layer shows up immediately above it.
    /// </summary>
    /// <param name="count">How many rows to return.</param>
    public IReadOnlyList<PerfRow> Slowest(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        List<PerfRow> all = [.. Stages, .. Layers];
        all.Sort(static (a, b) => b.TotalMs.CompareTo(a.TotalMs));
        return count >= all.Count ? all : all.GetRange(0, count);
    }

    /// <summary>The row with this name and kind, or null.</summary>
    /// <param name="name">The stage name or layer id.</param>
    /// <param name="kind">Stage or layer.</param>
    /// <param name="phase">For a layer row, which phase; ignored for a stage row.</param>
    public PerfRow? Find(string name, PerfRowKind kind, LayerPhase? phase = null)
    {
        IReadOnlyList<PerfRow> rows = kind == PerfRowKind.Stage ? Stages : Layers;
        for (int i = 0; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Name, name, StringComparison.Ordinal) &&
                (kind == PerfRowKind.Stage || phase is null || rows[i].Phase == phase))
            {
                return rows[i];
            }
        }

        return null;
    }

    private static double Fps(double ms) => ms > 0 ? 1000.0 / ms : 0;
}
