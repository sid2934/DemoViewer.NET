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
///     A rebind is only real if it survives the whole route — settings → profile → the view's tunnelling
///     handler → <c>ExecuteAction</c>. These run the real controls under real headless key events, because
///     two failure modes are both invisible to a unit test of the profile: the view
///     still calling the SHIPPED static table, and the KeyUp half of hold-to-pan still hard-coded to Space.
/// </summary>
[NotInParallel]
[Category("Render")]
public class Playback2DKeybindRoutingTests
{
    [Test]
    public async Task AReboundGesture_Routes_AndTheVacatedKeyDoesNot()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            vm.ApplyKeymapOverrides(["NextRound=Shift+R"]);

            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            view.Focus();
            Playback2DTimelineHarness.Pump();

            window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Shift);
            Playback2DTimelineHarness.Pump();

            await Assert.That(ctx.NextEvents.Count).IsEqualTo(1);
            await Assert.That(ctx.NextEvents[0][0]).IsEqualTo("round_freeze_end");

            // The shipped key is now inert — a view still consulting Playback2DKeymap directly would
            // fire here and pass every other assertion in this file.
            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();
            await Assert.That(ctx.NextEvents.Count).IsEqualTo(1)
                .Because("E was vacated by the override, so it must reach nothing");
        });
    }

    /// <summary>
    ///     The KeyUp hazard. Nothing but the release ever clears the router's pan flag, so a hard-coded
    ///     <c>Space</c> in <c>OnKeyUp</c> would leave a user who rebound hold-to-pan stuck in pan mode from
    ///     the first time they used it — with the pen selected and no way out short of restarting the tab.
    /// </summary>
    [Test]
    public async Task ReboundHoldPan_EntersAndLeavesPanMode()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            vm.ApplyKeymapOverrides(["HoldPan=B"]);

            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            Scene2DHost host = Playback2DTimelineHarness.SceneHost(view);

            vm.Annotations.SelectTool(ToolKind.Draw);
            view.Focus();
            Playback2DTimelineHarness.Pump();

            window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();
            await Assert.That(host.Router.IsSpaceHeld).IsTrue();

            window.KeyReleaseQwerty(PhysicalKey.B, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();
            await Assert.That(host.Router.IsSpaceHeld).IsFalse()
                .Because("the release follows the BINDING; a hard-coded Space would strand the surface");

            // Space is no longer the pan key, so under the pen it is plain play/pause again.
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();
            await Assert.That(host.Router.IsSpaceHeld).IsFalse();
            await Assert.That(ctx.PlayCount).IsEqualTo(1);
        });
    }

    /// <summary>
    ///     <b>The rebind that lands MID-HOLD.</b> <c>OnKeyUp</c> resolved <c>HoldPan</c> against the
    ///     CURRENT profile, so rebinding it — from the Settings page, or by an editor saving
    ///     <c>settings.json</c>, which the tab watches live — while the key was still down made the
    ///     release match nothing. Nothing else ever clears the router's pan flag, so the surface panned
    ///     forever, under the pen, with no way out short of reopening the tab.
    /// </summary>
    [Test]
    public async Task HoldPan_ReboundWhileHeld_StillReleases()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            vm.ApplyKeymapOverrides(["HoldPan=B"]);

            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            Scene2DHost host = Playback2DTimelineHarness.SceneHost(view);

            vm.Annotations.SelectTool(ToolKind.Draw);
            view.Focus();
            Playback2DTimelineHarness.Pump();

            window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();
            await Assert.That(host.Router.IsSpaceHeld).IsTrue();

            // The binding moves out from under the held key.
            vm.ApplyKeymapOverrides(["HoldPan=M"]);
            Playback2DTimelineHarness.Pump();

            window.KeyReleaseQwerty(PhysicalKey.B, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            await Assert.That(host.Router.IsSpaceHeld).IsFalse()
                .Because("the release follows the key that STARTED the hold, not the current binding");

            // And the NEW binding is live for the next hold, so the latch did not freeze the mapping.
            window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();
            await Assert.That(host.Router.IsSpaceHeld).IsTrue();

            window.KeyReleaseQwerty(PhysicalKey.M, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();
            await Assert.That(host.Router.IsSpaceHeld).IsFalse();
        });
    }

    /// <summary>
    ///     Shipped defaults, unchanged: the hold-to-pan shadow still works when nothing is rebound. The
    ///     test above would also pass on a build that broke it.
    /// </summary>
    [Test]
    public async Task DefaultHoldPan_StillEntersAndLeavesPanMode()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            Scene2DHost host = Playback2DTimelineHarness.SceneHost(view);

            vm.Annotations.SelectTool(ToolKind.Draw);
            view.Focus();
            Playback2DTimelineHarness.Pump();

            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();
            await Assert.That(host.Router.IsSpaceHeld).IsTrue();
            await Assert.That(ctx.PlayCount).IsEqualTo(0)
                .Because("a pan must not start by un-pausing the demo under the user's pen");

            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();
            await Assert.That(host.Router.IsSpaceHeld).IsFalse();
        });
    }

    /// <summary>
    ///     The whole point of the profile: a settings file full of nonsense costs the user nothing but the
    ///     rebinds themselves. The tab builds, the keys still work, and the reasons are available.
    /// </summary>
    [Test]
    public async Task AnUnusableOverrideFile_LeavesTheTabFullyKeyboardOperable()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            string[] bad =
            [
                "NextRound", "NextRound=Bogus", "Teleport=Y", "NextRound=Ctrl+O",
                "NextRound=D", "FitCamera=G"
            ];
            vm.ApplyKeymapOverrides(["", .. bad]);

            Console.WriteLine("[keybind-routing] " + string.Join(" | ", vm.KeymapRejections));

            // Which rows, not how many — the blank one is skipped silently and every other is named.
            foreach (string row in bad)
            {
                await Assert.That(vm.KeymapRejections.Any(r => r.StartsWith(row + ":", StringComparison.Ordinal))).IsTrue()
                    .Because($"the tab has to name the row it dropped: {row}");
            }

            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            view.Focus();
            Playback2DTimelineHarness.Pump();

            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            await Assert.That(ctx.PlayCount).IsEqualTo(1);
            await Assert.That(ctx.NextEvents.Count).IsEqualTo(1);
            await Assert.That(ctx.NextEvents[0][0]).IsEqualTo("round_freeze_end");
        });
    }

    /// <summary>
    ///     With no container there are no settings, and the tab must open on the shipped table rather than
    ///     on nothing — the same trade every other optional dependency in this view-model makes.
    /// </summary>
    [Test]
    public async Task NoContainer_OpensOnTheShippedTable()
    {
        Playback2DTabViewModel vm = new();
        try
        {
            await Assert.That(vm.KeymapRejections).IsEmpty();
            await Assert.That(vm.Keymap.GestureText(Playback2DAction.NextRound)).IsEqualTo("E");
            await Assert.That(vm.Keymap.Bindings).IsEquivalentTo(Playback2DKeymap.Default);
        }
        finally
        {
            vm.Dispose();
        }
    }
}
