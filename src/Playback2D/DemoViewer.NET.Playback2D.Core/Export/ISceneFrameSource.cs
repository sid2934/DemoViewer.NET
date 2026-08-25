namespace DemoViewer.NET.Playback2D.Core.Export;

/// <summary>
///     A finite sequence of frames to render — design §5.7, verbatim.
///     <para>
///         B4 owns the export pipeline and every other type in §5.7; this interface lands early because
///         B1's benchmark harness consumes it and the harness is the CI budget gate. Declared once here
///         so B4 adds <c>IFrameSink</c>, <c>ExportRequest</c> and <c>SceneExportSession</c> alongside it
///         rather than re-declaring this one.
///     </para>
///     <para>
///         A returned <see cref="Scene2DFrame" /> follows the usual lifetime contract: it is valid until
///         the source's next call, because a source that replays a tracker refills one frame in place.
///     </para>
/// </summary>
public interface ISceneFrameSource
{
    /// <summary>How many frames this source can produce.</summary>
    int FrameCount { get; }

    /// <summary>The injected clock for a frame.</summary>
    /// <param name="frameIndex">Index into this source, 0-based.</param>
    SceneTime TimeAt(int frameIndex);

    /// <summary>The world state for a frame.</summary>
    /// <param name="frameIndex">Index into this source, 0-based.</param>
    Scene2DFrame FrameAt(int frameIndex);
}
