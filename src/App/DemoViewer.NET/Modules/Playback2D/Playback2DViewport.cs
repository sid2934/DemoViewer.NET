#region

using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Theming;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The active camera mode for the 2D viewport. Applies per slice where applicable; a manual
///     pan/zoom on a slice pauses the auto-mode for that slice (<see cref="SliceCamera.ManualOverride" />).
/// </summary>
public enum CameraMode
{
    /// <summary>Fit all observed positions once; static until changed (the original behaviour).</summary>
    Fit,

    /// <summary>Continuously + smoothly keep all ALIVE players in view, lerped per render frame.</summary>
    Alive,

    /// <summary>Fix the view to the all-demo observed extent (an approximation — no real radar metadata).</summary>
    Map,

    /// <summary>Smoothly keep the view centred on one selected player; holds last-known if they die/orphan.</summary>
    FollowPlayer
}

/// <summary>
///     The custom-drawn 2D viewport. Renders a scaled grid background and one team-coloured disc
///     per live player with a heading stub and an event-driven ring. Each rendered floor slice owns its own
///     <see cref="SliceCamera" /> so pan (drag) / zoom (wheel) act independently per floor — the pointer
///     is hit-tested to the band under the cursor. The camera MODE drives each slice's target every
///     render frame; the smooth modes (Alive / Follow) lerp toward their target off the render loop, NOT the
///     per-tick push, and a manual gesture flips that slice to a free/manual camera.
/// </summary>
public sealed class Playback2DViewport : Control, IPlayback2DSurface
{
    // Default fixed world rectangle drawn before any position is observed, e.g. [-3000,3000]^2.
    private const double DefaultWorldExtent = 3000;
    private const double GridStepWorld = 512; // one CS2 cell width; matches PositionUtil.CellWidth
    private const double RadarOpacity = 0.9; // baked radar background, slightly muted so markers pop

    // Smooth-mode framing constants. Alive/Follow target a tight fit with padding; Follow zooms in.
    private const double AlivePadding = 0.18; // extra margin around the alive-players bounds
    private const double FollowHalfWorld = 900; // half-extent of the box Follow keeps centred (world u)
    private const double LerpResponse = 7.0; // exponential-decay rate (higher = snappier) for the lerp

    // ── Marker position interpolation ──
    // Per-slot smoothed DRAW position (world units), chased toward the latest sampled marker position on the
    // render loop so markers GLIDE between discrete pushes instead of stepping. Camera targeting stays on the
    // RAW positions (above) — only the rendered dot is smoothed. Snaps on a large jump (seek / backward /
    // round reset / respawn elsewhere — teleport detection) so a glide never streaks across the map.
    private const double MarkerLerpResponse = 16.0; // exponential-decay rate (snappier than the camera)
    private const float MarkerSnapDistanceSq = 250f * 250f; // ≥ max plausible per-push move ⇒ a jump = teleport
    private const float MarkerSettleEpsilonSq = 0.5f * 0.5f; // within this, snap + treat as settled (stops loop)

    // Per-player FOV "view cone": a fan of horizontal rays across the player's ~106° horizontal FOV, each
    // clipped to the first collision wall at eye height, filled faintly in the team colour. A 2D visibility
    // footprint for the PICTURE (endorsed for viz — the stat itself stays full-3D). Needs the vision engine.
    private const int ConeRays = 26;
    private const float ConeHalfFovDeg = 53f;
    private const float ConeMaxRange = 3200f;

    // Z floor-split. Observes player Z each push; networked m_MinimapVerticalSectionHeights override
    // the histogram when present. Each detected slice gets its own SliceCamera below.
    private readonly FloorSplitter _floors = new();
    private readonly HashSet<int> _liveSlots = new(16);
    private readonly List<int> _pruneScratch = new(8);

    // ── "Vision" overlay: 3D line-of-sight sightlines ──
    // Directed viewer→enemy pairs the viewer can currently SEE (could-see = clear LOS + in view frustum),
    // recomputed once per render from the raw marker positions via the SAME VisibilityAnalyzer.EvaluatePair
    // the stat uses (no duplicated eye/anchor/frustum math). Lines are drawn per floor band (either-endpoint).
    private readonly List<(PlayerMarker Viewer, PlayerMarker Target)> _sightlines = new();
    private readonly List<Vector4> _smokeScratch = new(4);
    private readonly Dictionary<int, (float X, float Y)> _smoothedPos = new(16);

    // Avalonia bitmaps for THIS control only. The scene path owns SKImages and a DrawingContext cannot
    // draw one; rather than make every map load pay for two full-resolution decodes so a temporary
    // escape hatch can render, the legacy control decodes its own on first use. Deleted with it in B5.
    private readonly LegacyRadarBitmapCache _legacyRadar = new();

    private readonly Typeface _typeface = new("Consolas,Menlo,monospace");
    private readonly List<(PlayerMarker Marker, VisibilityAnalyzer.Vantage Vantage)> _visionScratch = new(12);

    // Per-slice cameras, indexed by slice index (0 = lowest floor). Rebuilt structurally when the slice
    // count changes (preserve existing, Fit newly-appeared, drop removed); the band height is derived from
    // the current slice count at render time.
    private SliceCamera[] _cameras = Array.Empty<SliceCamera>();
    private int _cameraSliceCount = -1;
    private double _cameraViewW, _cameraViewH;

    // Pan drag state — captured per slice so the gesture stays bound to the band it began on.
    private bool _dragging;
    private int _dragSlice = -1;
    private int _followSlot = -1;

    // Smooth-mode render-loop driver. RequestAnimationFrame re-arms while any slice is still settling;
    // the lerp factor is derived from the real frame dt (NOT the tick push), so Alive/Follow move smoothly.
    private bool _frameLoopArmed;
    private bool _hasObservedPositions;
    private bool _havePrevFrameTime;
    private bool _initialFitApplied; // one-shot auto-fit once positions are known
    private double _lastDt = 1.0 / 60; // real seconds since the previous animation frame (clamped).
    private Point _lastPointer;
    private double _maxX = DefaultWorldExtent, _maxY = DefaultWorldExtent;

    // Running observed-extent bound — also the Map-mode bounds proxy: the all-demo observed
    // min/max (only ever widened), surfaced to the user as an approximation until real radar metadata lands.
    private double _minX = -DefaultWorldExtent, _minY = -DefaultWorldExtent;

    // Active camera mode. Defaults to Fit (the original behaviour). FollowPlayer needs _followSlot set.
    private CameraMode _mode = CameraMode.Fit;

    private CanvasPalette _palette;
    private TimeSpan _prevFrameTime;

    private Playback2DTabViewModel? _vm;

    public Playback2DViewport()
    {
        Focusable = true;
        ClipToBounds = true;
        _palette = BuildPalette(); // T1a — resolve the initial canvas palette from the current theme
    }

