#region

using Avalonia;
using Avalonia.Controls;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Extensions.Sdk.Ui.Controls;

/// <summary>
///     The 2D scene over frames of your own, drawn as the 2D Playback tab draws a demo: the map's radar art and
///     floors as panes, the frame's markers with their trails, smokes and fires, the ink of the source's
///     <see cref="ISceneSource.Ink" /> with the tab's drawing tools over it, pan and zoom, and the layers and
///     pointer tools you add. <see cref="MapView" /> is the static map with nothing moving.
///     <para>
///         Set <see cref="Source" /> to what to draw; the view re-reads it on every
///         <see cref="ISceneSource.FrameUpdated" />. A press goes to the source first
///         (<see cref="ISceneSource.OnPress" />), then to the active tool: one of the drawing tools, chosen with
///         <see cref="SetActiveTool" />, or a tool you added whose <see cref="IPointerTool.Kind" /> you chose.
///         Space, Control and the middle button always pan, and the wheel zooms.
///     </para>
///     <para>
///         Layers and tools persist across the control leaving and re-entering the visual tree; the layer
///         factories are called again whenever the scene rebuilds. Call members on the UI thread. Outside the
///         app (a designer, a bare test) there is no host to draw the scene and the control is empty.
///     </para>
/// </summary>
public sealed class SceneView : Control
{
    /// <summary>Defines the <see cref="Source" /> property.</summary>
    public static readonly StyledProperty<ISceneSource?> SourceProperty =
        AvaloniaProperty.Register<SceneView, ISceneSource?>(nameof(Source));

    private readonly ISceneViewBackend? _backend;
    private readonly Dictionary<string, Func<ISceneLayer>> _layers = new(StringComparer.Ordinal);
    private readonly List<IPointerTool> _tools = [];

    /// <summary>Creates the view over the host's scene renderer.</summary>
    public SceneView()
    {
        ClipToBounds = true;
        _backend = SceneViewHost.Create();
        if (_backend is not null)
        {
            _backend.TextEditRequested += (at, emPixels) => TextEditRequested?.Invoke(at, emPixels);
            LogicalChildren.Add(_backend.View);
            VisualChildren.Add(_backend.View);
        }
    }

    /// <summary>What the view draws, or null for nothing.</summary>
    public ISceneSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>The panes as last arranged, one per floor on screen.</summary>
    public IReadOnlyList<LevelPane> Panes => _backend?.Panes ?? [];

    /// <summary>The tool presses go to. <see cref="ToolKind.PanZoom" /> pans.</summary>
    public ToolKind ActiveTool => _backend?.ActiveTool ?? ToolKind.PanZoom;

    /// <summary>
    ///     Raised when the text tool placed a label and wants its text typed: the point in this view the label's
    ///     top-left sits at, and the em size in screen pixels at the current zoom. Show an editor there and hand
    ///     its result to <see cref="CompleteTextEdit" />.
    /// </summary>
    public event Action<Point, double>? TextEditRequested;

    /// <summary>
    ///     A layer drawn among the scene's own, ordered by <see cref="ISceneLayer.Slot" /> and
    ///     <see cref="ISceneLayer.Order" />. The built layer's <see cref="ISceneLayer.Id" /> must be
    ///     <paramref name="id" />; adding an id again replaces that layer.
    /// </summary>
    /// <param name="id">The layer's id. The scene's own ids are refused.</param>
    /// <param name="layer">Builds a fresh layer; called now and whenever the scene rebuilds.</param>
    /// <returns>Removes the layer.</returns>
    /// <exception cref="ArgumentException"><paramref name="id" /> is one of the scene's own layers.</exception>
    public IDisposable AddLayer(string id, Func<ISceneLayer> layer)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(layer);
        if (SceneViewHost.OwnLayerIds.Contains(id))
        {
            throw new ArgumentException($"'{id}' is one of the scene's own layers.", nameof(id));
        }

