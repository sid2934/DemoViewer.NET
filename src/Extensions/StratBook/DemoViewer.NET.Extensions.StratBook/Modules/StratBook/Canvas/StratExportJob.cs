#region

using System.Globalization;
using System.Runtime.CompilerServices;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Frames;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Hud;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Keyframes;
using DemoViewer.NET.Playback2D.Layers;
using DemoViewer.NET.Playback2D.Levels;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.ViewModels.Playback2D;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.StratBook.Canvas;

/// <summary>
///     The open strat as it stood when Export was pressed, taken on the UI thread by
///     <see cref="StratCanvasViewModel.CaptureForExport" />. Everything here is either immutable or a copy the
///     export owns: the canvas keeps editing its own track set and ink while the GIF renders.
/// </summary>
/// <param name="Projection">The path's projection: schedule, labels, utility, round length. Immutable.</param>
/// <param name="Tracks">A copy of the canvas's track set, so a drag during the render moves nothing in it.</param>
/// <param name="Ink">A private session over a <c>Reset</c> copy of the projected document, never the live one.</param>
/// <param name="MapName">The strat's map; the export loads its own bundle for it.</param>
/// <param name="Title">The strat's name, for the default file name.</param>
/// <param name="FallbackBounds">The world rectangle to frame when the map has no bundle.</param>
public sealed record StratExportCapture(
    StratSceneProjection Projection,
    TokenTrackSet Tracks,
    AnnotationSession Ink,
    string MapName,
    string Title,
    WorldBounds FallbackBounds);

/// <summary>
///     Strat Export: the strat's half of a <c>SceneExportRunner</c> run. A strat has no demo, so it
///     supplies its own <see cref="StratFrameSource" /> and its own HUD through
///     <see cref="ExportSceneSetup.Source" />; everything downstream of that is the 2D export's.
///     <para>
///         <b>The capture rides the request.</b> The dialog only knows how to freeze an ink session at Start, so
///         the capture is keyed by that session and read back by reference on the pool thread; two Starts cannot
///         trade strats, for the reason <c>Scene2DExportRequest.Ink</c> gives.
///     </para>
///     <para>
///         <b>Private art.</b> The setup takes its own hold on the map bundle and hands it to the runner as
///         <see cref="ExportSceneSetup.Owned" />: the canvas drops its own when the user opens a strat on
///         another map, which can happen mid-render.
///     </para>
/// </summary>
public static class StratExportJob
{
    /// <summary>Default frame rate: the GIF rate that divides 100 and keeps a round under the cap.</summary>
    public const int DefaultFps = 20;

    /// <summary>Default width. Square, so a radar fills it and the text arithmetic holds.</summary>
    public const int DefaultWidth = 640;

    /// <summary>Default height: the square's other side.</summary>
    public const int DefaultHeight = 640;

    /// <summary>How long the default range runs past the last step: 2 s on the strat clock.</summary>
    public const int TailTicks = 2 * StepSchedule.TicksPerSecond;

    /// <summary>The refusal when the request carries no capture this job made.</summary>
    public const string NoStratRefusal = "There is no open strat to export.";

    private static readonly ConditionalWeakTable<AnnotationSession, StratExportCapture> _captures = new();

    /// <summary>
    ///     The layers a strat export draws: the seven scene layers, then the ink and the round clock when asked
    ///     for. Named explicitly rather than left to the null-include default, which draws no opt-in layer.
    /// </summary>
    /// <param name="ink">Whether the strat's strokes are burned in.</param>
    /// <param name="clock">Whether <c>hud.clock</c> counts the round down.</param>
    public static HashSet<string> LayerIds(bool ink, bool clock)
    {
        HashSet<string> ids = new(StringComparer.Ordinal)
        {
            SceneLayerIds.Radar,
            SceneLayerIds.Trails,
            SceneLayerIds.AreaEffects,
            SceneLayerIds.Vision,
            SceneLayerIds.Markers,
            SceneLayerIds.Bomb,
            SceneLayerIds.FloorLabel
        };

        if (ink)
        {
            ids.Add(SceneLayerIds.Annotations);
        }

        if (clock)
        {
            ids.Add(SceneLayerIds.HudClock);
        }

        return ids;
    }

    /// <summary>
    ///     The ranges the dialog offers, in strat ticks, the default first: the first step to the last plus
    ///     <see cref="TailTicks" />, then the round from its start when the first step is later than that.
    /// </summary>
    /// <param name="projection">The path being exported.</param>
    public static IReadOnlyList<ExportRangeOption> Ranges(StratSceneProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        int first = projection.Ticks.Count > 0 ? projection.Ticks[0] : 0;
        int end = projection.ContentEndTick + TailTicks;
        List<ExportRangeOption> ranges =
        [
            new(Describe("First step to last + 2 s", first, end, projection), first, end)
        ];

        if (first > 0)
        {
            ranges.Add(new ExportRangeOption(Describe(projection.CountsUp ? "From the start" : "From the round's start", 0, end, projection), 0, end));
        }

        return ranges;
    }

