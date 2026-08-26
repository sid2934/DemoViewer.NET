#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The 2D viewport's chrome contract. Until D4 every toolbar floated over the canvas in ONE grid cell
///     as a sibling of everything else in it, so an overlap was invisible in the XAML and total at runtime:
///     the LATER sibling paints over the earlier one AND wins its hit tests, which is exactly how B2
///     mounted the annotation toolbar underneath A4's overlay-toggle strip in the shared top-left corner.
///     D4 moved the persistent chrome into its own docked <c>Auto</c> row, which retires the corner fight
///     but adds two of its own: a docked strip is measured against the COLUMN rather than the window, and a
///     collapse bit that does not actually give the height back is a chevron that lies.
///     <para>
///         Geometry is the only honest assertion here — an "is it in the right container" test would have
///         passed on the broken tree, because everything was in the right container the whole time.
///     </para>
/// </summary>
[NotInParallel]
public class Playback2DHudLayoutTests
{
    // Every interactive chrome region in the left column, at the level where they are SIBLINGS — the
    // docked toolbar's three stacked members and the canvas cell's three floating ones. Deliberately not
    // ViewportToolbar itself (it contains the first three) and not the kill-feed / live-sync stack (it is
    // IsHitTestVisible=False, so it cannot steal anything).
    private static readonly string[] _chromeRegions =
    [
        "ChromeHeader", "OverlayToggles", "AnnotationToolbarHost",
        "TransportBar", "LevelStrip", "ChromeRestoreButton"
    ];

    private static readonly string[] _toolButtons = ["PanTool", "DrawTool", "EraseTool"];

    [Test]
    public async Task InteractiveChromeRegions_DoNotOverlapEachOther()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            (Window _, Playback2DView view) = Playback2DTimelineHarness.Show(vm);

            // The widest state the chrome ever reaches: everything the user can reveal, revealed.
            vm.IsOverlayBarOpen = true;
            Playback2DTimelineHarness.Pump();

            List<(string Name, Rect Bounds)> boxes = Boxes(view);
            foreach ((string name, Rect rect) in boxes)
            {
                Console.WriteLine($"[chrome-layout] {name} = {rect}");
            }

            // The two the B2 regression was about have to be on screen, or the test is vacuous.
            await Assert.That(boxes.Select(b => b.Name)).Contains("AnnotationToolbarHost");
            await Assert.That(boxes.Select(b => b.Name)).Contains("OverlayToggles");

