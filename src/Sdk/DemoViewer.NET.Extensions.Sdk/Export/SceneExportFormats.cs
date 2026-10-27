namespace DemoViewer.NET.Extensions.Sdk.Export;

/// <summary>
///     The output format ids <see cref="SceneExportChoices.FormatId" />, <see cref="SceneExportClip.FormatId" />
///     and <see cref="IExtensionSceneExport.SupportedFps" /> take.
///     <para>
///         <b>Persisted keys</b>: they reach saved defaults and command lines, so they are never renamed.
///     </para>
/// </summary>
public static class SceneExportFormats
{
    /// <summary>WebM / VP9. The default: an LGPL ffmpeg produces it, so the managed download suffices.</summary>
    public const string WebM = "webm";

    /// <summary>MP4 / H.264. Needs a GPL ffmpeg the user installed themselves.</summary>
    public const string Mp4 = "mp4";

    /// <summary>Animated GIF. The only format that works with no ffmpeg at all.</summary>
    public const string Gif = "gif";
}
