using CS2DemoKit.Parser;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Annotations;
using DemoViewer.NET.Playback2D.Hud;
using DemoViewer.NET.Playback2D.Levels;

namespace DemoViewer.NET.Extensions.Sdk.Export;

/// <summary>One selectable output size.</summary>
/// <param name="Label">What the user picks, e.g. <c>"Square (640x640)"</c>.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public sealed record SceneExportSize(string Label, int Width, int Height);

/// <summary>
///     One selectable range, in the units of the frames the export reads: demo frame indices for an export
///     over a demo, the caller's own clock for one over frames it holds.
/// </summary>
/// <param name="Label">What the user picks, e.g. <c>"First step to last + 2 s  (1:50 to 1:08)"</c>.</param>
/// <param name="Start">First unit, inclusive.</param>
/// <param name="End">Last unit, inclusive.</param>
public sealed record SceneExportRange(string Label, int Start, int End);

/// <summary>An extension's own starting choices for the export pane over its scene.</summary>
/// <param name="FormatId">The output format id: <c>"gif"</c>, <c>"webm"</c> or <c>"mp4"</c>.</param>
/// <param name="Fps">Output frames per second. One of <see cref="IExtensionSceneExport.SupportedFps" />.</param>
/// <param name="Width">Output width in pixels.</param>
/// <param name="Height">Output height in pixels.</param>
/// <param name="IncludeHud">Whether the HUD layers start on.</param>
/// <param name="IncludeHudClock">Whether the round clock starts on.</param>
/// <param name="IncludeAnnotations">Whether the ink starts on.</param>
/// <param name="IncludeVision">Whether the vision cones start on.</param>
public sealed record SceneExportChoices(
    string FormatId,
    int Fps,
    int Width,
    int Height,
    bool IncludeHud,
    bool IncludeHudClock,
    bool IncludeAnnotations,
    bool IncludeVision);

/// <summary>
///     Builds the frames of an export that has no demo behind it, on the render's own thread once the host
///     holds the map's art.
///     <para>
///         A factory rather than a finished <see cref="ISceneFrames" />, so the frames are built after the
///         host's heavy-job gate has let the run through, as the demo path's own source is. The art is the
///         host's hold for the duration of the run: read it, never dispose it.
///     </para>
/// </summary>
/// <param name="map">The map's art, or null where the host ships none for the scene's map.</param>
public delegate ISceneFrames SceneFramesFactory(IMapAsset? map);

/// <summary>How many output frames a range renders to at a rate and a speed.</summary>
/// <param name="start">First unit of the range, inclusive.</param>
/// <param name="end">Last unit, inclusive.</param>
/// <param name="fps">Output frame rate.</param>
/// <param name="speed">Playback-rate multiplier; 1 is realtime.</param>
public delegate int SceneExportFrameCount(int start, int end, int fps, double speed);

/// <summary>
///     The scene one export renders. Everything on it is either a value or an object the export owns
///     outright: the host renders for minutes on a background thread while the user carries on, so a live
///     view would put changes made DURING the render into frames it had already passed.
///     <para>
///         Frames come from exactly one of two places. <see cref="Frames" /> with <see cref="TickRate" /> is
///         an export over a demo's parse, which the host replays through its own private tracker.
///         <see cref="Source" /> is an export over frames the caller already holds, and stands in for both.
///     </para>
/// </summary>
public sealed record SceneExportScene
{
    /// <summary>
    ///     A parsed demo's frame list. Read-only and safe to share: the list is immutable post-parse. Leave it
    ///     empty when <see cref="Source" /> is set.
    /// </summary>
    public IReadOnlyList<DemoFrame> Frames { get; init; } = [];

    /// <summary>The demo's tick rate. 0 when <see cref="Source" /> is set.</summary>
    public int TickRate { get; init; }

    /// <summary>Frames the caller already holds, or null to replay <see cref="Frames" />.</summary>
    public SceneFramesFactory? Source { get; init; }

    /// <summary>The map's logical name (<c>de_mirage</c>), for the host's art lookup; null draws the grid.</summary>
    public string? MapName { get; init; }

