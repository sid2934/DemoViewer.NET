#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Timeline;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     What draws a <see cref="SceneView" />: a <see cref="Scene2DHost" /> bound to the view's
///     <see cref="ISceneSource" /> through <see cref="SourceFrameHost" />, which answers the frame host contract
///     from the source and fixes the toggles the source does not carry (radar, trails and area effects on;
///     vision, the bomb ring and zone outlines off). A layer factory, a tool or a source call an extension
///     wrote runs under its guard.
/// </summary>
internal sealed class HostedSceneView : Control, ISceneViewBackend, IDisposable
{
    private readonly Scene2DHost _host = new();
    private readonly Dictionary<IPointerTool, IPointerTool> _tools = new(ReferenceEqualityComparer.Instance);
    private SourceFrameHost? _bound;

    /// <summary>Creates the view over a fresh scene host.</summary>
    public HostedSceneView()
    {
        ClipToBounds = true;
        _host.TextEditRequested += (at, emPixels) => TextEditRequested?.Invoke(at, emPixels);
        LogicalChildren.Add(_host);
        VisualChildren.Add(_host);
    }

    /// <summary>The scene host drawing the view.</summary>
    internal Scene2DHost Host => _host;

    /// <inheritdoc />
    public Control View => this;

    /// <inheritdoc />
    public ISceneSource? Source
    {
        get => _bound?.Source;
        set
        {
            if (ReferenceEquals(_bound?.Source, value))
            {
                return;
            }

            _bound?.Detach();
            _bound = value is null ? null : new SourceFrameHost(value);
            _host.DataContext = _bound;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<LevelPane> Panes => _host.Panes;

    /// <inheritdoc />
    public ToolKind ActiveTool => _host.Router.ActiveKind;

    /// <inheritdoc />
    public event Action<Point, double>? TextEditRequested;

    /// <inheritdoc />
    public void Dispose()
    {
        Source = null;
        _host.Dispose();
    }

    /// <inheritdoc />
    public void SetLayer(string id, Func<ISceneLayer> layer) => _host.AddLayer(id, () => Build(id, layer));

    /// <inheritdoc />
    public void RemoveLayer(string id) => _host.RemoveLayer(id);

    /// <inheritdoc />
    public void AddTool(IPointerTool tool)
    {
        IPointerTool guarded = Guard(tool);
        _tools[tool] = guarded;
        _host.AddTool(guarded);
    }

    /// <inheritdoc />
    public void RemoveTool(IPointerTool tool)
    {
        if (_tools.Remove(tool, out IPointerTool? guarded))
        {
            _host.RemoveTool(guarded);
        }
    }

    /// <inheritdoc />
    public void SetActiveTool(ToolKind kind) => _host.SetActiveTool(kind);

    /// <inheritdoc />
    public void SetHoldPan(bool held) => _host.SetSpacePanHeld(held);

    /// <inheritdoc />
    public void CancelGesture() => _host.CancelActiveGesture();

    /// <inheritdoc />
    public void CompleteTextEdit(string? text) => _host.CompleteTextEdit(text);

    /// <inheritdoc />
    public void Fit() => _host.FitToExtent();

    /// <inheritdoc />
    public LevelPane? PaneAt(double x, double y) => _host.PaneAtHostPoint((float)x, (float)y);

    /// <inheritdoc />
    public void Invalidate() => _host.InvalidateVisual();

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        _host.Measure(availableSize);
        return default;
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        _host.Arrange(new Rect(finalSize));
        return finalSize;
    }

    // An extension's layer or tool runs as the extension's; anything else runs as it is.
    private static ISceneLayer Build(string id, Func<ISceneLayer> factory)
    {
        if (ExtensionFaults.Current is not { } faults || faults.Owner(factory) is not { } owner)
        {
            return factory();
        }

        ExtensionGuard guard = faults.GuardFor(owner);
        ISceneLayer? built = guard.Run("scene view layer factory", factory, null);
        return built is null ? new GuardedSceneLayer(id, EmptySceneLayer.Instance, guard) : new GuardedSceneLayer(id, built, guard);
    }

    private static IPointerTool Guard(IPointerTool tool) =>
        ExtensionFaults.Current is { } faults && faults.Owner(tool.GetType().Assembly) is { } owner
            ? new GuardedPointerTool(tool, faults.GuardFor(owner))
            : tool;

    /// <summary>
    ///     An <see cref="ISceneSource" /> as the scene host reads it. The host discovers its frame host through
    ///     its DataContext, so this is what the host's DataContext is set to.
    /// </summary>
    private sealed class SourceFrameHost : ISceneFrameHost, ITokenEditingHost
    {
        private readonly ExtensionGuard? _guard;

        public SourceFrameHost(ISceneSource source)
        {
            Source = source;
            _guard = ExtensionFaults.Current is { } faults && faults.Owner(source.GetType().Assembly) is { } owner
                ? faults.GuardFor(owner)
                : null;
            source.FrameUpdated += OnFrameUpdated;
        }

        public ISceneSource Source { get; }

        public Scene2DFrame CurrentFrame => Source.Frame;

        public IMapAsset? MapAsset => Source.MapAsset;

        public VisibilityEngine? VisionEngine => null;

        public AnnotationSession? AnnotationSession => Source.Ink;

        public bool IsAnnotationsEnabled => Source.Ink is not null;

        public bool ShowRadar => true;

        public bool ShowTrails => true;

        public bool ShowAreaEffects => true;

        public bool ShowVision => false;

        public bool ShowBombRing => false;

        public bool ShowViewCones => Source.ShowViewCones;

        public bool ShowZones => false;

        public PlaceResolver? Zones => null;

        public ITokenEditor? TokenEditor => Source.TokenEditor;

        public event Action? FrameUpdated;

        public void Detach() => Source.FrameUpdated -= OnFrameUpdated;

        public void ApplyAnnotationLevelRebuild(IReadOnlyDictionary<double, double> zMinMap)
        {
            if (_guard is null)
            {
                Source.OnLevelsMoved(zMinMap);
                return;
            }

            _guard.Run("scene source", () => Source.OnLevelsMoved(zMinMap));
        }

        public bool TryPointerPreHandler(ScenePointer press)
        {
            ScenePress converted = new(press.Level, press.WorldX, press.WorldY,
                new Point(press.Screen.X, press.Screen.Y), Convert(press.Modifiers));
            return _guard is null
                ? Source.OnPress(converted)
                : _guard.Run("scene source", () => Source.OnPress(converted), false, FaultKind.Recurring);
        }

        private void OnFrameUpdated() => FrameUpdated?.Invoke();

        private static KeyModifiers Convert(ToolModifiers modifiers)
        {
            KeyModifiers result = KeyModifiers.None;
            if ((modifiers & ToolModifiers.Shift) != 0)
            {
                result |= KeyModifiers.Shift;
            }

            if ((modifiers & ToolModifiers.Control) != 0)
            {
                result |= KeyModifiers.Control;
            }

            if ((modifiers & ToolModifiers.Alt) != 0)
            {
                result |= KeyModifiers.Alt;
            }

            return result;
        }
    }
}

/// <summary>
///     What draws a <see cref="SceneTimeline" />: the 2D tab's own <see cref="Playback2DTimelineViewModel" />,
///     shown through a fresh <see cref="TimelineControl" /> per <see cref="TimelineView" />.
/// </summary>
internal sealed class HostedTimeline : ITimelineBackend
{
    private readonly Playback2DTimelineViewModel _timeline = new() { IsVisible = true };

