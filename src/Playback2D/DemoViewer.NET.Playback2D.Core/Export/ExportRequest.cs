#region

using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Export;

/// <summary>
///     Everything an export renders — design §5.7, verbatim.
///     <para>
///         Frame indices are <b>source-relative</b>: they index the <see cref="ISceneFrameSource" />, not
///         the demo. <c>TrackerFrameSource.DemoFrameIndexOf</c> maps one back when a caller needs the
///         demo's own numbering.
///     </para>
/// </summary>
/// <param name="StartFrame">First source frame, inclusive.</param>
/// <param name="EndFrame">Last source frame, inclusive.</param>
/// <param name="Fps">Output frame rate. With <paramref name="Speed" /> it fixes the fixed timestep.</param>
/// <param name="Size">Output pixel size. Even in both axes for <c>webm</c>/<c>mp4</c> (plan D8).</param>
/// <param name="Speed">Playback-rate multiplier; 1 is realtime.</param>
/// <param name="FormatId">One of <see cref="ExportFormats" />. A persisted key.</param>
/// <param name="LayerIds">
///     Which layers to draw. <b>Empty means "every enabled layer"</b>, with the two HUD layers off —
///     <c>hud.clock</c> and <c>hud.killfeed</c> render only when named explicitly, because an export that
///     silently burned in a scoreboard would be a surprise, not a feature.
/// </param>
/// <param name="Camera">How the camera moves for the whole export.</param>
public sealed record ExportRequest(
    int StartFrame,
    int EndFrame,
    int Fps,
    SKSizeI Size,
    double Speed,
    string FormatId,
    IReadOnlySet<string> LayerIds,
    CameraScript Camera)
{
    /// <summary>Frames this request will produce.</summary>
    public int FrameCount => EndFrame - StartFrame + 1;

    /// <summary>The fixed timestep every frame is advanced by, in seconds.</summary>
    public double DeltaSeconds => Speed / Fps;
}

/// <summary>
///     Well-known <see cref="ExportRequest.FormatId" /> values. <b>Persisted keys</b> — they appear in
///     saved defaults and in CLI arguments, so they are never renamed.
/// </summary>
public static class ExportFormats
{
    /// <summary>WebM / VP9. The default: present in LGPL ffmpeg builds, so the managed download can produce it.</summary>
    public const string WebM = "webm";

    /// <summary>MP4 / H.264. Needs a GPL ffmpeg the user installed themselves (plan D9).</summary>
    public const string Mp4 = "mp4";

    /// <summary>Animated GIF. The only format that works with no ffmpeg at all.</summary>
    public const string Gif = "gif";

    /// <summary>Every id, in dialog order.</summary>
    public static IReadOnlyList<string> All { get; } = [WebM, Mp4, Gif];

    /// <summary>True when the format is encoded through a <c>yuv420p</c> pipeline and needs even dimensions.</summary>
    /// <param name="formatId">The format id.</param>
    public static bool RequiresEvenDimensions(string formatId) =>
        string.Equals(formatId, WebM, StringComparison.Ordinal) ||
        string.Equals(formatId, Mp4, StringComparison.Ordinal);
}
