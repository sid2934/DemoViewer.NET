#region

using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Cameras;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Vision;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The Playback2D v2 surface: a bare <see cref="Control" /> that owns a
///     <see cref="SceneCompositor" /> and submits one immutable frame per paint to a custom draw
///     operation.
///     <para>
///         <b>The Advance/Render split is the whole design.</b> Everything that mutates — the level
///         set, pane reconciliation, camera lerps, marker smoothing, the vision solve — happens on the
///         UI thread inside <see cref="AdvanceAndSubmit" />, under the render gate. The draw operation
///         then replays immutable state on Avalonia's render thread. The pre-v2 control did all of that
///         inside <c>Control.Render</c>, which is why it could not be exported, benchmarked or tested
///         without a window.
///     </para>
///     <para>
///         The self-terminating animation loop is preserved exactly: it re-arms only while a camera is
///         still settling or a marker is still gliding, so an idle tab requests no frames at all.
///     </para>
/// </summary>
public sealed class Scene2DHost : Control, IPlayback2DSurface, ILevelSurface, IDisposable
{
    private readonly LevelCrossingTracker _crossings = new();
    private readonly SceneRenderGate _gate = new();
    private readonly PanZoomGesture _gesture = new();
    private readonly MapSpaceFactory _levels = new();
    private readonly LevelSelection _levelSelection;
    private readonly PaneSet _panes;
    private readonly SingleLayout _singleLayout = new();
    private readonly StackedLayout _stackedLayout = new();
    private readonly List<LevelPaneSnapshot> _snapshots = new(4);
    private readonly MarkerSmoother _smoother = new();
    private LevelDisplayMode _displayMode = LevelDisplayMode.Stacked;

    private SceneCompositor _compositor;
    private RadarLayer _radarLayer;
    private VisionLayer _visionLayer;
    private TextBlobCache _text;

    private WriteableBitmap? _fallbackBitmap;
    private int _followSlot = -1;
    private int _frameLoopArmCount;
    private bool _frameLoopArmed;
    private bool _havePrevFrameTime;
    private bool _initialFitApplied;
    private double _lastDt = 1.0 / 60;
    private (int FrameIndex, int Tick) _lastFrameIdentity = (-1, -1);
    private bool _leaseUnavailable;
    private bool _released;
    private CameraMode _mode = CameraMode.Fit;
    private ScenePalette _palette = ScenePalette.Dark;
    private TimeSpan _prevFrameTime;
    private readonly Lock _submissionLock = new();
    private int _gateStressFrames;
    private SceneSubmission? _lastSubmission;
    private long _submissionId;
    private Playback2DTabViewModel? _vm;

    /// <summary>Creates the host and registers the seven scene layers.</summary>
    public Scene2DHost()
    {
        Focusable = true;
        ClipToBounds = true;

        _panes = new PaneSet(_stackedLayout);
        _smoother.LevelCrossings = _crossings;

        _levelSelection = new LevelSelection(_levels.Space);
        _levelSelection.ActiveLevelChanged += OnActiveLevelChanged;
        _levels.Space.LevelSetChanged += OnLevelSetChanged;

        BuildScene();
    }

    /// <summary>
    ///     Builds the text cache, the seven layers and the compositor over them.
    ///     <para>
    ///         Separate from the constructor because the host <i>releases</i> all of it on detach and
    ///         has to be able to build it again on a re-attach: Avalonia detaches and re-attaches the
    ///         same control on a re-parent, a re-template and a presenter recycling its content, and a
    ///         host that could only be born once renders nothing for the rest of the session.
    ///     </para>
    /// </summary>
    [MemberNotNull(nameof(_compositor), nameof(_radarLayer), nameof(_visionLayer), nameof(_text))]
    private void BuildScene()
    {
        _text = new TextBlobCache();
        _radarLayer = new RadarLayer();
        _visionLayer = new VisionLayer(
            new VisibilityEngineSolver(() => _vm?.VisionEngine, _smoother), _smoother);

        _compositor = new SceneCompositor
        {
            Gate = _gate
        };
        _compositor.Add(_radarLayer);
        _compositor.Add(new TrailLayer());
        _compositor.Add(new AreaEffectLayer());
        _compositor.Add(_visionLayer);
        _compositor.Add(new MarkerLayer(_smoother, _text));
        _compositor.Add(new BombLayer());
        _compositor.Add(new FloorLabelLayer(_text));

        // The map bundle is re-pulled on the next SyncFromViewModel, so the fresh radar layer is bound.
        _boundAsset = null;
        _released = false;
    }

