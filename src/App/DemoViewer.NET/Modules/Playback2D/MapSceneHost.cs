#region

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     A map with no demo behind it: the playback scene's radar, level model and panes, one static frame,
///     pan and zoom. The Query Canvas and the Utility Book's map derive from it and add their own layers
///     and clicks.
///     <para>
///         <b>A sibling of <see cref="Scene2DHost" />, not a fork of it.</b> Every piece that draws or
///         routes is the same Core type the playback surface uses (<see cref="SceneCompositor" />,
///         <see cref="MapSpaceFactory" />, <see cref="PaneSet" />, <see cref="InputToolRouter" />,
///         <see cref="RadarLayer" />), and the one frame it submits is the map's static frame: the bundle's
///         radar images and bounds, no markers. It has none of the playback host's animation loop, follow
///         rigs, vision solve or annotation binding; none of those means anything with no tick.
///     </para>
///     <para>
///         A left press the <see cref="PrimaryTool" /> refuses becomes a pan; the tool is restored at the
///         release. The router's own diversions (Space, middle, Ctrl) and the wheel work unchanged.
///     </para>
/// </summary>
public abstract class MapSceneHost : Control, IDisposable
{
    private readonly SceneRenderGate _gate = new();
    private readonly MapSpaceFactory _levels = new();
    private readonly PaneSet _panes = new(new StackedLayout());
    private readonly List<LevelPaneSnapshot> _snapshots = new(4);
    private readonly Lock _submissionLock = new();
    private readonly HostToolServices _toolServices;

    private LoadedMapAsset? _boundAsset;
    private string _boundMap = "";
    private SceneCompositor _compositor;
    private WriteableBitmap? _fallbackBitmap;
    private Scene2DFrame _frame = Scene2DFrame.Empty;
    private SceneSubmission? _lastSubmission;
    private ScenePalette _palette = ScenePalette.Dark;
    private bool _panFallback;
    private RadarLayer _radarLayer;
    private bool _released;
    private long _submissionId;
    private TextBlobCache _text;

    /// <summary>Creates the host and its base layer stack.</summary>
    protected MapSceneHost()
    {
        Focusable = true;
        ClipToBounds = true;

        _toolServices = new HostToolServices(this);
        Router = new InputToolRouter(_toolServices, new PanZoomTool());

        BuildScene();
    }

    /// <summary>The pointer router: pan and zoom, plus whatever tool a derived host registers.</summary>
    internal InputToolRouter Router { get; }

    /// <summary>True once the platform refused a Skia lease; every later frame takes the CPU path.</summary>
    internal bool LeaseUnavailable { get; private set; }

    /// <summary>The tool a plain left press goes to. Pan and zoom unless a derived host has its own.</summary>
    protected virtual ToolKind PrimaryTool => ToolKind.PanZoom;

    /// <summary>The panes as arranged at the last frame, for a derived host's hit test.</summary>
    protected PaneSet Panes => _panes;

    /// <summary>The level set of the bound map, for a derived host's hit test.</summary>
    protected MapSpace LevelSpace => _levels.Space;

    /// <inheritdoc />
    public void Dispose()
    {
        ReleaseResources();
        GC.SuppressFinalize(this);
    }

    /// <summary>The pane under a host-space point, or null.</summary>
    /// <param name="x">Host X.</param>
    /// <param name="y">Host Y.</param>
    internal LevelPane? PaneAtHostPoint(float x, float y) => _panes.PaneAt(x, y);

    /// <summary>Repaint request from a pointer tool.</summary>
    internal void RequestToolRender() => InvalidateVisual();

    /// <summary>Adds a derived host's layers after every scene build. Called under the render gate.</summary>
    /// <param name="compositor">The compositor to add to.</param>
    protected virtual void AddLayers(SceneCompositor compositor)
    {
    }

    /// <summary>Re-reads the DataContext: a derived host binds its view model here.</summary>
    /// <param name="dataContext">The current DataContext, or null when detached.</param>
    protected virtual void AttachDataContext(object? dataContext)
    {
    }

    /// <summary>Escape pressed with no gesture to cancel. The query canvas disarms, the utility map steps back.</summary>
    protected virtual void OnEscape()
    {
    }

    /// <summary>Runs an action on the compositor under the render gate.</summary>
    /// <param name="action">What to do.</param>
    protected void WithCompositor(Action<SceneCompositor> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using (_gate.Enter())
        {
            action(_compositor);
        }
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachDataContext(DataContext);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_released)
        {
            BuildScene();
        }

