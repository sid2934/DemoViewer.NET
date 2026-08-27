#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <b>A gate is not a capability.</b>
///     <para>
///         The annotation toolbar's visibility was bound to the <c>playback2d.annotations</c> FEATURE, so
///         under <c>DV_PLAYBACK2D_RENDERER=legacy</c> the whole docked tool row rendered over the pre-v2
///         viewport — which has no <c>InputToolRouter</c>, no ink layer and no gesture to cancel. Every
///         button in it was inert, and one of them was worse than inert: <c>ToolDraw</c> still succeeded,
///         so <c>IsDrawingToolActive</c> went true, so the keymap's <c>WhenToolActive</c> rows shadowed the
///         always-scoped ones, and <c>Space</c> → <c>HoldPan</c> and <c>Esc</c> → <c>CancelGesture</c>
///         both fell through a <c>is Scene2DHost</c> check and returned <b>without setting
///         <c>Handled</c></b>. The user lost play/pause and clear-follow with no visible cause, recoverable
///         only by pressing D again. Docking the toolbar into permanent chrome made this more likely.
///     </para>
///     <para>
///         These run the REAL view under real headless key events. A view-model test cannot see this: the
///         defect is entirely in which surface got mounted, and only the View knows that.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class Playback2DSurfaceCapabilityTests
{
    /// <summary>
    ///     The mount itself. Gated on the feature alone this row was present under both surfaces; gated on
    ///     the surface's capability it exists only where something can service it.
    /// </summary>
    [Test]
    public async Task TheAnnotationToolbar_MountsOnTheSceneSurface_AndNotOnTheLegacyOne()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel scene, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            (Window _, Playback2DView sceneView) =
                Playback2DTimelineHarness.Show(scene, renderer: Playback2DRendererKind.Scene);

            (Playback2DTabViewModel legacy, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            (Window _, Playback2DView legacyView) =
                Playback2DTimelineHarness.Show(legacy, renderer: Playback2DRendererKind.Legacy);

            Playback2DTimelineHarness.Pump();

            bool onScene = Toolbar(sceneView).IsEffectivelyVisible;
            bool onLegacy = Toolbar(legacyView).IsEffectivelyVisible;
            Console.WriteLine($"[surface-capability] toolbar scene={onScene} legacy={onLegacy} "
                              + $"annotationsEnabled scene={scene.IsAnnotationsEnabled} "
                              + $"legacy={legacy.IsAnnotationsEnabled}");

            await Assert.That(onScene).IsTrue()
                .Because("the v2 host implements IAnnotationSurface, so the feature is the only question");
            await Assert.That(onLegacy).IsFalse()
                .Because("the pre-v2 viewport has no router, no ink layer and no gesture to cancel — a "
                         + "complete tool row over it is an offer nothing can honour");

            await Assert.That(scene.IsAnnotationsEnabled).IsTrue();
            await Assert.That(legacy.IsAnnotationsEnabled).IsFalse();
        });
    }

    /// <summary>
    ///     The transport half of the same defect: pressing the draw key under the legacy surface must not
    ///     take <c>Space</c> away from play/pause.
    /// </summary>
    [Test]
    public async Task UnderTheLegacySurface_TheDrawKeyIsRefused_AndSpaceStillPlays()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Legacy);
            view.Focus();
            Playback2DTimelineHarness.Pump();

            window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            Console.WriteLine($"[surface-capability] after D: tool={vm.Annotations.ActiveTool} "
                              + $"drawing={vm.Annotations.IsDrawingToolActive}");

            await Assert.That(vm.Annotations.ActiveTool).IsEqualTo(ToolKind.PanZoom)
                .Because("ExecuteAction(ToolDraw) must refuse when nothing can host the tool");
            await Assert.That(vm.Annotations.IsDrawingToolActive).IsFalse();

            // The payload. With the tool active the keymap's tool-scoped Space (HoldPan) shadowed the
            // always-scoped one (TogglePlay), and the surface arm that would have handled it did not
            // exist — so the key was consumed by nothing and play/pause was simply gone.
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            Console.WriteLine($"[surface-capability] after Space: play={ctx.PlayCount} pause={ctx.PauseCount}");
            await Assert.That(ctx.PlayCount).IsEqualTo(1)
                .Because("Space is play/pause whenever no drawing tool is — or can be — active");
        });
    }

    /// <summary>
    ///     The other stolen key. <c>Esc</c> is clear-follow at <c>Always</c> scope and cancel-gesture at
    ///     tool scope, so the same false <c>toolActive</c> silently retired the only way to stop following
    ///     a player from the keyboard.
    /// </summary>
    [Test]
    public async Task UnderTheLegacySurface_EscapeStillClearsTheFollowTarget()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Legacy);
            view.Focus();
            Playback2DTimelineHarness.Pump();

            vm.NotifyFollowSlotChanged(1);
            window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.None); // the trap: select a draw tool
            Playback2DTimelineHarness.Pump();

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            Console.WriteLine($"[surface-capability] after Esc: slot={vm.FollowedSlot} "
                              + $"status='{vm.FollowStatus}'");
            await Assert.That(vm.FollowedSlot).IsEqualTo(-1);
        });
    }

    /// <summary>
    ///     Selecting a tool BEFORE the view mounts — restored session state does exactly this, since
    ///     <c>Playback2DTabState</c> carries the last tool — must not survive a bind onto a surface that
    ///     cannot host it. Otherwise the capability is right and <c>IsDrawingToolActive</c> is still true.
    /// </summary>
    [Test]
    public async Task ATooLSelectedBeforeTheBind_IsPutBackWhenTheSurfaceCannotHostIt()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            vm.Annotations.SelectTool(ToolKind.Draw);
            await Assert.That(vm.Annotations.IsDrawingToolActive).IsTrue();

            (Window _, Playback2DView _) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Legacy);
            Playback2DTimelineHarness.Pump();

            Console.WriteLine($"[surface-capability] restored tool after legacy bind: "
                              + $"{vm.Annotations.ActiveTool}");
            await Assert.That(vm.Annotations.IsDrawingToolActive).IsFalse();
        });
    }

    private static AnnotationToolbar Toolbar(Playback2DView view) =>
        view.FindControl<AnnotationToolbar>("AnnotationToolbarHost")
        ?? throw new InvalidOperationException("AnnotationToolbarHost is not in the view's name scope — "
                                               + "the element was renamed and this suite is measuring nothing.");
}
