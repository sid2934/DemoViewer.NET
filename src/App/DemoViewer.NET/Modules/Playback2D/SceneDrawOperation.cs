#region

using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using DemoViewer.NET.Playback2D.Core.Compositing;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The one place the scene reaches Avalonia's render thread: a custom draw operation that leases
///     the platform's <see cref="SKCanvas" /> and hands it to the compositor.
///     <para>
///         <b>What this operation may touch</b> is deliberately tiny (plan §5.8): the immutable
///         <see cref="SceneSubmission" /> captured on the UI thread, the shared compositor — and only
///         inside the render gate — and the leased canvas. It must never reach for the view-model, a
///         control, a property, the dispatcher, or the pane set; those all live on the UI thread and
///         reading them here is the tearing bug design risk 2 is about.
///     </para>
///     <para>
///         <b>The lease is probed by failure.</b> There is no way to ask Avalonia in advance whether the
///         current backend can hand out a Skia canvas, so the operation reports the first null lease and
///         the host permanently switches to its <c>WriteableBitmap</c> path from the next frame on.
///     </para>
/// </summary>
internal sealed class SceneDrawOperation : ICustomDrawOperation
{
    private readonly SceneCompositor _compositor;
    private readonly SceneRenderGate _gate;
    private readonly Action _onLeaseUnavailable;
    private readonly SceneSubmission _submission;

    /// <summary>Creates an operation over one submission.</summary>
    /// <param name="bounds">The control's bounds, in control-local coordinates.</param>
    /// <param name="compositor">The shared layer stack.</param>
    /// <param name="gate">The host's render gate.</param>
    /// <param name="submission">The immutable frame state.</param>
    /// <param name="onLeaseUnavailable">Invoked once when no Skia lease is obtainable.</param>
    public SceneDrawOperation(Rect bounds, SceneCompositor compositor, SceneRenderGate gate,
        in SceneSubmission submission, Action onLeaseUnavailable)
    {
        Bounds = bounds;
        _compositor = compositor;
        _gate = gate;
        _submission = submission;
        _onLeaseUnavailable = onLeaseUnavailable;
    }

    /// <inheritdoc />
    public Rect Bounds { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     True inside the control's bounds. <b>This is load-bearing, not a formality.</b> A control
    ///     whose only content is a custom draw operation has no other hit-testable geometry, so
    ///     returning false here makes the whole surface transparent to the pointer — pan and zoom
    ///     silently stop working while the scene still renders perfectly. The operation paints every
    ///     pixel of <see cref="Bounds" />, so claiming them is also simply true.
    /// </remarks>
    public bool HitTest(Point p) => Bounds.Contains(p);

    /// <inheritdoc />
    /// <remarks>
    ///     Never equal. Every submission carries new state, so treating two operations as equal would
    ///     let Avalonia skip a frame that actually differs.
    /// </remarks>
    public bool Equals(ICustomDrawOperation? other) => false;

    /// <inheritdoc />
    public void Render(ImmediateDrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The non-generic overload: ImmediateDrawingContext.TryGetFeature takes a Type here, not a
        // type argument.
        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
        {
            _onLeaseUnavailable();
            return;
        }

        using ISkiaSharpApiLease lease = feature.Lease();
        SKCanvas canvas = lease.SkCanvas;

        // The canvas arrives with the control's transform and clip already applied, so the submission's
        // control-local coordinates land in the right place. Save/restore anyway: a layer that leaked a
        // clip would otherwise corrupt the rest of the window, and that is a miserable bug to find.
        int save = canvas.Save();
        try
        {
            using (_gate.Enter())
            {
                _compositor.Render(canvas, in _submission);
            }
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    /// <inheritdoc />
    /// <remarks>Nothing to release: the operation owns no unmanaged state, only references.</remarks>
    public void Dispose()
    {
    }
}