        RefreshPalette();
        ActualThemeVariantChanged += OnThemeVariantChanged;
        AttachDataContext(DataContext);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ActualThemeVariantChanged -= OnThemeVariantChanged;
        AttachDataContext(null);
        ReleaseResources();
    }

    // ── Pointer input, translated to pane-and-world samples for the router. ──────────────────────────

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        ArgumentNullException.ThrowIfNull(e);

        Focus();
        ToolPointerEvent sample = Translate(e, ButtonOf(e));
        if (Router.OnPressed(in sample))
        {
            e.Pointer.Capture(this);
            return;
        }

        // The primary tool refused a left press: nothing armed, nothing under the pointer. That drag is a
        // pan, and the router only diverts on Space, middle and Ctrl, so the diversion is made here and
        // undone at the release.
        if (sample.Button == ToolPointerButton.Left && PrimaryTool != ToolKind.PanZoom && Router.ActiveKind == PrimaryTool)
        {
            Router.SetActive(ToolKind.PanZoom);
            if (Router.OnPressed(in sample))
            {
                _panFallback = true;
                e.Pointer.Capture(this);
                return;
            }

            Router.SetActive(PrimaryTool);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        ArgumentNullException.ThrowIfNull(e);

        if (!Router.IsGestureOpen)
        {
            return;
        }

        ToolPointerEvent sample = Translate(e, ButtonOf(e));
        Router.OnMoved(in sample);
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        ArgumentNullException.ThrowIfNull(e);

        ToolPointerEvent sample = Translate(e, ButtonOf(e.InitialPressMouseButton));
        if (Router.OnReleased(in sample))
        {
            e.Pointer.Capture(null);
            RestoreToolAfterPan();
        }
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Router.CancelActive();
        RestoreToolAfterPan();
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ArgumentNullException.ThrowIfNull(e);

        Point p = e.GetPosition(this);
        LevelPane? pane = _panes.PaneAt((float)p.X, (float)p.Y);
        if (pane is null)
        {
            return;
        }

        Router.OnWheel(new ToolWheelEvent(pane, new SKPoint((float)p.X, (float)p.Y),
            new SKPoint((float)p.X - pane.ViewportRect.Left, (float)p.Y - pane.ViewportRect.Top),
            e.Delta.Y, Translate(e.KeyModifiers)));
        e.Handled = true;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        ArgumentNullException.ThrowIfNull(e);

        switch (e.Key)
        {
            case Key.Escape:
                Router.CancelActive();
                RestoreToolAfterPan();
                OnEscape();
                e.Handled = true;
                break;
            case Key.Space:
                Router.IsSpaceHeld = true;
                e.Handled = true;
                break;
        }
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key == Key.Space)
        {
            Router.IsSpaceHeld = false;
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        ArgumentNullException.ThrowIfNull(context);

        Rect bounds = new(Bounds.Size);
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            return;
        }

        SceneSubmission submission;
        using (_gate.Enter())
        {
            submission = AdvanceAndSubmit(bounds);
        }

        if (LeaseUnavailable)
        {
            RenderCpuFallback(context, bounds, in submission);
            return;
        }

        context.Custom(new SceneDrawOperation(bounds, _compositor, _gate, in submission, OnLeaseUnavailable));
    }

    private void RestoreToolAfterPan()
    {
        if (!_panFallback)
        {
            return;
        }

        _panFallback = false;
        Router.SetActive(PrimaryTool);
    }

    // The UI thread, inside the gate: level derivation from the bundle, pane reconciliation, and the
    // submission. No camera advance: the panes hold whatever the fit or the user's pan left them at.
    private SceneSubmission AdvanceAndSubmit(Rect bounds)
    {
        SKSize host = new((float)bounds.Width, (float)bounds.Height);
        SceneTime time = _frame.Time;

        if (_levels.Update(_frame))
        {
            _compositor.InvalidateCaches();
            _panes.RetainUnarranged(_levels.Space.LastChange);
        }

        _panes.Reconcile(_levels.Space, LevelDisplayMode.Stacked, host, CurrentExtent());
        _panes.SyncCameraEpochs();
        _compositor.Advance(in time, _frame);
        _panes.CopySnapshots(_snapshots);

        SceneSubmission submission = new(
            Interlocked.Increment(ref _submissionId),
            _frame,
            time,
            _snapshots,
            _palette,
            RenderPurpose.Interactive,
            new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height),
            (float)(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0),
            _levels.Space);

        lock (_submissionLock)
        {
            _lastSubmission = submission;
        }

        return submission;
    }

    // The same WriteableBitmap path Scene2DHost takes when the platform hands out no Skia lease.
    private void RenderCpuFallback(DrawingContext context, Rect bounds, in SceneSubmission submission)
    {
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scaling));
        int height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scaling));

        if (_fallbackBitmap is null ||
            _fallbackBitmap.PixelSize.Width != width || _fallbackBitmap.PixelSize.Height != height)
        {
            _fallbackBitmap?.Dispose();
            _fallbackBitmap = new WriteableBitmap(new PixelSize(width, height),
                new Vector(96 * scaling, 96 * scaling), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        using (ILockedFramebuffer framebuffer = _fallbackBitmap.Lock())
        {
            SKImageInfo info = new(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using SKSurface surface = SKSurface.Create(info, framebuffer.Address, framebuffer.RowBytes);
            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)scaling);

            using (_gate.Enter())
            {
                _compositor.Render(canvas, in submission);
            }

            surface.Flush();
        }

        context.DrawImage(_fallbackBitmap, bounds);
    }

    private void OnLeaseUnavailable()
    {
        if (LeaseUnavailable)
        {
            return;
        }

        LeaseUnavailable = true;
        Dispatcher.UIThread.Post(InvalidateVisual);
    }

    // ── Scene lifetime. ─────────────────────────────────────────────────────────────────────────────

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_compositor), nameof(_radarLayer), nameof(_text))]
    private void BuildScene()
    {
        _text = new TextBlobCache();
        _radarLayer = new RadarLayer();
        _compositor = new SceneCompositor
        {
            Gate = _gate
        };
        _compositor.Add(_radarLayer);
        _compositor.Add(new FloorLabelLayer(_text));
        AddLayers(_compositor);

        _boundAsset = null;
        _boundMap = "";
        _released = false;
    }

    private void ReleaseResources()
    {
        if (_released)
        {
            return;
        }

        _released = true;

        using (_gate.Enter())
        {
            _compositor.Dispose();
        }

        _text.Dispose();
        _fallbackBitmap?.Dispose();
        _fallbackBitmap = null;
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e) => RefreshPalette();

    private void RefreshPalette()
    {
        _palette = ScenePaletteFactory.Build(ActualThemeVariant);
        using (_gate.Enter())
        {
            _compositor.InvalidateCaches();
        }

        InvalidateVisual();
    }

    /// <summary>
    ///     Binds a map: the radar art, the nav floors and the bounds. The level set and the panes are rebuilt
    ///     from scratch so the previous map's cameras cannot survive onto this one; the same map and bundle
    ///     again only repaints.
    /// </summary>
    /// <param name="map">The map name.</param>
    /// <param name="asset">The map's baked bundle, or null when this host has none.</param>
    protected void BindMap(string map, LoadedMapAsset? asset)
    {
        if (ReferenceEquals(asset, _boundAsset) && string.Equals(_boundMap, map, StringComparison.Ordinal)
            && _frame.Map.MapName == map)
        {
            InvalidateVisual();
            return;
        }

        _boundAsset = asset;
        _boundMap = map;
        _levels.Reset();
        _panes.Clear();

        using (_gate.Enter())
        {
            _compositor.InvalidateCaches();
        }

        _levels.SetAuthoritativeFloors(asset?.Floors);
        _levels.RadarBinder = asset is null ? null : new MapRadarBinder(asset);
        _radarLayer.RadarBoundsOverride = asset is null ? null : MapAssetPipeline.RadarBounds(asset);
        _frame = FrameFor(map, asset);
        InvalidateVisual();
    }

    // The one frame this host ever draws: the map, its radar layers and its playable extent, and no
    // players. Without a bundle there is nothing to derive a level from, so no pane is arranged and the
    // view says so beside the canvas.
    private static Scene2DFrame FrameFor(string map, LoadedMapAsset? asset)
    {
        if (asset is null)
        {
            return new Scene2DFrame
            {
                Map = new SceneMapInfo
                {
                    MapName = map
                }
            };
        }

        WorldBounds bounds = MapAssetPipeline.RadarBounds(asset);
        return new Scene2DFrame
        {
            Map = new SceneMapInfo
            {
                MapName = map,
                NetworkedBounds = bounds,
                ObservedBounds = bounds,
                Radars = MapAssetPipeline.DescribeRadars(asset)
            }
        };
    }

    private WorldBounds CurrentExtent() => _frame.Map.NetworkedBounds ?? _frame.Map.ObservedBounds;

    /// <summary>Host point to pane, pane-local point and world point, as the router and a hit test read them.</summary>
    protected ToolPointerEvent Translate(PointerEventArgs e, ToolPointerButton button)
    {
        Point position = e.GetPosition(this);
        float x = (float)position.X;
        float y = (float)position.Y;
        LevelPane? pane = _panes.PaneAt(x, y);

        SKPoint world = default;
        SKPoint local = default;
        if (pane is not null)
        {
            local = new SKPoint(x - pane.ViewportRect.Left, y - pane.ViewportRect.Top);
            (double worldX, double worldY) = pane.Camera.Current.ScreenToWorld(local.X, local.Y);
            world = new SKPoint((float)worldX, (float)worldY);
        }

        return new ToolPointerEvent
        {
            Pane = pane,
            Screen = new SKPoint(x, y),
            PaneLocal = local,
            World = world,
            Pressure = 0.5f,
            Button = button,
            Modifiers = Translate(e.KeyModifiers)
        };
    }

    private static ToolPointerButton ButtonOf(PointerEventArgs e)
    {
        PointerPointProperties properties;
        try
        {
            properties = e.GetCurrentPoint(null).Properties;
        }
        catch (InvalidOperationException)
        {
            return ToolPointerButton.Left;
        }

        if (properties.IsRightButtonPressed)
        {
            return ToolPointerButton.Right;
        }

        if (properties.IsMiddleButtonPressed)
        {
            return ToolPointerButton.Middle;
        }

        return properties.IsLeftButtonPressed ? ToolPointerButton.Left : ToolPointerButton.None;
    }

    // InitialPressMouseButton names the button that came up; the pressed flags on a release describe
    // what is still down, which is the wrong question (see Scene2DHost).
    private static ToolPointerButton ButtonOf(MouseButton button) => button switch
    {
        MouseButton.Left => ToolPointerButton.Left,
        MouseButton.Right => ToolPointerButton.Right,
        MouseButton.Middle => ToolPointerButton.Middle,
        _ => ToolPointerButton.None
    };

    private static ToolModifiers Translate(KeyModifiers modifiers)
    {
        ToolModifiers result = ToolModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            result |= ToolModifiers.Shift;
        }

        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            result |= ToolModifiers.Control;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            result |= ToolModifiers.Alt;
        }

        return result;
    }

    /// <summary>
    ///     <see cref="IToolServices" /> over this host. The annotation members are the interface's, not
    ///     these canvases': there is no ink on them, so the session is a throwaway and the anchor lookups
    ///     answer nothing.
    /// </summary>
    private sealed class HostToolServices(MapSceneHost host) : IToolServices
    {
        private readonly long _origin = Stopwatch.GetTimestamp();

        public AnnotationSession Session { get; } = new(new AnnotationDocument());

        public int CurrentTick => 0;

        public long NowMilliseconds => (long)Stopwatch.GetElapsedTime(_origin).TotalMilliseconds;

        public LevelPane? PaneAt(SKPoint screen) => host.PaneAtHostPoint(screen.X, screen.Y);

        public SKPoint ScreenToWorld(LevelPane pane, SKPoint screen)
        {
            ArgumentNullException.ThrowIfNull(pane);
            (double x, double y) = pane.Camera.Current.ScreenToWorld(
                screen.X - pane.ViewportRect.Left, screen.Y - pane.ViewportRect.Top);
            return new SKPoint((float)x, (float)y);
        }

        public SKPoint WorldToScreen(LevelPane pane, SKPoint world)
        {
            ArgumentNullException.ThrowIfNull(pane);
            (double x, double y) = pane.Camera.Current.WorldToScreen(world.X, world.Y);
            return new SKPoint((float)x + pane.ViewportRect.Left, (float)y + pane.ViewportRect.Top);
        }

        public double WorldUnitsPerPixel(LevelPane pane)
        {
            ArgumentNullException.ThrowIfNull(pane);
            double scale = pane.Camera.Current.EffectiveScale;
            return scale > 0 ? 1 / scale : 1;
        }

        public bool TryResolveEntityAnchor(LevelPane pane, SKPoint world, float worldRadius,
            out ulong steamId, out float dx, out float dy)
        {
            steamId = 0;
            dx = 0;
            dy = 0;
            return false;
        }

        public bool TryResolveDrawOffset(LevelPane pane, AnnotationElement element,
            out float offsetX, out float offsetY)
        {
            offsetX = 0;
            offsetY = 0;
            return false;
        }

        // No text tool is registered on a static map, so nothing ever asks.
        public void RequestTextEdit(Guid elementId)
        {
        }

        // No static map hosts the strat token editor.
        public ITokenEditor? Tokens => null;

        public void RequestRender() => host.RequestToolRender();
    }
}