    /// <summary>
    ///     The HUD the export's HUD layers read, or null for none. A clock that has to follow the frame being
    ///     drawn belongs in a <see cref="Source" />, which is the only thing here that knows which frame that
    ///     is.
    /// </summary>
    public IHudDataSource? Hud { get; init; }

    /// <summary>
    ///     The ink to burn in, or null for none. A <b>snapshot</b>, never the live session: the render reads it
    ///     off the UI thread.
    /// </summary>
    public AnnotationSession? Ink { get; init; }

    /// <summary>The scene colours, or null for the host's current theme.</summary>
    public ScenePalette? Palette { get; init; }

    /// <summary>How the map's floors are laid out. Stacked by default, as every export has been.</summary>
    public LevelDisplayMode DisplayMode { get; init; }
}

/// <summary>Why an export did not produce a file. <see cref="None" /> is the one success value.</summary>
public enum SceneExportRefusal
{
    /// <summary>It produced the file.</summary>
    None,

    /// <summary>There was nothing to render: no scene was captured, or the range lies outside the frames.</summary>
    NothingToExport,

    /// <summary>The request breaks a format rule: an unsupported rate, an odd dimension, the GIF caps.</summary>
    Invalid,

    /// <summary>The host would not start it: something else owns the machine, or a video format has no encoder.</summary>
    Refused,

    /// <summary>It was cancelled. No partial file survives.</summary>
    Cancelled,

    /// <summary>It failed while rendering or encoding.</summary>
    Failed
}

/// <summary>
///     How one export ended. A <b>typed outcome</b> rather than an exception: a refusal is an ordinary answer
///     to "can this render", and the only part of it an extension should put in front of a user is
///     <see cref="Message" />, which the host writes.
/// </summary>
/// <param name="Refusal">Why it did not produce a file, or <see cref="SceneExportRefusal.None" />.</param>
/// <param name="Message">User-facing copy for a refusal or a failure; null on success.</param>
/// <param name="OutputPath">The file written, or null when none was.</param>
public sealed record SceneExportOutcome(SceneExportRefusal Refusal, string? Message = null, string? OutputPath = null)
{
    /// <summary>True when the file at <see cref="OutputPath" /> exists and is finished.</summary>
    public bool Completed => Refusal is SceneExportRefusal.None;
}

/// <summary>
///     One clip the host renders with no pane and no status chip: the caller already knows the range, the
///     size and the file name, and nothing is watching it. Rendered on the calling job's thread, so a caller
///     that wants the host's heavy-job gate takes it itself.
/// </summary>
public sealed record SceneExportClip
{
    /// <summary>What to render.</summary>
    public required SceneExportScene Scene { get; init; }

    /// <summary>Where the encoded file goes. Its directory must exist.</summary>
    public required string OutputPath { get; init; }

    /// <summary>The output format id: <c>"gif"</c>, <c>"webm"</c> or <c>"mp4"</c>.</summary>
    public required string FormatId { get; init; }

    /// <summary>The demo the clip comes from, for the host's own diagnostics; empty where there is none.</summary>
    public string DemoPath { get; init; } = "";

    /// <summary>Output frames per second. One of <see cref="IExtensionSceneExport.SupportedFps" />.</summary>
    public required int Fps { get; init; }

    /// <summary>Output width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Output height in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>
    ///     First server tick of the clip. The host resolves ticks to frames against the scene's own frames and
    ///     clamps the range to them; a range wholly outside them is
    ///     <see cref="SceneExportRefusal.NothingToExport" />.
    /// </summary>
    public int FromTick { get; init; }

    /// <summary>Last server tick, inclusive.</summary>
    public int ToTick { get; init; }

    /// <summary>The account the camera follows, or null to frame the map.</summary>
    public ulong? FollowSteamId { get; init; }

    /// <summary>
    ///     The layers to draw. Null or empty is the default scene with no opt-in layer: no ink, no HUD and no
    ///     telestration is ever burned in without being named.
    /// </summary>
    public IReadOnlySet<string>? LayerIds { get; init; }

