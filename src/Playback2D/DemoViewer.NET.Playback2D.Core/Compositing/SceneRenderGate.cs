namespace DemoViewer.NET.Playback2D.Core.Compositing;

/// <summary>
///     Serializes the UI thread's advance-and-submit against the render thread's draw op (plan §5.8,
///     design risk 2).
///     <para>
///         The compositor's picture caches and every layer's <c>Advance</c>-built buffers are shared
///         mutable state; the draw op runs on Avalonia's render thread. One plain monitor, taken by the
///         UI thread for the whole advance and by the op for the duration of <c>Render</c>, is the whole
///         mechanism. Neither side acquires any other lock while holding it, so the design is
///         deadlock-free by construction rather than by review.
///     </para>
///     <para>
///         It lives in Core, not the App, because the assertion it exists to support —
///         <c>Debug.Assert(gate.IsHeld)</c> on every cache mutation — is inside
///         <see cref="SceneCompositor" />. Headless consumers (export, the CLI, tests) leave
///         <see cref="SceneCompositor.Gate" /> null: single-threaded callers have nothing to serialize.
///     </para>
/// </summary>
public sealed class SceneRenderGate
{
    private readonly Lock _lock = new();

    /// <summary>Whether the calling thread currently holds the gate. Debug assertions only.</summary>
    public bool IsHeld => _lock.IsHeldByCurrentThread;

    /// <summary>
    ///     Enters the gate, blocking until it is free. Dispose the returned scope to leave. No
    ///     re-entrancy is expected — the two call sites are disjoint — but the underlying monitor is
    ///     re-entrant, so a nested enter is a no-op rather than a deadlock.
    /// </summary>
    public IDisposable Enter()
    {
        _lock.Enter();
        return new Scope(_lock);
    }

    private sealed class Scope : IDisposable
    {
        private Lock? _held;

        public Scope(Lock held) => _held = held;

        public void Dispose()
        {
            Lock? held = _held;
            _held = null;
            held?.Exit();
        }
    }
}
