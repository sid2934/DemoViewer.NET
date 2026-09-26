#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Dependencies;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.Services.Review;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>Renders the GIFs of one demo's planned clips. The seam every Lineup Clip test replaces.</summary>
public interface ILineupClipRenderer
{
    /// <summary>
    ///     Renders each job's GIF to its <see cref="LineupClipJob.GifPath" />. Runs on a worker; one demo
    ///     per call, so the demo is parsed once for all of its clips.
    /// </summary>
    /// <param name="demoPath">The demo every job is in.</param>
    /// <param name="jobs">That demo's clips.</param>
    /// <param name="ct">Stops the batch; a GIF cut short does not survive.</param>
    /// <returns>The jobs whose GIF was written.</returns>
    Task<IReadOnlyList<LineupClipJob>> RenderAsync(string demoPath, IReadOnlyList<LineupClipJob> jobs,
        CancellationToken ct);
}

/// <summary>
///     Lineup Clip Render (plan.md §3, Phase 4): every Lineup Card with a repeated throw position gets a
///     short GIF of the throw, the camera on the thrower, written beside the <c>setpos</c>/<c>setang</c>
///     line it was thrown from, with no one pressing anything.
///     <para>
///         <b>Queued through the Review Queue.</b> <see cref="Plan" /> puts each clip in the queue under a
///         "Lineup clips, &lt;map&gt;" title card, and the worker renders only clips still in it: taking a clip
///         out of the queue before its turn is how a user says not to render it. Planning runs when the
///         Grenade Index changes, so a demo indexed in the background has its clips queued and rendered
///         after it.
///     </para>
///     <para>
///         <b>The pair.</b> The renderer writes the GIF through the existing export session; this service
///         writes the sidecar only once the GIF exists, so a pair on disk is always a finished one, and a
///         lineup is planned again while either half is missing.
///     </para>
///     <para>
///         <b>Threading.</b> <see cref="Plan" /> and the queue's <c>Changed</c> run on the UI thread (the
///         queue's own rule); the worker renders one demo at a time on the pool.
///     </para>
/// </summary>
public sealed class LineupClipService : IDisposable
{
    private readonly Func<IReadOnlyList<GrenadeCluster>> _clusters;
    private readonly CancellationTokenSource _cts = new();
    private readonly string? _directory;
    private readonly Func<bool> _enabled;
    private readonly Func<string, bool> _fileExists;
    private readonly object _gate = new();
    private readonly Action<string>? _log;
    private readonly List<(Guid EntryId, LineupClipJob Job)> _pending = [];
    private readonly HashSet<string> _planned = new(StringComparer.Ordinal);
    private readonly ReviewQueue _queue;
    private readonly ILineupClipRenderer _renderer;
    private readonly Action<string, string> _writeText;
    private bool _disposed;
    private bool _running;

    /// <param name="clusters">Every landing cluster in the Grenade Index, all maps.</param>
    /// <param name="queue">The Review Queue the clips are put in.</param>
    /// <param name="directory">Where the pairs are written; null (the browser) plans nothing.</param>
    /// <param name="enabled">The live <c>GrenadesSettings.RenderLineupClips</c>.</param>
    /// <param name="renderer">Renders a demo's GIFs.</param>
    /// <param name="fileExists">The existence probe; <see cref="File.Exists" /> when null.</param>
    /// <param name="writeText">Writes a sidecar; an atomic write when null.</param>
    /// <param name="log">Optional line sink.</param>
    public LineupClipService(Func<IReadOnlyList<GrenadeCluster>> clusters, ReviewQueue queue, string? directory,
        Func<bool> enabled, ILineupClipRenderer renderer, Func<string, bool>? fileExists = null,
        Action<string, string>? writeText = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(renderer);
        _clusters = clusters;
        _queue = queue;
        _directory = directory;
        _enabled = enabled;
        _renderer = renderer;
        _fileExists = fileExists ?? File.Exists;
        _writeText = writeText ?? DemoCacheStore.WriteAtomic;
        _log = log;
        _queue.Changed += OnQueueChanged;
    }