    /// <summary>Line sink for the chosen encoder and the encoder's own output; null discards both.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
///     The export pane the host opens over an extension's scene: the ranges, the sizes, the rate, the format,
///     the output folder, the encoder acquisition and the status chip, all the host's.
/// </summary>
public sealed record SceneExportPaneRequest
{
    /// <summary>The pane's heading.</summary>
    public required string Title { get; init; }

    /// <summary>The default file name, without its extension.</summary>
    public required string FileStem { get; init; }

    /// <summary>The ranges offered, the default first.</summary>
    public required IReadOnlyList<SceneExportRange> Ranges { get; init; }

    /// <summary>The extension's starting choices.</summary>
    public required SceneExportChoices Choices { get; init; }

    /// <summary>
    ///     How many frames a range renders to. The pane sizes the range with this and the host re-stamps the
    ///     rendered range from the built source, so the two can never disagree.
    /// </summary>
    public required SceneExportFrameCount OutputFrameCount { get; init; }

    /// <summary>
    ///     The scene as it stands, taken on the UI thread when the user presses Start. Null refuses the run
    ///     with <see cref="SceneExportRefusal.NothingToExport" />, so a scene closed while the pane was open
    ///     cannot be rendered from a stale capture.
    /// </summary>
    public required Func<SceneExportScene?> Capture { get; init; }

    /// <summary>The size presets offered, the default first; null for the host's own.</summary>
    public IReadOnlyList<SceneExportSize>? Sizes { get; init; }

    /// <summary>
    ///     Builds the layer set from the pane's ink and clock choices, in that order, or null for the host's
    ///     demo set. Non-null also hides the pane's demo-only toggles, which a scene with no kill feed, roster
    ///     or vision has nothing to answer.
    /// </summary>
    public Func<bool, bool, IReadOnlySet<string>>? Layers { get; init; }

    /// <summary>The refusal copy when <see cref="Capture" /> answers null; null for the host's own.</summary>
    public string? NothingToExport { get; init; }

    /// <summary>Line sink for the chosen encoder and the encoder's own output; null discards both.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
///     An open export pane. Dispose it to close the pane; an export that has started keeps running, and its
///     progress stays on the host's status chip.
/// </summary>
public interface ISceneExportPane : IDisposable
{
    /// <summary>
    ///     The pane's view model, for a <c>ContentControl</c> in the extension's own view. The host owns its
    ///     view: bind it, do not reach into it.
    /// </summary>
    object ViewModel { get; }

    /// <summary>Raised on the UI thread when the user starts the export.</summary>
    event Action? Started;
}

/// <summary>
///     The host's 2D scene export, as one extension sees it. Resolve it from
///     <see cref="IExtensionContext.SceneExport" />; null means this host exports nothing, which is the
///     browser build, and an extension's export surface stays hidden.
///     <para>
///         <b>One export at a time, per host.</b> The pane and the chip are the host's, and so is the refusal
///         when something else already owns the machine. An extension supplies the scene and reads the
///         outcome.
///     </para>
/// </summary>
public interface IExtensionSceneExport
{
    /// <summary>The output size presets the host's own export offers, the default first.</summary>
    IReadOnlyList<SceneExportSize> SizePresets { get; }

    /// <summary>
    ///     The frame rates a format supports, in pane order. GIF has its own, shorter list: a rate off it is
    ///     refused as <see cref="SceneExportRefusal.Invalid" /> rather than silently resampled.
    /// </summary>
    /// <param name="formatId">The output format id.</param>
    IReadOnlyList<int> SupportedFps(string formatId);

    /// <summary>Opens the host's export pane over an extension's scene.</summary>
    /// <param name="request">What the pane offers and what it exports.</param>
    ISceneExportPane OpenPane(SceneExportPaneRequest request);

    /// <summary>
    ///     Renders one clip, with no pane and no chip. Returns how it ended; it throws only for a cancellation
    ///     the caller asked for.
    /// </summary>
    /// <param name="clip">What to render and where to put it.</param>
    /// <param name="ct">Cancels the render. The output file must not survive.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct" /> was cancelled.</exception>
    Task<SceneExportOutcome> RenderAsync(SceneExportClip clip, CancellationToken ct);
}
