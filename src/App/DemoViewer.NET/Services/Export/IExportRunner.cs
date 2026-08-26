#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Hud;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Vision;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Frames;

#endregion

namespace DemoViewer.NET.Services.Export;

/// <summary>
///     Does the actual rendering for one export.
///     <para>
///         The seam exists so <see cref="ExportJobService" /> can be tested for what it is responsible
///         for — refusals, the gate, single-flight, status ordering — without a demo, a compositor or an
///         ffmpeg on the machine. <see cref="SceneExportRunner" /> is the one production implementation.
///     </para>
/// </summary>
public interface IExportRunner
{
    /// <summary>Renders and encodes. Runs on a background thread; must not touch the dispatcher.</summary>
    /// <param name="request">What to render and where to put it.</param>
    /// <param name="progress">Progress reports, forwarded to the job's status.</param>
    /// <param name="ct">Cancels the render; the output file must not survive.</param>
    Task RunAsync(Scene2DExportRequest request, IProgress<ExportProgress> progress, CancellationToken ct);
}

/// <summary>
///     Everything an export needs from the live 2D tab, <b>captured once</b> when the user presses Start.
///     <para>
///         It is a capture, not a live view: the demo's frame list is immutable post-parse, and everything
///         else here is either a value or an object the export owns outright. In particular the export
///         builds its <b>own</b> compositor — sharing the window's would mean an unsynchronised layer
///         stack being advanced from two threads, which is the hazard B1 recorded as its carry-forward 28.
///     </para>
/// </summary>
/// <param name="Frames">The parsed demo's frame list. Read-only and shared safely with the app's own tracker.</param>
/// <param name="TickRate">The demo's tick rate.</param>
/// <param name="MapName">The map's logical name, for asset lookup.</param>
/// <param name="Palette">Resolved theme colours, so the video matches what the user is looking at.</param>
/// <param name="DisplayMode">Stacked or single-level, mirroring the live host.</param>
/// <param name="Vision">The line-of-sight solver, or null to export without cones.</param>
/// <param name="Hud">
///     Builds the tick → HUD state function <b>over the export's own frame source</b>, or null to export
///     without the HUD layers.
///     <para>
///         A factory rather than a value, because the clock half of the HUD has to read the frame the
///         export is drawing. Handing over a finished <c>IHudDataSource</c> meant the only thing a tab
///         could close over was its <i>live viewport's</i> frame — so every frame of the video carried
///         the scoreboard as it stood when Start was pressed, and moved if the user resumed playback
///         while it rendered. The frame source is the export's private tracker; nothing else on this
///         record can answer "what round is this frame".
///     </para>
/// </param>
/// <param name="MapAssets">The decoded map bundle, for radar art and authoritative floors. Not owned.</param>
/// <param name="Annotations">
///     The ink to burn in, or null to export without it.
///     <para>
///         A <b>snapshot</b> of the tab's document, taken on the UI thread when Start is pressed — never
///         the live session. The export renders for minutes on a pool thread while the user keeps drawing,
///         and <c>AnnotationLayer</c> re-records its cached pictures whenever <c>Document.Version</c> moves:
///         handing over the live document would put strokes made DURING the render into frames the export
///         had already passed, and would read a <c>List</c> the UI thread is mutating.
///     </para>
/// </param>
public sealed record ExportSceneSetup(
    IReadOnlyList<DemoFrame> Frames,
    int TickRate,
    string? MapName,
    ScenePalette Palette,
    LevelDisplayMode DisplayMode,
    IVisionSolver? Vision,
    Func<TrackerFrameSource, IHudDataSource>? Hud,
    LoadedMapAsset? MapAssets,
    AnnotationSession? Annotations = null);
