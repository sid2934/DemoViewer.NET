#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The timeline footer's track toggles are the only INTERACTIVE controls in the docked timeline, and
///     they sit at the end of a row of readouts that grow during playback. They were laid out as the
///     trailing <c>Auto</c> column of a grid, which is a shape that cannot fail visibly: <c>Auto</c> columns
///     are measured unconstrained and never shrink, so the <c>*</c> status column collapsed to zero and the
///     toggles were arranged past the control's right edge — 99 px past it at a 1000 px window, 279 px at
///     820 px — where the <c>GridSplitter</c> and the roster panel (later siblings of the root grid, so they
///     paint over it) took every click.
///     <para>
///         Geometry is the assertion, in the same style as <see cref="Playback2DHudLayoutTests" />: a test
///         that the toggles are inside the right CONTAINER passed on the broken tree, because they were.
///     </para>
/// </summary>
[NotInParallel]
public class TimelineFooterLayoutTests
{
    /// <summary>
    ///     The footer at its WIDEST, which is also when it is being read: mid-playback on a 90 000-frame
    ///     demo, six-digit frame and tick, a follow target, the pointer over the scrub bar and Live Sync
    ///     pinning the speed. Every one of those readouts is untrimmed monospace, and the shipped fixture
    ///     with all of them blank fits by eight pixels — so a test that leaves them blank proves nothing.
    /// </summary>
    /// <param name="windowWidth">Window width; 820 is the responsive floor the HUD contract is pinned at.</param>
    [Test]
    [Arguments(1400)]
    [Arguments(1000)]
    [Arguments(820)]
    public async Task TrackToggles_AreFullyInsideTheTimeline_AndClickable(int windowWidth)
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab(90000);
            ctx.Push(87654, 175308);

            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm, windowWidth);
            vm.Timeline.FollowStatus = "following Charlie · requested";
            vm.Timeline.SpeedLockNote = "speed pinned by Live Sync";
            vm.Timeline.UpdateHover(300);
            Playback2DTimelineHarness.Pump(4);

            TimelineControl timeline = Playback2DTimelineHarness.Timeline(view);
            Rect bounds = new(default, timeline.Bounds.Size);

            int probed = 0;
            foreach (CheckBox toggle in timeline.GetVisualDescendants().OfType<CheckBox>())
            {
                if (!toggle.IsEffectivelyVisible || toggle.Bounds.Width <= 0 || toggle.Bounds.Height <= 0)
                {
                    continue; // a track this demo cannot feed shows no checkbox at all
                }

                probed++;
                Rect painted = PaintedExtent(toggle, timeline);
                Point centre = Playback2DTimelineHarness.ToWindow(
                    toggle, window, toggle.Bounds.Width / 2, toggle.Bounds.Height / 2);

                IInputElement? hit = window.InputHitTest(centre);
                bool reachable = hit is Visual v
                                 && (ReferenceEquals(v, toggle) || v.GetVisualAncestors().Contains(toggle));

                Console.WriteLine($"[footer] window={windowWidth} '{toggle.Content}' painted={painted} "
                                  + $"in={bounds} centre={centre} hit={hit?.GetType().Name} ok={reachable}");

                await Assert.That(painted.Right).IsLessThanOrEqualTo(bounds.Right)
                    .Because("the toggles are the trailing group; nothing clips, so overflow is a click "
                             + "the roster panel silently takes");
                await Assert.That(painted.Bottom).IsLessThanOrEqualTo(bounds.Bottom)
                    .Because("the Fluent check box is a 20 px square pinned to y=6..26 of a hard-coded "
                             + "32 px band, so the footer row has to be at least 26 px to hold it");
                await Assert.That(painted.Left).IsGreaterThanOrEqualTo(bounds.Left);
                await Assert.That(painted.Top).IsGreaterThanOrEqualTo(bounds.Top);
                await Assert.That(reachable).IsTrue();
            }

            // round + kill + bomb. Without this the loop above passes on a footer that renders no toggles.
            await Assert.That(probed).IsEqualTo(3);
        });
    }

    /// <summary>
    ///     The readouts are what YIELDS. They dock left in priority order, so the low-value ones ellipsize
    ///     into whatever the toggles and their betters left rather than pushing anything off the control.
    /// </summary>
    [Test]
    public async Task Readouts_YieldToTheToggles_RatherThanPushingThemOff()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab(90000);
            ctx.Push(87654, 175308);

            (Window _, Playback2DView view) = Playback2DTimelineHarness.Show(vm, 820);
            vm.Timeline.FollowStatus = "following Charlie · requested";
            vm.Timeline.SpeedLockNote = "speed pinned by Live Sync";
            Playback2DTimelineHarness.Pump(4);

            TimelineControl timeline = Playback2DTimelineHarness.Timeline(view);
            double right = timeline.Bounds.Width;

            foreach (TextBlock readout in timeline.GetVisualDescendants().OfType<TextBlock>())
            {
                if (!readout.IsEffectivelyVisible
                    || readout.TranslatePoint(default, timeline) is not { } origin)
                {
                    continue;
                }

                Rect box = new(origin, readout.Bounds.Size);
                Console.WriteLine($"[footer-readout] '{readout.Text}' {box}");
                await Assert.That(box.Right).IsLessThanOrEqualTo(right);
            }
        });
    }

    // What the control actually PAINTS, in the timeline's coordinate space. A bare layout Panel is
    // excluded: the Fluent check box hangs its 20 px glyph inside a 32 px Grid that draws nothing, and
    // counting that Grid would fail a row the user sees as correct.
    private static Rect PaintedExtent(Control control, Visual relativeTo)
    {
        Rect extent = new(control.TranslatePoint(default, relativeTo)!.Value, control.Bounds.Size);

        foreach (Visual descendant in control.GetVisualDescendants())
        {
            if (descendant is not Control child
                || child.Bounds.Width <= 0 || child.Bounds.Height <= 0
                || child is Panel { Background: null }
                || child.TranslatePoint(default, relativeTo) is not { } origin)
            {
                continue;
            }

            extent = extent.Union(new Rect(origin, child.Bounds.Size));
        }

        return extent;
    }
}
