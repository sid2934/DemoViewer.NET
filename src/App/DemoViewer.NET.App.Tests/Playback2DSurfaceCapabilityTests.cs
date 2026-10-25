#region

using Avalonia.Controls;
using Avalonia.Headless;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <b>A gate is not a capability.</b> The annotation toolbar's visibility asks the mounted surface
///     whether it implements <c>IAnnotationSurface</c>, not whether the <c>playback2d.annotations</c>
///     feature is on: a gate says whether the user is ALLOWED to draw, the capability says whether there
///     is anything to draw on. Bound to the feature alone, the whole docked tool row renders over a
///     surface with no router, no ink layer and no gesture to cancel, and picking Draw then flips
///     <c>IsDrawingToolActive</c> so the keymap's <c>WhenToolActive</c> rows shadow the always-scoped
///     ones and <c>Space</c>/<c>Esc</c> resolve to handlers that fall through and never set
///     <c>Handled</c>.
///     <para>
///         This runs the REAL view: the defect is entirely in which surface got mounted, and only the
///         View knows that.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class Playback2DSurfaceCapabilityTests
{
    [Test]
    public async Task TheAnnotationToolbar_MountsOnTheSceneSurface()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            (Window _, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            Playback2DTimelineHarness.Pump();

            bool mounted = Toolbar(view).IsEffectivelyVisible;
            Console.WriteLine($"[surface-capability] toolbar={mounted} "
                              + $"annotationsEnabled={vm.IsAnnotationsEnabled}");

            await Assert.That(mounted).IsTrue()
                .Because("the v2 host implements IAnnotationSurface, so the feature is the only question");
            await Assert.That(vm.IsAnnotationsEnabled).IsTrue();
        });
    }

    private static AnnotationToolbar Toolbar(Playback2DView view) =>
        view.FindControl<AnnotationToolbar>("AnnotationToolbarHost")
        ?? throw new InvalidOperationException("AnnotationToolbarHost is not in the view's name scope — "
                                               + "the element was renamed and this suite is measuring nothing.");
}