    /// <summary>Creates the timeline and forwards its seeks.</summary>
    public HostedTimeline()
    {
        _timeline.SeekRequested += frameIndex => SeekRequested?.Invoke(frameIndex);
    }

    /// <summary>The view model the control binds.</summary>
    internal Playback2DTimelineViewModel ViewModel => _timeline;

    /// <inheritdoc />
    public int TotalFrames => _timeline.TotalFrames;

    /// <inheritdoc />
    public IReadOnlyList<TimelineMarker> Markers => _timeline.BuiltMarkers;

    /// <inheritdoc />
    public IReadOnlyList<TimelineBand> Bands => _timeline.BuiltBands;

    /// <inheritdoc />
    public event Action<int>? SeekRequested;

    /// <inheritdoc />
    public void RegisterTrack(ITimelineTrack track) => _timeline.RegisterTrack(track);

    /// <inheritdoc />
    public void UnregisterTrack(ITimelineTrack track) => _timeline.UnregisterTrack(track);

    /// <inheritdoc />
    public void Rebuild(ITimelineData? data) => _timeline.Rebuild(data);

    /// <inheritdoc />
    public void UpdatePlayhead(int frameIndex, int tick) => _timeline.UpdatePlayhead(frameIndex, tick);

    /// <inheritdoc />
    public void RequestSeek(int frameIndex) => _timeline.RequestSeekToFrame(frameIndex);

    /// <inheritdoc />
    public Control CreateView() => new TimelineControl { DataContext = _timeline };

    /// <inheritdoc />
    public void Dispose() => _timeline.Dispose();
}

/// <summary>Installs the renderers the SDK's map, scene and timeline controls are built over.</summary>
internal static class SdkSceneHosts
{
    /// <summary>
    ///     Sets every factory. Idempotent. Call before the first control or <see cref="SceneTimeline" /> is
    ///     built: one built earlier has no renderer and stays empty.
    /// </summary>
    public static void Install()
    {
        MapViewHost.Factory = static () => new HostedMapView();
        SceneViewHost.Factory = static () => new HostedSceneView();
        TimelineHost.Factory = static () => new HostedTimeline();
    }
}
