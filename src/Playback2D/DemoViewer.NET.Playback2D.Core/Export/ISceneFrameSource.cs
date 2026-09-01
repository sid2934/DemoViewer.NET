namespace DemoViewer.NET.Playback2D.Core.Export;

/// <summary>
///     A finite sequence of frames to render: design §5.7, verbatim.
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

/// <summary>
///     An <see cref="ISceneFrameSource" /> with a one-time, expensive warm-up: a demo-backed source
///     replaying a tracker from frame zero to reach the export's first frame (B4 D2).
///     <para>
///         Optional by design: a fixture source has nothing to prepare, and <c>SceneExportSession</c>
///         reports an <c>ExportPhase.Seeking</c> only for sources that say they need one. Implementations
///         must be <b>idempotent</b>: the export job may prepare the source before handing it over, and
///         the session prepares again rather than trusting the caller.
///     </para>
/// </summary>
public interface IPreparableFrameSource
{
    /// <summary>True until the warm-up has run.</summary>
    bool NeedsPreparation { get; }

    /// <summary>Runs the warm-up. Blocking and CPU-bound; never call it from a UI thread.</summary>
    /// <param name="ct">Cancels the warm-up.</param>
    void Prepare(CancellationToken ct);
}
