#region

using Avalonia;
using Avalonia.Controls;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Levels;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The level strip in a real window: that it stays out of the way, that a single-floor map sees no
///     new chrome at all, and that a manual pick actually changes what the canvas draws.
/// </summary>
[NotInParallel]
[Category("Render")]
public class Playback2DLevelStripTests
{
    /// <summary>
    ///     Most maps have one floor, and they must look exactly as they did, no buttons, no gutter,
    ///     nothing.
    /// </summary>
    [Test]
    public async Task Strip_IsCollapsed_OnSingleLevelMap()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);

            PushFloors(ctx, 64f, 64f, 40);
            Playback2DTimelineHarness.Pump();

            Scene2DHost host = Playback2DTimelineHarness.SceneHost(view);
            Console.WriteLine($"[strip] single-floor levels={host.Levels.Levels.Count}");

            await Assert.That(host.Levels.Levels.Count).IsLessThanOrEqualTo(1);
            await Assert.That(vm.LevelStrip.HasMultipleLevels).IsFalse();
            await Assert.That(Strip(view).IsVisible).IsFalse();

            window.Close();
        });
    }

    /// <summary>
    ///     The AUTO chip is a <c>ToggleButton</c> with <c>IsChecked="{Binding IsAutoEnabled}"</c>, so a
    ///     user's flip sets the PROPERTY and never touches the command, which was the only thing that
    ///     raised <c>SettingsChanged</c>. AUTO applied instantly, looked right, and was gone on the next
    ///     launch. The old test drove the command, i.e. the one path the UI does not take.
    ///     <para>
    ///         Three things at once, because each of them is a way the fix could be wrong: the property
    ///         path persists; it persists ONCE per flip (a raise is a full settings read-serialize-write-
    ///         move-reload, and this view-model is one click away from a write storm); and a <b>gate</b>
    ///         going off does not persist, or shipping the gate off would take a real preference away for
    ///         good.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TogglingAutoThroughTheProperty_PersistsExactlyOnce()
    {
        LevelStripViewModel strip = new();
        int saves = 0;
        strip.SettingsChanged += () => saves++;

        // No surface bound: this is the binding's own path, and it must not need one.
        strip.IsAutoEnabled = false;
        await Assert.That(saves).IsEqualTo(1)
            .Because("the ToggleButton's two-way binding is the ONLY path a user has to this flag");

        strip.IsAutoEnabled = true;
        await Assert.That(saves).IsEqualTo(2);

        strip.IsAutoEnabled = true;
        Console.WriteLine($"[strip-auto] saves after 3 assignments (one a no-op) = {saves}");
        await Assert.That(saves).IsEqualTo(2).Because("an unchanged value is not a preference change");

        // Loading persisted state must not immediately re-save it.
        strip.ApplySettings(LevelDisplayMode.Stacked, false);
        await Assert.That(saves).IsEqualTo(2)
            .Because("ApplySettings is the restore path; a save here is a write on every activation");

        // The feature gate closing is not the user changing their mind.
        strip.IsAutoEnabled = true;
        int beforeGate = saves;
        strip.IsAutoAvailable = false;
        Console.WriteLine($"[strip-auto] gate off: enabled={strip.IsAutoEnabled} saves={saves}");
        await Assert.That(strip.IsAutoEnabled).IsFalse();
        await Assert.That(saves).IsEqualTo(beforeGate)
            .Because("a gated-off release must not overwrite AutoLevelFollow with the gate's state");
    }

    [Test]
    public async Task Strip_OrdersChips_HighestFirst()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);

            Scene2DHost host = TwoFloors(ctx, view);

            await Assert.That(vm.LevelStrip.HasMultipleLevels).IsTrue();
            await Assert.That(Strip(view).IsVisible).IsTrue();
            await Assert.That(vm.LevelStrip.Chips).HasCount().EqualTo(host.Levels.Levels.Count);

            // Highest floor first, matching the stacked bands' own top-to-bottom order.
            await Assert.That(vm.LevelStrip.Chips[0].Id).IsEqualTo(host.Levels.Levels[^1].Id);
            await Assert.That(vm.LevelStrip.Chips[^1].Id).IsEqualTo(host.Levels.Levels[0].Id);

            window.Close();
        });
    }

    [Test]
    [Arguments(1100, 650)]
    [Arguments(700, 420)]
    public async Task Strip_DoesNotOverlap_TimelineOrKillFeed(int width, int height)
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, width, height, Playback2DRendererKind.Scene);

            TwoFloors(ctx, view);

            // The strip's IsVisible flips INSIDE a render pass. The level set is derived while the host
            // draws, and a collapsed control has no measured size to invalidate from, so the headless
            // pump alone leaves it at zero bounds. Ask for the measure explicitly.
            Strip(view).InvalidateMeasure();
            Playback2DTimelineHarness.Pump(5);

            Rect strip = BoundsIn(Strip(view), window);
            Rect timeline = BoundsIn(Playback2DTimelineHarness.Timeline(view), window);
            Rect hud = BoundsIn(view.FindControl<StackPanel>("HudStack")!, window);

            Console.WriteLine($"[strip] {width}x{height} strip={strip} timeline={timeline} hud={hud}");

            await Assert.That(strip.Width).IsGreaterThan(0);
            await Assert.That(strip.Intersects(timeline)).IsFalse();
            await Assert.That(strip.Intersects(hud)).IsFalse();

            window.Close();
        });
    }

    [Test]
    public async Task ManualPick_SwitchesPane_AndDisablesAuto()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);

            Scene2DHost host = TwoFloors(ctx, view);
            MapLevelId lowest = host.Levels.Levels[0].Id;

            LevelChipViewModel lower = vm.LevelStrip.Chips[^1];
            await Assert.That(lower.Id).IsEqualTo(lowest);

            vm.LevelStrip.SelectCommand.Execute(lower);
            Playback2DTimelineHarness.Pump();

            await Assert.That(vm.LevelStrip.IsAutoEnabled).IsFalse();
            await Assert.That(host.AutoLevelFollow).IsFalse();
            await Assert.That(host.DisplayMode).IsEqualTo(LevelDisplayMode.Single);
            await Assert.That(host.ActiveLevelId).IsEqualTo(lowest);
            await Assert.That(host.PaneCountForTest).IsEqualTo(1);
            await Assert.That(host.PrimaryPaneLevelForTest).IsEqualTo(lowest);

            // …and back to stacked returns every floor to its own band.
            vm.LevelStrip.ToggleDisplayModeCommand.Execute(null);
            Playback2DTimelineHarness.Pump();

            await Assert.That(host.DisplayMode).IsEqualTo(LevelDisplayMode.Stacked);
            await Assert.That(host.PaneCountForTest).IsEqualTo(host.Levels.Levels.Count);

            window.Close();
        });
    }

    /// <summary>
    ///     The gate covers AutoFollow only. The strip still picks floors with it off. A manual picker
    ///     does not need a permanent persisted key of its own.
    /// </summary>
    [Test]
    public async Task AutoChip_Hidden_WhenFeatureGateOff()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);

            Scene2DHost host = TwoFloors(ctx, view);
            await Assert.That(vm.LevelStrip.IsAutoAvailable).IsTrue();

            ctx.Gate!.SetEnabled("playback2d.levels.auto", false);
            Playback2DTimelineHarness.Pump();

            await Assert.That(vm.IsAutoLevelEnabled).IsFalse();
            await Assert.That(vm.LevelStrip.IsAutoAvailable).IsFalse();
            await Assert.That(vm.LevelStrip.IsAutoEnabled).IsFalse();
            await Assert.That(host.AutoLevelFollow).IsFalse();

            // The strip itself is still there and still picks floors.
            await Assert.That(Strip(view).IsVisible).IsTrue();
            vm.LevelStrip.SelectCommand.Execute(vm.LevelStrip.Chips[^1]);
            Playback2DTimelineHarness.Pump();
            await Assert.That(host.ActiveLevelId).IsEqualTo(host.Levels.Levels[0].Id);

            window.Close();
        });
    }

    /// <summary>
    ///     The visible no-radar state. A histogram-derived split on a map with no baked bundle binds no
    ///     radar at all, which is exactly the case a user would otherwise read as a broken map.
    /// </summary>
    [Test]
    public async Task NoRadarChip_ShowsGlyphAndTooltip()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);

            TwoFloors(ctx, view);

            foreach (LevelChipViewModel chip in vm.LevelStrip.Chips)
            {
                await Assert.That(chip.HasRadar).IsFalse();
                await Assert.That(chip.HasNoRadar).IsTrue();
                await Assert.That(chip.Tooltip).Contains("no baked radar");
                await Assert.That(chip.ZRange).StartsWith("z[");
            }

            window.Close();
        });
    }

    /// <summary>
    ///     <b>The whole AutoFollow chain, through the real wiring:</b> the VM's follow funnel →
    ///     <c>Scene2DHost._followSlot</c> → <c>LevelSelection.FollowedSlot</c> → the hysteresis dwell →
    ///     <c>SingleLayout.ActiveLevelId</c> → the arranged pane. <c>LevelSelectionTests</c> pins the
    ///     decision in isolation and <see cref="ManualPick_SwitchesPane_AndDisablesAuto" /> pins the
    ///     strip, but neither involves a followed player, so nothing else catches the seam between them
    ///     going quiet, and a level chooser wired to a followed slot that is never assigned looks
    ///     exactly like one whose dwell has not elapsed.
    /// </summary>
    [Test]
    public async Task AutoFollow_ShowsTheFollowedPlayersFloor_AndAManualPickOverridesUntilReleased()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);

            Scene2DHost host = TwoFloors(ctx, view);
            MapLevelId lower = host.Levels.Levels[0].Id;
            MapLevelId upper = host.Levels.Levels[^1].Id;
            await Assert.That(host.AutoLevelFollow).IsTrue();

            // Follow a player on the UPPER floor: the shown level must become theirs.
            vm.NotifyFollowSlotChanged(2);
            bool followed = PushUntil(ctx, () => host.ActiveLevelId == upper);
            Console.WriteLine($"[autofollow] follow slot 2 -> active={host.ActiveLevelId} " +
                              $"(lower={lower} upper={upper})");
            await Assert.That(followed).IsTrue()
                .Because("AutoFollow must reach the followed player's floor");

            // In Single mode that decision is what the one arranged pane shows.
            vm.LevelStrip.ToggleDisplayModeCommand.Execute(null);
            Playback2DTimelineHarness.Pump(5);
            await Assert.That(host.DisplayMode).IsEqualTo(LevelDisplayMode.Single);
            await Assert.That(host.PaneCountForTest).IsEqualTo(1);
            await Assert.That(host.PrimaryPaneLevelForTest).IsEqualTo(upper);

            // A manual pick PINS the other floor, and keeps it while the followed player stays put.
            vm.LevelStrip.SelectCommand.Execute(vm.LevelStrip.Chips[^1]);
            PushUntil(ctx, () => false);
            Console.WriteLine($"[autofollow] manual pick -> active={host.ActiveLevelId} " +
                              $"auto={host.AutoLevelFollow} pane0={host.PrimaryPaneLevelForTest}");
            await Assert.That(host.AutoLevelFollow).IsFalse();
            await Assert.That(host.ActiveLevelId).IsEqualTo(lower);
            await Assert.That(host.PrimaryPaneLevelForTest).IsEqualTo(lower);

            // Releasing the pin, re-arming AUTO, hands the decision back to the followed player.
            // A method, not a command: the AUTO chip is a ToggleButton bound to IsAutoEnabled, so the
            // generated command was never on the user's path.
            vm.LevelStrip.EnableAuto();
            bool released = PushUntil(ctx, () => host.ActiveLevelId == upper);
            Console.WriteLine($"[autofollow] AUTO re-arm -> active={host.ActiveLevelId} " +
                              $"pane0={host.PrimaryPaneLevelForTest}");
            await Assert.That(released).IsTrue();
            await Assert.That(host.PrimaryPaneLevelForTest).IsEqualTo(upper);

            window.Close();
        });
    }

    // Pushes frames until the condition holds, or the cap is reached. The AutoFollow dwell is 0.35 s of
    // SCENE time and the host's dt comes from the headless animation clock, so a fixed pump count would
    // be a timing bet; stopping early on the outcome is not.
    private static bool PushUntil(Playback2DFakeContext ctx, Func<bool> done, int cap = 400)
    {
        for (int i = 0; i < cap; i++)
        {
            PushFloors(ctx, 64f, 704f, 1);
            if (done())
            {
                return true;
            }
        }

        return false;
    }

    private static Border Strip(Playback2DView view) =>
        view.FindControl<Border>("LevelStrip")
        ?? throw new InvalidOperationException("level strip not found");

    private static Rect BoundsIn(Visual visual, Window window)
    {
        Point origin = visual.TranslatePoint(default, window) ?? default;
        return new Rect(origin, visual.Bounds.Size);
    }

    // Two Z clusters far enough apart for FloorSplitter's density-valley rule (gap threshold 180u),
    // pushed until the histogram has enough samples to split.
    private static Scene2DHost TwoFloors(Playback2DFakeContext ctx, Playback2DView view)
    {
        Scene2DHost host = Playback2DTimelineHarness.SceneHost(view);
        PushFloors(ctx, 64f, 704f, 60);
        Playback2DTimelineHarness.Pump();

        Console.WriteLine($"[strip] levels={host.Levels.Levels.Count} " +
                          $"radar={host.Levels.RadarBinding}");
        return host;
    }

    private static void PushFloors(Playback2DFakeContext ctx, float lowerZ, float upperZ, int pushes)
    {
        for (int i = 0; i < pushes; i++)
        {
            ctx.PushMarkers(
                (0, 2, -800f, 600f, lowerZ, 90f),
                (1, 2, -700f, 500f, lowerZ, 90f),
                (2, 3, 900f, -500f, upperZ, 270f),
                (3, 3, 800f, -400f, upperZ, 270f));
            Playback2DTimelineHarness.Pump(1);
        }
    }
}
