#region

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DemoViewer.NET.Visualization.Internal;

#endregion

namespace DemoViewer.NET.Visualization;

/// <summary>
///     Avalonia control that renders a directed graph with MSAGL-computed layout.
///     Bind its <see cref="ViewModel" /> property to a <see cref="GraphViewModel" />.
///     Supports pan (drag) and zoom (scroll wheel).
/// </summary>
public sealed class GraphView : Control
{
    // Max pointer travel (screen px) between press and release for a left-action to count as a CLICK
    // (→ pick the node) rather than a DRAG (→ pan the graph) while pick mode is armed.
    private const double PickClickThreshold = 5.0;

    // Screen-space tolerance (px) for an edge to count as "under the cursor". Generous because edge
    // lines are thin; tune from GUI feel.
    private const double EdgeHitTolerancePx = 6.0;

    /// <summary>View model property.</summary>
    public static readonly StyledProperty<GraphViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<GraphView, GraphViewModel?>(nameof(ViewModel));

    /// <summary>
    ///     When <c>true</c>, the next left-click on a node raises <see cref="NodePicked" /> instead of
    ///     panning/resetting the view (a "click a node to add it" gesture). The cursor switches to a
    ///     crosshair while armed. Bound from the host (e.g. a breakpoint-condition editor's pick button).
    /// </summary>
    public static readonly StyledProperty<bool> PickModeProperty =
        AvaloniaProperty.Register<GraphView, bool>(nameof(PickMode));

    private readonly PanZoomHandler _panZoom = new();

    // Press position of the in-progress left gesture while picking, for the click-vs-drag test on
    // release. Null when no left gesture is active.
    private Point? _pickPressPoint;

