#region

using System.Runtime.CompilerServices;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.Sdk.Export;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Annotations;
using DemoViewer.NET.Playback2D.Hud;
using DemoViewer.NET.Playback2D.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Playback2D.Pipeline.Rendering;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.ViewModels.Playback2D;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     Turns the published <see cref="SceneExportScene" /> into the export's own
///     <see cref="ExportSceneSetup" />, and a <see cref="SceneExportClip" />'s tick range into the frame range
///     the runner takes. Both halves are here so the pane path and the headless path build the same scene.
/// </summary>
internal static class SceneExportScenes
{
    /// <summary>Takes a hold on a map's art by name, or null where the app ships none for it.</summary>
    internal static IMapAssetLease? Acquire(string? mapName)
    {
        try
        {
            return MapAssets.Acquire(mapName);
        }
        catch (Exception)
        {
            return null; // the grid, as a strat export without a bundle
        }
    }

    /// <summary>The setup one run renders.</summary>
    /// <param name="scene">The captured scene.</param>
    /// <param name="palette">The resolved colours; <see cref="SceneExportScene.Palette" /> wins when it is set.</param>
    /// <param name="art">The map's art, or null for the grid.</param>
    /// <param name="owned">
    ///     Released by the runner when the run ends, however it ends. The pane path hands its lease over; a
    ///     headless caller holds its own for the whole call and passes null.
    /// </param>
    internal static ExportSceneSetup ToSetup(SceneExportScene scene, ScenePalette palette, IMapAsset? art,
        IDisposable? owned)
    {
        IHudDataSource? hud = scene.Hud;
        Func<TrackerFrameSource, IHudDataSource>? demoHud = hud is null ? null : _ => hud;
        return new ExportSceneSetup(scene.Frames, scene.TickRate, scene.MapName, scene.Palette ?? palette,
            scene.DisplayMode, null, demoHud, art, scene.Ink)
        {
            Source = scene.Source is { } source ? () => source(art) : null,
            SourceHud = hud,
            Owned = owned
        };
    }

    /// <summary>
    ///     The clip's range as the runner takes it: the tick range resolved against the demo's own frames and
    ///     clamped to them. Null when the range lies wholly outside them, which is the one case a planner
    ///     cannot rule out without the parse.
    /// </summary>
    /// <param name="clip">The clip.</param>
    /// <param name="frames">The demo's frames; empty for a held-frames clip.</param>
    /// <param name="outputFrames">Output frames for a held-frames clip, whose range is the caller's own clock.</param>
    internal static Scene2DExportRequest? Resolve(SceneExportClip clip, IReadOnlyList<DemoFrame> frames,
        int? outputFrames = null)
    {
        int start = clip.FromTick;
        int end = clip.ToTick;
        int count;
        if (outputFrames is int held)
        {
            count = held;
        }
        else
        {
            if (frames.Count == 0 || clip.ToTick < frames[0].ServerTick || clip.FromTick > frames[^1].ServerTick)
            {
                return null;
            }

            start = Math.Max(0, TrackerFrameSource.FrameIndexForTick(frames, Math.Max(clip.FromTick, frames[0].ServerTick)));
            end = TrackerFrameSource.FrameIndexForTick(frames, Math.Min(clip.ToTick, frames[^1].ServerTick));
            if (end < start)
            {
                return null;
            }

            count = TrackerFrameSource.OutputFrameCount(frames, start, end, clip.Fps, 1.0, clip.Scene.TickRate);
        }

        CameraScript camera = clip.FollowSteamId is { } steamId
            ? new CameraScript.FollowPlayer(steamId)
            : new CameraScript.Fixed(new Dictionary<MapLevelId, ViewportTransform>());
        ExportRequest core = new(0, Math.Max(0, count - 1), clip.Fps,
            new SKSizeI(clip.Width, clip.Height), 1.0, clip.FormatId, LayerSet(clip.LayerIds), camera);
        return new Scene2DExportRequest(core, clip.OutputPath, clip.DemoPath, start, end,
            Palette: clip.Scene.Palette ?? ScenePalette.Dark);
    }

    // Null and empty both reach the runner as an empty set, where empty means the default scene with nothing
    // opt-in. Copied with the ordinal comparer the layer ids are compared under.
    private static HashSet<string> LayerSet(IReadOnlySet<string>? ids) =>
        ids is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(ids, StringComparer.Ordinal);
}

/// <summary>
///     The host's half of <see cref="IExtensionSceneExport" />: one export job per extension, the 2D export's
///     own pane and status chip over it, and the one <see cref="SceneExportRunner" /> every pixel goes
///     through.
///     <para>
///         <b>One job, built once.</b> Single-flight refusal and the chip's continuity both live on the job,
///         so closing and reopening the pane must not replace it.
///     </para>
/// </summary>
internal sealed class ExtensionSceneExport : IExtensionSceneExport, IDisposable
{
    /// <summary>The refusal when an extension's capture answers null and it named no copy of its own.</summary>
    internal const string NothingToExportRefusal = "There is nothing to export.";