        _layers[id] = layer;
        _backend?.SetLayer(id, layer);
        return new Removal(() =>
        {
            if (_layers.TryGetValue(id, out Func<ISceneLayer>? current) && ReferenceEquals(current, layer))
            {
                _layers.Remove(id);
                _backend?.RemoveLayer(id);
            }
        });
    }

    /// <summary>
    ///     A pointer tool <see cref="SetActiveTool" /> can select by its <see cref="IPointerTool.Kind" />. A kind
    ///     the scene's own drawing tools use is replaced by the tool for this view.
    /// </summary>
    /// <param name="tool">The tool.</param>
    /// <returns>Removes the tool; the view pans again if it was active.</returns>
    public IDisposable AddTool(IPointerTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!_tools.Contains(tool))
        {
            _tools.Add(tool);
            _backend?.AddTool(tool);
        }

        return new Removal(() =>
        {
            if (_tools.Remove(tool))
            {
                _backend?.RemoveTool(tool);
            }
        });
    }

    /// <summary>Selects the tool presses go to. A gesture in flight is cancelled first, and an open label is kept.</summary>
    /// <param name="kind">The tool; an unknown kind pans.</param>
    public void SetActiveTool(ToolKind kind) => _backend?.SetActiveTool(kind);

    /// <summary>Hold-to-pan: while held, every press pans whatever tool is active. Set it from your pan key.</summary>
    /// <param name="held">Whether the pan key is down.</param>
    public void SetHoldPan(bool held) => _backend?.SetHoldPan(held);

    /// <summary>Abandons the gesture in flight, if any.</summary>
    public void CancelGesture() => _backend?.CancelGesture();

    /// <summary>Hands the text editor's result back: the typed string, or null when the edit was cancelled. A blank string removes the label.</summary>
    /// <param name="text">The typed string, or null.</param>
    public void CompleteTextEdit(string? text) => _backend?.CompleteTextEdit(text);

    /// <summary>Fits every pane to the map again, undoing the user's pan and zoom.</summary>
    public void Fit() => _backend?.Fit();

    /// <summary>The pane under a point of this view, or null.</summary>
    /// <param name="point">A point in this view's coordinates.</param>
    public LevelPane? PaneAt(Point point) => _backend?.PaneAt(point.X, point.Y);

    /// <summary>Repaints, after something a layer draws changed.</summary>
    public void Invalidate() => _backend?.Invalidate();

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty && _backend is not null)
        {
            _backend.Source = change.GetNewValue<ISceneSource?>();
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        _backend?.View.Measure(availableSize);
        return default;
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        _backend?.View.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private sealed class Removal(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>What draws a <see cref="SceneView" />: the app's scene renderer, installed at startup.</summary>
internal interface ISceneViewBackend
{
    /// <summary>The control that draws and takes input.</summary>
    Control View { get; }

    /// <summary>What to draw.</summary>
    ISceneSource? Source { get; set; }

    /// <summary>The panes as last arranged.</summary>
    IReadOnlyList<LevelPane> Panes { get; }

    /// <summary>The tool presses go to.</summary>
    ToolKind ActiveTool { get; }

    /// <summary>The text tool placed a label.</summary>
    event Action<Point, double>? TextEditRequested;

    /// <summary>Adds or replaces a layer.</summary>
    void SetLayer(string id, Func<ISceneLayer> layer);

    /// <summary>Removes a layer.</summary>
    void RemoveLayer(string id);

    /// <summary>Registers a tool under its kind.</summary>
    void AddTool(IPointerTool tool);

    /// <summary>Removes a tool; pans if it was active.</summary>
    void RemoveTool(IPointerTool tool);

    /// <summary>Selects the active tool.</summary>
    void SetActiveTool(ToolKind kind);

    /// <summary>Hold-to-pan.</summary>
    void SetHoldPan(bool held);

    /// <summary>Cancels the gesture in flight.</summary>
    void CancelGesture();

    /// <summary>The text editor's result.</summary>
    void CompleteTextEdit(string? text);

    /// <summary>Fits the panes to the map.</summary>
    void Fit();

    /// <summary>The pane under a point of the view.</summary>
    LevelPane? PaneAt(double x, double y);

    /// <summary>Repaints.</summary>
    void Invalidate();
}

/// <summary>Where the app installs the scene renderer every <see cref="SceneView" /> is built over.</summary>
internal static class SceneViewHost
{
    /// <summary>The scene's own layer ids, which <see cref="SceneView.AddLayer" /> refuses.</summary>
    internal static readonly IReadOnlySet<string> OwnLayerIds = new HashSet<string>(StringComparer.Ordinal)
    {
        SceneLayerIds.Radar, SceneLayerIds.Trails, SceneLayerIds.AreaEffects, SceneLayerIds.Vision,
        SceneLayerIds.Markers, SceneLayerIds.Bomb, SceneLayerIds.FloorLabel, SceneLayerIds.Annotations,
        SceneLayerIds.Zones
    };

    /// <summary>Builds a renderer; null before the app installs one.</summary>
    internal static Func<ISceneViewBackend>? Factory { get; set; }

    internal static ISceneViewBackend? Create() => Factory?.Invoke();
}