    /// <summary>The clips queued and not yet rendered, oldest first.</summary>
    public IReadOnlyList<LineupClipJob> Pending
    {
        get
        {
            lock (_gate)
            {
                return [.. _pending.Select(p => p.Job)];
            }
        }
    }

    /// <summary>The worker's current run, so a test can await it.</summary>
    internal Task WorkerTask { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Changed -= OnQueueChanged;
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <summary>
    ///     Plans every clip the index calls for that has no pair on disk and was not planned earlier this
    ///     session, puts them in the Review Queue by map, and starts the worker. UI thread.
    /// </summary>
    /// <returns>How many clips were planned.</returns>
    public int Plan()
    {
        if (_disposed || _directory is null || !_enabled())
        {
            return 0;
        }

        List<LineupClipJob> jobs =
        [
            .. LineupClipPlanner.PlanAll(_clusters(), _directory, _fileExists).Where(j => _planned.Add(j.Key))
        ];
        if (jobs.Count == 0)
        {
            return 0;
        }

        List<(Guid, LineupClipJob)> planned = [];
        foreach (IGrouping<string, LineupClipJob> map in jobs.GroupBy(j => j.Map, StringComparer.OrdinalIgnoreCase))
        {
            _queue.Add(map.Select(LineupClipPlanner.ToReviewEntry), LineupClipPlanner.SectionTitle(map.Key));

            // Found rather than taken from what was added: a clip queued in an earlier session is skipped by
            // Add as a duplicate, and it is still the entry this render belongs to.
            foreach (LineupClipJob job in map)
            {
                if (EntryFor(job) is { } entry)
                {
                    planned.Add((entry.Id, job));
                }
            }
        }

        lock (_gate)
        {
            _pending.AddRange(planned);
        }

        StartWorker();
        return jobs.Count;
    }

    // A clip the user took out of the queue is not rendered.
    private void OnQueueChanged()
    {
        HashSet<Guid> ids = [.. _queue.Entries.Select(e => e.Id)];
        lock (_gate)
        {
            _pending.RemoveAll(p => !ids.Contains(p.EntryId));
        }
    }

    private ReviewEntry? EntryFor(LineupClipJob job) =>
        _queue.Clips.FirstOrDefault(e => string.Equals(e.Source, ReviewSources.Lineup, StringComparison.Ordinal)
                                         && string.Equals(e.DemoPath, job.DemoPath, StringComparison.OrdinalIgnoreCase)
                                         && e.FromTick == job.FromTick && e.ToTick == job.ToTick);

    private void StartWorker()
    {
        lock (_gate)
        {
            if (_running || _pending.Count == 0)
            {
                return;
            }

            _running = true;
        }

        CancellationToken ct = _cts.Token;
        WorkerTask = Task.Run(() => DrainAsync(ct), CancellationToken.None);
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && NextDemo() is { } batch)
            {
                IReadOnlyList<LineupClipJob> rendered;
                try
                {
                    rendered = await _renderer.RenderAsync(batch[0].DemoPath, batch, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"lineup clips: {batch[0].DemoPath}: {ex.Message}");
                    continue;
                }

                foreach (LineupClipJob job in rendered)
                {
                    WriteSidecar(job);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }
        }
    }

    // The next demo's clips, taken out of the pending list together so the demo is parsed once.
    private List<LineupClipJob>? NextDemo()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return null;
            }

            string demo = _pending[0].Job.DemoPath;
            List<LineupClipJob> batch =
            [
                .. _pending.Where(p => string.Equals(p.Job.DemoPath, demo, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Job)
            ];
            _pending.RemoveAll(p => string.Equals(p.Job.DemoPath, demo, StringComparison.OrdinalIgnoreCase));
            return batch;
        }
    }

    private void WriteSidecar(LineupClipJob job)
    {
        try
        {
            _writeText(job.SetposPath, job.ConsoleText + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"lineup clips: {job.SetposPath}: {ex.Message}");
        }
    }
}

