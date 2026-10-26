namespace DemoViewer.NET.Playback2D;

/// <summary>
///     A finite sequence of scene frames to render, supplied by whoever already holds them.
///     <para>
///         An export over a demo builds its frames from the parse; an export over something that never
///         had a demo, a strat on a board, builds them itself and hands them over through this. The
///         frames are the only thing such a caller supplies: the layer stack, the camera, the encoder
///         and the timing stay the host's.
///     </para>
///     <para>
///         <b>Lifetime.</b> A returned <see cref="Scene2DFrame" /> is valid only until the next call on
///         the same instance, so an implementation may refill one frame in place. A renderer holds one
///         frame at a time, which is what makes that safe to promise.
///     </para>
/// </summary>
public interface ISceneFrames
{
    /// <summary>How many frames this sequence can produce.</summary>
    int FrameCount { get; }

    /// <summary>The injected clock for a frame.</summary>
    /// <param name="frameIndex">Index into this sequence, 0-based.</param>
    SceneTime TimeAt(int frameIndex);

    /// <summary>The world state for a frame.</summary>
    /// <param name="frameIndex">Index into this sequence, 0-based.</param>
    Scene2DFrame FrameAt(int frameIndex);
}