    /// <summary>
    ///     Releases the compositor, its layers and the fallback bitmap. Also runs on detach: a tab's
    ///     view is destroyed and rebuilt on every activation, so leaking one compositor's worth of
    ///     SKPaints, SKPaths and recorded pictures per activation would be a steady native-memory climb.
    ///     Idempotent.
    /// </summary>
    public void Dispose() => ReleaseResources();

    /// <summary>The layer stack. B2 and B4 register their layers on it.</summary>
    public SceneCompositor Compositor => _compositor;

    /// <summary>The layout policy. B3 swaps in <c>SingleLayout</c> here.</summary>
    public ILevelLayoutPolicy LayoutPolicy
    {
        get => _panes.Policy;
        set => _panes.Policy = value;
    }

    /// <summary>The resolved level set. B3's level strip reads it.</summary>
    public MapSpace Levels => _levels.Space;

    /// <inheritdoc />
    public event Action? LevelStateChanged;

    /// <inheritdoc />
    public LevelDisplayMode DisplayMode
    {
        get => _displayMode;
        set
        {
            if (_displayMode == value || value == LevelDisplayMode.SideBySide)
            {
                return;
            }

            _displayMode = value;
            _panes.Policy = value == LevelDisplayMode.Single ? _singleLayout : _stackedLayout;

            // Every recorded picture is pane-local, and every pane just changed shape.
            using (_gate.Enter())
            {
                _compositor.InvalidateCaches();
            }

            LevelStateChanged?.Invoke();
            InvalidateVisual();
        }
    }

    /// <inheritdoc />
    public bool AutoLevelFollow
    {
        get => _levelSelection.Mode == LevelSelectionMode.AutoFollow;
        set
        {
            if (value == AutoLevelFollow)
            {
                return;
            }

            if (value)
            {
                _levelSelection.EnableAutoFollow();
            }
            else
            {
                _levelSelection.PickManually(_levelSelection.ActiveLevelId);
            }

            LevelStateChanged?.Invoke();
            InvalidateVisual();
        }
    }

    /// <inheritdoc />
    public MapLevelId ActiveLevelId => _levelSelection.ActiveLevelId;

