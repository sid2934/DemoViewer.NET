#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The viewport HUD's furniture must not fight over the same pixels. Every interactive overlay in the
///     left column's single grid cell is a sibling of every other one, so an overlap is invisible in the
///     XAML and total at runtime: the LATER sibling paints over the earlier one AND wins its hit tests,
///     which is exactly how B2 mounted the annotation toolbar underneath A4's overlay-toggle strip in the
///     shared top-left corner.
///     <para>
///         Geometry is the only honest assertion here — an "is it in the right container" test would have
///         passed on the broken tree.
///     </para>
/// </summary>
[NotInParallel]
public class Playback2DHudLayoutTests
{
    // Every interactive (non-IsHitTestVisible=False) overlay mounted over the viewport. The kill-feed /
    // live-sync stack is deliberately excluded: it is display-only, so it cannot steal anything.
    private static readonly string[] _hudChrome =
    [
        "AnnotationToolbarHost", "OverlayToggles", "TransportBar", "LevelStrip", "ExportButton"
    ];

    private static readonly string[] _toolButtons = ["PanTool", "DrawTool", "EraseTool"];

    [Test]
    public async Task InteractiveHudOverlays_DoNotOverlapEachOther()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            (Window _, Playback2DView view) = Playback2DTimelineHarness.Show(vm);

            List<(string Name, Rect Bounds)> boxes = Boxes(view);
            foreach ((string name, Rect rect) in boxes)
            {
                Console.WriteLine($"[hud-layout] {name} = {rect}");
            }

            // At least the two the B2 regression is about have to be on screen, or the test is vacuous.
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

                Console.WriteLine($"[hud-hit] {name} centre={centre} hit={hit?.GetType().Name} ok={reachable}");
                await Assert.That(reachable).IsTrue();
            }
        });
    }

    /// <summary>
    ///     Gated off, the toolbar must leave NO trace in the stack — not a collapsed-but-spaced slot that
    ///     pushes the overlay strip around, and nothing hit-testable over the canvas.
    /// </summary>
    [Test]
    public async Task AnnotationsGateOff_LeavesTheOverlayStripExactlyWhereItWas()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window _, Playback2DView view) = Playback2DTimelineHarness.Show(vm);

            Rect withToolbar = Box(view, "OverlayToggles")!.Value;

            ctx.Gate!.SetEnabled("playback2d.annotations", false);
            Playback2DTimelineHarness.Pump();

            Rect withoutToolbar = Box(view, "OverlayToggles")!.Value;
            Rect? toolbar = Box(view, "AnnotationToolbarHost");

            Console.WriteLine($"[hud-gate] strip on={withToolbar} off={withoutToolbar} toolbar={toolbar}");

            // The strip is the STABLE element: the toolbar appears BELOW it, so a gate flip must not
            // move it a pixel.
            await Assert.That(withoutToolbar).IsEqualTo(withToolbar);

            // And the toolbar leaves no zero-height-but-margined ghost behind.
            await Assert.That(toolbar).IsNull();
        });
    }

    /// <summary>
    ///     The top-left HUD column must stay inside the viewport column. Nothing in the left cell clips, so
    ///     an over-wide toolbar runs under the splitter and the roster panel (later siblings, so they paint
    ///     over it and win its hit tests) — the same class of collision as the corner overlap, just with the
    ///     right-hand pane instead of a sibling overlay.
    /// </summary>
    [Test]
    [Arguments(1400)]
    [Arguments(1000)]
    [Arguments(820)]
    public async Task TopLeftHud_StaysInsideTheViewportColumn(int windowWidth)
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            (Window _, Playback2DView view) = Playback2DTimelineHarness.Show(vm, windowWidth);

            Control column = view.GetVisualDescendants().OfType<Control>()
                .First(c => c.Name == "ViewportHost");
            Rect hud = Box(view, "TopLeftHud")!.Value;

            // The TRAILING control of the toolbar's tool row is the honest probe. DesiredSize is clamped to
            // the available width by Layoutable.MeasureCore, so a clipped row still reports "I fit"; only
            // the last item's arranged rect tells the truth about whether the row ran off the column.
            AnnotationToolbar toolbar = view.GetVisualDescendants().OfType<AnnotationToolbar>().Single();
            Button clear = toolbar.FindControl<Button>("ClearAllButton")
                           ?? throw new InvalidOperationException("ClearAllButton not found");
            Rect clearBox = new(clear.TranslatePoint(default, view)!.Value, clear.Bounds.Size);

            Console.WriteLine(
                $"[hud-width] window={windowWidth} column={column.Bounds.Width:F0} hud={hud} "
                + $"clear={clearBox}");

            await Assert.That(hud.Right).IsLessThanOrEqualTo(column.Bounds.Width);
            await Assert.That(clearBox.Right).IsLessThanOrEqualTo(column.Bounds.Width);
        });
    }

    private static List<(string Name, Rect Bounds)> Boxes(Playback2DView view)
    {
        List<(string, Rect)> boxes = [];
        foreach (string name in _hudChrome)
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
