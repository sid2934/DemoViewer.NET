#region

using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.ViewModels.Situations;

#endregion

namespace DemoViewer.NET.Modules.Situations;

/// <summary>
///     The Query Canvas surface: a <see cref="MapSceneHost" /> hosting the query token layer, the Overlay
///     View heatmap and the query token tool. A left drag on empty map with no rail slot armed pans: the
///     tool refuses that press and the base re-routes it to pan and zoom.
/// </summary>
public sealed class QueryCanvasHost : MapSceneHost
{
    private OverlayHeatmapLayer? _overlayLayer;
    private QueryTokenLayer? _queryLayer;
    private QueryCanvasViewModel? _vm;

    /// <inheritdoc />
    protected override ToolKind PrimaryTool => _vm is null ? ToolKind.PanZoom : ToolKind.QueryToken;

    /// <inheritdoc />
    protected override void AddLayers(SceneCompositor compositor)
    {
        // A rebuilt scene has none of the view model's layers; they are re-added at the next attach.
        _queryLayer = null;
        _overlayLayer = null;
        _vm = null;
    }

    /// <inheritdoc />
    protected override void OnEscape() => _vm?.Disarm();

    /// <inheritdoc />
    protected override void AttachDataContext(object? dataContext)
    {
        QueryCanvasViewModel? vm = dataContext as QueryCanvasViewModel;
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

        Router.CancelActive();
        _vm = vm;

        WithCompositor(compositor =>
        {
            if (_queryLayer is not null)
            {
                compositor.Remove(SceneLayerIds.Query);
                _queryLayer = null;
            }

            if (_overlayLayer is not null)
            {
                compositor.Remove(SceneLayerIds.Overlay);
                _overlayLayer = null;
            }

            if (vm is not null)
            {
                _queryLayer = new QueryTokenLayer(vm.Document);
                compositor.Add(_queryLayer);
                // Under the tokens: the heat is what a token is being placed on, never what hides it.
                _overlayLayer = new OverlayHeatmapLayer(vm.Overlay);
                compositor.Add(_overlayLayer);
            }
        });

        if (vm is null)
        {
            Router.SetActive(ToolKind.PanZoom);
            return;
        }

        Router.Register(vm.Tool);
        Router.SetActive(ToolKind.QueryToken);
        vm.MapChanged += OnMapChanged;
        vm.Document.Changed += OnDocumentChanged;
        vm.Overlay.Changed += OnDocumentChanged;
        OnMapChanged();
    }

    private void OnDocumentChanged() => InvalidateVisual();

    private void OnMapChanged() => BindMap(_vm?.Map ?? "", _vm?.MapAsset);
}
