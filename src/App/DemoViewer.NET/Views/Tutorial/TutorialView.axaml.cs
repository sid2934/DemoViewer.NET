#region

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DemoViewer.NET.Controls;
using DemoViewer.NET.ViewModels.Tutorial;

#endregion

namespace DemoViewer.NET.Views.Tutorial;

/// <summary>
///     Code-behind for the walkthrough overlay. The only logic here is <b>layout</b> of the callout bubble
///     relative to the spotlight — a rendering/geometry concern derived from bound state, not data pushing
///     (the same category as the GraphView / Playback2DViewport render code-behind). It positions the
///     callout on a <see cref="Canvas" /> per <see cref="CalloutPlacement" /> and clamps it fully on-screen
///     so a spotlight near any window edge keeps its bubble visible.
/// </summary>
public partial class TutorialView : UserControl
{
    private const double EdgeMargin = 16; // min gap from the window edge
    private const double Gap = 14; // gap between the spotlight and the callout
    private const double Pad = 8; // must match SpotlightScrim.HolePadding so the bubble clears the frame

    private INotifyPropertyChanged? _observed;

    public TutorialView()
    {
        InitializeComponent();

        // LayoutUpdated fires after measure/arrange, when Callout.Bounds is known — the reliable driver for
        // headless capture (the render timer is pumped, so this settles before the frame is grabbed). It also
        // re-measures the anchored region every pass, so the spotlight self-heals as a tab realizes, the window
        // resizes, or the target moves — the engine only decides WHICH region; the view resolves WHERE it is.
        LayoutUpdated += (_, _) =>
        {
            MeasureAnchor();
            PositionCallout();
        };
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    ///     Whether the spotlight's breathing pulse animation is enabled. True at runtime (the <c>.pulsing</c>
    ///     class is applied whenever a step is spotlighted); a headless capture sets this false and pins
    ///     <see cref="SetStaticPulse" /> to inspect a fixed phase deterministically (the animation runs at
    ///     Animation priority and would otherwise override a static <see cref="SpotlightScrim.Pulse" />).
    ///     Visual-only — the same "presentation derived from bound state" category as the layout code below.
    /// </summary>
    public bool AnimatePulse { get; set; } = true;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _observed = DataContext as INotifyPropertyChanged;
        if (_observed is not null)
        {
            _observed.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdatePulse();
        PositionCallout();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Reposition on any layout-affecting change (or a null/"" broadcast).
        if (e.PropertyName is null
            or ""
            or nameof(TutorialViewModel.SpotlightRect)
            or nameof(TutorialViewModel.CurrentStep)
            or nameof(TutorialViewModel.ActiveTarget)
            or nameof(TutorialViewModel.HasSpotlight))
        {
            MeasureAnchor();
            PositionCallout();
        }

        // Start/stop the spotlight breath as the step gains/loses its cut-out (or the tour ends).
        if (e.PropertyName is null
            or ""
            or nameof(TutorialViewModel.CurrentStep)
            or nameof(TutorialViewModel.HasSpotlight)
            or nameof(TutorialViewModel.IsActive))
        {
            UpdatePulse();
        }
    }

    // Applies the breathing-pulse animation class only while a spotlight step is on screen (and animation is
    // enabled). Classes.Set is idempotent, so a step→step transition that keeps HasSpotlight true does not
    // restart the breath. Colour is unaffected (it comes from the scrim's {DynamicResource} border token).
    private void UpdatePulse()
    {
        bool spotlit = AnimatePulse
                       && DataContext is TutorialViewModel { IsActive: true, HasSpotlight: true };
        Scrim.Classes.Set("pulsing", spotlit);
    }

    /// <summary>
    ///     Pins the spotlight to a fixed breathing phase for a deterministic headless capture (0 = dim
    ///     trough, 1 = bright peak). Only meaningful with <see cref="AnimatePulse" /> set false, so the
    ///     animation isn't overriding it. Visual-only test/design hook.
    /// </summary>
    public void SetStaticPulse(double phase) => Scrim.Pulse = phase;

    // Resolves the current step's target region to its live control (via the anchor registry) and pushes its
    // on-screen rectangle — in THIS overlay's coordinate space, which fills the window — into the VM. The
    // engine sets only the Target (through CurrentStep); geometry stays here in the view, where the visual
    // tree is. No-spotlight steps and not-yet-realized targets leave the rect untouched (a following pass
    // settles it once the tab's content attaches).
    private void MeasureAnchor()
    {
        if (DataContext is not TutorialViewModel vm || !vm.IsActive)
        {
            return;
        }

        if (vm.CurrentStep is not { HasSpotlight: true } || this.GetVisualRoot() is null)
        {
            return;
        }

        // The engine picks WHICH region via ActiveTarget (the gateway overrides its step target to the first
        // library card when one exists); geometry is resolved here, against the live visual tree.
        TutorialTarget target = vm.ActiveTarget;

        Rect? rect;
        if (target == TutorialTarget.FirstLibraryCard)
        {
            // Not a registered anchor — it's whatever demo card is realized first in the Library grid.
            rect = MeasureFirstDemoCard();
        }
        else if (TutorialAnchor.TryResolve(target, out Control anchor))
        {
            // The tab-nav step points at the *header strip*, not the whole workspace: a TabControl's bounds
            // cover its content too, so measure the union of its realized TabItem headers instead.
            rect = target == TutorialTarget.TabNav ? MeasureTabHeaders(anchor) : MeasureControl(anchor);
        }
        else
        {
            return; // target not realized yet — a following pass settles it once its tab content attaches
        }

        if (rect is { Width: > 0, Height: > 0 } r)
        {
            vm.SpotlightRect = r;
        }
    }

    // The first realized, effectively-visible demo card in the Library grid, in overlay space (null if none
    // realized yet — an empty library or a not-yet-laid-out tab). Both the card and list views tag their item
    // Borders "demoCard", so this frames whichever view is showing.
    private Rect? MeasureFirstDemoCard()
    {
        if (this.GetVisualRoot() is not Visual root)
        {
            return null;
        }

        foreach (Border card in root.GetVisualDescendants().OfType<Border>())
        {
            if (card.Classes.Contains("demoCard")
                && card.IsEffectivelyVisible
                && MeasureControl(card) is { Width: > 0, Height: > 0 } r)
            {
                return r;
            }
        }

        return null;
    }

    // A control's own on-screen rectangle in this overlay's coordinate space (null if not transformable yet).
    private Rect? MeasureControl(Visual anchor) =>
        anchor.TransformToVisual(this) is { } matrix
            ? new Rect(anchor.Bounds.Size).TransformToAABB(matrix)
            : null;

    // Union of the realized TabItem headers under a TabControl, in overlay space — the tab strip, tightly framed.
    private Rect? MeasureTabHeaders(Visual tabControl)
    {
        Rect? union = null;
        foreach (TabItem item in tabControl.GetVisualDescendants().OfType<TabItem>())
        {
            if (MeasureControl(item) is not { Width: > 0, Height: > 0 } r)
            {
                continue;
            }

            union = union is { } u ? u.Union(r) : r;
        }

        return union;
    }

    private void PositionCallout()
    {
        if (DataContext is not TutorialViewModel vm)
        {
            return;
        }

        double w = Bounds.Width;
        double h = Bounds.Height;
        double cw = Callout.Bounds.Width;
        double ch = Callout.Bounds.Height;
        if (w <= 0 || h <= 0 || cw <= 0 || ch <= 0)
        {
            return; // not measured yet — a later LayoutUpdated pass will settle it
        }

        double x;
        double y;

        if (!vm.HasSpotlight)
        {
            x = (w - cw) / 2;
            y = (h - ch) / 2;
        }
        else
        {
            Rect hole = Inflate(vm.SpotlightRect, Pad);

            // Honour the step's placement hint when there's room; otherwise flip to the opposite side. This
            // keeps the hint a hint — a target near the edge it points away from (e.g. a top-docked strip
            // hinted "Above") auto-corrects instead of clamping the bubble on top of what it points at.
            CalloutPlacement place = vm.Placement;
            if (!Fits(place, hole, w, h, cw, ch) && Fits(Opposite(place), hole, w, h, cw, ch))
            {
                place = Opposite(place);
            }

            (x, y) = Compute(place, hole, w, h, cw, ch);
            x = Math.Clamp(x, EdgeMargin, Math.Max(EdgeMargin, w - cw - EdgeMargin));
            y = Math.Clamp(y, EdgeMargin, Math.Max(EdgeMargin, h - ch - EdgeMargin));
        }

        double curX = Canvas.GetLeft(Callout);
        double curY = Canvas.GetTop(Callout);
        if (double.IsNaN(curX) || Math.Abs(curX - x) > 0.5)
        {
            Canvas.SetLeft(Callout, x);
        }

        if (double.IsNaN(curY) || Math.Abs(curY - y) > 0.5)
        {
            Canvas.SetTop(Callout, y);
        }
    }

    // Does the callout clear the window margins on the placement's primary axis?
    private static bool Fits(CalloutPlacement p, Rect hole, double w, double h, double cw, double ch) =>
        p switch
        {
            CalloutPlacement.Above => hole.Top - Gap - ch >= EdgeMargin,
            CalloutPlacement.Below => hole.Bottom + Gap + ch <= h - EdgeMargin,
            CalloutPlacement.Left => hole.Left - Gap - cw >= EdgeMargin,
            CalloutPlacement.Right => hole.Right + Gap + cw <= w - EdgeMargin,
            _ => true // Center always fits
        };

    private static CalloutPlacement Opposite(CalloutPlacement p) =>
        p switch
        {
            CalloutPlacement.Above => CalloutPlacement.Below,
            CalloutPlacement.Below => CalloutPlacement.Above,
            CalloutPlacement.Left => CalloutPlacement.Right,
            CalloutPlacement.Right => CalloutPlacement.Left,
            _ => CalloutPlacement.Center
        };

    private static (double X, double Y) Compute(
        CalloutPlacement p, Rect hole, double w, double h, double cw, double ch) =>
        p switch
        {
            CalloutPlacement.Above => (hole.Center.X - cw / 2, hole.Top - Gap - ch),
            CalloutPlacement.Left => (hole.Left - Gap - cw, hole.Center.Y - ch / 2),
            CalloutPlacement.Right => (hole.Right + Gap, hole.Center.Y - ch / 2),
            CalloutPlacement.Center => ((w - cw) / 2, (h - ch) / 2),
            _ => (hole.Center.X - cw / 2, hole.Bottom + Gap) // Below
        };

    private static Rect Inflate(Rect r, double d) =>
        new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);
}
