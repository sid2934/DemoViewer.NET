#region

using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;

#endregion

namespace DemoViewer.NET.Services.Export;

/// <summary>
///     The encoder half of an App export: which rung of the ffmpeg ladder to take, the refusal when there is
///     no ffmpeg and a video format was asked for, and the sink the session writes into.
///     <para>
///         Shared by <see cref="SceneExportRunner" /> (a demo) and the Strat Book's export (a strat): the two
///         differ only in the frame source and the layer stack, so the ladder, the managed GIF floor and the
///         refusal copy live once.
///     </para>
/// </summary>
/// <param name="managedFfmpegDirectory">Where an app-managed ffmpeg lives.</param>
/// <param name="locate">Finds ffmpeg given the managed directory; <c>FfmpegLocator.Locate</c> in production.</param>
/// <param name="log">Line sink for the chosen encoder and ffmpeg's stderr, or null.</param>
/// <param name="encoders">Resolves and verifies the rung.</param>
internal sealed class ExportEncoding(
    Func<string?> managedFfmpegDirectory,
    Func<string?, FfmpegLocation> locate,
    Action<string>? log,
    EncoderSelector encoders)
{
    /// <summary>
    ///     Finds ffmpeg and picks the encoder, refusing a video format with no ffmpeg. Runs BEFORE any frame is
    ///     built: the ladder walk spawns one short ffmpeg per hardware rung, and a refusal has to arrive before
    ///     the export spends a minute rendering rather than after (plan P2 D1).
    /// </summary>
    /// <exception cref="ExportRefusedException">A video format was asked for and no ffmpeg exists.</exception>
    public (FfmpegLocation Ffmpeg, EncoderSelection? Encoder) Resolve(Scene2DExportRequest request,
        CancellationToken ct)
    {
        FfmpegLocation ffmpeg = locate(managedFfmpegDirectory());
        bool gif = string.Equals(request.Core.FormatId, ExportFormats.Gif, StringComparison.Ordinal);

        if (!ffmpeg.Found && !gif)
        {
            throw new ExportRefusedException(SceneExportRunner.NoFfmpegRefusal);
        }

        EncoderSelection? encoder = gif && !ffmpeg.Found
            ? null
            : encoders.Select(request.Core.FormatId, request.EncoderOverride,
                ExportQualities.ParseOrDefault(request.Quality), ffmpeg.Directory, ct);

        if (encoder is not null)
        {
            log?.Invoke("video encoder: " + encoder.Describe());
        }

        return (ffmpeg, encoder);
    }

    /// <summary>The sink for a resolved encoder: ffmpeg when there is one, the managed GIF writer when not.</summary>
    public IFrameSink BuildSink(Scene2DExportRequest request, ExportRequest core, FfmpegLocation ffmpeg,
        EncoderSelection? encoder)
    {
        if (!ffmpeg.Found)
        {
            // The floor. Reached only for GIF: Resolve refused the video formats.
            return new ManagedGifSink(request.OutputPath, core.Fps);
        }

        return new FfmpegFrameSink(new FfmpegSinkOptions(
            request.OutputPath,
            core.FormatId,
            core.Size.Width,
            core.Size.Height,
            core.Fps,
            ffmpeg.Directory,
            encoder,
            Log: log));
    }
}