    /// <summary>The refusal when a clip's tick range lies outside the demo it names.</summary>
    internal const string OutsideTheDemoRefusal = "The clip's range lies outside the demo.";

    // Keyed by the ink session the pane puts on the request: the one object that is one-per-run, for the
    // reason Scene2DExportRequest.Ink gives. A second Start cannot trade scenes with a parked first one.
    private readonly ConditionalWeakTable<AnnotationSession, SceneExportScene> _captures = new();

    private readonly string _chipId;
    private readonly IFirstPartyExportChips? _chips;
    private readonly FirstPartyExports _exports;
    private readonly string _featureId;
    private readonly ExtensionGuard _guard;
    private readonly Action<string>? _reveal;
    private readonly SceneExportRunner _runner;
    private readonly IFirstPartyShellState? _shell;

    private ExportJobService? _job;
    private string? _nothingToExport;
    private Action<string>? _paneLog;
    private Playback2DExportStatusViewModel? _status;

    /// <summary>Creates the extension's export.</summary>
    /// <param name="extensionId">The extension, which the chip is filed under.</param>
    /// <param name="featureId">The extension's master switch, which the chip shows under.</param>
    /// <param name="exports">The app's export plumbing: the heavy-job gate and the saved folder.</param>
    /// <param name="shell">Whether something else owns the machine; null never refuses.</param>
    /// <param name="chips">The status strip; null leaves the export chip-less.</param>
    /// <param name="guard">The extension's fault scope, which its own callbacks run under.</param>
    /// <param name="reveal">Shows a finished file in the OS file manager; null on a head without one.</param>
    internal ExtensionSceneExport(string extensionId, string featureId, FirstPartyExports exports,
        IFirstPartyShellState? shell, IFirstPartyExportChips? chips, ExtensionGuard guard,
        Action<string>? reveal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentNullException.ThrowIfNull(exports);
        ArgumentNullException.ThrowIfNull(guard);
        _chipId = "ext." + extensionId + ".export";
        _featureId = featureId;
        _exports = exports;
        _shell = shell;
        _chips = chips;
        _guard = guard;
        _reveal = reveal;

        // Every seam named, the 2D tab's rule: an omitted optional is invisible at the call site. The
        // no-setup refusal is unreachable because Setup throws its own, which carries the pane's copy.
        _runner = new SceneExportRunner(
            Setup,
            surfaces: RenderSurfaceProviderFactory.CreateCpu,
            managedFfmpegDirectory: static () => FirstPartySceneExport.ManagedFfmpegDirectory,
            log: Log,
            encoderProbe: EncoderProbeCache.Shared,
            locateFfmpeg: null,
            noSetupRefusal: null);
    }

    /// <summary>
    ///     Cancels a running export and drops the chip's subscription. The host holds one of these for as long
    ///     as the extension is loaded, so in practice only a shutdown reaches it.
    /// </summary>
    public void Dispose()
    {
        _status?.Dispose();
        _job?.Dispose();
    }

    /// <inheritdoc />
    public IReadOnlyList<SceneExportSize> SizePresets { get; } =
        [.. FirstPartySceneExport.SizePresets.Select(size => new SceneExportSize(size.Label, size.Width, size.Height))];

    /// <inheritdoc />
    public IReadOnlyList<int> SupportedFps(string formatId) => SceneExportSession.SupportedFps(formatId);

    /// <inheritdoc />
    public ISceneExportPane OpenPane(SceneExportPaneRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _nothingToExport = request.NothingToExport;
        _paneLog = request.Log;

        ExportJobService job = Job();
        Func<SceneExportScene?> capture = _guard.Wrap<SceneExportScene?>("export capture", request.Capture, null);
        Playback2DExportDialogViewModel dialog = FirstPartySceneExport.NewDialog(
            _exports,
            new SceneExportDefaults(request.Choices.FormatId, request.Choices.Fps, request.Choices.Width,
                request.Choices.Height, request.Choices.IncludeHud, request.Choices.IncludeHudClock,
                request.Choices.IncludeAnnotations, request.Choices.IncludeVision),
            [.. request.Ranges.Select(range => new ExportRangeOption(range.Label, range.Start, range.End))],
            job,
            (start, end, fps, speed) => request.OutputFrameCount(start, end, fps, speed),
            _shell is null ? null : () => _shell.IsLiveSyncSessionActive,
            () => Key(capture()),
            new ExportDialogScene(request.Title, request.FileStem, Sizes(request.Sizes), Layers(request.Layers)));

        return new Pane(dialog);
    }

