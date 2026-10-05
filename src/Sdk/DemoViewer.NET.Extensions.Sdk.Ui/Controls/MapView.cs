#region

using Avalonia;
using Avalonia.Controls;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Tools;

#endregion

namespace DemoViewer.NET.Extensions.Sdk.Ui.Controls;

/// <summary>
///     A CS2 map with no demo behind it: the map's radar art, its floors as panes, pan and zoom, and the
///     layers and tools you add. Name the map with <see cref="MapName" />; the host loads its art.
///     <para>
///         Composition, not inheritance: add a layer with <see cref="AddLayer" />, a pointer tool with
///         <see cref="AddTool" />, and choose the tool a press goes to with <see cref="SetPrimaryTool" />. A left
///         press the primary tool refuses pans. Space, Control and the middle button always pan, and the
///         wheel zooms.
///     </para>
///     <para>
///         Layers and tools persist across the control leaving and re-entering the visual tree; the layer
///         factories are called again whenever the map rebuilds its scene. Call members on the UI thread.
///         Outside the app (a designer, a bare test) there is no host to draw the map and the control is empty.
///     </para>
/// </summary>
public sealed class MapView : Control
{
    /// <summary>Defines the <see cref="MapName" /> property.</summary>
    public static readonly StyledProperty<string?> MapNameProperty =
        AvaloniaProperty.Register<MapView, string?>(nameof(MapName));

    private readonly IMapViewBackend? _backend;
    private readonly Dictionary<string, Func<ISceneLayer>> _layers = new(StringComparer.Ordinal);
    private readonly List<IMapTool> _tools = [];

    /// <summary>Creates the view over the host's map renderer.</summary>
    public MapView()
    {
        ClipToBounds = true;
        _backend = MapViewHost.Create();
        if (_backend is not null)
        {
            _backend.EscapePressed += () => EscapePressed?.Invoke(this, EventArgs.Empty);
            _backend.MapBound += () => MapBound?.Invoke(this, EventArgs.Empty);
            LogicalChildren.Add(_backend.View);
            VisualChildren.Add(_backend.View);
        }
    }

    /// <summary>The map's name, such as <c>de_mirage</c>, or null for no map.</summary>
    public string? MapName
    {
        get => GetValue(MapNameProperty);
        set => SetValue(MapNameProperty, value);
    }

    /// <summary>
    ///     True when the host has art for <see cref="MapName" />. The art is loaded while the view is in the visual
    ///     tree, so this is false before the view is shown. False shows nothing: say so beside the view.
    /// </summary>
    public bool HasMap => _backend?.HasMap ?? false;

    /// <summary>The bound map's floors, lowest first; empty with no map.</summary>
    public MapSpace? Space => _backend?.Space;

    /// <summary>The panes as last arranged, one per floor on screen.</summary>
    public IReadOnlyList<LevelPane> Panes => _backend?.Panes ?? [];

    /// <summary>The tool a press goes to, or null for pan and zoom.</summary>
    public IMapTool? PrimaryTool { get; private set; }

    /// <summary>Raised when Escape is pressed with no gesture to cancel.</summary>
    public event EventHandler? EscapePressed;

    /// <summary>Raised after a map is bound or unbound, once <see cref="HasMap" /> and <see cref="Space" /> describe it.</summary>
    public event EventHandler? MapBound;

    /// <summary>
    ///     A layer drawn over the radar, ordered by <see cref="ISceneLayer.Slot" /> and
    ///     <see cref="ISceneLayer.Order" />. The built layer's <see cref="ISceneLayer.Id" /> must be
    ///     <paramref name="id" />; adding an id again replaces that layer.
    /// </summary>
    /// <param name="id">The layer's id. The radar's and the floor label's ids are the view's own and are refused.</param>
    /// <param name="layer">Builds a fresh layer; called now and whenever the map rebuilds its scene.</param>
    /// <returns>Removes the layer.</returns>
    /// <exception cref="ArgumentException"><paramref name="id" /> is one of the view's own layers.</exception>
    public IDisposable AddLayer(string id, Func<ISceneLayer> layer)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(layer);
        if (MapViewHost.OwnLayerIds.Contains(id))
        {
            throw new ArgumentException($"'{id}' is one of the map view's own layers.", nameof(id));
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

    /// <summary>A pointer tool <see cref="SetPrimaryTool" /> can choose.</summary>
    /// <param name="tool">The tool.</param>
    /// <returns>Removes the tool; the view pans again if it was the primary one.</returns>
    public IDisposable AddTool(IMapTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!_tools.Contains(tool))
        {
            _tools.Add(tool);
        }

        return new Removal(() =>
        {
            _tools.Remove(tool);
            if (ReferenceEquals(PrimaryTool, tool))
            {
                SetPrimaryTool(null);
            }
        });
    }

