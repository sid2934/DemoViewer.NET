using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Annotations;
using DemoViewer.NET.Playback2D.Compositing;
using DemoViewer.NET.Playback2D.Input;
using DemoViewer.NET.Playback2D.Levels;
using DemoViewer.NET.Playback2D.Timeline;
using SkiaSharp;

namespace ThirdPartyFake.Scene;

// The parts a third-party extension contributes to a scene and a timeline. They live in THIS assembly on
// purpose: fault attribution is assembly identity plus load context, so the same part written in a test
// assembly belongs to no extension and is filed under its bare id.

/// <summary>A scene layer of the fake's own.</summary>
public sealed class FakeSceneLayer : ISceneLayer
{
    /// <summary>A factory whose method lives in this assembly, which is what <c>AddLayer</c> attributes.</summary>
    public static ISceneLayer Create() => new FakeSceneLayer();

    /// <inheritdoc />
    public string Id => "heat";

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Overlay;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public LayerCacheHint Cache => LayerCacheHint.Dynamic;

    /// <inheritdoc />
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public int ContentVersion => 0;

    /// <inheritdoc />
    public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

    /// <inheritdoc />
    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>A pointer tool of the fake's own, naming itself as the extension would.</summary>
public sealed class FakeSceneTool : IPointerTool
{
    /// <inheritdoc />
    public string Id => "pin";

    /// <inheritdoc />
    public ToolKind Kind => ToolKind.Map;

    /// <inheritdoc />
    public bool OnPressed(in ToolPointerEvent e, IToolServices s) => false;

    /// <inheritdoc />
    public void OnMoved(in ToolPointerEvent e, IToolServices s)
    {
    }

    /// <inheritdoc />
    public void OnReleased(in ToolPointerEvent e, IToolServices s)
    {
    }

    /// <inheritdoc />
    public void OnCancelled(IToolServices s)
    {
    }
}

/// <summary>A timeline track of the fake's own, optionally throwing where the host builds its markers.</summary>
public sealed class FakeTimelineTrack : ITimelineTrack
{
    /// <summary>Throw from <see cref="BuildMarkers" />.</summary>
    public bool FailMarkers { get; init; }

    /// <inheritdoc />
    public string Id => "tags";

    /// <inheritdoc />
    public string DisplayName => "Fake tags";

    /// <inheritdoc />
    public event Action? Changed
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public bool IsAvailable(ITimelineData data) => true;

    /// <inheritdoc />
    public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data)
    {
        if (FailMarkers)
        {
            throw new InvalidOperationException("markers");
        }

        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data) => [];
}

/// <summary>A scene source of the fake's own, optionally throwing from the member the host reads per render.</summary>
public sealed class FakeSceneSource : ISceneSource
{
    /// <summary>Throw from <see cref="MapAsset" />.</summary>
    public bool FailMapAsset { get; init; }

    /// <inheritdoc />
    public Scene2DFrame Frame => Scene2DFrame.Empty;

    /// <inheritdoc />
    public IMapAsset? MapAsset => FailMapAsset ? throw new InvalidOperationException("map") : null;

    /// <inheritdoc />
    public AnnotationSession? Ink => null;

    /// <inheritdoc />
    public event Action? FrameUpdated
    {
        add { }
        remove { }
    }
}