    // Delegating properties keep the original field names so every render/helper call site is unchanged; each
    // is a cheap record-field read of the active bundle (not a resource lookup).
    private IBrush BackgroundBrush => _palette.Background;
    private IPen MinorGridPen => _palette.MinorGrid;
    private IPen MajorGridPen => _palette.MajorGrid;
    private IBrush LabelBrush => _palette.Label;
    private IBrush TeamTBrush => _palette.TeamT;
    private IBrush TeamCtBrush => _palette.TeamCt;
    private IBrush NeutralBrush => _palette.Neutral;
    private IPen SightlineTPen => _palette.SightlineT;
    private IPen SightlineCtPen => _palette.SightlineCt;
    private IBrush ConeTFill => _palette.ConeT;
    private IBrush ConeCtFill => _palette.ConeCt;
    private IBrush ConeNeutralFill => _palette.ConeNeutral;
    private Color RingShooting => _palette.RingShooting;
    private Color RingDamage => _palette.RingDamage;
    private Color RingBlinded => _palette.RingBlinded;
    private Color RingDead => _palette.RingDead;
    private IBrush BombFill => _palette.Bomb;
    private IPen BombTrackPen => _palette.BombTrack;
    private IPen BombDetonationPen => _palette.BombDetonation;
    private IPen BombDefusePen => _palette.BombDefuse;
    private IBrush SmokeFill => _palette.Smoke;
    private IPen SmokePen => _palette.SmokeStroke;
    private IBrush FireFill => _palette.Fire;
    private Color TrailHe => _palette.TrailHe;
    private Color TrailFlash => _palette.TrailFlash;
    private Color TrailSmoke => _palette.TrailSmoke;
    private Color TrailMolotov => _palette.TrailMolotov;
    private Color TrailDecoy => _palette.TrailDecoy;

    /// <summary>The active camera mode. Set by the View's mode selector; re-applies fit + clears overrides.</summary>
    public CameraMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            // Switching mode re-arms every slice's auto-camera (clears the manual override) so the user's
            // explicit choice takes effect immediately rather than being held off by a prior manual pan.
            for (int i = 0; i < _cameras.Length; i++)
            {
                _cameras[i].ManualOverride = false;
            }

            if (_mode == CameraMode.Fit)
            {
                ApplyFitToAllSlices();
            }

