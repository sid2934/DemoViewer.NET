#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Annotations;
using DemoViewer.NET.Playback2D.Compositing;
using DemoViewer.NET.Playback2D.Input;
using DemoViewer.NET.Playback2D.Levels;
using DemoViewer.NET.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Zones;
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
    private readonly ExtensionFaults? _faults;
    private SourceFrameHost? _bound;

    /// <summary>Creates the view over a fresh scene host.</summary>
    /// <param name="faults">
    ///     The tracker a contributed layer or tool of this view is attributed to, or null for the
    ///     process's. A test host files under a scope of its own without installing one process-wide.
    /// </param>
    public HostedSceneView(ExtensionFaults? faults = null)
    {
        _faults = faults;
        ClipToBounds = true;
        _host.TextEditRequested += (at, emPixels) => TextEditRequested?.Invoke(at, emPixels);
        LogicalChildren.Add(_host);
        VisualChildren.Add(_host);
    }

    /// <summary>The scene host drawing the view.</summary>
    internal Scene2DHost Host => _host;

    // Read per add, not cached: the desktop head installs the process's tracker before the first view is
    // built, but a host built in a test or a designer may outlive one being installed.
    private ExtensionFaults? Faults => _faults ?? ExtensionFaults.Current;

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
    public string ActiveTool => _host.Router.ActiveToolId;

    /// <inheritdoc />
    public event Action<Point, double>? TextEditRequested;

    /// <inheritdoc />
    public void Dispose()
    {
        Source = null;
        _host.Dispose();
    }

    /// <inheritdoc />
    public string AddLayer(string id, Func<ISceneLayer> layer)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(layer);
        (string filed, Func<ISceneLayer> build) = ContributedSceneLayer.File(id, layer, "scene view layer factory", Faults);

        // The host holds the registry and refuses a duplicate, so nothing is recorded before it has taken it.
        _host.AddLayer(filed, build);
        return filed;
    }

    /// <inheritdoc />
    public void RemoveLayer(string id) => _host.RemoveLayer(id);

    /// <inheritdoc />
    public string AddTool(IPointerTool tool)
    {
        // The same instance again answers with the id it already holds: building a second wrapper would
        // compose the same id and the router would refuse it as a duplicate of the tool's own self.
        if (_tools.TryGetValue(tool, out IPointerTool? already))
        {
            return already.Id;
        }

        IPointerTool filed = Guard(tool);

        // Register first. A refused id must leave nothing recorded here either.
        _host.AddTool(filed);
        _tools[tool] = filed;
        return filed.Id;
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
    public void SetActiveTool(string toolId) => _host.SetActiveTool(toolId);

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

    // A tool from an extension assembly is filed under it, so it can take neither a scene tool's id nor
    // another extension's. One from no loaded extension (a test, the capture tool) keeps its bare id, and a
    // bare id that collides with a scene tool's is what the router's refusal is there to say out loud.
    private IPointerTool Guard(IPointerTool tool) =>
        Faults is { } faults && faults.Owner(tool.GetType().Assembly) is { } owner
            ? new GuardedPointerTool(tool, faults.GuardFor(owner))
            : tool;

    /// <summary>
    ///     An <see cref="ISceneSource" /> as the scene host reads it. The host discovers its frame host through
    ///     its DataContext, so this is what the host's DataContext is set to.
    /// </summary>
    internal sealed class SourceFrameHost : ISceneFrameHost, ITokenEditingHost
    {
        private readonly ExtensionGuard? _guard;

        public SourceFrameHost(ISceneSource source)
            : this(source, ExtensionFaults.Current is { } faults && faults.Owner(source.GetType().Assembly) is { } owner
                ? faults.GuardFor(owner)
                : null)
        {
        }

        /// <summary>A source read under <paramref name="guard" />, or as it is when that is null.</summary>
        public SourceFrameHost(ISceneSource source, ExtensionGuard? guard)
        {
            Source = source;
            _guard = guard;
            source.FrameUpdated += OnFrameUpdated;
        }

        public ISceneSource Source { get; }

        // The host reads these on every render, so a throwing member is a recurring site, not a counted one.
        public Scene2DFrame CurrentFrame => Read(() => Source.Frame, Scene2DFrame.Empty) ?? Scene2DFrame.Empty;

        public IMapAsset? MapAsset => Read(() => Source.MapAsset, null);

        public VisibilityEngine? VisionEngine => null;

        public AnnotationSession? AnnotationSession => Read(() => Source.Ink, null);

        public bool IsAnnotationsEnabled => AnnotationSession is not null;

        public bool ShowRadar => true;

        public bool ShowTrails => true;

        public bool ShowAreaEffects => true;

        public bool ShowVision => false;

        public bool ShowBombRing => false;

        public bool ShowViewCones => Read(() => Source.ShowViewCones, false);

        public bool ShowZones => false;

        public PlaceResolver? Zones => null;

        public ITokenEditor? TokenEditor => Read(() => Source.TokenEditor, null);

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

        // The host's handlers touch the visual tree, so a raise from a worker is posted to the UI thread.
        private void OnFrameUpdated() => UiThreadMarshal.Run(() => FrameUpdated?.Invoke());

        private T Read<T>(Func<T> member, T fallback) =>
            _guard is null ? member() : _guard.Run("scene source", member, fallback, FaultKind.Recurring);

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
    private readonly Dictionary<ITimelineTrack, ITimelineTrack> _tracks = new(ReferenceEqualityComparer.Instance);

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
    public void RegisterTrack(ITimelineTrack track)
    {
        if (_tracks.ContainsKey(track))
        {
            return;
        }

        ITimelineTrack registered = ExtensionFaults.Current is { } faults && faults.Owner(track.GetType().Assembly) is { } owner
            ? new GuardedTimelineTrack(track, faults.GuardFor(owner))
            : track;
        _tracks[track] = registered;
        _timeline.RegisterTrack(registered);
    }

    /// <inheritdoc />
    public void UnregisterTrack(ITimelineTrack track)
    {
        if (!_tracks.Remove(track, out ITimelineTrack? registered))
        {
            return;
        }

        _timeline.UnregisterTrack(registered);
        (registered as GuardedTimelineTrack)?.Detach();
    }

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

/// <summary>
///     An extension's own timeline track with every call into it run as the extension's. A throwing build
///     draws nothing for that track.
/// </summary>
internal sealed class GuardedTimelineTrack : ITimelineTrack
{
    private readonly ExtensionGuard _guard;
    private readonly ITimelineTrack _inner;

    // The id and name are read once: the timeline reads them on every rebuild.
    public GuardedTimelineTrack(ITimelineTrack inner, ExtensionGuard guard)
    {
        _inner = inner;
        _guard = guard;
        Id = guard.Run("timeline track", () => inner.Id, guard.Scope.Id + ".track");
        DisplayName = guard.Run("timeline track", () => inner.DisplayName, guard.Scope.Name);
        guard.Run("timeline track", () => _inner.Changed += OnChanged);
    }

    public string Id { get; }

    public string DisplayName { get; }

    public event Action? Changed;

    public bool IsAvailable(ITimelineData data) =>
        _guard.Run("timeline track", () => _inner.IsAvailable(data), false);

    public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data) =>
        _guard.Run("timeline track", () => _inner.BuildMarkers(data), []);

    public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data) =>
        _guard.Run("timeline track", () => _inner.BuildBands(data), []);

    public void Detach() => _guard.Run("timeline track", () => _inner.Changed -= OnChanged);

    private void OnChanged() => UiThreadMarshal.Run(() => Changed?.Invoke());
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