    /// <summary>
    ///     Sends presses to <paramref name="tool" />: every press the view does not pan, left and right. A
    ///     gesture in flight is cancelled first.
    /// </summary>
    /// <param name="tool">A tool added with <see cref="AddTool" />, or null for pan and zoom.</param>
    /// <exception cref="ArgumentException"><paramref name="tool" /> was not added.</exception>
    public void SetPrimaryTool(IMapTool? tool)
    {
        if (tool is not null && !_tools.Contains(tool))
        {
            throw new ArgumentException("Add the tool with AddTool first.", nameof(tool));
        }

        PrimaryTool = tool;
        _backend?.SetPrimaryTool(tool);
    }

    /// <summary>The pane under a point of this view, or null.</summary>
    /// <param name="point">A point in this view's coordinates.</param>
    public LevelPane? PaneAt(Point point) => _backend?.PaneAt(point.X, point.Y);

    /// <summary>Cancels the gesture in flight, if any.</summary>
    public void CancelGesture() => _backend?.CancelGesture();

    /// <summary>Fits every pane to the map again, undoing the user's pan and zoom.</summary>
    public void Fit() => _backend?.Fit();

    /// <summary>Repaints, after something a layer draws changed.</summary>
    public void Invalidate() => _backend?.Invalidate();

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MapNameProperty && _backend is not null)
        {
            _backend.MapName = change.GetNewValue<string?>();
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

/// <summary>What draws a <see cref="MapView" />: the app's map renderer, installed at startup.</summary>
internal interface IMapViewBackend
{
    /// <summary>The control that draws and takes input.</summary>
    Control View { get; }

    /// <summary>The map to show.</summary>
    string? MapName { get; set; }

    /// <summary>True when art for the map was found.</summary>
    bool HasMap { get; }

    /// <summary>The bound map's floors.</summary>
    MapSpace Space { get; }

    /// <summary>The panes as last arranged.</summary>
    IReadOnlyList<LevelPane> Panes { get; }

    /// <summary>Escape with no gesture to cancel.</summary>
    event Action? EscapePressed;

    /// <summary>A map was bound or unbound.</summary>
    event Action? MapBound;

    /// <summary>Adds or replaces a layer.</summary>
    void SetLayer(string id, Func<ISceneLayer> layer);

    /// <summary>Removes a layer.</summary>
    void RemoveLayer(string id);

    /// <summary>The tool presses go to, or null for pan and zoom.</summary>
    void SetPrimaryTool(IMapTool? tool);

    /// <summary>The pane under a point of the view.</summary>
    LevelPane? PaneAt(double x, double y);

    /// <summary>Cancels the gesture in flight.</summary>
    void CancelGesture();

    /// <summary>Fits the panes to the map.</summary>
    void Fit();

    /// <summary>Repaints.</summary>
    void Invalidate();
}

/// <summary>Where the app installs the map renderer every <see cref="MapView" /> is built over.</summary>
internal static class MapViewHost
{
    /// <summary>The view's own layer ids, which <see cref="MapView.AddLayer" /> refuses.</summary>
    internal static readonly IReadOnlySet<string> OwnLayerIds =
        new HashSet<string>(StringComparer.Ordinal) { SceneLayerIds.Radar, SceneLayerIds.FloorLabel };

    /// <summary>Builds a renderer; null before the app installs one.</summary>
    internal static Func<IMapViewBackend>? Factory { get; set; }

    internal static IMapViewBackend? Create() => Factory?.Invoke();
}