    /// <summary>
    ///     Freezes a capture onto its ink session, the object the dialog puts on the request. Returns that session,
    ///     which is what the dialog's ink capture hands back.
    /// </summary>
    /// <param name="capture">The strat as it stands at Start.</param>
    public static AnnotationSession Register(StratExportCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        _captures.AddOrUpdate(capture.Ink, capture);
        return capture.Ink;
    }

    /// <summary>The spec a capture exports as over one range, at one rate and speed.</summary>
    /// <param name="capture">The frozen strat.</param>
    /// <param name="asset">The map bundle, or null to frame <see cref="StratExportCapture.FallbackBounds" /> on the grid.</param>
    /// <param name="startTick">First strat tick rendered.</param>
    /// <param name="endTick">Last strat tick, inclusive.</param>
    /// <param name="fps">Output frame rate.</param>
    /// <param name="speed">Playback-rate multiplier.</param>
    public static StratSceneSpec BuildSpec(StratExportCapture capture, IMapAsset? asset, int startTick,
        int endTick, int fps, double speed)
    {
        ArgumentNullException.ThrowIfNull(capture);
        StratSceneProjection projection = capture.Projection;

        // SectionHeights null and the bundle's bounds, the canvas's own spec: the canvas and the file draw the
        // same frames, and the bundle's floors reach the export through AuthoritativeFloors instead.
        return new StratSceneSpec(capture.Tracks, projection.Schedule, capture.Ink, projection.Labels,
            capture.MapName, asset is null ? [] : asset.DescribeRadars(),
            asset is null ? capture.FallbackBounds : asset.RadarBounds, null,
            projection.Utility, projection.RoundSeconds, startTick, endTick, fps, speed)
        {
            Routes = projection.Routed,
            CountsUp = projection.CountsUp
        };
    }

    /// <summary>
    ///     The scene one strat export renders: the capture riding the request's ink session, its own hold on
    ///     the strat's map art, and the strat frame source over the requested tick range. Null when the
    ///     request carries no capture this type registered, which the runner reports as
    ///     <see cref="NoStratRefusal" />.
    /// </summary>
    /// <param name="request">The run being set up. Its range is in strat ticks, not demo frames.</param>
    /// <param name="mapLoader">Takes a hold on the strat's map bundle by name; null draws the grid.</param>
    public static ExportSceneSetup? Setup(Scene2DExportRequest request, Func<string?, IMapAssetLease?> mapLoader)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mapLoader);

        if (request.Ink is not { } ink || !_captures.TryGetValue(ink, out StratExportCapture? capture))
        {
            return null;
        }

        IMapAssetLease? asset = SafeLoad(mapLoader, capture.MapName);
        try
        {
            // The dialog's range is in strat ticks: DemoStartFrame and DemoEndFrame carry them, and the dialog
            // sized the request with StratFrameSource.OutputFrameCount, the source's own arithmetic.
            StratSceneSpec spec = BuildSpec(capture, asset, request.DemoStartFrame, request.DemoEndFrame,
                request.Core.Fps, request.Core.Speed);

            // No frames and no tick rate: Source stands in for both. No vision solver either, since the
            // layer is named with the other six and draws nothing without one.
            return new ExportSceneSetup([], 0, capture.MapName, request.Palette ?? ScenePalette.Dark,
                LevelDisplayMode.Stacked, null, null, asset, spec.Ink)
            {
                Source = () => new StratFrameSource(spec),
                SourceHud = new StratHudDataSource(spec.RoundSeconds, spec.CountsUp),
                Owned = asset
            };
        }
        catch
        {
            asset?.Dispose();
            throw;
        }
    }

    private static IMapAssetLease? SafeLoad(Func<string?, IMapAssetLease?> mapLoader, string map)
    {
        try
        {
            return mapLoader(map);
        }
        catch (Exception)
        {
            return null; // the grid, as on the canvas: a strat without a bundle still exports
        }
    }

    // "First step to last + 2 s  (1:50 to 1:08)": the clock the coach wrote the steps on, +0:08 from a trigger.
    private static string Describe(string label, int fromTick, int toTick, StratSceneProjection projection) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{label}  ({Clock(projection, fromTick)} to {Clock(projection, toTick)})");

    private static string Clock(StratSceneProjection projection, int tick)
    {
        double seconds = StratClock.AtSecondsAtTick(projection.ClockInfo, tick);
        string sign = projection.CountsUp ? seconds < 0 ? "-" : "+" : seconds < 0 ? "-" : "";
        int whole = (int)Math.Round(Math.Abs(seconds));
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{whole / 60}:{whole % 60:00}");
    }
}
