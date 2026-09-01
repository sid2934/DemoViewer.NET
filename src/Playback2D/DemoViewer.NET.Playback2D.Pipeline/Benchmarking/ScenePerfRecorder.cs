#region

using System.Diagnostics;
using DemoViewer.NET.Playback2D.Core.Compositing;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Benchmarking;

/// <summary>
///     The stages one frame of the pipeline passes through, outside the layers.
///     <para>
///         They are sequential and non-overlapping, so their sum is the frame. Layer rows are
///         <b>nested inside</b> <see cref="Advance" /> and <see cref="Render" />, never additional to
///         them.
///     </para>
/// </summary>
public enum PerfStage
{
    /// <summary>
    ///     <c>ISceneFrameSource.TimeAt</c> + <c>FrameAt</c>: on a demo-backed run this is the entity
    ///     tracker's decode plus <c>SceneFrameBuilder</c>, and on a fixture-backed run it is ~free.
    ///     Separating it is the whole reason "is the exporter slow or is the decoder slow" is answerable.
    /// </summary>
    Source = 0,

    /// <summary>Level derivation, pane reconciliation, camera advance, and every layer's Advance.</summary>
    Advance = 1,

    /// <summary>The raster: clear, every layer's Render over every pane, and the surface flush.</summary>
    Render = 2,

    /// <summary>
    ///     <c>SKSurface.ReadPixels</c> into the staging buffer. Export-only; a bench never leaves the
    ///     surface.
    /// </summary>
    Readback = 3,

    /// <summary>
    ///     <c>IFrameSink.WriteAsync</c>. Export-only, and the <b>backpressure</b> number:
    ///     <c>ChannelVideoFrameSource</c> is a bounded channel of four with <c>FullMode.Wait</c>, so time
    ///     spent here is time the encoder is behind the renderer. Under <c>--no-encode</c> the same stage
    ///     measures the hashing sink instead, which is what makes the two runs comparable.
    /// </summary>
    Encode = 4
}

/// <summary>
///     Per-layer and per-stage capture for one run (plan <c>P1-perf-instrumentation</c> §3.2–§3.3).
///     Implements Core's clock-free <see cref="ISceneProfiler" /> and adds the stage API the export and
///     bench harnesses drive.
///     <para>
///         <b>It lives in Pipeline.Benchmarking because it owns a stopwatch.</b> Core is banned from
///         <see cref="Stopwatch" /> outright (design §5.1, enforced by <c>BannedApiTests</c> against
///         compiled IL); this namespace is already exempt, for the harness next door, and for the same
///         reason. Measuring from outside is the contract, not a workaround.
///     </para>
///     <para>
///         <b>Zero steady-state allocation while capturing.</b> Every sample is a raw
///         <see cref="Stopwatch.GetTimestamp" /> delta accumulated into a flat <c>long[]</c> and pushed
///         once per <see cref="EndFrame" /> into a wrapping ring. Rings are allocated on their first
///         push (i.e. by the warmup frames) and never again. Nothing is converted, sorted or formatted
///         until <see cref="Snapshot" />, which runs after the measured window.
///     </para>
///     <para>
///         <b>Not thread-safe; one run at a time.</b> The export loop hands off between pool threads
///         across <c>await</c>, which is fine: the calls are sequential, never concurrent.
///     </para>
/// </summary>
public sealed class ScenePerfRecorder : ISceneProfiler
{
    /// <summary>Frames of history each ring holds before it wraps.</summary>
    public const int DefaultCapacity = 4096;

    /// <summary>Layer slots tracked. A scene stack is nine layers; 32 is headroom, not a guess.</summary>
    public const int DefaultMaxLayers = 32;

    private const int PhaseCount = 2;
    private const int StageCount = 5;

    private readonly long[] _cacheRecorded;
    private readonly long[] _cacheReplayed;
    private readonly long[] _cacheUncached;
    private readonly int _capacity;
    private readonly long[] _layerAccum;
    private readonly int[] _layerCount;
    private readonly int[] _layerHead;
    private readonly string?[] _layerNames;
    private readonly long[]?[] _layerRing;
    private readonly long[] _layerStart;
    private readonly bool[] _layerTouched;
    private readonly int _maxLayers;
    private readonly long[] _stageAccum;
    private readonly int[] _stageCount;
    private readonly int[] _stageHead;
    private readonly long[]?[] _stageRing;
    private readonly long[] _stageStart;
    private readonly bool[] _stageTouched;
    private readonly long[] _totalRing;
    private int _totalCount;
    private int _totalHead;