    /// <inheritdoc />
    public async Task<SceneExportOutcome> RenderAsync(SceneExportClip clip, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clip);
        using IMapAssetLease? art = SceneExportScenes.Acquire(clip.Scene.MapName);
        try
        {
            ExportSceneSetup setup = SceneExportScenes.ToSetup(clip.Scene, ScenePalette.Dark, art, null);
            int? held = null;
            if (setup.Source is { } source)
            {
                // No gate to wait for on this path, so the frames are built here rather than on the render's
                // thread: their count is what validation needs before anything is encoded.
                ISceneFrames frames = source();
                held = frames.FrameCount;
                setup = setup with { Source = () => frames };
            }

            if (SceneExportScenes.Resolve(clip, clip.Scene.Frames, held) is not { } request)
            {
                return new SceneExportOutcome(SceneExportRefusal.NothingToExport, OutsideTheDemoRefusal);
            }

            SceneExportSession.Validate(request.Core);
            await new SceneExportRunner(_ => setup, RenderSurfaceProviderFactory.CreateCpu,
                    static () => FirstPartySceneExport.ManagedFfmpegDirectory, clip.Log, EncoderProbeCache.Shared,
                    locateFfmpeg: null, noSetupRefusal: null)
                .RunAsync(request, NoProgress.Instance, ct).ConfigureAwait(false);
            return new SceneExportOutcome(SceneExportRefusal.None, null, clip.OutputPath);
        }
        catch (ExportValidationException ex)
        {
            return new SceneExportOutcome(SceneExportRefusal.Invalid, ex.Message);
        }
        catch (ExportRefusedException ex)
        {
            return new SceneExportOutcome(SceneExportRefusal.Refused, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SceneExportOutcome(SceneExportRefusal.Failed, ex.Message);
        }
    }

    private ExportJobService Job()
    {
        if (_job is { } existing)
        {
            return existing;
        }

        _job = _exports.NewJob(_runner, _shell is null ? null : () => _shell.IsLiveSyncSessionActive,
            _shell is null ? null : () => _shell.IsReelJobRunning, Log);
        _status = new Playback2DExportStatusViewModel(_job, _reveal);
        _chips?.Mount(_chipId, _featureId, _status);
        return _job;
    }

    // The ink session the capture rides on. A scene with no ink still needs a key, and an empty session
    // draws nothing: the ink layer renders only when the pane's layer set names it.
    private AnnotationSession? Key(SceneExportScene? scene)
    {
        if (scene is null)
        {
            return null;
        }

        AnnotationSession key = scene.Ink ?? new AnnotationSession(new AnnotationDocument());
        _captures.AddOrUpdate(key, scene);
        return key;
    }

    private ExportSceneSetup Setup(Scene2DExportRequest request)
    {
        if (request.Ink is not { } ink || !_captures.TryGetValue(ink, out SceneExportScene? scene))
        {
            throw new ExportRefusedException(_nothingToExport ?? NothingToExportRefusal);
        }

        IMapAssetLease? art = SceneExportScenes.Acquire(scene.MapName);
        try
        {
            return SceneExportScenes.ToSetup(scene, request.Palette ?? ScenePalette.Dark, art, art);
        }
        catch
        {
            art?.Dispose();
            throw;
        }
    }

    // From the export's pool thread and the encoder's own output pump; the chip marshals.
    private void Log(string line)
    {
        _status?.AppendLog(line);
        _paneLog?.Invoke(line);
    }

    private static IReadOnlyList<ExportSizeOption> Sizes(IReadOnlyList<SceneExportSize>? sizes) =>
        sizes is null
            ? FirstPartySceneExport.SizePresets
            : [.. sizes.Select(size => new ExportSizeOption(size.Label, size.Width, size.Height))];

    private static Func<bool, bool, HashSet<string>>? Layers(Func<bool, bool, IReadOnlySet<string>>? layers) =>
        layers is null ? null : (ink, clock) => new HashSet<string>(layers(ink, clock), StringComparer.Ordinal);

    private sealed class Pane(Playback2DExportDialogViewModel dialog) : ISceneExportPane
    {
        public object ViewModel => dialog;

        public event Action? Started
        {
            add => dialog.StartRequested += value;
            remove => dialog.StartRequested -= value;
        }

        public void Dispose() => dialog.Dispose();
    }

    // Nobody watches a headless clip's progress; Progress<T> would post each report to the pool for nothing.
    private sealed class NoProgress : IProgress<ExportProgress>
    {
        public static readonly NoProgress Instance = new();

        public void Report(ExportProgress value)
        {
        }
    }
}