    /// <summary>Initializes the graph view: enables bounds clipping and antialiased rendering.</summary>
    public GraphView()
    {
        // Avalonia controls do not clip render output to their Bounds by default.
        // This control paints pan/zoom-transformed geometry that routinely falls
        // outside Bounds, so without clipping it bleeds onto sibling/ancestor UI.
        ClipToBounds = true;

        // Crisp lines and text at fractional zoom (thin pens / monospace text
        // can shimmer at non-integer scales otherwise).
        RenderOptions.SetEdgeMode(this, EdgeMode.Antialias);
        RenderOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);
    }

    /// <summary>The bound <see cref="GraphViewModel" /> driving rendering.</summary>
    public GraphViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>
    ///     When <c>true</c>, a left-click picks the node under the cursor (raising
    ///     <see cref="NodePicked" />) rather than panning. See <see cref="PickModeProperty" />.
    /// </summary>
    public bool PickMode
    {
        get => GetValue(PickModeProperty);
        set => SetValue(PickModeProperty, value);
    }

    // The Analysis graph follows the app theme: ThemeStyle picks the Light preset (colours only — layout +
    // hit-testing unchanged) when this control's variant is Light, else the VM's Dark style. Subscribing on
    // attach makes a live theme toggle repaint the graph in the new palette.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnThemeVariantChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ActualThemeVariantChanged -= OnThemeVariantChanged;
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e) => InvalidateVisual();

    private GraphStyle ThemeStyle() => GraphStyle.FromTokens(ActualThemeVariant);

    /// <summary>
    ///     Raised when <see cref="PickMode" /> is active and a left-click lands on a node — carries the
    ///     hit node so the host can act on it (e.g. append it to a breakpoint condition). Not raised
    ///     when the click misses every node (the user simply tries again).
    /// </summary>
    public event EventHandler<IGraphNode>? NodePicked;

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        GraphViewModel? vm = ViewModel;
        GraphStyle style = ThemeStyle();
        context.DrawRectangle(new SolidColorBrush(style.CanvasBackground), null,
            new Rect(0, 0, Bounds.Width, Bounds.Height));

        if (vm is null || !TryGetViewTransform(out LayoutResult? layout, out double scale, out double offX, out double offY))
        {
            return;
        }

        Point ToScreen(double lx, double ly)
        {
            return new Point(offX + lx * scale, offY + ly * scale);
        }

        GraphRenderer.DrawGroups(context, layout, style, scale, ToScreen);
        GraphRenderer.DrawNodeBackgrounds(context, vm.Nodes, layout, style, scale, ToScreen);
        GraphRenderer.DrawEdges(context, vm.Edges, layout, style, scale, ToScreen);
        GraphRenderer.DrawSelfLoops(context, vm.Edges, layout, style, scale, ToScreen);

        if (vm.Tables is not null)
        {
            for (int t = 0; t < vm.Tables.Count && t < layout.Tables.Count; t++)
            {
                GraphRenderer.DrawColumnEdges(context, vm.Tables[t], layout.Tables[t], style, scale, ToScreen);
            }
        }

        GraphRenderer.DrawNodeText(context, vm.Nodes, layout, style, scale, ToScreen);

        if (vm.Tables is not null)
        {
            for (int t = 0; t < vm.Tables.Count && t < layout.Tables.Count; t++)
            {
                TableRenderer.DrawTable(context, vm.Tables[t], layout.Tables[t], style, scale, ToScreen);
                GraphRenderer.DrawColumnEdgeLabels(context, vm.Tables[t], layout.Tables[t], style, scale, ToScreen);
            }
        }
    }

    /// <summary>Resets pan to (0,0) and zoom to 1× so the layout refits the control.</summary>
    public void ResetView()
    {
        _panZoom.Reset();
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_panZoom.OnPointerMoved(this, e))
        {
            InvalidateVisual();
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // Pick mode: a left-press starts a gesture that's EITHER a click (→ pick the node on release)
        // OR a drag (→ pan the graph). Record the press point for that decision, and still let the
        // pan handler track the drag so the graph stays repositionable while picking. Skip the
        // double-click reset (a click should pick, not reset). The pick itself fires on RELEASE.
        if (PickMode && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pickPressPoint = e.GetPosition(this);
            if (_panZoom.OnPointerPressed(this, e))
            {
                e.Handled = true;
            }

            return;
        }

        // Double-click (left button) resets the view. Release any capture the
        // single-click drag handler grabbed on the first click so the pointer
        // doesn't stay stuck to this control after the reset.
        if (e.ClickCount >= 2 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ResetView();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        // The right-click debugger gesture is handled on RELEASE (see OnPointerReleased) — opening a
        // popup on press lets its light-dismiss swallow the release. PanZoomHandler only pans on
        // left-button, so a right-press simply falls through here without starting a drag.
        if (_panZoom.OnPointerPressed(this, e))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    ///     Raised on a right-click that lands on a graph node. Carries the hit node and the
    ///     pointer position (relative to this control) so the host can anchor a context menu.
    /// </summary>
    public event EventHandler<GraphElementContextEventArgs>? NodeContextRequested;

    /// <summary>
    ///     Raised on a right-click that lands on a graph edge (and not a node — nodes win). Carries the
    ///     hit edge and pointer position so the host can anchor an edge breakpoint menu.
    /// </summary>
    public event EventHandler<GraphElementContextEventArgs>? EdgeContextRequested;

    /// <summary>
    ///     Returns the node whose box contains <paramref name="screen" /> (control-relative
    ///     coordinates), or <c>null</c>. Topmost (last-drawn) node wins on overlap. Inverts the
    ///     same pan/zoom transform <see cref="Render" /> uses, so a hit matches what's drawn.
    /// </summary>
    public IGraphNode? HitTestNode(Point screen)
    {
        GraphViewModel? vm = ViewModel;
        if (vm is null || !TryGetViewTransform(out LayoutResult? layout, out double scale, out double offX, out double offY))
        {
            return null;
        }

        double lx = (screen.X - offX) / scale;
        double ly = (screen.Y - offY) / scale;

        NodeStyleConfig ns = vm.Style.Node;
        double hw = ns.Width / 2;
        double hh = ns.Height / 2;

        IReadOnlyList<IGraphNode> nodes = vm.Nodes;
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            IGraphNode node = nodes[i];
            if (layout.NodePositions.TryGetValue(node, out NodePosition? pos)
                && Math.Abs(lx - pos.X) <= hw && Math.Abs(ly - pos.Y) <= hh)
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>
    ///     Returns the visible edge whose route passes nearest <paramref name="screen" /> within the
    ///     hit tolerance, or <c>null</c>. Self-loops are tested against their sampled Bézier; straight
    ///     edges against their route polyline. Works in logical space (tolerance scaled by zoom) so a
    ///     hit matches what's drawn at any zoom.
    /// </summary>
    public IGraphEdge? HitTestEdge(Point screen)
    {
        GraphViewModel? vm = ViewModel;
        if (vm is null || !TryGetViewTransform(out LayoutResult? layout, out double scale, out double offX, out double offY))
        {
            return null;
        }

        double lx = (screen.X - offX) / scale;
        double ly = (screen.Y - offY) / scale;
        double tol = EdgeHitTolerancePx / scale; // logical tolerance scales inversely with zoom
        double bestDist2 = tol * tol;
        IGraphEdge? best = null;

        foreach (IGraphEdge edge in vm.Edges)
        {
            if (!edge.IsVisible)
            {
                continue;
            }

            bool isSelfLoop = ReferenceEquals(edge.Source, edge.Destination);
            IReadOnlyList<Point>? route = isSelfLoop
                ? layout.SelfLoopRoutes.GetValueOrDefault(edge)
                : layout.EdgeRoutes.GetValueOrDefault(edge);
            if (route is null || route.Count < 2)
            {
                continue;
            }

            // Self-loops are 4-point cubic Béziers; sample to a polyline so distance follows the curve.
            IReadOnlyList<Point> poly = isSelfLoop ? EdgeGeometry.SampleBezierRoute(route) : route;
            double d2 = EdgeGeometry.MinDistanceSquaredToPolyline(lx, ly, poly);
            if (d2 <= bestDist2)
            {
                bestDist2 = d2;
                best = edge;
            }
        }

        return best;
    }

    // True when press→release moved less than the click threshold (so the gesture was a click, not a
    // drag-to-pan). Compares squared distance to avoid a sqrt.
    private static bool IsClick(Point press, Point release)
    {
        double dx = release.X - press.X;
        double dy = release.Y - press.Y;
        return dx * dx + dy * dy <= PickClickThreshold * PickClickThreshold;
    }

    // Computes the pan/zoom screen transform exactly as Render does. Single source of truth shared
    // by Render and hit-testing so a clicked point maps to the element actually under it.
    private bool TryGetViewTransform(out LayoutResult layout, out double scale, out double offX, out double offY)
    {
        layout = null!;
        scale = 1;
        offX = 0;
        offY = 0;

        LayoutResult? current = ViewModel?.CurrentLayout;
        if (current is null || Bounds.Width < 1 || Bounds.Height < 1)
        {
            return false;
        }

        double logicalW = Math.Max(current.TotalWidth, 1);
        double logicalH = Math.Max(current.TotalHeight, 1);
        double baseScale = Math.Max(Math.Min(Bounds.Width / logicalW, Bounds.Height / logicalH), 0.01);

        scale = baseScale * _panZoom.Zoom;
        offX = _panZoom.PanX + (Bounds.Width - logicalW * baseScale) / 2;
        offY = _panZoom.PanY + (Bounds.Height - logicalH * baseScale) / 2;
        layout = current;
        return true;
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // Pick gesture: end any pan the press started, then — only if the pointer barely moved (a
        // click, not a drag-to-pan) — hit-test under the cursor and raise NodePicked. This lets the
        // user reposition the graph mid-pick (drag) yet still pick by clicking. A miss is a no-op.
        if (PickMode && e.InitialPressMouseButton == MouseButton.Left)
        {
            Point release = e.GetPosition(this);
            bool wasClick = _pickPressPoint is { } press && IsClick(press, release);
            _pickPressPoint = null;
            _panZoom.OnPointerReleased(e); // release the pan capture taken on press

            if (wasClick)
            {
                IGraphNode? picked = HitTestNode(release);
                if (picked is not null)
                {
                    NodePicked?.Invoke(this, picked);
                }
            }

            e.Handled = true;
            return;
        }

        // Debugger gesture: right-button release hit-tests the element under the cursor and raises a
        // context request so the host can show an add/remove-breakpoint menu. Firing on release
        // (not press) avoids the popup's light-dismiss eating the release event.
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            Point p = e.GetPosition(this);

            // Nodes win over edges on overlap (smaller, on top). Fall through to edges only on a miss.
            IGraphNode? node = HitTestNode(p);
            if (node is not null)
            {
                NodeContextRequested?.Invoke(this, new GraphElementContextEventArgs(p, node, null));
                e.Handled = true;
                return;
            }

            IGraphEdge? edge = HitTestEdge(p);
            if (edge is not null)
            {
                EdgeContextRequested?.Invoke(this, new GraphElementContextEventArgs(p, null, edge));
                e.Handled = true;
            }

            return;
        }

        if (_panZoom.OnPointerReleased(e))
        {
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_panZoom.OnPointerWheelChanged(this, e))
        {
            InvalidateVisual();
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewModelProperty)
        {
            if (change.OldValue is GraphViewModel oldVm)
            {
                oldVm.PropertyChanged -= OnViewModelChanged;
            }

            if (change.NewValue is GraphViewModel newVm)
            {
                newVm.PropertyChanged += OnViewModelChanged;
            }

            InvalidateVisual();
        }
        else if (change.Property == PickModeProperty)
        {
            // Crosshair while armed signals the click-a-node mode; restore the default otherwise.
            Cursor = PickMode ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) =>
        InvalidateVisual();
}

/// <summary>
///     Payload for <see cref="GraphView.NodeContextRequested" /> (and the future edge equivalent):
///     the control-relative pointer position plus exactly one hit element. <see cref="Node" /> is set
///     for a node hit, <see cref="Edge" /> for an edge hit; the other is <c>null</c>.
/// </summary>
public sealed class GraphElementContextEventArgs(Point position, IGraphNode? node, IGraphEdge? edge) : EventArgs
{
    /// <summary>Pointer position relative to the <see cref="GraphView" />, for anchoring a menu.</summary>
    public Point Position { get; } = position;

    /// <summary>The hit node, or <c>null</c> when an edge was hit.</summary>
    public IGraphNode? Node { get; } = node;

    /// <summary>The hit edge, or <c>null</c> when a node was hit.</summary>
    public IGraphEdge? Edge { get; } = edge;
}