    /// <summary>Creates a recorder.</summary>
    /// <param name="capacity">Frames of history per ring. Older frames are overwritten.</param>
    /// <param name="maxLayers">Layer slots tracked; anything beyond is ignored rather than resized.</param>
    public ScenePerfRecorder(int capacity = DefaultCapacity, int maxLayers = DefaultMaxLayers)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLayers);

        _capacity = capacity;
        _maxLayers = maxLayers;

        int slots = maxLayers * PhaseCount;
        _layerNames = new string?[maxLayers];
        _layerStart = new long[slots];
        _layerAccum = new long[slots];
        _layerRing = new long[slots][];
        _layerCount = new int[slots];
        _layerHead = new int[slots];
        _layerTouched = new bool[slots];

        _cacheReplayed = new long[maxLayers];
        _cacheRecorded = new long[maxLayers];
        _cacheUncached = new long[maxLayers];

        _stageStart = new long[StageCount];
        _stageAccum = new long[StageCount];
        _stageRing = new long[StageCount][];
        _stageCount = new int[StageCount];
        _stageHead = new int[StageCount];
        _stageTouched = new bool[StageCount];

        _totalRing = new long[capacity];
    }

    /// <summary>Frames closed by <see cref="EndFrame" /> since construction or the last <see cref="Reset" />.</summary>
    public int Frames { get; private set; }

    /// <inheritdoc />
    public void BeginLayer(int index, string layerId, LayerPhase phase)
    {
        if ((uint)index >= (uint)_maxLayers)
        {
            return;
        }

        // Reference compare, not string compare: a layer id is a stable literal the layer holds, so this
        // is a pointer test on the fast path and only relabels if the stack was rebuilt under us.
        if (!ReferenceEquals(_layerNames[index], layerId))
        {
            _layerNames[index] = layerId;
        }

        _layerStart[index * PhaseCount + (int)phase] = Stopwatch.GetTimestamp();
    }

    /// <inheritdoc />
    public void EndLayer(int index, LayerPhase phase)
    {
        if ((uint)index >= (uint)_maxLayers)
        {
            return;
        }

        int slot = index * PhaseCount + (int)phase;

        // += rather than =: a layer drawn into three panes costs the frame three deltas, and the frame
        // sample is what the layer cost the frame.
        _layerAccum[slot] += Stopwatch.GetTimestamp() - _layerStart[slot];
        _layerTouched[slot] = true;
    }

    /// <inheritdoc />
    public void RecordPicture(int index, PictureCacheOutcome outcome)
    {
        if ((uint)index >= (uint)_maxLayers)
        {
            return;
        }

        switch (outcome)
        {
            case PictureCacheOutcome.Replayed:
                _cacheReplayed[index]++;
                break;
            case PictureCacheOutcome.Recorded:
                _cacheRecorded[index]++;
                break;
            default:
                _cacheUncached[index]++;
                break;
        }
    }

    /// <summary>Opens a stage. Stages do not overlap; a second open replaces the first.</summary>
    /// <param name="stage">Which stage.</param>
    public void BeginStage(PerfStage stage) => _stageStart[(int)stage] = Stopwatch.GetTimestamp();

    /// <summary>Closes a stage, folding its elapsed ticks into this frame's accumulator.</summary>
    /// <param name="stage">Which stage.</param>
    public void EndStage(PerfStage stage)
    {
        int index = (int)stage;
        _stageAccum[index] += Stopwatch.GetTimestamp() - _stageStart[index];
        _stageTouched[index] = true;
    }

    /// <summary>
    ///     Closes the frame: every touched accumulator is pushed into its ring and zeroed, and the sum of
    ///     the stage accumulators becomes this frame's total.
    /// </summary>
    public void EndFrame()
    {
        long total = 0;
        bool staged = false;

        for (int s = 0; s < StageCount; s++)
        {
            if (!_stageTouched[s])
            {
                continue;
            }

            staged = true;
            total += _stageAccum[s];
            Push(_stageRing, _stageHead, _stageCount, s, _stageAccum[s]);
            _stageAccum[s] = 0;
        }

        long layerTotal = 0;
        for (int slot = 0; slot < _layerAccum.Length; slot++)
        {
            if (!_layerTouched[slot])
            {
                continue;
            }

            layerTotal += _layerAccum[slot];
            Push(_layerRing, _layerHead, _layerCount, slot, _layerAccum[slot]);
            _layerAccum[slot] = 0;
        }

        // A caller that drives only the compositor (a test, a host that has no pipeline stages)
        // still deserves meaningful share percentages, and the layers are the whole frame it can see.
        // With stages present they are the denominator, and the layers stay nested inside them.
        if (!staged)
        {
            total = layerTotal;
        }

        _totalRing[_totalHead] = total;
        _totalHead = (_totalHead + 1) % _capacity;
        if (_totalCount < _capacity)
        {
            _totalCount++;
        }

        Frames++;
    }

    /// <summary>
    ///     Zeroes every sample, counter and frame count, keeping the rings and the layer labels.
    ///     <para>
    ///         The benchmark calls this <b>after</b> its warmup, so the rings are allocated by warmup
    ///         frames and the measured window (the one the §6 bytes/frame gate reads) only ever writes
    ///         into arrays that already exist.
    ///     </para>
    ///     <para>
    ///         The <c>touched</c> flags are part of "every counter" and are cleared with the rest. They
    ///         are what decides whether a row exists at all, so leaving them set would carry a slot that
    ///         only the warmup ever exercised into the report as a row of zeros: reading as "measured and
    ///         free" when the truth is "not measured". Absent, not zero, is the honest answer, and it is
    ///         the same rule the stage rows already follow: a stage a harness never drives does not appear.
    ///     </para>
    /// </summary>
    public void Reset()
    {
        Array.Clear(_layerStart);
        Array.Clear(_layerAccum);
        Array.Clear(_layerCount);
        Array.Clear(_layerHead);
        Array.Clear(_layerTouched);
        Array.Clear(_cacheReplayed);
        Array.Clear(_cacheRecorded);
        Array.Clear(_cacheUncached);
        Array.Clear(_stageStart);
        Array.Clear(_stageAccum);
        Array.Clear(_stageCount);
        Array.Clear(_stageHead);
        Array.Clear(_stageTouched);

        _totalCount = 0;
        _totalHead = 0;
        Frames = 0;
    }

    /// <summary>
    ///     Projects the captured ticks into a report: milliseconds, nearest-rank percentiles, totals,
    ///     share-of-frame and the cache counters. Allocates: deliberately, and only here, outside any
    ///     measured window.
    /// </summary>
    public PerfReport Snapshot()
    {
        double[] scratch = new double[_capacity];
        FrameTimeStats frame = Stats(_totalRing, _totalCount, _totalHead, scratch, out double frameTotalMs);

        List<PerfRow> stages = [];
        for (int s = 0; s < StageCount; s++)
        {
            if (!_stageTouched[s] || _stageCount[s] == 0)
            {
                continue;
            }

            FrameTimeStats stats = Stats(_stageRing[s], _stageCount[s], _stageHead[s], scratch,
                out double sum);
            stages.Add(new PerfRow(StageName((PerfStage)s), PerfRowKind.Stage, null, _stageCount[s],
                stats, sum, Share(sum, frameTotalMs), 0, 0, 0));
        }

        List<PerfRow> layers = [];
        for (int slot = 0; slot < _layerAccum.Length; slot++)
        {
            if (!_layerTouched[slot] || _layerCount[slot] == 0)
            {
                continue;
            }

            int index = slot / PhaseCount;
            LayerPhase phase = (LayerPhase)(slot % PhaseCount);
            FrameTimeStats stats = Stats(_layerRing[slot], _layerCount[slot], _layerHead[slot], scratch,
                out double sum);

            // Cache counters are per layer, not per phase, and belong on the Render row: the only phase
            // a picture cache is consulted in.
            bool render = phase == LayerPhase.Render;
            layers.Add(new PerfRow(
                _layerNames[index] ?? $"layer[{index}]",
                PerfRowKind.Layer,
                phase,
                _layerCount[slot],
                stats,
                sum,
                Share(sum, frameTotalMs),
                render ? _cacheReplayed[index] : 0,
                render ? _cacheRecorded[index] : 0,
                render ? _cacheUncached[index] : 0));
        }

        return new PerfReport(Frames, frameTotalMs, frame, stages, layers);
    }

    private static double Share(double value, double total) => total > 0 ? value / total * 100.0 : 0;

    private static string StageName(PerfStage stage) => stage switch
    {
        PerfStage.Source => "source",
        PerfStage.Advance => "advance",
        PerfStage.Render => "render",
        PerfStage.Readback => "readback",
        _ => "encode"
    };

    // The ring is allocated on its first push (that is, during warmup) and never again. Every push
    // after that is a store into an array that already exists.
    private void Push(long[]?[] rings, int[] heads, int[] counts, int slot, long ticks)
    {
        long[] ring = rings[slot] ??= new long[_capacity];
        ring[heads[slot]] = ticks;
        heads[slot] = (heads[slot] + 1) % _capacity;
        if (counts[slot] < _capacity)
        {
            counts[slot]++;
        }
    }

    private FrameTimeStats Stats(long[]? ring, int count, int head, double[] scratch, out double totalMs)
    {
        totalMs = 0;
        if (ring is null || count == 0)
        {
            return default;
        }

        // The live window is the `count` entries ending at `head`: when the ring has wrapped that starts
        // at head, and when it has not, head == count and the window starts at 0.
        int start = count == _capacity ? head : 0;
        double perTick = 1000.0 / Stopwatch.Frequency;

        for (int i = 0; i < count; i++)
        {
            double ms = ring[(start + i) % _capacity] * perTick;
            scratch[i] = ms;
            totalMs += ms;
        }

        // Nearest-rank, via the same helper the budget gate reads, so a perf row and a budget row cannot
        // disagree about what p99 means. It sorts in place, which is why totalMs is summed first.
        return FrameTimeStats.From(scratch.AsSpan(0, count));
    }
}