            ArmFrameLoopIfNeeded();
            InvalidateVisual();
        }
    }

    /// <summary>Test-only: the rendered transform of slice 0 (the camera state tests assert motion against).</summary>
    internal ViewportTransform PrimaryCameraTransform =>
        _cameras.Length > 0 ? _cameras[0].Current : default;

    /// <summary>Test-only: whether slice 0's camera is in manual-override (a manual pan/zoom paused auto).</summary>
    internal bool PrimaryCameraManual => _cameras.Length > 0 && _cameras[0].ManualOverride;

    /// <summary>The slot the FollowPlayer mode tracks; -1 = none. Setting it also selects FollowPlayer.</summary>
    public int FollowSlot
    {
        get => _followSlot;
        set
        {
            _followSlot = value;
            Mode = CameraMode.FollowPlayer; // selecting a player implies Follow (clears overrides + arms loop)
        }
    }

    /// <summary>Could-see sightlines computed for the last render (test hook for the Vision overlay).</summary>
    internal int SightlineCount => _sightlines.Count;

    // Resolves every canvas colour from its Pb2dCanvas* token for this control's variant (fallback = the Dark
    // hex when app resources are unavailable). Called on attach + ActualThemeVariantChanged; never per frame.
    private CanvasPalette BuildPalette()
    {
        ThemeVariant v = ActualThemeVariant;

        SolidColorBrush B(string key, string fb)
        {
            return new SolidColorBrush(ThemeColors.Get(key, v, fb));
        }

        Pen P(string key, string fb, double w)
        {
            return new Pen(new SolidColorBrush(ThemeColors.Get(key, v, fb)), w);
        }

        Color C(string key, string fb)
        {
            return ThemeColors.Get(key, v, fb);
        }

        return new CanvasPalette(
            B("Pb2dCanvasBg", "#15181C"),
            P("Pb2dCanvasMinorGrid", "#22272E", 1),
            P("Pb2dCanvasMajorGrid", "#2E3742", 1),
            B("Pb2dCanvasLabel", "#9AA4AF"),
            B("Pb2dTeamT", "#E0A030"),
            B("Pb2dTeamCt", "#4A90D9"),
            B("Pb2dCanvasNeutral", "#888888"),
            P("Pb2dCanvasSightlineT", "#70E0A030", 1),
            P("Pb2dCanvasSightlineCt", "#704A90D9", 1),
            B("Pb2dCanvasConeT", "#3CE0A030"),
            B("Pb2dCanvasConeCt", "#3C4A90D9"),
            B("Pb2dCanvasConeNeutral", "#2C888888"),
            C("Pb2dCanvasRingShooting", "#FFD400"),
            C("Pb2dCanvasRingDamage", "#F44336"),
            C("Pb2dCanvasRingBlinded", "#FFFFFFFF"),
            C("Pb2dCanvasRingDead", "#555B62"),
            B("Pb2dCanvasBomb", "#F03A2E"),
            P("Pb2dCanvasBombTrack", "#40FFFFFF", 2),
            P("Pb2dCanvasBombDetonation", "#FF5040", 3),
            P("Pb2dCanvasBombDefuse", "#40C4FF", 3),
            B("Pb2dCanvasSmoke", "#66AEB6BD"),
            P("Pb2dCanvasSmokeStroke", "#88C8CED4", 1),
            B("Pb2dCanvasFire", "#78FF6A1A"),
            C("Pb2dCanvasTrailHe", "#FF5252"),
            C("Pb2dCanvasTrailFlash", "#FFE082"),
            C("Pb2dCanvasTrailSmoke", "#B0BEC5"),
            C("Pb2dCanvasTrailMolotov", "#FF7043"),
            C("Pb2dCanvasTrailDecoy", "#81C784"),
            C("Pb2dCanvasMarkerRingT", "#C8881F"),
            C("Pb2dCanvasMarkerRingCt", "#357ABD"),
            C("Pb2dCanvasMarkerRingNeutral", "#666666"));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachVm(DataContext as Playback2DTabViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RefreshCanvasPalette(); // L2a — pick the Dark/Light canvas bundle for the current variant
        ActualThemeVariantChanged += OnThemeVariantChanged;
        AttachVm(DataContext as Playback2DTabViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ActualThemeVariantChanged -= OnThemeVariantChanged;
        AttachVm(null);
        _legacyRadar.Clear();
        _frameLoopArmed = false;
        _havePrevFrameTime = false;
    }

    // L2a — swap the cached canvas palette when the theme flips (Settings toggle / OS change) and repaint.
    // Resolve+cache here (not per-frame): the render hot path only reads _palette.
    private void OnThemeVariantChanged(object? sender, EventArgs e) => RefreshCanvasPalette();

    private void RefreshCanvasPalette()
    {
        _palette = BuildPalette(); // re-resolve the Pb2dCanvas* tokens for the new variant
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
        _smoothedPos.Clear(); // a new VM (or detach) must not glide markers from a previous demo's positions

        if (_vm is not null)
        {
            _vm.FrameUpdated += OnFrameUpdated;
            InvalidateVisual();
        }
    }

    // One VM push → one observed-extent update + one repaint (the whole render-frame-coalescing story). The
    // smooth modes do NOT key their motion off this push — they advance on the render frame (ArmFrameLoop).
    private void OnFrameUpdated()
    {
        UpdateObservedExtent();
        ArmFrameLoopIfNeeded();
        InvalidateVisual();
    }

    private void UpdateObservedExtent()
    {
        if (_vm is null)
        {
            return;
        }

        // The map's real networked Z-floor boundaries, when the VM has read them. Idempotent.
        _floors.SetSectionHeights(_vm.SectionHeights);

        // Baked-bundle nav floors — when present these OVERRIDE the histogram + section
        // heights entirely (validated map-intrinsic bands); null → histogram fallback. Pulled each push so a
        // late-arriving bundle (MapName after activation) takes effect without a re-activation.
        _floors.SetAuthoritativeFloors(_vm.AuthoritativeFloors);

        bool any = _vm.Markers.Count > 0;

        if (any && !_hasObservedPositions)
        {
            // First push with real positions: drop the default rectangle and bound tightly to the players.
            _hasObservedPositions = true;
            _minX = _minY = double.MaxValue;
            _maxX = _maxY = double.MinValue;
            _initialFitApplied = false; // force a fit on the next render now that we have an extent
        }

        if (any)
        {
            foreach (PlayerMarker m in _vm.Markers)
            {
                Widen(m.WorldX, m.WorldY);
                // Always fold Z into the histogram — even with section heights present, the splitter needs
                // the observed distribution to validate that the heights separate REAL floor clusters (the
                // single-floor radar-section guard); it falls back to the histogram when they don't.
                _floors.Observe(m.WorldZ);
            }
        }
    }

    private void Widen(double x, double y)
    {
        if (x < _minX)
        {
            _minX = x;
        }

        if (x > _maxX)
        {
            _maxX = x;
        }

        if (y < _minY)
        {
            _minY = y;
        }

        if (y > _maxY)
        {
            _maxY = y;
        }
    }

    /// <summary>
    ///     Re-frames the viewport to the observed (or default) extent — the explicit "Fit". Sets the
    ///     mode to <see cref="CameraMode.Fit" /> (left-click semantics), clears every slice's manual override,
    ///     and applies the fit to all slices immediately.
    /// </summary>
    public void FitToExtent()
    {
        _mode = CameraMode.Fit;
        _followSlot = -1;
        _initialFitApplied = true;
        ApplyFitToAllSlices();
        InvalidateVisual();
    }

    // ── Pointer interaction: hit-test the band under the cursor, act on THAT slice's camera only. ──

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Point p = e.GetPosition(this);
        _dragSlice = SliceIndexAtScreenY(p.Y);
        _dragging = true;
        _lastPointer = p;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || _dragSlice < 0 || _dragSlice >= _cameras.Length)
        {
            return;
        }

        Point p = e.GetPosition(this);
        ref SliceCamera cam = ref _cameras[_dragSlice];
        cam.Current = cam.Current.WithPanDelta(p.X - _lastPointer.X, p.Y - _lastPointer.Y);
        cam.ManualOverride = true; // a manual pan pauses the auto-mode for this slice
        _lastPointer = p;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        _dragSlice = -1;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Point p = e.GetPosition(this);
        int slice = SliceIndexAtScreenY(p.Y);
        if (slice < 0 || slice >= _cameras.Length)
        {
            return;
        }

        // Zoom about the band-local cursor position (the band's transform has ViewHeight = bandHeight).
        double bandHeight = _cameraViewH > 0 ? _cameraViewH : Bounds.Height;
        double bandLocalY = p.Y - ScreenSectionOffset(slice, bandHeight);

        ref SliceCamera cam = ref _cameras[slice];
        double factor = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
        cam.Current = cam.Current.ZoomAbout(p.X, bandLocalY, factor);
        cam.ManualOverride = true; // a manual zoom pauses the auto-mode for this slice
        InvalidateVisual();
        e.Handled = true;
    }

    // Maps a screen Y to the slice index drawn at that Y (top band = highest slice). Returns 0 in the
    // single-floor case. Mirrors the band layout in Render (section = floor(y/bandHeight), sliceIndex
    // inverted so the highest floor is on top).
    private int SliceIndexAtScreenY(double screenY)
    {
        int count = Math.Max(1, _cameraSliceCount);
        if (count <= 1 || Bounds.Height < 1)
        {
            return 0;
        }

        double bandHeight = Bounds.Height / count;
        int section = (int)Math.Clamp(Math.Floor(screenY / bandHeight), 0, count - 1);
        return count - 1 - section; // highest floor on top → invert
    }

    // Screen-Y offset of the top of the band that renders the given slice index.
    private double ScreenSectionOffset(int sliceIndex, double bandHeight)
    {
        int count = Math.Max(1, _cameraSliceCount);
        if (count <= 1)
        {
            return 0;
        }

        int section = count - 1 - sliceIndex;
        return section * bandHeight;
    }

    // ── Camera lifecycle: keep _cameras sized to the slice count, fit new slices, drop removed ones. ──

    private void EnsureCameras(int sliceCount, double viewW, double bandHeight)
    {
        bool structural = sliceCount != _cameraSliceCount;
        bool resized = Math.Abs(viewW - _cameraViewW) > 0.5 || Math.Abs(bandHeight - _cameraViewH) > 0.5;

        if (!structural && !resized && _cameras.Length == sliceCount)
        {
            return;
        }

        SliceCamera[] next = new SliceCamera[sliceCount];
        for (int i = 0; i < sliceCount; i++)
        {
            if (i < _cameras.Length)
            {
                // Slice index already existed: preserve its camera (its pan/zoom/manual-override identity),
                // just re-fit it to the current band rectangle (the band height changes with the slice count).
                next[i] = _cameras[i];
                next[i].Current = next[i].Current.WithViewport(viewW, bandHeight);
            }
            else
            {
                // Newly-appeared slice: fit it to the current observed extent.
                next[i] = new SliceCamera(ViewportTransform.Fit(viewW, bandHeight, _minX, _minY, _maxX, _maxY));
            }
        }

        _cameras = next;
        _cameraSliceCount = sliceCount;
        _cameraViewW = viewW;
        _cameraViewH = bandHeight;
    }

    private void ApplyFitToAllSlices()
    {
        for (int i = 0; i < _cameras.Length; i++)
        {
            _cameras[i].Current = ViewportTransform.Fit(_cameraViewW, _cameraViewH, _minX, _minY, _maxX, _maxY);
            _cameras[i].ManualOverride = false;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        Rect bounds = new(Bounds.Size);
        context.FillRectangle(BackgroundBrush, bounds);

        if (bounds.Width < 1 || bounds.Height < 1)
        {
            return;
        }

        IReadOnlyList<FloorSlice> slices = _floors.Slices;
        int sectionCount = Math.Max(1, slices.Count);
        double bandHeight = bounds.Height / sectionCount;

        EnsureCameras(sectionCount, bounds.Width, bandHeight);

        // One-shot auto-fit once positions are known (auto-fit is NOT continuous; it fights
        // pan). After the first fit the per-slice cameras own their own transforms.
        if (!_initialFitApplied && _hasObservedPositions)
        {
            ApplyFitToAllSlices();
            _initialFitApplied = true;
        }

        // Advance the smooth camera modes (Alive / Follow / Map) toward their targets for this render frame.
        // Drives off the render loop (real dt), NOT the tick push. Fit + manual slices are untouched.
        AdvanceCameras();

        // Interpolate marker positions toward their latest sampled spot — runs in ALL modes (Fit too),
        // so re-arm the render loop while any marker is still gliding even when the camera is static.
        if (_vm is not null && AdvanceMarkers(_vm.Markers, _lastDt))
        {
            ArmFrameLoopIfNeeded();
        }

        // Recompute the current tick's could-see sightlines once (cheap); DrawSection draws them per band.
        RebuildSightlines();

        if (sectionCount == 1)
        {
            DrawSection(context, bounds, _cameras[0].Current, -1, null);
            return;
        }

        for (int section = 0; section < sectionCount; section++)
        {
            // Highest floor on top: section 0 (top band) = highest slice.
            int sliceIndex = sectionCount - 1 - section;
            Rect bandRect = new(0, section * bandHeight, bounds.Width, bandHeight);

            FloorSlice slice = slices[sliceIndex];
            string label = $"floor {sliceIndex}  z[{slice.MinZ:F0}..{slice.MaxZ:F0}]";

            using (context.PushClip(bandRect))
            using (context.PushTransform(Matrix.CreateTranslation(0, bandRect.Y)))
            {
                DrawSection(context, new Rect(0, 0, bandRect.Width, bandRect.Height),
                    _cameras[sliceIndex].Current, sliceIndex, label);
            }

            if (section > 0)
            {
                context.DrawLine(MajorGridPen, new Point(0, bandRect.Y), new Point(bounds.Width, bandRect.Y));
            }
        }
    }

    // ── Smooth camera advance. Per render frame: compute each non-manual slice's target for the active
    // mode, lerp the camera toward it, and re-arm the frame loop while any slice is still settling. ──

    private void AdvanceCameras()
    {
        // Exponential-decay smoothing: independent of frame rate, converges without overshoot. The dt is the
        // REAL render-frame delta (stamped by the animation-frame callback), so the motion is render-driven.
        double t = 1 - Math.Exp(-LerpResponse * _lastDt);

        bool anyMoving = false;

        for (int i = 0; i < _cameras.Length; i++)
        {
            if (_cameras[i].ManualOverride || _mode == CameraMode.Fit)
            {
                continue; // Fit + manual slices are static until the user acts.
            }

            if (!TryComputeTarget(i, out ViewportTransform target))
            {
                continue; // no target this frame (e.g. no alive players, no followed marker) — hold.
            }

            if (_cameras[i].IsSettledAt(target))
            {
                _cameras[i].Current = target; // snap the residual so we can stop the loop.
                continue;
            }

            _cameras[i] = _cameras[i].StepToward(target, t);
            anyMoving = true;
        }

        _frameLoopArmed = false;
        if (anyMoving)
        {
            ArmFrameLoopIfNeeded();
        }
    }

    // ── Marker interpolation. Per render frame: chase each marker's smoothed draw position toward its
    // latest sampled spot with a frame-rate-independent exponential approach; snap on first appearance and on
    // a teleport-sized jump (seek / round reset / respawn elsewhere). Returns true while any marker is still
    // gliding so the caller keeps the render loop armed. Internal so a unit test can drive it with a known dt
    // (the headless harness can't be relied on to advance the RAF dt). ──
    internal bool AdvanceMarkers(IReadOnlyList<PlayerMarker> markers, double dt)
    {
        float t = (float)(1 - Math.Exp(-MarkerLerpResponse * dt));
        bool anyMoving = false;
        _liveSlots.Clear();

        foreach (PlayerMarker m in markers)
        {
            _liveSlots.Add(m.Slot);
            float tx = m.WorldX, ty = m.WorldY;

            if (!_smoothedPos.TryGetValue(m.Slot, out (float X, float Y) cur))
            {
                _smoothedPos[m.Slot] = (tx, ty); // first appearance — start ON the player, never glide from 0,0
                continue;
            }

            float dx = tx - cur.X, dy = ty - cur.Y;
            float distSq = dx * dx + dy * dy;

            if (distSq >= MarkerSnapDistanceSq || distSq <= MarkerSettleEpsilonSq)
            {
                // Teleport (a jump no player makes in one push) or already settled → snap, no glide.
                _smoothedPos[m.Slot] = (tx, ty);
                continue;
            }

            _smoothedPos[m.Slot] = (cur.X + dx * t, cur.Y + dy * t);
            anyMoving = true;
        }

        // Prune slots that left (disconnect / never re-emitted) so a re-join doesn't glide from a stale spot.
        if (_smoothedPos.Count != _liveSlots.Count)
        {
            _pruneScratch.Clear();
            foreach (int slot in _smoothedPos.Keys)
            {
                if (!_liveSlots.Contains(slot))
                {
                    _pruneScratch.Add(slot);
                }
            }

            foreach (int slot in _pruneScratch)
            {
                _smoothedPos.Remove(slot);
            }
        }

        return anyMoving;
    }

    /// <summary>Test-only: the smoothed (interpolated) draw position for a slot, or null if not tracked.</summary>
    internal (float X, float Y)? SmoothedMarkerPosition(int slot) =>
        _smoothedPos.TryGetValue(slot, out (float X, float Y) p) ? p : null;

    // Computes the target transform for a slice under the active smooth mode. Returns false when the mode has
    // no target this frame (caller holds the current camera).
    private bool TryComputeTarget(int sliceIndex, out ViewportTransform target)
    {
        target = default;
        if (_vm is null)
        {
            return false;
        }

        switch (_mode)
        {
            case CameraMode.Map:
                // Frame the REAL networked playable-map bounds (the radar bounding box, m_vMinimapMins/Maxs)
                // when the map publishes them; else fall back to the all-demo observed extent (the proxy).
                if (_vm.MapBounds is { } mb)
                {
                    target = ViewportTransform.Fit(_cameraViewW, _cameraViewH, mb.MinX, mb.MinY, mb.MaxX, mb.MaxY);
                }
                else
                {
                    target = ViewportTransform.Fit(_cameraViewW, _cameraViewH, _minX, _minY, _maxX, _maxY);
                }

                return true;

            case CameraMode.Alive:
                return TryFitAlive(sliceIndex, out target);

            case CameraMode.FollowPlayer:
                return TryFollow(sliceIndex, out target);

            default:
                return false;
        }
    }

    // Fits the alive players ASSIGNED TO THIS SLICE (per the floor split) with padding. Falls back to all
    // alive players when the slice has none (so an empty floor band still frames the action above/below).
    private bool TryFitAlive(int sliceIndex, out ViewportTransform target)
    {
        target = default;
        if (_vm is null)
        {
            return false;
        }

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        int count = 0;

        foreach (PlayerMarker m in _vm.Markers)
        {
            if (!m.IsAlive)
            {
                continue;
            }

            if (_cameras.Length > 1 && _floors.SliceIndexFor(m.WorldZ) != sliceIndex)
            {
                continue;
            }

            minX = Math.Min(minX, m.WorldX);
            minY = Math.Min(minY, m.WorldY);
            maxX = Math.Max(maxX, m.WorldX);
            maxY = Math.Max(maxY, m.WorldY);
            count++;
        }

        if (count == 0)
        {
            return false; // no alive players on this slice this frame — hold the camera.
        }

        // Pad so players aren't pinned to the very edge; a single alive player gets a fixed box around them.
        double padX = Math.Max((maxX - minX) * AlivePadding, FollowHalfWorld);
        double padY = Math.Max((maxY - minY) * AlivePadding, FollowHalfWorld);
        target = ViewportTransform.Fit(_cameraViewW, _cameraViewH,
            minX - padX, minY - padY, maxX + padX, maxY + padY);
        return true;
    }

    // Centres on the followed player's marker (by slot). When the followed player is dead/orphaned the VM
    // still emits a gray marker at their last-known position, so Follow keeps centring on it; only when no
    // marker carries that slot at all does Follow hold the current camera (graceful orphan handling).
    private bool TryFollow(int sliceIndex, out ViewportTransform target)
    {
        target = default;
        if (_vm is null || _followSlot < 0)
        {
            return false;
        }

        foreach (PlayerMarker m in _vm.Markers)
        {
            if (m.Slot != _followSlot)
            {
                continue;
            }

            // Only the slice the followed player is on tracks them; other slices hold (no target).
            if (_cameras.Length > 1 && _floors.SliceIndexFor(m.WorldZ) != sliceIndex)
            {
                return false;
            }

            target = ViewportTransform.Fit(_cameraViewW, _cameraViewH,
                m.WorldX - FollowHalfWorld, m.WorldY - FollowHalfWorld,
                m.WorldX + FollowHalfWorld, m.WorldY + FollowHalfWorld);
            return true;
        }

        return false; // followed slot has no marker at all → hold last camera (graceful).
    }

    // ── Render-frame driver. RequestAnimationFrame gives a real, frame-rate-correct dt; a stopwatch
    // fallback covers the headless harness if RAF doesn't advance there. ──

    private void ArmFrameLoopIfNeeded()
    {
        if (_frameLoopArmed)
        {
            return;
        }

        // The loop drives BOTH the smooth camera modes AND marker interpolation, so it must run in Fit
        // mode too. Callers gate on actual movement (a still-settling camera or a gliding marker), so the
        // loop self-terminates the frame after everything settles — no perpetual spin while idle.

        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        _frameLoopArmed = true;
        top.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan now)
    {
        _frameLoopArmed = false;

        // Real frame dt from the animation-frame timestamp, clamped so a long stall (paused / backgrounded /
        // first frame) doesn't jump the camera. This is the render-frame delta the lerp consumes.
        if (_havePrevFrameTime)
        {
            double dt = (now - _prevFrameTime).TotalSeconds;
            _lastDt = Math.Clamp(dt, 1.0 / 240, 1.0 / 15);
        }

        _prevFrameTime = now;
        _havePrevFrameTime = true;
        InvalidateVisual(); // AdvanceCameras runs in Render and re-arms while still settling.
    }

    // Renders one floor section: the grid + only the markers assigned to this floor slice (or all markers
    // when sliceIndex < 0, the single-floor case). Coordinates are band-local; the supplied transform maps
    // world → band-local screen.
    private void DrawSection(DrawingContext context, Rect bounds, ViewportTransform transform,
        int sliceIndex, string? label)
    {
        // Baked radar background when a bundle is loaded + enabled, else the synthetic grid.
        if (!(_vm is { ShowRadar: true } && TryDrawRadar(context, transform, sliceIndex)))
        {
            DrawGrid(context, bounds, transform);
        }

        if (_vm is not null)
        {
            // Grenade flight trails BENEATH the area effects + markers (fading comet lines). Each SEGMENT is
            // floor-assigned by its own points' Z (not the trail's current tip), so a grenade whose arc crosses
            // floors (e.g. a Nuke upper→lower throw) draws each portion on the correct band instead of the whole
            // arc on the tip's floor.
            if (_vm.ShowTrails)
            {
                foreach (GrenadeTrail trail in _vm.GrenadeTrails)
                {
                    DrawTrajectory(context, trail, transform, sliceIndex);
                }
            }

            // Grenade area effects UNDER the markers (smoke clouds + inferno fire cells), slice-filtered by Z.
            if (_vm.ShowAreaEffects)
            {
                foreach (AreaEffect fx in _vm.AreaEffects)
                {
                    if (sliceIndex < 0 || _floors.SliceIndexFor(fx.WorldZ) == sliceIndex)
                    {
                        DrawAreaEffect(context, fx, transform);
                    }
                }
            }

            // Line-of-sight overlay BENEATH the markers (so the discs stay readable on top): per-player FOV
            // view cones (area fills) first, then the could-see sightlines over them.
            if (_vm.ShowVision)
            {
                DrawViewCones(context, transform, sliceIndex);
                DrawSightlines(context, transform, sliceIndex);
            }

            foreach (PlayerMarker marker in _vm.Markers)
            {
                if (sliceIndex >= 0 && _floors.SliceIndexFor(marker.WorldZ) != sliceIndex)
                {
                    continue;
                }

                DrawMarker(context, marker, transform);
            }

            // Bomb timer ring on the slice the planted C4 sits on (or always, single-floor).
            if (_vm.ShowBombRing && _vm.Bomb is { } bomb &&
                (sliceIndex < 0 || _floors.SliceIndexFor(bomb.WorldZ) == sliceIndex))
            {
                DrawBomb(context, bomb, transform);
            }
        }

        if (label is not null)
        {
            FormattedText text = new(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _typeface, 11, LabelBrush);
            context.DrawText(text, new Point(8, 6));
        }
    }

    private void RebuildSightlines()
    {
        _sightlines.Clear();
        if (_vm is not { ShowVision: true } vm || vm.VisionEngine is not { } engine)
        {
            return;
        }

        _visionScratch.Clear();
        foreach (PlayerMarker m in vm.Markers)
        {
            if (!m.IsAlive)
            {
                continue;
            }

            Vector3 feet = new(m.WorldX, m.WorldY, m.WorldZ);
            Vector3 eye = PlayerVantage.Eye(feet, m.DuckAmount);
            Vector3 fwd = PlayerVantage.Forward(m.PitchDegrees, m.YawDegrees);
            _visionScratch.Add((m, new VisibilityAnalyzer.Vantage(m.Slot, m.Team, feet, eye, fwd, true, m.DuckAmount)));
        }

        // Active smoke clouds occlude could-see (vision), matching the stat. Sourced from the already-computed
        // AreaEffects (smoke discs), so the overlay and the drawn clouds can never disagree on where smoke is.
        _smokeScratch.Clear();
        foreach (AreaEffect fx in vm.AreaEffects)
        {
            if (fx.Kind == AreaEffectKind.Smoke)
            {
                _smokeScratch.Add(new Vector4(fx.WorldX, fx.WorldY, fx.WorldZ, fx.WorldRadius));
            }
        }

        ReadOnlySpan<Vector4> smokeSpan = CollectionsMarshal.AsSpan(_smokeScratch);
        for (int i = 0; i < _visionScratch.Count; i++)
        {
            for (int j = 0; j < _visionScratch.Count; j++)
            {
                if (i == j || !VisibilityAnalyzer.AreEnemies(_visionScratch[i].Vantage, _visionScratch[j].Vantage))
                {
                    continue;
                }

                (_, bool couldSee) = VisibilityAnalyzer.EvaluatePair(
                    engine, _visionScratch[i].Vantage, _visionScratch[j].Vantage, 53f, 37f, smokeSpan);
                if (couldSee)
                {
                    _sightlines.Add((_visionScratch[i].Marker, _visionScratch[j].Marker));
                }
            }
        }
    }

    // Draws the precomputed sightlines that belong on this floor band (either endpoint on it, mirroring the
    // grenade-trail rule), connecting the smoothed marker dots so lines meet the players they describe.
    private void DrawSightlines(DrawingContext context, ViewportTransform transform, int sliceIndex)
    {
        foreach ((PlayerMarker viewer, PlayerMarker target) in _sightlines)
        {
            if (sliceIndex >= 0 &&
                _floors.SliceIndexFor(viewer.WorldZ) != sliceIndex &&
                _floors.SliceIndexFor(target.WorldZ) != sliceIndex)
            {
                continue;
            }

            (float vx, float vy) = SmoothedMarkerPosition(viewer.Slot) ?? (viewer.WorldX, viewer.WorldY);
            (float tx, float ty) = SmoothedMarkerPosition(target.Slot) ?? (target.WorldX, target.WorldY);
            (double sx0, double sy0) = transform.WorldToScreen(vx, vy);
            (double sx1, double sy1) = transform.WorldToScreen(tx, ty);
            context.DrawLine(viewer.Team == 3 ? SightlineCtPen : SightlineTPen,
                new Point(sx0, sy0), new Point(sx1, sy1));
        }
    }

    private void DrawViewCones(DrawingContext context, ViewportTransform transform, int sliceIndex)
    {
        if (_vm is not { VisionEngine: { } engine } vm)
        {
            return;
        }

        foreach (PlayerMarker m in vm.Markers)
        {
            if (!m.IsAlive || sliceIndex >= 0 && _floors.SliceIndexFor(m.WorldZ) != sliceIndex)
            {
                continue;
            }

            DrawOneCone(context, transform, engine, m);
        }
    }

    private void DrawOneCone(DrawingContext context, ViewportTransform transform, VisibilityEngine engine, PlayerMarker m)
    {
        // Apex + rays from the smoothed marker XY (so the cone stays glued to the dot) at raw eye height.
        (float px, float py) = SmoothedMarkerPosition(m.Slot) ?? (m.WorldX, m.WorldY);
        float eyeZ = PlayerVantage.Eye(new Vector3(px, py, m.WorldZ), m.DuckAmount).Z;
        Vector3 eye = new(px, py, eyeZ);

        (double apexX, double apexY) = transform.WorldToScreen(px, py);
        StreamGeometry geometry = new();
        using (StreamGeometryContext gc = geometry.Open())
        {
            gc.BeginFigure(new Point(apexX, apexY), true);
            for (int i = 0; i < ConeRays; i++)
            {
                float deg = m.YawDegrees - ConeHalfFovDeg + 2f * ConeHalfFovDeg * i / (ConeRays - 1);
                float rad = deg * (MathF.PI / 180f);
                float cos = MathF.Cos(rad), sin = MathF.Sin(rad);
                float dist = engine.Raycast(eye, new Vector3(cos, sin, 0f), ConeMaxRange, out float t) ? t : ConeMaxRange;
                (double ex, double ey) = transform.WorldToScreen(px + cos * dist, py + sin * dist);
                gc.LineTo(new Point(ex, ey));
            }

            gc.EndFigure(true);
        }

        IBrush fill = m.Team switch
        {
            2 => ConeTFill,
            3 => ConeCtFill,
            _ => ConeNeutralFill
        };
        context.DrawGeometry(fill, null, geometry);
    }

    // Draws the baked radar bitmap for this floor band, placed via the bundle's world Bounds through the
    // shared transform, slightly muted so bright markers pop. Returns false when there's no bundle/image (the
    // caller then falls back to the grid). The image's top-left pixel is world (MinX, MaxY); its bottom-right
    // is (MaxX, MinY) — Y is inverted by the transform. rotate/zoom from the overview txt are in-game
    // minimap-widget hints and are deliberately NOT applied: verified that dust2 (rotate=1/zoom=1.1) aligns
    // correctly with pos/scale alone — round-start CTs land on CT spawn, Ts on T spawn (matching awpy/boltobserv).
    private bool TryDrawRadar(DrawingContext context, ViewportTransform transform, int sliceIndex)
    {
        LoadedMapAsset? asset = _vm?.MapAsset;
        if (asset is null)
        {
            return false;
        }

        string? image = ResolveRadarImage(asset, sliceIndex);
        if (image is null || _legacyRadar.Get(asset, image) is not { } bitmap)
        {
            return false;
        }

        WorldBoundsDto b = asset.Bundle.Bounds;
        (double x0, double y0) = transform.WorldToScreen(b.MinX, b.MaxY);
        (double x1, double y1) = transform.WorldToScreen(b.MaxX, b.MinY);
        Rect dest = new(new Point(x0, y0), new Point(x1, y1));

        using (context.PushOpacity(RadarOpacity))
        {
            context.DrawImage(bitmap, dest);
        }

        return true;
    }

    // Selects which baked radar image applies to a floor band. Floor bands (nav) and radar layers
    // (verticalsections) are both ordered by Z, so when their counts match we index-match (lowest floor →
    // lowest layer); otherwise, or for the single-floor render (sliceIndex < 0), we use the primary
    // (highest-altitude) image.
    private string? ResolveRadarImage(LoadedMapAsset asset, int sliceIndex)
    {
        IReadOnlyList<RadarLayerDto> layers = asset.Bundle.RadarLayers;
        if (layers.Count == 0)
        {
            return asset.Bundle.RadarImages.Count > 0 ? asset.Bundle.RadarImages[0] : null;
        }

        if (sliceIndex >= 0)
        {
            IReadOnlyList<FloorSlice> floors = _floors.Slices;
            if (floors.Count == layers.Count)
            {
                List<RadarLayerDto> byZ = layers.OrderBy(l => l.MinZ).ToList();
                return byZ[Math.Clamp(sliceIndex, 0, byZ.Count - 1)].Image;
            }
        }

        return layers.OrderByDescending(l => l.MinZ).First().Image;
    }

    private void DrawGrid(DrawingContext context, Rect bounds, ViewportTransform transform)
    {
        // World extent currently visible: invert the four corners.
        (double wx0, double wy1) = transform.ScreenToWorld(0, 0);
        (double wx1, double wy0) = transform.ScreenToWorld(bounds.Width, bounds.Height);

        double startX = Math.Floor(wx0 / GridStepWorld) * GridStepWorld;
        double endX = wx1;
        double startY = Math.Floor(wy0 / GridStepWorld) * GridStepWorld;
        double endY = wy1;

        // Guard against an absurd line count if zoomed all the way out.
        int maxLines = 400;
        int countX = (int)((endX - startX) / GridStepWorld);
        int countY = (int)((endY - startY) / GridStepWorld);
        if (countX > maxLines || countY > maxLines || countX < 0 || countY < 0)
        {
            return;
        }

        for (double wx = startX; wx <= endX; wx += GridStepWorld)
        {
            (double sx, _) = transform.WorldToScreen(wx, 0);
            bool major = Math.Abs(wx) < 1e-3;
            context.DrawLine(major ? MajorGridPen : MinorGridPen,
                new Point(sx, 0), new Point(sx, bounds.Height));
        }

        for (double wy = startY; wy <= endY; wy += GridStepWorld)
        {
            (_, double sy) = transform.WorldToScreen(0, wy);
            bool major = Math.Abs(wy) < 1e-3;
            context.DrawLine(major ? MajorGridPen : MinorGridPen,
                new Point(0, sy), new Point(bounds.Width, sy));
        }
    }

    private void DrawMarker(DrawingContext context, PlayerMarker marker, ViewportTransform transform)
    {
        // Draw at the smoothed (interpolated) position; fall back to the raw sample before the first
        // AdvanceMarkers pass has tracked this slot. The floor-slice assignment above stays on the raw Z.
        (float dx, float dy) = SmoothedMarkerPosition(marker.Slot) ?? (marker.WorldX, marker.WorldY);
        (double sx, double sy) = transform.WorldToScreen(dx, dy);
        Point center = new(sx, sy);
        const double Radius = 9;

        IBrush fill = marker.Team switch
        {
            2 => TeamTBrush,
            3 => TeamCtBrush,
            _ => NeutralBrush
        };

        // Heading stub from yaw (NOT velocity) — drawn behind the disc so the disc occludes its root.
        if (marker.IsAlive)
        {
            double yawRad = marker.YawDegrees * Math.PI / 180.0;
            // World yaw 0 = +X (east); screen Y is inverted so subtract the sin component.
            Point tip = new(
                center.X + Math.Cos(yawRad) * (Radius + 8),
                center.Y - Math.Sin(yawRad) * (Radius + 8));
            Pen headingPen = new(fill, 2);
            context.DrawLine(headingPen, center, tip);
        }

        // Disc fill (hollow/grey when dead).
        IBrush discFill = marker.IsAlive ? fill : Brushes.Transparent;
        context.DrawEllipse(discFill, null, center, Radius, Radius);

        // Event-driven ring — colour + alpha and precedence resolved on the VM side.
        Color ringColor = marker.Ring switch
        {
            RingState.Shooting => RingShooting,
            RingState.TakingDamage => RingDamage,
            RingState.Blinded => RingBlinded,
            RingState.Dead => RingDead,
            _ => RingColorForTeam(marker.Team)
        };
        byte alpha = (byte)Math.Clamp(marker.RingAlpha * 255, 0, 255);
        Pen ringPen = new(new SolidColorBrush(Color.FromArgb(alpha, ringColor.R, ringColor.G, ringColor.B)),
            marker.Ring == RingState.Team ? 1.5 : 2.5);
        context.DrawEllipse(null, ringPen, center, Radius, Radius);

        // Number / initials label centred on the disc.
        FormattedText text = new(marker.Label, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _typeface, 10, marker.IsAlive ? Brushes.Black : LabelBrush);
        context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }

    private Color RingColorForTeam(int team) => team switch
    {
        2 => _palette.MarkerRingT,
        3 => _palette.MarkerRingCt,
        _ => _palette.MarkerRingNeutral
    };

    // Grenade area effect (A4): a translucent disc at the world position, world-radius scaled to screen.
    // Smoke = a big gray cloud (subtle outline); Fire = a small orange cell (the cells cluster into the shape).
    private void DrawAreaEffect(DrawingContext context, AreaEffect fx, ViewportTransform transform)
    {
        (double sx, double sy) = transform.WorldToScreen(fx.WorldX, fx.WorldY);
        Point center = new(sx, sy);
        double r = Math.Max(2, fx.WorldRadius * transform.EffectiveScale);

        if (fx.Kind == AreaEffectKind.Smoke)
        {
            context.DrawEllipse(SmokeFill, SmokePen, center, r, r);
        }
        else
        {
            context.DrawEllipse(FireFill, null, center, r, r);
        }
    }

    // Grenade flight trail (A4): the projectile's arc as a fading polyline, colour-keyed by grenade kind.
    // Draws ONLY the segments whose points lie on floor <paramref name="sliceIndex" /> (a segment belongs to a
    // floor if EITHER endpoint maps to it, so the crossing segment bridges both bands continuously); the whole
    // arc when sliceIndex < 0 (single-floor render). trail.Alpha dims the line after detonation; the head dot
    // (current position) draws only on the tip's floor. Cheap — a handful of live trails, ≤256 points each.
    private void DrawTrajectory(DrawingContext context, GrenadeTrail trail, ViewportTransform transform,
        int sliceIndex)
    {
        List<GrenadeTrailPoint> pts = trail.Points;
        if (pts.Count < 2)
        {
            return;
        }

        Color c = trail.Kind switch
        {
            GrenadeKind.He => TrailHe,
            GrenadeKind.Flash => TrailFlash,
            GrenadeKind.Smoke => TrailSmoke,
            GrenadeKind.Molotov => TrailMolotov,
            GrenadeKind.Decoy => TrailDecoy,
            _ => TrailSmoke
        };
        double alpha = Math.Clamp(trail.Alpha, 0, 1);

        List<(int Start, int End)> runs = FloorSegmentRuns(pts, sliceIndex, _floors.SliceIndexFor);
        StreamGeometry geo = new();
        using (StreamGeometryContext g = geo.Open())
        {
            foreach ((int start, int end) in runs)
            {
                (double sx, double sy) = transform.WorldToScreen(pts[start].X, pts[start].Y);
                g.BeginFigure(new Point(sx, sy), false);
                for (int i = start + 1; i <= end; i++)
                {
                    (double ex, double ey) = transform.WorldToScreen(pts[i].X, pts[i].Y);
                    g.LineTo(new Point(ex, ey));
                }

                g.EndFigure(false);
            }
        }

        byte lineA = (byte)Math.Clamp(alpha * 200, 0, 255);
        Pen pen = new(new SolidColorBrush(Color.FromArgb(lineA, c.R, c.G, c.B)), 2);
        context.DrawGeometry(null, pen, geo);

        // Head dot at the live position (brighter) — only on the floor the current tip sits on.
        GrenadeTrailPoint head = pts[^1];
        if (sliceIndex >= 0 && _floors.SliceIndexFor(head.Z) != sliceIndex)
        {
            return;
        }

        (double hx, double hy) = transform.WorldToScreen(head.X, head.Y);
        byte headA = (byte)Math.Clamp(alpha * 240, 0, 255);
        SolidColorBrush headBrush = new(Color.FromArgb(headA, c.R, c.G, c.B));
        context.DrawEllipse(headBrush, null, new Point(hx, hy), 2.5, 2.5);
    }

    // The contiguous point-index runs of a trail whose SEGMENTS belong to floor <paramref name="sliceIndex" />.
    // A segment (i-1 → i) belongs to the floor if EITHER endpoint's Z maps to it (via <paramref name="floorOf" />)
    // — so a grenade whose arc crosses floors renders each portion on the right band, and the single crossing
    // segment bridges both bands (drawn on each) for visual continuity. sliceIndex &lt; 0 → one run over all
    // points (single-floor render). Each run (Start, End) is a polyline of points[Start..End]. Internal + pure
    // for unit testing the multi-level split without a full render.
    internal static List<(int Start, int End)> FloorSegmentRuns(
        IReadOnlyList<GrenadeTrailPoint> pts, int sliceIndex, Func<double, int> floorOf)
    {
        List<(int, int)> runs = new();
        if (pts.Count < 2)
        {
            return runs;
        }

        int runStart = -1;
        for (int i = 1; i < pts.Count; i++)
        {
            bool onFloor = sliceIndex < 0
                           || floorOf(pts[i - 1].Z) == sliceIndex
                           || floorOf(pts[i].Z) == sliceIndex;
            if (onFloor)
            {
                if (runStart < 0)
                {
                    runStart = i - 1;
                }
            }
            else if (runStart >= 0)
            {
                runs.Add((runStart, i - 1));
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            runs.Add((runStart, pts.Count - 1));
        }

        return runs;
    }

    // Planted-C4 timer ring (A4): a red diamond at the bomb, a dim ring track, a bright RED detonation arc
    // depleting clockwise from 12 o'clock (m_flC4Blow countdown), and — during a defuse — an inner CYAN arc
    // depleting alongside it (the defuse-vs-detonation race made spatial).
    private void DrawBomb(DrawingContext context, BombMarker bomb, ViewportTransform transform)
    {
        (double sx, double sy) = transform.WorldToScreen(bomb.WorldX, bomb.WorldY);
        Point center = new(sx, sy);

        const double IconR = 6;
        StreamGeometry diamond = new();
        using (StreamGeometryContext g = diamond.Open())
        {
            g.BeginFigure(new Point(center.X, center.Y - IconR), true);
            g.LineTo(new Point(center.X + IconR, center.Y));
            g.LineTo(new Point(center.X, center.Y + IconR));
            g.LineTo(new Point(center.X - IconR, center.Y));
            g.EndFigure(true);
        }

        context.DrawGeometry(BombFill, null, diamond);

        const double DetonateR = 16;
        context.DrawEllipse(null, BombTrackPen, center, DetonateR, DetonateR);
        DrawArc(context, BombDetonationPen, center, DetonateR, bomb.DetonationFraction);

        if (bomb.BeingDefused)
        {
            const double DefuseR = 11;
            context.DrawEllipse(null, BombTrackPen, center, DefuseR, DefuseR);
            DrawArc(context, BombDefusePen, center, DefuseR, bomb.DefuseFraction);
        }
    }

    // Stroke an arc of `fraction` (0..1) of a circle, starting at 12 o'clock and sweeping clockwise.
    private static void DrawArc(DrawingContext context, IPen pen, Point center, double radius, double fraction)
    {
        double sweep = Math.Clamp(fraction, 0, 1) * 360.0;
        if (sweep <= 0.5)
        {
            return;
        }

        sweep = Math.Min(sweep, 359.99); // a full 360 collapses start==end; the track ring shows "full" anyway
        Point start = PointOnCircle(center, radius, -90);
        Point end = PointOnCircle(center, radius, -90 + sweep);

        StreamGeometry geo = new();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geo);
    }

    private static Point PointOnCircle(Point c, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        return new Point(c.X + radius * Math.Cos(rad), c.Y + radius * Math.Sin(rad));
    }

    // ── 2D canvas colour palette (theme-aware, T1a) ─────────────────────────────────────────────────
    //   The renderer's colours are TOKENS (Pb2dCanvas* + Pb2dTeamT/Ct) resolved from the app's theme
    //   dictionaries for this control's variant, so ANY theme — built-in or a user drop-in — colours the radar
    //   with no code change here (central theme system, design notes in git history). BuildPalette resolves
    //   them ONCE per theme-change into _palette; the render hot path reads _palette through the delegating
    //   properties below, so there is NO per-frame resource lookup. The baked radar BITMAP (TryDrawRadar) is a
    //   theme-independent dark asset and is NOT recoloured — only the synthetic grid + overlays adapt. The hex
    //   literals in BuildPalette are the design-time fallback (the Dark values) for when app resources aren't
    //   available (tests / design surface).
    private sealed record CanvasPalette(
        IBrush Background,
        IPen MinorGrid,
        IPen MajorGrid,
        IBrush Label,
        IBrush TeamT,
        IBrush TeamCt,
        IBrush Neutral,
        IPen SightlineT,
        IPen SightlineCt,
        IBrush ConeT,
        IBrush ConeCt,
        IBrush ConeNeutral,
        Color RingShooting,
        Color RingDamage,
        Color RingBlinded,
        Color RingDead,
        IBrush Bomb,
        IPen BombTrack,
        IPen BombDetonation,
        IPen BombDefuse,
        IBrush Smoke,
        IPen SmokeStroke,
        IBrush Fire,
        Color TrailHe,
        Color TrailFlash,
        Color TrailSmoke,
        Color TrailMolotov,
        Color TrailDecoy,
        Color MarkerRingT,
        Color MarkerRingCt,
        Color MarkerRingNeutral);
}
