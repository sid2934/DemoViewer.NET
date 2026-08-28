#region

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DemoViewer.NET.Modules.Playback2D.Annotations;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Cameras;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Export;
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
///         <b>Advance and Render are split.</b> Everything that mutates (the level set, pane
///         reconciliation, camera lerps, marker smoothing, the vision solve) happens on the UI thread
///         inside <see cref="AdvanceAndSubmit" />, under the render gate. The draw operation
///         then replays immutable state on Avalonia's render thread. The pre-v2 control did all of that
///         inside <c>Control.Render</c>, which is why it could not be exported, benchmarked or tested
///         without a window.
///     </para>
///     <para>
///         The self-terminating animation loop is preserved exactly: it re-arms only while a camera is
///         still settling or a marker is still gliding, so an idle tab requests no frames at all.
///     </para>
/// </summary>
public sealed class Scene2DHost : Control, IPlayback2DSurface, ILevelSurface, IAnnotationSurface,
    IDisposable
{
    private readonly List<InkPoint> _coalesced = new(64);
    private readonly SceneRenderGate _gate = new();
    private readonly LevelSelection _levelSelection;
    private readonly MapSpaceFactory _levels = new();
    private readonly PaneSet _panes;
    private readonly SingleLayout _singleLayout = new();
    private readonly MarkerSmoother _smoother = new();
    private readonly List<LevelPaneSnapshot> _snapshots = new(4);
    private readonly StackedLayout _stackedLayout = new();
    private readonly Lock _submissionLock = new();

    // The input path. Pan, draw and erase all reach the panes through the router, ONE seam, so they
    // cannot disagree about which pane a gesture captured. The services start over a throwaway session
    // and are re-pointed at the tab's real one when a view-model binds; rebuilding the router there
    // would drop a live gesture.
    private readonly SceneHostToolServices _toolServices;

    private LoadedMapAsset? _boundAsset;
    private AnnotationSession? _boundSession;

    private SceneCompositor _compositor;
    private LevelDisplayMode _displayMode = LevelDisplayMode.Stacked;

    private WriteableBitmap? _fallbackBitmap;
    private int _followSlot = -1;
    private bool _frameLoopArmed;
    private int _gateStressFrames;
    private bool _havePrevFrameTime;
    private bool _initialFitApplied;
    private double _lastDt = 1.0 / 60;
    private (int FrameIndex, int Tick) _lastFrameIdentity = (-1, -1);
    private SceneSubmission? _lastSubmission;
    private CameraMode _mode = CameraMode.Fit;
    private ScenePalette _palette = ScenePalette.Dark;
    private TimeSpan _prevFrameTime;
    private RadarLayer _radarLayer;
    private bool _released;
    private long _submissionId;
    private TextBlobCache _text;
    private VisionLayer _visionLayer;
    private Playback2DTabViewModel? _vm;

    /// <summary>Creates the host and registers the seven scene layers.</summary>
    public Scene2DHost()
    {
        Focusable = true;
        ClipToBounds = true;

        _panes = new PaneSet(_stackedLayout);
        _smoother.LevelCrossings = CrossingsForTest;

        _levelSelection = new LevelSelection(_levels.Space);
        _levelSelection.ActiveLevelChanged += OnActiveLevelChanged;
        _levels.Space.LevelSetChanged += OnLevelSetChanged;

        _toolServices = new SceneHostToolServices(this, new AnnotationSession(new AnnotationDocument()));
        Router = new InputToolRouter(_toolServices, new PanZoomTool());
        Router.Register(new DrawTool());
        Router.Register(new EraseTool());

        BuildScene();
    }

    /// <summary>The pointer-tool router. The view drives tool selection and gesture cancellation through it.</summary>
    internal InputToolRouter Router { get; }

    /// <summary>The frame currently being shown. Read by the tool services; never retained.</summary>
    internal Scene2DFrame CurrentSceneFrame => _vm?.CurrentFrame ?? Scene2DFrame.Empty;

    /// <summary>The annotation layer, once a session has been bound. Test hook.</summary>
    internal AnnotationLayer? AnnotationLayerForTest { get; private set; }

    /// <summary>The layer stack. B2 and B4 register their layers on it.</summary>
    public SceneCompositor Compositor => _compositor;

    /// <summary>The layout policy. B3 swaps in <c>SingleLayout</c> here.</summary>
    public ILevelLayoutPolicy LayoutPolicy
    {
        get => _panes.Policy;
        set => _panes.Policy = value;
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
    internal LevelCrossingTracker CrossingsForTest { get; } = new();

    /// <summary>Test hook: whether the lowest pane is in manual override.</summary>
    internal bool PrimaryCameraManual =>
        _panes.Panes.Count > 0 && _panes.Panes[0].Camera.ManualOverride;

    /// <summary>Test hook: could-see segments solved for the last advance.</summary>
    internal int SightlineCount => _visionLayer.SightlineCount;

    /// <summary>Test hook: true once the Skia lease failed and the CPU fallback took over.</summary>
    internal bool LeaseUnavailable { get; private set; }

    /// <summary>
    ///     Test hook: how many times the animation loop has been armed. The loop is
    ///     <b>self-terminating</b>: it re-arms only while a camera is settling or a marker is gliding, so
    ///     on an idle tab this stops growing. A loop that spins forever burns a core in the background
    ///     and is invisible until someone notices the fan.
    /// </summary>
    internal int FrameLoopArmCountForTest { get; private set; }

    /// <summary>Test hook: the id of the most recent submission. Must be strictly monotonic.</summary>
    internal long LastSubmissionIdForTest => Interlocked.Read(ref _submissionId);

    /// <summary>Test hook: how many frames the gate stress worker managed to draw.</summary>
    internal int GateStressFramesForTest => _gateStressFrames;

    // The three above ARE IAnnotationSurface; they predate it and are internal, and an implicit
    // implementation would have to make them public. Explicit forwarding keeps the surface exactly as
    // wide as it was while letting the view ask "can this thing host ink?" instead of "is this thing a
    // Scene2DHost?".
    void IAnnotationSurface.SetActiveTool(ToolKind kind) => SetActiveTool(kind);

    void IAnnotationSurface.SetSpacePanHeld(bool held) => SetSpacePanHeld(held);

    void IAnnotationSurface.CancelActiveGesture() => CancelActiveGesture();

    /// <summary>
    ///     Releases the compositor, its layers and the fallback bitmap. Also runs on detach: a tab's
    ///     view is destroyed and rebuilt on every activation, so leaking one compositor's worth of
    ///     SKPaints, SKPaths and recorded pictures per activation would be a steady native-memory climb.
    ///     Idempotent.
    /// </summary>
    public void Dispose() => ReleaseResources();

    /// <summary>The resolved level set. The level strip reads it.</summary>
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

    /// <summary>The pane under a host-space point, or null. The successor to <c>SliceIndexAtScreenY</c>.</summary>
    /// <param name="x">Host X.</param>
    /// <param name="y">Host Y.</param>
    internal LevelPane? PaneAtHostPoint(float x, float y) => _panes.PaneAt(x, y);

    /// <summary>Repaint request from a pointer tool. Coalesced by Avalonia.</summary>
    internal void RequestToolRender()
    {
        ArmFrameLoopIfNeeded();
        InvalidateVisual();
    }

    /// <summary>Hold-to-pan. The view sets it from the Space key.</summary>
    /// <param name="held">Whether Space is down.</param>
    internal void SetSpacePanHeld(bool held) => Router.IsSpaceHeld = held;

    /// <summary>Esc: abandons whatever gesture is in flight.</summary>
    internal void CancelActiveGesture()
    {
        Router.CancelActive();
        InvalidateVisual();
    }

    /// <summary>Selects the active pointer tool.</summary>
    /// <param name="kind">The tool.</param>
    internal void SetActiveTool(ToolKind kind) => Router.SetActive(kind);

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

        // The map bundle and the annotation session are re-pulled on the next SyncFromViewModel, so the
        // fresh layers are bound.
        _boundAsset = null;
        AnnotationLayerForTest = null;
        _boundSession = null;
        _released = false;
    }

    /// <summary>
    ///     Freezes the live panes' cameras into a <see cref="CameraScript.MirrorLiveView" />: capture
    ///     once, at Start. Panning the real window afterwards changes nothing about the video, so an
    ///     export is reproducible from its request alone.
    ///     <para>
    ///         The snapshot is taken here rather than assembled by the export dialog because
    ///         <see cref="PaneSet" /> is the only pane-lifetime owner and it is private to this control.
    ///         An empty <c>Fixed</c> script leaves every exported pane on the fit its own level was born
    ///         with: right for a whole round, wrong for a user who had zoomed into A site.
    ///     </para>
    ///     <para>
    ///         Keyed by <see cref="MapLevelId" />, never by pane index: a level set that gains a floor
    ///         mid-export must not slide every camera down one band. Panes with no level yet, the state
    ///         before the first frame push, produce an empty script, which resolves to the per-level fit.
    ///     </para>
    /// </summary>
    public CameraScript CaptureCameraScript()
    {
        IReadOnlyList<LevelPane> panes = _panes.Panes;
        ImmutableArray<PaneCameraSnapshot>.Builder builder =
            ImmutableArray.CreateBuilder<PaneCameraSnapshot>(panes.Count);

        for (int i = 0; i < panes.Count; i++)
        {
            LevelPane pane = panes[i];
            builder.Add(new PaneCameraSnapshot(pane.LevelId, pane.Camera.Current,
                pane.Camera.ManualOverride));
        }

        return new CameraScript.MirrorLiveView(builder.ToImmutable(), _displayMode);
    }

    /// <summary>Test hook: forces the CPU fallback path on, so it is exercised without a broken backend.</summary>
    internal void ForceLeaseUnavailableForTest() => LeaseUnavailable = true;

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
        // touches the compositor; RefreshPalette invalidates its caches on the very next line.
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

    // ── Pointer input. Every gesture goes through the router; this control's job is to turn Avalonia
    //    events into pane-and-world coordinates and nothing else.

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        ArgumentNullException.ThrowIfNull(e);

        // The toolbar edits the SESSION (it has no seam to the router), so the button→tool map is
        // refreshed from it here, at the one moment the router reads it. Same "sampled at press time"
        // discipline as the divert expression, and it cannot go stale between a toolbar click and the
        // next gesture the way a bind-time or frame-time mirror would while the tab sits paused.
        Router.SecondaryTool = _boundSession?.SecondaryTool;

        ToolPointerEvent sample = Translate(e, false);
        if (Router.OnPressed(in sample))
        {
            e.Pointer.Capture(this);
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

        ToolPointerEvent sample = Translate(e, true);
        Router.OnMoved(in sample);
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        ArgumentNullException.ThrowIfNull(e);

        // InitialPressMouseButton, never ButtonOf: the pressed-button flags on a RELEASE describe what is
        // STILL down, so a plain left release reports None and a chorded middle release reports Left,
        // the one value that would let a stray middle click close the left stroke. Avalonia names the
        // button this release actually belongs to; the router refuses the rest.
        ToolPointerEvent sample =
            Translate(e, true, ButtonOf(e.InitialPressMouseButton));

        // Capture follows the GESTURE. A refused chord release leaves it held, or the remainder of the
        // drag would arrive at whatever is under the cursor instead of at the stroke that owns it.
        if (Router.OnReleased(in sample))
        {
            e.Pointer.Capture(null);
        }
    }

    /// <summary>
    ///     An OS-cancelled contact (a touch or pen lifted out of range, a system gesture, another element
    ///     taking the pointer) <b>abandons</b> the gesture.
    ///     <para>
    ///         Cancel, never commit: no button was released, so treating it as a release would write a
    ///         stroke the user did not finish. Without it the gesture stays open with capture gone, and
    ///         <c>OnPointerMoved</c> (which gates only on <c>IsGestureOpen</c>) keeps extending the stroke
    ///         with no button held, for as long as the pointer stays over the surface.
    ///     </para>
    /// </summary>
    /// <param name="e">The capture-lost event.</param>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Router.CancelActive();
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
    }

    // Avalonia event → pane-resolved, world-resolved tool sample. The coalesced samples are the reason
    // a fast stroke looks smooth: a 1000 Hz digitiser delivers dozens of points per 60 Hz frame, and
    // taking only the primary one turns a curve into a polyline.
    private ToolPointerEvent Translate(PointerEventArgs e, bool includeIntermediate,
        ToolPointerButton? button = null)
    {
        Point position = e.GetPosition(this);
        float x = (float)position.X;
        float y = (float)position.Y;
        LevelPane? pane = _panes.PaneAt(x, y);

        float pressure = 0.5f;
        try
        {
            PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
            pressure = properties.Pressure > 0 ? properties.Pressure : 0.5f;
        }
        catch (InvalidOperationException)
        {
            // A synthetic event with no backing device reports no properties; 0.5 with simulated
            // pressure is upstream perfect-freehand's own default and looks right for a mouse.
        }

        _coalesced.Clear();
        if (includeIntermediate && pane is not null)
        {
            IReadOnlyList<PointerPoint>? points = e.GetIntermediatePoints(this);
            if (points is not null)
            {
                // Avalonia (11.3.12) returns the sub-frame history OLDEST-FIRST and appends THIS
                // event's own point LAST: GetIntermediatePoints is literally
                // "previous raw points ++ GetCurrentPoint". The ink wants oldest-first and the tool
                // appends the primary point itself, so the list is walked forwards and the TRAILING
                // entry is dropped. Walking it backwards folds every fast drag back on itself and
                // duplicates the primary sample; pinned by
                // Playback2DAnnotationHostTests.CoalescedSamples_ReachTheInk_OldestFirst_AndOnlyOnce.
                for (int i = 0; i < points.Count - 1; i++)
                {
                    PointerPoint point = points[i];
                    (double wx, double wy) = pane.Camera.Current.ScreenToWorld(
                        point.Position.X - pane.ViewportRect.Left,
                        point.Position.Y - pane.ViewportRect.Top);
                    float p = point.Properties.Pressure > 0 ? point.Properties.Pressure : pressure;
                    _coalesced.Add(new InkPoint((float)wx, (float)wy, p));
                }
            }
        }

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
            Pressure = pressure,
            Button = button ?? ButtonOf(e),
            Modifiers = Translate(e.KeyModifiers),
            Intermediate = CollectionsMarshal.AsSpan(_coalesced)
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

    // The button a release BELONGS to, from PointerReleasedEventArgs.InitialPressMouseButton, the only
    // thing on a release that names the button that came up rather than the ones still held.
    // The X buttons reach no tool, so they map to None and the router reads that as "the gesture's own".
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
            CrossingsForTest.Reset();
        }

        SKSize host = new((float)bounds.Width, (float)bounds.Height);

        if (_levels.Update(frame))
        {
            // A rebuilt level set invalidates every recorded picture: the bands moved, and a PerCamera
            // picture is keyed on a level id that may now describe a different Z range. The ink layer
            // holds its own per-level pictures, outside the compositor's cache, so it is told too.
            _compositor.InvalidateCaches();
            AnnotationLayerForTest?.InvalidateLevels();
            CrossingsForTest.Reset();
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
        CrossingsForTest.EndFrame();

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

    // ── The CPU fallback. ────────────────────────────────────────────────────────────────────────────

    // Renders into a cached WriteableBitmap on the UI thread and blits it. The SKSurface is created
    // DIRECTLY over the locked framebuffer, so there is no full-frame ReadPixels copy per frame. That
    // is also why CpuSurfaceProvider is not used here: that seam is for offscreen consumers that own
    // their own memory.
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
        if (LeaseUnavailable)
        {
            return;
        }

        LeaseUnavailable = true;
        // The op runs on the render thread; hop back before touching the control.
        Dispatcher.UIThread.Post(InvalidateVisual);
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
        CrossingsForTest.Reset();
        _levels.Reset();
        _panes.Clear();
        _initialFitApplied = false;
        _lastFrameIdentity = (-1, -1);

        // A gesture in flight belongs to the outgoing view-model's document; carrying it across would
        // commit half a stroke into a different demo's annotations.
        Router.CancelActive();

        if (_vm is null)
        {
            BindAnnotations(null);
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

        BindAnnotations(vm.AnnotationSession);
        _compositor.SetEnabled(SceneLayerIds.Annotations, vm.IsAnnotationsEnabled);

        LoadedMapAsset? asset = vm.MapAsset;
        if (!ReferenceEquals(asset, _boundAsset))
        {
            _boundAsset = asset;
            _levels.SetAuthoritativeFloors(asset?.Floors);
            _levels.RadarBinder = asset is null ? null : new MapRadarBinder(asset);
            _radarLayer.RadarBoundsOverride = asset is null ? null : MapAssetPipeline.RadarBounds(asset);
        }
    }

    // Registers (or drops) the ink layer for the tab's session.
    //
    // Under the render gate on purpose: RenderPane walks the layer list BY INDEX on Avalonia's render
    // thread, and this is the first phase that adds or removes a layer in response to something a user
    // did. An unsynchronized mutation there surfaces as an intermittent ArgumentOutOfRangeException on
    // the render thread, which no golden would ever catch.
    private void BindAnnotations(AnnotationSession? session)
    {
        if (ReferenceEquals(_boundSession, session))
        {
            return;
        }

        _boundSession = session;

        using (_gate.Enter())
        {
            if (AnnotationLayerForTest is not null)
            {
                _compositor.Remove(SceneLayerIds.Annotations);
                AnnotationLayerForTest = null;
            }

            if (session is not null)
            {
                AnnotationLayerForTest = new AnnotationLayer(session);
                _compositor.Add(AnnotationLayerForTest);
            }
        }

        if (session is null)
        {
            return;
        }

        _toolServices.Session = session;
        Router.SetActive(session.ActiveTool);
    }

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
        FrameLoopArmCountForTest++;
        top.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan now)
    {
        _frameLoopArmed = false;

        // The ONE wall-clock reading in the whole pipeline, and it happens here in the App: Core
        // receives it as data. Clamped so a long stall (paused, backgrounded, or the very first frame)
        // cannot make the camera jump.
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
            CrossingsForTest.Update(markers[i].Slot, markers[i].WorldZ, space);
        }
    }

    private void OnLevelSetChanged()
    {
        _levelSelection.OnLevelSetChanged();
        _singleLayout.ActiveLevelId = _levelSelection.ActiveLevelId;
        RebaseAnnotationAnchors();
        LevelStateChanged?.Invoke();
    }

    /// <summary>
    ///     Rebases level-anchored ink when the level SET moves under it.
    ///     <para>
    ///         An annotation drawn on a floor stores that floor's quantized <c>ZMin</c>, and the
    ///         histogram that derives the bands moves the boundary all demo long. Without this a stroke
    ///         drawn on Nuke lower stops matching any pane the first time the split shifts, and silently
    ///         disappears. This is the wire into the remap chain (<c>TryRemapAnchor</c> →
    ///         <c>ApplyLevelRebuild</c> → <c>RemapWorldLevels</c>).
    ///     </para>
    ///     <para>
    ///         Allocation-free unless a band actually moved, and not a per-frame path anyway:
    ///         <c>LevelSetChanged</c> fires on a rebuild that changed something, not on every push.
    ///     </para>
    /// </summary>
    private void RebaseAnnotationAnchors()
    {
        if (_vm is null)
        {
            return;
        }

        LevelSetChange change = _levels.Space.LastChange;
        if (change.IsEmpty || change.LevelsBefore.Count == 0)
        {
            return;
        }

        Dictionary<double, double>? moved = null;
        for (int i = 0; i < change.LevelsBefore.Count; i++)
        {
            double before = change.LevelsBefore[i].ZMin;
            if (!change.TryRemapAnchor(before, out double after))
            {
                continue;
            }

            // Anchors are stamped QUANTIZED (DrawTool: MapSpace.QuantizeZ(pane.Level.ZMin)), so the
            // map has to be keyed the same way; a raw-Z key would match nothing and rebase nothing.
            double oldKey = MapSpace.QuantizeZ(before);
            double newKey = MapSpace.QuantizeZ(after);
            if (oldKey.Equals(newKey))
            {
                continue;
            }

            moved ??= new Dictionary<double, double>();
            moved[oldKey] = newKey;
        }

        if (moved is not null)
        {
            _vm.ApplyAnnotationLevelRebuild(moved);
        }
    }

    private void OnActiveLevelChanged()
    {
        _singleLayout.ActiveLevelId = _levelSelection.ActiveLevelId;
        LevelStateChanged?.Invoke();
    }

    private WorldBounds CurrentExtent() => _vm?.CurrentFrame.Map.ObservedBounds ?? WorldBounds.Default;
}