    /// <inheritdoc />
    public void PickLevel(MapLevelId id)
    {
        _levelSelection.PickManually(id);
        DisplayMode = LevelDisplayMode.Single;
        _singleLayout.ActiveLevelId = _levelSelection.ActiveLevelId;
        LevelStateChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>
    ///     Follow-camera deadzone half-extent in world units. B1's one deliberate behaviour change;
    ///     0 reproduces the pre-v2 feel exactly.
    /// </summary>
    public double FollowDeadzoneHalfWorld { get; set; } = 180;

    /// <summary>Test hook: the rendered transform of the lowest pane. Same name as the pre-v2 control's.</summary>
    internal ViewportTransform PrimaryCameraTransform =>
        _panes.Panes.Count > 0 ? _panes.Panes[0].Camera.Current : default;

    /// <summary>Test hook: how many panes are arranged. 1 under <c>Single</c>, one per floor under <c>Stacked</c>.</summary>
    internal int PaneCountForTest => _panes.Panes.Count;

    /// <summary>Test hook: the level the first arranged pane is showing.</summary>
    internal MapLevelId PrimaryPaneLevelForTest =>
        _panes.Panes.Count > 0 ? _panes.Panes[0].LevelId : MapLevelId.None;

    /// <summary>Test hook: which entities changed floor on the last advanced frame.</summary>
    internal LevelCrossingTracker CrossingsForTest => _crossings;

    /// <summary>Test hook: whether the lowest pane is in manual override.</summary>
    internal bool PrimaryCameraManual =>
        _panes.Panes.Count > 0 && _panes.Panes[0].Camera.ManualOverride;

    /// <summary>Test hook: could-see segments solved for the last advance.</summary>
    internal int SightlineCount => _visionLayer.SightlineCount;

    /// <summary>Test hook: true once the Skia lease failed and the CPU fallback took over.</summary>
    internal bool LeaseUnavailable => _leaseUnavailable;

    /// <summary>Test hook: forces the CPU fallback path on, so it is exercised without a broken backend.</summary>
    internal void ForceLeaseUnavailableForTest() => _leaseUnavailable = true;

    /// <summary>
    ///     Test hook: how many times the animation loop has been armed. The loop is
    ///     <b>self-terminating</b> — it re-arms only while a camera is settling or a marker is gliding —
    ///     so on an idle tab this stops growing. A loop that spins forever burns a core in the
    ///     background and is invisible until someone notices the fan.
    /// </summary>
    internal int FrameLoopArmCountForTest => _frameLoopArmCount;

    /// <summary>Test hook: the id of the most recent submission. Must be strictly monotonic.</summary>
    internal long LastSubmissionIdForTest => Interlocked.Read(ref _submissionId);

    /// <summary>Test hook: how many frames the gate stress worker managed to draw.</summary>
    internal int GateStressFramesForTest => _gateStressFrames;

    /// <summary>
    ///     Test hook: replays the last submission on the CALLING thread, exactly as the draw operation
    ///     would. Used by the render-gate stress test to put a real second thread against the compositor
    ///     while the UI thread advances (design risk 2); there is no other way to exercise that race
    ///     without a real GPU backend under the headless harness.
    /// </summary>
    /// <param name="canvas">A canvas the caller owns.</param>
    internal void RenderForGateStressTest(SKCanvas canvas)
    {
        SceneSubmission submission;
        lock (_submissionLock)
        {
            if (_lastSubmission is not { } captured)
            {
                return;
            }

            submission = captured;
        }

        using (_gate.Enter())
        {
            _compositor.Render(canvas, in submission);
        }

        Interlocked.Increment(ref _gateStressFrames);
    }

    /// <summary>The active camera mode. Re-arms every pane's auto camera and re-applies the fit.</summary>
    public CameraMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            _panes.ResetAll();
            ApplyRigs();

            if (_mode == CameraMode.Fit)
            {
                _panes.FitAll(CurrentExtent());
            }

            ArmFrameLoopIfNeeded();
            InvalidateVisual();
        }
    }

    /// <summary>The slot the follow camera tracks; -1 = none. Setting it also selects follow mode.</summary>
    public int FollowSlot
    {
        get => _followSlot;
        set
        {
            _followSlot = value;
            Mode = CameraMode.FollowPlayer;
        }
    }

    /// <summary>Re-frames every pane to the observed extent and clears the manual overrides.</summary>
    public void FitToExtent()
    {
        _mode = CameraMode.Fit;
        _followSlot = -1;
        _initialFitApplied = true;
        ApplyRigs();
        _panes.ResetAll();
        _panes.FitAll(CurrentExtent());
        InvalidateVisual();
    }

    /// <summary>Test hook: the smoothed draw position for a slot. Same name as the pre-v2 control's.</summary>
    /// <param name="slot">Roster slot.</param>
    internal (float X, float Y)? SmoothedMarkerPosition(int slot) => _smoother.Position(slot);

    /// <summary>Test hook: drives the marker smoothing with a known dt.</summary>
    /// <param name="markers">The markers to chase.</param>
    /// <param name="dt">Seconds since the previous frame.</param>
    internal bool AdvanceMarkers(IReadOnlyList<PlayerMarker> markers, double dt) =>
        _smoother.Advance(markers, dt);

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachVm(DataContext as Playback2DTabViewModel);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // A re-attach of a host that was released on a previous detach. Rebuild before anything below
        // touches the compositor — RefreshPalette invalidates its caches on the very next line.
        if (_released)
        {
            BuildScene();
        }

        RefreshPalette();
        ActualThemeVariantChanged += OnThemeVariantChanged;
        AttachVm(DataContext as Playback2DTabViewModel);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ActualThemeVariantChanged -= OnThemeVariantChanged;
        AttachVm(null);
        _frameLoopArmed = false;
        _havePrevFrameTime = false;

        ReleaseResources();
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

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        ArgumentNullException.ThrowIfNull(e);

        Point p = e.GetPosition(this);
        _gesture.Press(_panes, (float)p.X, (float)p.Y);
        e.Pointer.Capture(this);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        ArgumentNullException.ThrowIfNull(e);

        Point p = e.GetPosition(this);
        if (_gesture.Move((float)p.X, (float)p.Y))
        {
            InvalidateVisual();
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        ArgumentNullException.ThrowIfNull(e);

        _gesture.Release();
        e.Pointer.Capture(null);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ArgumentNullException.ThrowIfNull(e);

        Point p = e.GetPosition(this);
        if (_gesture.Wheel(_panes, (float)p.X, (float)p.Y, e.Delta.Y))
        {
            InvalidateVisual();
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

        if (_leaseUnavailable)
        {
            RenderCpuFallback(context, bounds, in submission);
            return;
        }

        context.Custom(new SceneDrawOperation(bounds, _compositor, _gate, in submission,
            OnLeaseUnavailable));
    }

    // ── UI thread, inside the gate. The ONLY place scene state mutates. ───────────────────────────────

    private SceneSubmission AdvanceAndSubmit(Rect bounds)
    {
        Scene2DFrame frame = _vm?.CurrentFrame ?? Scene2DFrame.Empty;

        // A seek must be consumed exactly once. The animation loop re-renders the SAME frame many times
        // while markers glide, and re-applying the discontinuity on each of those would freeze every
        // dot on its raw position for as long as the loop ran.
        (int FrameIndex, int Tick) identity = (frame.Time.FrameIndex, frame.Time.Tick);
        bool discontinuity = frame.Time.IsDiscontinuity && identity != _lastFrameIdentity;
        _lastFrameIdentity = identity;

        SceneTime time = frame.Time with
        {
            DeltaSeconds = _lastDt,
            IsDiscontinuity = discontinuity
        };

        if (discontinuity)
        {
            ResetDeadzones();
            _crossings.Reset();
        }

        SKSize host = new((float)bounds.Width, (float)bounds.Height);

        if (_levels.Update(frame))
        {
            // A rebuilt level set invalidates every recorded picture: the bands moved, and a PerCamera
            // picture is keyed on a level id that may now describe a different Z range.
            _compositor.InvalidateCaches();
            _crossings.Reset();
            _panes.RetainUnarranged(_levels.Space.LastChange);
        }

        // The followed player's level wins: A1's follow funnel sets _followSlot, and AutoFollow shows
        // whichever floor that player is on. Nothing followed leaves the choice where the user put it.
        _levelSelection.FollowedSlot =
            _mode == CameraMode.FollowPlayer && _followSlot >= 0 ? _followSlot : null;
        _levelSelection.Update(in time, frame);
        _singleLayout.ActiveLevelId = _levelSelection.ActiveLevelId;

        if (_panes.Reconcile(_levels.Space, _displayMode, host, CurrentExtent()))
        {
            ApplyRigs();
        }

        UpdateCrossings(frame);

        // One-shot auto-fit once real positions exist. Deliberately NOT continuous: a fit that re-runs
        // every frame fights the user's pan.
        if (!_initialFitApplied && frame.Markers.Count > 0)
        {
            _panes.FitAll(CurrentExtent());
            _initialFitApplied = true;
        }

        // Both halves matter to whether the loop re-arms: a camera still lerping toward its rig's target,
        // or a marker still gliding toward its sample. Once neither is true nothing asks for another
        // frame, which is what makes an idle tab cost nothing.
        bool keepArmed = CameraAdvancer.Advance(_panes, frame, in time);
        _panes.SyncCameraEpochs();
        keepArmed |= _compositor.Advance(in time, frame);

        // A crossing is true for exactly one frame, and everything that cares has now advanced.
        _crossings.EndFrame();

        if (keepArmed)
        {
            ArmFrameLoopIfNeeded();
        }

        _panes.CopySnapshots(_snapshots);

        SceneSubmission submission = new(
            Interlocked.Increment(ref _submissionId),
            frame,
            time,
            _snapshots,
            _palette,
            RenderPurpose.Interactive,
            new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height),
            (float)(VisualRoot?.RenderScaling ?? 1.0),
            _levels.Space);

        // Published for the gate stress hook only. The op itself receives the submission by value at
        // construction; nothing on the render thread reads this field outside that test.
        lock (_submissionLock)
        {
            _lastSubmission = submission;
        }

        return submission;
    }

    // ── The CPU fallback (plan T13). ─────────────────────────────────────────────────────────────────

    // Renders into a cached WriteableBitmap on the UI thread and blits it. The SKSurface is created
    // DIRECTLY over the locked framebuffer, so there is no full-frame ReadPixels copy per frame —
    // which is also why CpuSurfaceProvider is not used here: that seam is for offscreen consumers that
    // own their own memory (plan decision D-7).
    private void RenderCpuFallback(DrawingContext context, Rect bounds, in SceneSubmission submission)
    {
        double scaling = VisualRoot?.RenderScaling ?? 1.0;
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
        if (_leaseUnavailable)
        {
            return;
        }

        _leaseUnavailable = true;
        // The op runs on the render thread; hop back before touching the control.
        Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
    }

    // ── Lifecycle + the animation loop. ──────────────────────────────────────────────────────────────

    private void OnThemeVariantChanged(object? sender, EventArgs e) => RefreshPalette();

    private void RefreshPalette()
    {
        _palette = ScenePaletteFactory.Build(ActualThemeVariant);

        // Cached pictures were recorded with the old colours.
        using (_gate.Enter())
        {
            _compositor.InvalidateCaches();
        }

        InvalidateVisual();
    }

    private void AttachVm(Playback2DTabViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm))
        {
            return;
        }

        if (_vm is not null)
        {
            _vm.FrameUpdated -= OnFrameUpdated;
        }

        _vm = vm;

        // A new view-model (or a detach) must not glide markers from a previous demo's positions, and
        // its level split belongs to a different map.
        _smoother.Clear();
        _crossings.Reset();
        _levels.Reset();
        _panes.Clear();
        _initialFitApplied = false;
        _lastFrameIdentity = (-1, -1);

        if (_vm is null)
        {
            return;
        }

        _vm.FrameUpdated += OnFrameUpdated;
        SyncFromViewModel();
        InvalidateVisual();
    }

    private void OnFrameUpdated()
    {
        SyncFromViewModel();
        ArmFrameLoopIfNeeded();
        InvalidateVisual();
    }

    // Pulls the per-push view-model state the scene cannot derive from the frame: the overlay toggles
    // (compositor state, decision D5) and the map bundle. Both are cheap comparisons in the steady
    // state; the bundle is pulled every push so a late-arriving map takes effect without a
    // re-activation, exactly as the pre-v2 AuthoritativeFloors pull did.
    private void SyncFromViewModel()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        _radarLayer.UseRadarImage = vm.ShowRadar;
        _compositor.SetEnabled(SceneLayerIds.Trails, vm.ShowTrails);
        _compositor.SetEnabled(SceneLayerIds.AreaEffects, vm.ShowAreaEffects);
        _compositor.SetEnabled(SceneLayerIds.Vision, vm.ShowVision);
        _compositor.SetEnabled(SceneLayerIds.Bomb, vm.ShowBombRing);

        LoadedMapAsset? asset = vm.MapAsset;
        if (!ReferenceEquals(asset, _boundAsset))
        {
            _boundAsset = asset;
            _levels.SetAuthoritativeFloors(asset?.Floors);
            _levels.RadarBinder = asset is null ? null : new MapRadarBinder(asset);
            _radarLayer.RadarBoundsOverride = asset is null ? null : MapAssetPipeline.RadarBounds(asset);
        }
    }

    private LoadedMapAsset? _boundAsset;

    private void ArmFrameLoopIfNeeded()
    {
        if (_frameLoopArmed)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        _frameLoopArmed = true;
        _frameLoopArmCount++;
        top.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan now)
    {
        _frameLoopArmed = false;

        // The ONE wall-clock reading in the whole pipeline, and it happens here in the App: Core
        // receives it as data (plan §5.8). Clamped so a long stall — paused, backgrounded, or the very
        // first frame — cannot make the camera jump.
        if (_havePrevFrameTime)
        {
            _lastDt = Math.Clamp((now - _prevFrameTime).TotalSeconds, 1.0 / 240, 1.0 / 15);
        }

        _prevFrameTime = now;
        _havePrevFrameTime = true;
        InvalidateVisual();
    }

    private void ApplyRigs()
    {
        CameraRigFactory.Kind kind = _mode switch
        {
            CameraMode.Alive => CameraRigFactory.Kind.Alive,
            CameraMode.Map => CameraRigFactory.Kind.Map,
            CameraMode.FollowPlayer => CameraRigFactory.Kind.FollowPlayer,
            _ => CameraRigFactory.Kind.Fit
        };

        _panes.SetRig(_ => CameraRigFactory.For(kind, _followSlot, FollowDeadzoneHalfWorld));
    }

    private void ResetDeadzones()
    {
        IReadOnlyList<LevelPane> panes = _panes.Panes;
        for (int i = 0; i < panes.Count; i++)
        {
            if (panes[i].Rig is FollowPlayerRig follow)
            {
                follow.ResetDeadzone();
            }
        }
    }

    // Indexed and allocation-free: one dictionary write per marker over an existing key. Skipped
    // entirely on a single-floor map, which is most of them.
    private void UpdateCrossings(Scene2DFrame frame)
    {
        MapSpace space = _levels.Space;
        if (space.Levels.Count < 2)
        {
            return;
        }

        IReadOnlyList<PlayerMarker> markers = frame.Markers;
        for (int i = 0; i < markers.Count; i++)
        {
            _crossings.Update(markers[i].Slot, markers[i].WorldZ, space);
        }
    }

    private void OnLevelSetChanged()
    {
        _levelSelection.OnLevelSetChanged();
        _singleLayout.ActiveLevelId = _levelSelection.ActiveLevelId;
        LevelStateChanged?.Invoke();
    }

    private void OnActiveLevelChanged()
    {
        _singleLayout.ActiveLevelId = _levelSelection.ActiveLevelId;
        LevelStateChanged?.Invoke();
    }

    private WorldBounds CurrentExtent() => _vm?.CurrentFrame.Map.ObservedBounds ?? WorldBounds.Default;
}
