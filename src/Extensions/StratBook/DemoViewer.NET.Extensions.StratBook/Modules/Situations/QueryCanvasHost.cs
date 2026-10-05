#region

using Avalonia;
using Avalonia.Controls;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.ViewModels.Situations;

#endregion

namespace DemoViewer.NET.Modules.Situations;

/// <summary>
///     The Query Canvas surface: a <see cref="MapView" /> with the query token layer, the Overlay View heatmap
///     and the query token tool as its primary tool. A left drag on empty map with no rail slot armed pans:
///     the tool refuses that press and the view pans instead.
/// </summary>
public sealed class QueryCanvasHost : Decorator
{
    private readonly List<IDisposable> _attached = [];
    private readonly MapView _map = new();
    private QueryCanvasViewModel? _vm;

    /// <summary>Creates the surface.</summary>
    public QueryCanvasHost()
    {
        Child = _map;
        _map.EscapePressed += (_, _) => _vm?.Disarm();
    }

    /// <summary>The map view this surface is built on. For tests.</summary>
    internal MapView Map => _map;

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (VisualRoot is not null)
        {
            Attach(DataContext as QueryCanvasViewModel);
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach(DataContext as QueryCanvasViewModel);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Attach(null);
    }

    private void Attach(QueryCanvasViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm))
        {
            return;
        }

        if (_vm is not null)
        {
            _vm.MapChanged -= OnMapChanged;
            _vm.Document.Changed -= OnDocumentChanged;
            _vm.Overlay.Changed -= OnDocumentChanged;
        }

        _map.CancelGesture();
        _map.SetPrimaryTool(null);
        foreach (IDisposable added in _attached)
        {
            added.Dispose();
        }

        _attached.Clear();
        _vm = vm;
        if (vm is null)
        {
            return;
        }

        _attached.Add(_map.AddLayer(SceneLayerIds.Query, () => new QueryTokenLayer(vm.Document)));
        // Under the tokens: the heat is what a token is being placed on, never what hides it.
        _attached.Add(_map.AddLayer(SceneLayerIds.Overlay, () => new OverlayHeatmapLayer(vm.Overlay)));
        _attached.Add(_map.AddTool(vm.Tool));
        _map.SetPrimaryTool(vm.Tool);
        vm.MapChanged += OnMapChanged;
        vm.Document.Changed += OnDocumentChanged;
        vm.Overlay.Changed += OnDocumentChanged;
        OnMapChanged();
    }

    private void OnDocumentChanged() => _map.Invalidate();

    private void OnMapChanged() => _map.MapName = _vm?.Map;
}
