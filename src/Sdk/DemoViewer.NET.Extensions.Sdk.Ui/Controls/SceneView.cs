#region

using Avalonia;
using Avalonia.Controls;
using DemoViewer.NET.Playback2D.Compositing;
using DemoViewer.NET.Playback2D.Input;
using DemoViewer.NET.Playback2D.Layers;
using DemoViewer.NET.Playback2D.Levels;

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
///         <see cref="SetActiveTool" />, or a tool you added, by the id <see cref="AddTool" /> reports.
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

    /// <summary>
    ///     The id of the tool presses go to: one of the scene's own, or the one
    ///     <see cref="IToolHandle.ToolId" /> reported for a tool of yours.
    /// </summary>
    public string ActiveTool => _backend?.ActiveTool ?? ToolIds.Of(ToolKind.PanZoom);

    /// <summary>
    ///     Raised when the text tool placed a label and wants its text typed: the point in this view the label's
    ///     top-left sits at, and the em size in screen pixels at the current zoom. Show an editor there and hand
    ///     its result to <see cref="CompleteTextEdit" />.
    /// </summary>
    public event Action<Point, double>? TextEditRequested;

    /// <summary>
    ///     A layer drawn among the scene's own, ordered by <see cref="ISceneLayer.Slot" /> and
    ///     <see cref="ISceneLayer.Order" />. The host files it under your extension, so
    ///     <paramref name="id" /> is yours to choose: the filed form cannot collide with the scene's own
    ///     layers or with another extension's, so even an id the scene uses is yours to take locally. The
    ///     handle reports the id it was filed under, and the built layer's own
    ///     <see cref="ISceneLayer.Id" /> is not read.
    /// </summary>
    /// <param name="id">The layer's id within your extension: letters, digits, '.', '-' and '_'.</param>
    /// <param name="layer">Builds a fresh layer; called now and whenever the scene rebuilds.</param>
    /// <returns>The id it was filed under, and removes the layer.</returns>
    /// <exception cref="ArgumentException">
    ///     <paramref name="id" /> has a character outside letters, digits, '.', '-' and '_', or its filed
    ///     form is one this view or its host already holds. Host code, which has no extension to be filed
    ///     under, is refused the scene's own ids as well, since its id reaches the host unchanged.
    /// </exception>
    public ILayerHandle AddLayer(string id, Func<ISceneLayer> layer)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(layer);
        // The host first: it composes the id and refuses a duplicate, so a refused add must leave nothing
        // recorded here either. With no host to file it, the id is its own and this map is the only
        // registry there is.
        string filed = _backend?.AddLayer(id, layer) ?? id;
        if (!_layers.TryAdd(filed, layer))
        {
            throw new ArgumentException($"A layer is already registered as '{filed}'.", nameof(id));
        }

        return new LayerHandle(filed, () =>
        {
            if (_layers.TryGetValue(filed, out Func<ISceneLayer>? current) && ReferenceEquals(current, layer))
            {
                _layers.Remove(filed);
                _backend?.RemoveLayer(filed);
            }
        });
    }

    /// <summary>
    ///     A pointer tool of your own. The host files it under your extension, so its
    ///     <see cref="IPointerTool.Id" /> is yours to choose and can take neither the scene's own nor another
    ///     extension's. Select it with <see cref="SetActiveTool" /> and the handle's
    ///     <see cref="IToolHandle.ToolId" />; adding the same tool again hands back the same id.
    /// </summary>
    /// <param name="tool">The tool.</param>
    /// <returns>The id it was filed under, and removes it; the view pans again if it was active.</returns>
    /// <exception cref="ArgumentException">
    ///     <see cref="IPointerTool.Id" /> is empty, has a character outside letters, digits, '.', '-' and
    ///     '_', or is one another tool on this view already holds.
    /// </exception>
    public IToolHandle AddTool(IPointerTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        // The host first: a refused id must leave nothing added here either.
        string id = _backend?.AddTool(tool) ?? tool.Id;
        if (!_tools.Contains(tool))
        {
            _tools.Add(tool);
        }

        return new ToolHandle(id, () =>
        {
            if (_tools.Remove(tool))
            {
                _backend?.RemoveTool(tool);
            }
        });
    }

    /// <summary>
    ///     Selects the tool presses go to. A gesture in flight is cancelled first, and an open label is kept.
    ///     Reach one of the scene's own drawing tools through <see cref="ToolIds.Of" />, and one of yours
    ///     through <see cref="IToolHandle.ToolId" />.
    /// </summary>
    /// <param name="toolId">The tool's id; an id with no tool on this view pans.</param>
    public void SetActiveTool(string toolId) => _backend?.SetActiveTool(toolId);

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

    private sealed class LayerHandle(string id, Action dispose) : ILayerHandle
    {
        private Action? _dispose = dispose;

        public string LayerId => id;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    private sealed class ToolHandle(string id, Action dispose) : IToolHandle
    {
        private Action? _dispose = dispose;

        public string ToolId => id;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>A pointer tool added to a <see cref="SceneView" />: the id it was filed under, and its removal.</summary>
public interface IToolHandle : IDisposable
{
    /// <summary>
    ///     The id the host filed the tool under, which is what <see cref="SceneView.SetActiveTool" /> and
    ///     <see cref="SceneView.ActiveTool" /> speak. Not <see cref="IPointerTool.Id" />, which is the local
    ///     id you chose: the host composes that under your extension, so this is the only id that matches.
    /// </summary>
    string ToolId { get; }
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

    /// <summary>The id of the tool presses go to.</summary>
    string ActiveTool { get; }

    /// <summary>The text tool placed a label.</summary>
    event Action<Point, double>? TextEditRequested;

    /// <summary>Registers a layer, filed under its extension, and answers the id it was filed under.</summary>
    string AddLayer(string id, Func<ISceneLayer> layer);

    /// <summary>Removes a layer.</summary>
    void RemoveLayer(string id);

    /// <summary>Registers a tool, filed under its extension, and answers the id it was filed under.</summary>
    string AddTool(IPointerTool tool);

    /// <summary>Removes a tool; pans if it was active.</summary>
    void RemoveTool(IPointerTool tool);

    /// <summary>Selects the active tool by id.</summary>
    void SetActiveTool(string toolId);

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

    /// <summary>Builds a renderer; null before the app installs one.</summary>
    internal static Func<ISceneViewBackend>? Factory { get; set; }

    internal static ISceneViewBackend? Create() => Factory?.Invoke();
}