            List<string> overlaps = [];
            for (int i = 0; i < boxes.Count; i++)
            {
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    Rect intersection = boxes[i].Bounds.Intersect(boxes[j].Bounds);
                    if (intersection.Width > 0.5 && intersection.Height > 0.5)
                    {
                        overlaps.Add(
                            $"{boxes[i].Name} ∩ {boxes[j].Name} = "
                            + $"{intersection.Width:F0}×{intersection.Height:F0}px");
                    }
                }
            }

            await Assert.That(overlaps).IsEmpty();
        });
    }

    /// <summary>
    ///     The toolbar has to be REACHABLE, not merely non-overlapping: a control the shell reports as
    ///     covered at its own centre point is unclickable however good its Bounds look.
    /// </summary>
    [Test]
    public async Task AnnotationToolbar_ToolButtons_AreTheTopmostHitAtTheirOwnCentre()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);

            AnnotationToolbar toolbar = view.GetVisualDescendants().OfType<AnnotationToolbar>().Single();
            foreach (string name in _toolButtons)
            {
                ToggleButton button = toolbar.FindControl<ToggleButton>(name)
                                      ?? throw new InvalidOperationException($"{name} not found");

                Point centre = Playback2DTimelineHarness.ToWindow(
                    button, window, button.Bounds.Width / 2, button.Bounds.Height / 2);

                IInputElement? hit = window.InputHitTest(centre);
                bool reachable = hit is Visual v
                                 && (ReferenceEquals(v, button) || v.GetVisualAncestors().Contains(button));

                Console.WriteLine($"[chrome-hit] {name} centre={centre} hit={hit?.GetType().Name} ok={reachable}");
                await Assert.That(reachable).IsTrue();
            }
        });
    }

    /// <summary>
    ///     EVERY interactive control in the docked toolbar, at all three widths, fully inside the viewport
    ///     column and reachable at its own centre.
    ///     <para>
    ///         820 is the tight one: the column is ~490 px there, and nothing in this tab clips — an
    ///         over-wide strip simply runs under the <c>GridSplitter</c> and the roster panel, later
    ///         siblings of the root grid, which take both the paint and the clicks. Docking changed the
    ///         measure (the strip is now sized by the column, not by the window) but not that failure mode.
    ///     </para>
    ///     <para>
    ///         The export button is forced visible here. <c>CanExport</c> needs a real
    ///         <c>ModuleContext</c> carrying an export host, which the headless fake is not — so the widest
    ///         realistic header would otherwise never be measured, and the button is precisely the widest
    ///         thing the header reserves.
    ///     </para>
    /// </summary>
    /// <param name="windowWidth">Window width; 820 is the responsive floor the chrome contract is pinned at.</param>
    [Test]
    [Arguments(1400)]
    [Arguments(1000)]
    [Arguments(820)]
    public async Task EveryDockedControl_IsInsideTheColumn_AndHitTestable(int windowWidth)
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm, windowWidth);

            vm.IsOverlayBarOpen = true;
            vm.Annotations.Visibility = EnvelopeMode.Custom; // realizes the envelope editor's second row
            Playback2DTimelineHarness.Pump();

            Control column = Column(view);
            Border toolbar = view.FindControl<Border>("ViewportToolbar")
                             ?? throw new InvalidOperationException("ViewportToolbar not found");

            Button export = view.FindControl<Button>("ExportButton")
                            ?? throw new InvalidOperationException("ExportButton not found");
            export.IsVisible = true;
            Playback2DTimelineHarness.Pump();

            int probed = 0;
            foreach (Control control in toolbar.GetVisualDescendants().OfType<Control>())
            {
                if (control is not (Button or ToggleButton or CheckBox or ComboBox or Slider
                        or NumericUpDown or ColorPicker)
                    || !control.IsEffectivelyVisible
                    || control.Bounds.Width <= 0 || control.Bounds.Height <= 0
                    // A composite control's own template parts are its business, not the layout's.
                    || control.GetVisualAncestors().OfType<Control>()
                        .Any(a => a is ComboBox or Slider or NumericUpDown or ColorPicker))
                {
                    continue;
                }

                probed++;
                Rect box = new(control.TranslatePoint(default, view)!.Value, control.Bounds.Size);
                Point centre = Playback2DTimelineHarness.ToWindow(
                    control, window, control.Bounds.Width / 2, control.Bounds.Height / 2);

                IInputElement? hit = window.InputHitTest(centre);
                bool reachable = hit is Visual v
                                 && (ReferenceEquals(v, control)
                                     || v.GetVisualAncestors().Contains(control));

                Console.WriteLine(
                    $"[chrome-width] window={windowWidth} column={column.Bounds.Width:F0} "
                    + $"{Describe(control)} box={box} hit={hit?.GetType().Name} ok={reachable}");

                await Assert.That(box.Right).IsLessThanOrEqualTo(column.Bounds.Width)
                    .Because("nothing in this column clips, so overflow is a click the roster panel "
                             + "silently takes");
                await Assert.That(box.Left).IsGreaterThanOrEqualTo(0d);

                // Reachability is asserted only where it MEANS something. A disabled control (undo and
                // redo, with an empty document) is correctly transparent to the hit test, so demanding it
                // would be asserting that Fluent's disabled state is broken.
                if (control.IsEffectivelyEnabled)
                {
                    await Assert.That(reachable).IsTrue();
                }
            }

            // 3 tools + 2 pickers + R⌫ + 2 sliders + combo + Pin + Track + 3 undo/redo/clear + 4 envelope
            // boxes + ⌖now + 6 overlays + Overlays▾ + Export + collapse. A floor, not an exact count: the
            // point is that the loop above cannot pass by finding nothing.
            Console.WriteLine($"[chrome-width] window={windowWidth} probed={probed}");
            await Assert.That(probed).IsGreaterThanOrEqualTo(25);
        });
    }

    /// <summary>
    ///     The collapse chevron has to give the height BACK. A docked row whose contents merely go
    ///     invisible while the row keeps its size is the floating-chrome complaint with extra steps.
    /// </summary>
    [Test]
    public async Task CollapsingTheToolbar_GivesTheCanvasItsHeightBack()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            (Window _, Playback2DView view) = Playback2DTimelineHarness.Show(vm);

            vm.IsOverlayBarOpen = true;
            Playback2DTimelineHarness.Pump();

            Control surface = Column(view);
            double expanded = surface.Bounds.Height;
            double toolbarHeight = Box(view, "ViewportToolbar")!.Value.Height;

            vm.ToggleViewportToolbarCommand.Execute(null);
            Playback2DTimelineHarness.Pump();

            double collapsed = surface.Bounds.Height;
            Rect? ghost = Box(view, "ViewportToolbar");
            Rect? restore = Box(view, "ChromeRestoreButton");

            Console.WriteLine($"[chrome-collapse] surface {expanded:F0} → {collapsed:F0} "
                              + $"(toolbar was {toolbarHeight:F0}) ghost={ghost} restore={restore}");

            await Assert.That(vm.IsViewportToolbarOpen).IsFalse();
            await Assert.That(collapsed).IsEqualTo(expanded + toolbarHeight).Within(0.5)
                .Because("the row is Auto-sized, so a removed toolbar leaves no layout hole at all");
            await Assert.That(ghost).IsNull();

            // The way back is mounted BY the collapsed state, so a persisted "collapsed" can never be a
            // state with no exit — the hazard MainViewModel.RestoreSession guards against for the shell's
            // drawer and debugger rail.
            await Assert.That(restore).IsNotNull();

            vm.ToggleViewportToolbarCommand.Execute(null);
            Playback2DTimelineHarness.Pump();

            await Assert.That(Box(view, "ChromeRestoreButton")).IsNull();
            await Assert.That(Column(view).Bounds.Height).IsEqualTo(expanded).Within(0.5);
        });
    }

    /// <summary>
    ///     Gated off, the annotation toolbar must leave NO trace in the docked stack — not a
    ///     collapsed-but-spaced slot that pushes the header around, and nothing hit-testable anywhere.
    /// </summary>
    [Test]
    public async Task AnnotationsGateOff_LeavesTheChromeHeaderExactlyWhereItWas()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window _, Playback2DView view) = Playback2DTimelineHarness.Show(vm);

            vm.IsOverlayBarOpen = true;
            Playback2DTimelineHarness.Pump();

            Rect headerOn = Box(view, "ChromeHeader")!.Value;
            Rect overlaysOn = Box(view, "OverlayToggles")!.Value;

            ctx.Gate!.SetEnabled("playback2d.annotations", false);
            Playback2DTimelineHarness.Pump();

            Rect headerOff = Box(view, "ChromeHeader")!.Value;
            Rect overlaysOff = Box(view, "OverlayToggles")!.Value;
            Rect? toolbar = Box(view, "AnnotationToolbarHost");

            Console.WriteLine($"[chrome-gate] header on={headerOn} off={headerOff} "
                              + $"overlays on={overlaysOn} off={overlaysOff} toolbar={toolbar}");

            // The docked stack is ordered always-present → optional → gated, precisely so a gate flip can
            // only ever move what is BELOW it. Both of the members above the toolbar must be unmoved.
            await Assert.That(headerOff).IsEqualTo(headerOn);
            await Assert.That(overlaysOff).IsEqualTo(overlaysOn);

            // And the toolbar leaves no zero-height-but-spaced ghost behind.
            await Assert.That(toolbar).IsNull();
        });
    }

    /// <summary>
    ///     The two chrome bits round-trip through the FILELESS provider — the WASM branch, where a key
    ///     missing from <c>SettingsService.WriteInMemory</c> writes fine and forgets itself on the next
    ///     read with nothing to see anywhere. The 2D tab is browser-reachable, so both halves of the round
    ///     trip go through the production mapping rather than through hand-written assignments.
    /// </summary>
    [Test]
    public async Task ChromeState_RoundTripsThroughTheFilelessSettingsPath()
    {
        SettingsService svc = new(null); // no directory → the in-memory provider, i.e. the WASM branch

        Playback2DTabViewModel authored = new()
        {
            IsViewportToolbarOpen = false,
            IsOverlayBarOpen = true
        };

        svc.Write(s => authored.WriteChromeSettings(s.Playback2D));

        await Assert.That(svc.Current.Playback2D.ViewportToolbarOpen).IsFalse();
        await Assert.That(svc.Current.Playback2D.ViewportOverlayBarOpen).IsTrue();

        Playback2DTabViewModel restored = new();
        await Assert.That(restored.IsViewportToolbarOpen).IsTrue()
            .Because("a fresh tab ships with the toolbar shown");
        await Assert.That(restored.IsOverlayBarOpen).IsFalse()
            .Because("the six overlay toggles ship CLOSED — being always displayed is the reported defect");

        restored.ApplyChromeSettings(svc.Current.Playback2D);

        await Assert.That(restored.IsViewportToolbarOpen).IsFalse();
        await Assert.That(restored.IsOverlayBarOpen).IsTrue();

        authored.Dispose();
        restored.Dispose();
    }

    /// <summary>
    ///     The toolbar's gesture hints follow the KEYMAP, not a literal. D1 made keys configurable and left
    ///     five tooltips spelling "(D)", "(X)", "Ctrl+Z", "Ctrl+Shift+Z" and "Ctrl+X" — each of them wrong
    ///     for anyone who rebound that action, in the one place a user looks to learn what the key is.
    /// </summary>
    [Test]
    public async Task ToolbarGestureHints_FollowARebind()
    {
        Playback2DTabViewModel vm = new();

        await Assert.That(vm.Annotations.DrawToolTip).Contains("(D)");
        await Assert.That(vm.Annotations.ClearAllToolTip).Contains("(Ctrl+X)");

        vm.ApplyKeymapOverrides(["ToolDraw=G", "ClearAnnotations=Ctrl+Shift+Delete"]);

        Console.WriteLine($"[chrome-gesture] draw='{vm.Annotations.DrawToolTip}' "
                          + $"clear='{vm.Annotations.ClearAllToolTip}'");

        await Assert.That(vm.Annotations.DrawToolTip).Contains("(G)");
        await Assert.That(vm.Annotations.DrawToolTip).DoesNotContain("(D)");
        await Assert.That(vm.Annotations.ClearAllToolTip).Contains("(Ctrl+Shift+Delete)");

        // The hold-pan and cancel gestures are named in the draw hint too, and they were literals as well.
        await Assert.That(vm.Annotations.DrawToolTip).Contains("Space to pan");
        await Assert.That(vm.Annotations.DrawToolTip).Contains("Esc to cancel");

        vm.Dispose();
    }

    // The canvas cell's own control, which is what the docked rows leave behind. Its width is the column's
    // usable width and its height is what a collapse gives back.
    private static Control Column(Playback2DView view) =>
        view.GetVisualDescendants().OfType<Control>().First(c => c.Name == "ViewportHost");

    private static string Describe(Control control) =>
        control.Name is { Length: > 0 } name
            ? name
            : $"{control.GetType().Name}('{(control as ContentControl)?.Content}')";

    private static List<(string Name, Rect Bounds)> Boxes(Playback2DView view)
    {
        List<(string, Rect)> boxes = [];
        foreach (string name in _chromeRegions)
        {
            if (Box(view, name) is { } rect)
            {
                boxes.Add((name, rect));
            }
        }

        return boxes;
    }

    // The element's rect in the VIEW's coordinate space, or null when it is absent / collapsed / not
    // laid out — an invisible overlay cannot collide with anything.
    private static Rect? Box(Playback2DView view, string name)
    {
        Control? control = view.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c.Name == name);

        if (control is null || !control.IsEffectivelyVisible
                            || control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
        {
            return null;
        }

        Point? origin = control.TranslatePoint(default, view);
        return origin is null ? null : new Rect(origin.Value, control.Bounds.Size);
    }
}