/// <summary>
///     The production <see cref="ILineupClipRenderer" />: parses the demo once on a background slot of the
///     heavy-job gate, then renders each clip through <see cref="SceneExportRunner" />, the 2D export's own
///     runner and <c>SceneExportSession</c>, as a GIF (the managed encoder when no ffmpeg is installed).
///     Private everything, as every export: its own parse, map bundle, compositor and surface.
/// </summary>
public sealed class LineupClipRenderer : ILineupClipRenderer
{
    private readonly HeavyJobGate? _gate;
    private readonly Func<string, LoadedMapAsset?> _loadMap;
    private readonly Action<string>? _log;
    private readonly Func<string, ParsedDemo> _parse;

    /// <param name="gate">The heavy-job gate; the parse takes a background slot, so it yields to the user's own.</param>
    /// <param name="parse">Reads and parses a demo; <c>DemoParser.Parse</c> over the file when null.</param>
    /// <param name="loadMap">Finds a map's baked bundle; the pipeline's loader when null.</param>
    /// <param name="log">Line sink for the encoder choice, ffmpeg's stderr and a failed clip.</param>
    public LineupClipRenderer(HeavyJobGate? gate, Func<string, ParsedDemo>? parse = null,
        Func<string, LoadedMapAsset?>? loadMap = null, Action<string>? log = null)
    {
        _gate = gate;
        _parse = parse ?? (path => DemoParser.Parse(File.ReadAllBytes(path).AsMemory()));
        _loadMap = loadMap ?? (map => MapAssetPipeline.TryLoad(map));
        _log = log;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LineupClipJob>> RenderAsync(string demoPath, IReadOnlyList<LineupClipJob> jobs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(demoPath);
        ArgumentNullException.ThrowIfNull(jobs);
        if (jobs.Count == 0 || !File.Exists(demoPath))
        {
            return [];
        }

        using IDisposable? slot = _gate is null ? null : await _gate.AcquireBackgroundAsync(ct).ConfigureAwait(false);
        ParsedDemo demo = _parse(demoPath);
        using LoadedMapAsset? asset = SafeLoad(jobs[0].Map);

        ExportSceneSetup setup = new(demo.Frames, demo.TickRate, jobs[0].Map, ScenePalette.Dark,
            LevelDisplayMode.Stacked, null, null, asset);
        SceneExportRunner runner = new(_ => setup, RenderSurfaceProviderFactory.CreateCpu,
            static () => FfmpegDependency.ManagedDirectory, _log, EncoderProbeCache.Shared);

        List<LineupClipJob> rendered = [];
        foreach (LineupClipJob job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            if (LineupClipPlanner.BuildRequest(job, demo.Frames, demo.TickRate) is not { } request)
            {
                _log?.Invoke($"lineup clips: {job.Key} lies outside {demoPath}");
                continue;
            }

            try
            {
                SceneExportSession.Validate(request.Core);
                Directory.CreateDirectory(Path.GetDirectoryName(job.GifPath)!);
                await runner.RunAsync(request, NoProgress.Instance, ct).ConfigureAwait(false);
                rendered.Add(job);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Invoke($"lineup clips: {job.GifPath}: {ex.Message}");
            }
        }

        return rendered;
    }

    private LoadedMapAsset? SafeLoad(string map)
    {
        try
        {
            return _loadMap(map);
        }
        catch (Exception)
        {
            return null; // the grid, as a strat export without a bundle
        }
    }

    // Nobody watches a background clip's progress; Progress<T> would post each report to the pool for nothing.
    private sealed class NoProgress : IProgress<ExportProgress>
    {
        public static readonly NoProgress Instance = new();

        public void Report(ExportProgress value)
        {
        }
    }
}
