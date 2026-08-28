#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>A scriptable layer: records what the compositor asked of it, draws one flat rectangle.</summary>
internal sealed class RecordingLayer : ISceneLayer
{
    public RecordingLayer(string id, LayerSlot slot = LayerSlot.World, int order = 0)
    {
        Id = id;
        Slot = slot;
        Order = order;
    }

    public SKColor? Fill { get; init; }
    public bool AdvanceResult { get; set; }
    public int AdvanceCount { get; private set; }
    public int RenderCount { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>Called on <see cref="Dispose" />, for a test that cares about teardown ORDER.</summary>
    public Action? OnDispose { get; init; }

    public string Id { get; }
    public LayerSlot Slot { get; }
    public int Order { get; }
    public LayerCacheHint Cache => LayerCacheHint.Dynamic;
    public bool IsEnabled { get; set; } = true;
    public int ContentVersion => 0;

    public bool Advance(in SceneTime time, Scene2DFrame frame)
    {
        AdvanceCount++;
        return AdvanceResult;
    }

    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        RenderCount++;
        if (Fill is not { } colour)
        {
            return;
        }

        using SKPaint paint = new();
        paint.Color = colour;
        canvas.DrawRect(ctx.PaneBounds, paint);
    }

    public void Dispose()
    {
        Disposed = true;
        OnDispose?.Invoke();
    }
}

/// <summary>
///     Records the camera every pane was drawn with, frame by frame.
///     <para>
///         The only way to see what an export actually framed: <c>SceneExportSession</c> builds its
///         <c>HeadlessSceneRenderer</c> internally, so the pane cameras are unreachable from outside — but
///         every one of them arrives here as <c>SceneRenderContext.Transform</c>, which is the exact value
///         the pixels were produced from.
///     </para>
/// </summary>
internal sealed class CameraProbeLayer : ISceneLayer
{
    private readonly List<List<ViewportTransform>> _byFrame = [];

    /// <summary>One entry per <see cref="Advance" />, holding that frame's pane cameras in draw order.</summary>
    public IReadOnlyList<IReadOnlyList<ViewportTransform>> Frames => _byFrame;

    public string Id => "test.camera-probe";
    public LayerSlot Slot => LayerSlot.World;
    public int Order => 0;
    public LayerCacheHint Cache => LayerCacheHint.Dynamic;
    public bool IsEnabled { get; set; } = true;
    public int ContentVersion => 0;

    public bool Advance(in SceneTime time, Scene2DFrame frame)
    {
        _byFrame.Add([]);
        return false;
    }

    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        if (_byFrame.Count > 0)
        {
            _byFrame[^1].Add(ctx.Transform);
        }
    }

    public void Dispose()
    {
    }
}
