#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <b>B2's exit criterion, one case per clause:</b> "draw / erase / undo survive seek, zoom, level
///     switch and tab deactivate". Everything provable without a window is proved in the direct-execution
///     suite instead; what is left here genuinely needs a visual tree, real pointer plumbing and a real
///     render pass.
/// </summary>
[NotInParallel]
public class Playback2DAnnotationHostTests
{
    [Test]
    public async Task AnnotationLayer_IsRegistered_WhenTheGateIsOn()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();

            ISceneLayer? layer = f.Host.Compositor.Find(SceneLayerIds.Annotations);
            await Assert.That(layer).IsNotNull();
            await Assert.That(layer!.IsEnabled).IsTrue();
            await Assert.That(f.Vm.IsAnnotationsEnabled).IsTrue();

            // Registration, not render count: which overlays happen to be toggled on is the user's
            // business, and asserting a rendered-layer total would encode that incidental.
            string ids = string.Join(",", f.Host.Compositor.Layers.Select(l => l.Id));
            Console.WriteLine($"[annotations] layers={ids}");
            await Assert.That(f.Host.Compositor.Layers.Count).IsEqualTo(8)
                .Because("the ink layer joins B1's seven");
        });
    }

    /// <summary>Gated off, the toolbar is gone, the layer is skipped and the tools revert to pan.</summary>
    [Test]
    public async Task GateOff_HidesTheToolbar_AndDropsTheLayer()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.Vm.Annotations.SelectTool(ToolKind.Draw);

            f.Ctx.Gate!.SetEnabled("playback2d.annotations", false);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Vm.IsAnnotationsEnabled).IsFalse();
            await Assert.That(f.Vm.AnnotationSession).IsNull();
            await Assert.That(f.Vm.Annotations.ActiveTool).IsEqualTo(ToolKind.PanZoom);
            await Assert.That(f.Host.Compositor.Find(SceneLayerIds.Annotations)).IsNull();
        });
    }

    [Test]
    public async Task Draw_ThenSeek_StrokeSurvives()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.DrawStroke();

            await Assert.That(f.Document.Elements.Count).IsEqualTo(1);
            IReadOnlyList<InkPoint> before = f.Document.Elements[0].Points;

            // Seek backward, then forward past where the stroke was made.
            f.Ctx.Push(40, 640);
            Playback2DTimelineHarness.Pump();
            f.Ctx.Push(400, 6400);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Document.Elements.Count).IsEqualTo(1);
            await Assert.That(f.Document.Elements[0].Points).IsEquivalentTo(before);
        });
    }

    [Test]
    public async Task Draw_ThenZoomAndPan_StrokeStaysWorldAnchored()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.DrawStroke();

            AnnotationElement element = f.Document.Elements[0];
            InkPoint anchor = element.Points[0];
            Point screenBefore = f.WorldToWindow(anchor.X, anchor.Y);

            f.Vm.Annotations.SelectTool(ToolKind.PanZoom);
            Playback2DTimelineHarness.Pump();

            f.Window.MouseWheel(f.HostPoint(300, 300), new Vector(0, 1));
            Playback2DTimelineHarness.Pump();
            f.Window.MouseDown(f.HostPoint(300, 300), MouseButton.Left);
            f.Window.MouseMove(f.HostPoint(360, 340));
            f.Window.MouseUp(f.HostPoint(360, 340), MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            Point screenAfter = f.WorldToWindow(anchor.X, anchor.Y);

            await Assert.That(f.Document.Elements[0].Points).IsEquivalentTo(element.Points)
                .Because("ink is stored in WORLD units; a camera move must not touch a single sample");
            await Assert.That(Math.Abs(screenAfter.X - screenBefore.X)
                              + Math.Abs(screenAfter.Y - screenBefore.Y)).IsGreaterThan(1)
                .Because("...but it must land somewhere else on screen, or the camera did nothing");
        });
    }

    /// <summary>
    ///     Plan decision D6, and the reason the remap is history-transparent: the stroke follows its
    ///     PHYSICAL floor across a rebuild, and no undo slot is consumed doing it.
    /// </summary>
    [Test]
    public async Task Draw_ThenLevelRebuild_StrokeRemapsToSameLevel()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.DrawStroke();

            double before = ((SpaceRef.World)f.Document.Elements[0].Space).LevelMinZ;
            int undoBefore = f.Document.UndoDepth;

            f.Vm.ApplyAnnotationLevelRebuild(new Dictionary<double, double>
            {
                [before] = before + 64
            });

            await Assert.That(((SpaceRef.World)f.Document.Elements[0].Space).LevelMinZ)
                .IsEqualTo(before + 64);
            await Assert.That(f.Document.UndoDepth).IsEqualTo(undoBefore)
                .Because("a level rebuild is a system event; Ctrl+Z must not restore a stale anchor");
        });
    }

    [Test]
    public async Task Draw_ThenDeactivateReactivateTab_StrokeSurvives()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.DrawStroke();
            AnnotationElement drawn = f.Document.Elements[0];

            f.Vm.OnDeactivated();
            f.Vm.OnActivated(f.Ctx);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Document.Elements.Count).IsEqualTo(1);
            await Assert.That(f.Document.Elements[0]).IsEqualTo(drawn);
        });
    }

    /// <summary>
    ///     The WIRE B3 could not connect, closing its T8 (B5). B3 built and tested the whole remap —
    ///     <c>LevelSetChange.TryRemapAnchor</c> → <c>ApplyLevelRebuild</c> →
    ///     <c>AnnotationDocument.RemapWorldLevels</c> — and nothing in production called it, because B2
    ///     had not landed. The test above this one drives the VM entry point by hand; this one moves the
    ///     LEVEL SET and asserts the ink follows on its own.
    ///     <para>
    ///         It matters because the boundary really does move: the floor split is derived from a Z
    ///         histogram that changes all demo long, and an anchor stamped with the old band's quantized
    ///         <c>ZMin</c> stops matching any pane the moment it drifts — the stroke does not move, it
    ///         vanishes.
    ///     </para>
    /// </summary>
    [Test]
    public async Task LevelSetRebuild_RebasesTheStrokesAnchor_WithoutConsumingUndo()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();

            // Quantum multiples, so QuantizeZ is the identity here and the arithmetic below is exact.
            // A frame push after each rebuild, because the panes are reconciled inside Advance — a bare
            // render tick over an unchanged submission does not re-arrange them, and drawing onto a
            // stale pane would stamp the anchor with the level it is replacing.
            Rebuild(f, -768, -128, 512);

            f.DrawStroke();
            double before = ((SpaceRef.World)f.Document.Elements[0].Space).LevelMinZ;
            int undoBefore = f.Document.UndoDepth;
            int versionBefore = f.Document.Version;
            await Assert.That(before).IsEqualTo(-768).Or.IsEqualTo(-128)
                .Because("the stroke must be anchored to one of the two floors that exist");

            // The same two floors, both slid down one quantum — identity survives (overlap carry), so
            // every anchor follows its own band rather than being reassigned by containment.
            Rebuild(f, -832, -192, 448);

            double after = ((SpaceRef.World)f.Document.Elements[0].Space).LevelMinZ;
            Console.WriteLine($"[level-remap] anchor {before} → {after}");

            await Assert.That(after).IsEqualTo(before - 64)
                .Because("the band the stroke was drawn on moved down one quantum, and so must its anchor");
            await Assert.That(f.Document.UndoDepth).IsEqualTo(undoBefore)
                .Because("a level rebuild is a system event; Ctrl+Z must not restore a stale anchor");
            await Assert.That(f.Document.Version).IsGreaterThan(versionBefore)
                .Because("the ink layer re-records its dry picture on a version change");
        });
    }

    /// <summary>
    ///     ...and a rebuild that moves nothing must cost nothing: no document version bump, so the ink
    ///     layer does not re-record, and no spurious dirty state for the autosave to write.
    /// </summary>
    [Test]
    public async Task LevelSetRebuild_ThatMovesNoBand_LeavesTheDocumentAlone()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            Rebuild(f, -768, -128, 512);

            f.DrawStroke();
            double before = ((SpaceRef.World)f.Document.Elements[0].Space).LevelMinZ;
            int versionBefore = f.Document.Version;

            // A drift far too small to change any quantized key.
            Rebuild(f, -768, -126, 512);

            await Assert.That(((SpaceRef.World)f.Document.Elements[0].Space).LevelMinZ).IsEqualTo(before);
            await Assert.That(f.Document.Version).IsEqualTo(versionBefore);
        });
    }

    // Re-derives the level set as two contiguous bands and pushes a frame, so the panes are actually
    // reconciled onto it before anything is drawn.
    private static void Rebuild(Fixture f, double low, double mid, double high)
    {
        f.Host.Levels.Rebuild([new FloorSlice(low, mid), new FloorSlice(mid, high)]);
        f.Ctx.PushMarkers((0, 2, -800f, 600f, 64f, 90f), (1, 3, 900f, -500f, 64f, 270f));
        Playback2DTimelineHarness.Pump();
    }

    [Test]
    public async Task Erase_ThenUndo_StrokeReturns()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.DrawStroke();
            AnnotationElement drawn = f.Document.Elements[0];

            f.Vm.Annotations.SelectTool(ToolKind.Erase);
            Playback2DTimelineHarness.Pump();

            f.Window.MouseDown(f.HostPoint(300, 300), MouseButton.Left);
            f.Window.MouseMove(f.HostPoint(400, 320));
            f.Window.MouseUp(f.HostPoint(400, 320), MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Document.Elements).IsEmpty();

            f.Document.Undo();
            await Assert.That(f.Document.Elements.Count).IsEqualTo(1);
            await Assert.That(f.Document.Elements[0]).IsEqualTo(drawn)
                .Because("a restored stroke must come back byte-identical, samples included");
        });
    }

    /// <summary>
    ///     The undo-scope contract (design risk 13). A seek between the stroke and the Ctrl+Z must not
    ///     end up on the history stack — the document has no reference to a playhead at all.
    /// </summary>
    [Test]
    public async Task Undo_AfterSeek_UndoesTheStroke_NotTheSeek()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.DrawStroke();
            await Assert.That(f.Document.UndoDepth).IsEqualTo(1);

            f.Ctx.Push(500, 8000);
            Playback2DTimelineHarness.Pump();
            int frameAfterSeek = f.Ctx.CurrentFrameIndex;

            await Assert.That(f.Vm.Annotations.CanUndo).IsTrue()
                .Because("the toolbar's undo button must light up the moment the stroke is committed");
            await Assert.That(f.Vm.ExecuteAction(Playback2DAction.Undo)).IsTrue();

            await Assert.That(f.Document.Elements).IsEmpty();
            await Assert.That(f.Ctx.CurrentFrameIndex).IsEqualTo(frameAfterSeek)
                .Because("the annotation history holds annotations and nothing else");
        });
    }

    /// <summary>Plan decision D3: hold-Space diverts the next press to pan even under the draw tool.</summary>
    [Test]
    public async Task HoldSpace_DuringDrawTool_Pans()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.Vm.Annotations.SelectTool(ToolKind.Draw);
            f.FocusForKeys();

            ViewportTransform before = f.Host.PrimaryCameraTransform;

            f.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            f.Window.MouseDown(f.HostPoint(300, 300), MouseButton.Left);
            f.Window.MouseMove(f.HostPoint(360, 340));
            f.Window.MouseUp(f.HostPoint(360, 340), MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            f.Window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Document.Elements).IsEmpty()
                .Because("hold-Space diverts the whole gesture to pan; no ink is committed");
            await Assert.That(f.Host.PrimaryCameraTransform.PanX).IsEqualTo(before.PanX + 60).Within(0.5);
        });
    }

    [Test]
    public async Task Escape_MidStroke_LeavesNoElement()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.Vm.Annotations.SelectTool(ToolKind.Draw);
            f.FocusForKeys();

            f.Window.MouseDown(f.HostPoint(300, 300), MouseButton.Left);
            f.Window.MouseMove(f.HostPoint(340, 330));
            Playback2DTimelineHarness.Pump();
            await Assert.That(f.Vm.Annotations.Session.Wet.IsActive).IsTrue();

            f.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Vm.Annotations.Session.Wet.IsActive).IsFalse();
            await Assert.That(f.Document.Elements).IsEmpty();
            await Assert.That(f.Document.UndoDepth).IsEqualTo(0);
        });
    }

    /// <summary>
    ///     <b>Coalesced pointer samples must reach the ink oldest-first, exactly once.</b>
    ///     <para>
    ///         Headless <c>MouseMove</c> carries no sub-frame history, so nothing else in this suite ever
    ///         exercises the coalescing path — but a real 1000 Hz digitiser (and a plain mouse on a 60 Hz
    ///         surface) delivers a dozen points per event. Avalonia 11.3.12's
    ///         <c>GetIntermediatePoints</c> returns them OLDEST-FIRST with THIS event's own point
    ///         appended LAST; consuming that list backwards, or keeping the trailing entry, folds the
    ///         stroke back on itself on every fast drag.
    ///     </para>
    ///     <para>
    ///         The event is built through the internal constructor by reflection precisely so this test
    ///         pins Avalonia's real contract: an upstream flip in ordering fails here rather than
    ///         shipping as a zig-zag nobody can reproduce.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CoalescedSamples_ReachTheInk_OldestFirst_AndOnlyOnce()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.Vm.Annotations.SelectTool(ToolKind.Draw);
            Playback2DTimelineHarness.Pump();

            f.Window.MouseDown(f.HostPoint(200, 300), MouseButton.Left);
            Playback2DTimelineHarness.Pump();
            await Assert.That(f.Vm.Annotations.Session.Wet.IsActive).IsTrue();

            // Three sub-frame samples between the press and this move, in the order they happened.
            f.Host.RaiseEvent(PointerMoveWithHistory(f, [220, 240, 260], primary: 280, y: 300));
            Playback2DTimelineHarness.Pump();

            IReadOnlyList<InkPoint> samples = f.Vm.Annotations.Session.Wet.Points;
            string xs = string.Join(",", samples.Select(p => p.X.ToString("F0",
                System.Globalization.CultureInfo.InvariantCulture)));
            Console.WriteLine($"[coalesced] world x = {xs}");

            await Assert.That(samples.Count).IsEqualTo(5)
                .Because("press + three coalesced + the primary point, with nothing duplicated");

            for (int i = 1; i < samples.Count; i++)
            {
                await Assert.That(samples[i].X).IsGreaterThan(samples[i - 1].X)
                    .Because("the pointer only ever moved right, so the ink must too");
            }
        });
    }

    // A real PointerMovedEvent carrying previous raw points. The 9-argument constructor is internal to
    // Avalonia, so it is reached by reflection rather than re-implemented.
    private static PointerEventArgs PointerMoveWithHistory(Fixture f, double[] historyX, double primary,
        double y)
    {
        Type rawPoint = typeof(PointerPoint).Assembly.GetType("Avalonia.Input.Raw.RawPointerPoint")!;
        Type listType = typeof(List<>).MakeGenericType(rawPoint);
        System.Collections.IList history = (System.Collections.IList)Activator.CreateInstance(listType)!;
        System.Reflection.PropertyInfo position = rawPoint.GetProperty("Position")!;

        foreach (double x in historyX)
        {
            object point = Activator.CreateInstance(rawPoint)!;
            position.SetValue(point, f.HostPoint(x, y));
            history.Add(point);
        }

        Type readOnlyList = typeof(IReadOnlyList<>).MakeGenericType(rawPoint);
        Type lazyType = typeof(Lazy<>).MakeGenericType(readOnlyList);
        object lazy = Activator.CreateInstance(lazyType,
            [Delegate.CreateDelegate(typeof(Func<>).MakeGenericType(readOnlyList), history,
                listType.GetMethod("AsReadOnly")!)])!;

        System.Reflection.ConstructorInfo ctor = typeof(PointerEventArgs)
            .GetConstructors(System.Reflection.BindingFlags.Public
                             | System.Reflection.BindingFlags.NonPublic
                             | System.Reflection.BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 9);

        return (PointerEventArgs)ctor.Invoke([
            InputElement.PointerMovedEvent, f.Host,
            new Avalonia.Input.Pointer(1, PointerType.Mouse, isPrimary: true), f.Window,
            f.HostPoint(primary, y), 0UL,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None, lazy
        ]);
    }

    /// <summary>
    ///     Ctrl+X is bound while the pointer is captured mid-stroke, and "clear all" opens a gesture of
    ///     its own — which <c>AnnotationDocument</c> refuses to nest, by design. The toolbar must stand
    ///     down there rather than throw <c>InvalidOperationException</c> out of a key handler.
    /// </summary>
    [Test]
    public async Task ClearAll_MidStroke_StandsDown_InsteadOfThrowing()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.DrawStroke();
            await Assert.That(f.Document.Elements.Count).IsEqualTo(1);

            f.FocusForKeys();
            f.Window.MouseDown(f.HostPoint(200, 260), MouseButton.Left);
            f.Window.MouseMove(f.HostPoint(240, 270));
            Playback2DTimelineHarness.Pump();
            await Assert.That(f.Vm.Annotations.Session.Wet.IsActive).IsTrue();

            f.Vm.ExecuteAction(Playback2DAction.ClearAnnotations);
            f.Vm.Annotations.PinToNowCommand.Execute(null);

            f.Window.MouseUp(f.HostPoint(240, 270), MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Document.Elements.Count).IsEqualTo(2)
                .Because("the stroke in flight still commits; the clear was declined, not half-applied");
            await Assert.That(f.Document.UndoDepth).IsEqualTo(2);
        });
    }

    /// <summary>
    ///     <b>Entity anchoring has to work against the frames the APP builds</b>, not only against
    ///     hand-made ones. <c>PlayerMarker.SteamId</c> is the whole join key (design §5.4: slots recycle),
    ///     and both halves of the feature short-circuit on zero — the tool refuses to capture an anchor,
    ///     and the layer refuses to resolve one. A scene frame built without a slot→SteamId resolver
    ///     therefore makes tracked telestration silently unreachable in the running app while every
    ///     direct-execution test, which injects its own markers, stays green.
    /// </summary>
    [Test]
    public async Task EntityAnchor_IsCapturedFromARealSceneFrame()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();

            ulong expected = f.Ctx.Roster.Single(p => p.Slot == 1).SteamId;
            await Assert.That(expected).IsNotEqualTo(0ul);
            await Assert.That(f.Host.CurrentSceneFrame.Markers.Select(m => m.SteamId))
                .Contains(expected)
                .Because("the scene builder must be told how to turn a roster slot into a SteamId");

            PlayerMarker marker = f.Host.CurrentSceneFrame.Markers.Single(m => m.SteamId == expected);

            f.Vm.Annotations.AnchorToEntities = true;
            f.Vm.Annotations.SelectTool(ToolKind.Draw);
            Playback2DTimelineHarness.Pump();

            Point on = f.WorldToWindow(marker.WorldX, marker.WorldY);
            f.Window.MouseDown(on, MouseButton.Left);
            f.Window.MouseMove(new Point(on.X + 8, on.Y + 4));
            f.Window.MouseUp(new Point(on.X + 8, on.Y + 4), MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Document.Elements.Count).IsEqualTo(1);
            await Assert.That(f.Document.Elements[0].Space).IsTypeOf<SpaceRef.Entity>();
            await Assert.That(((SpaceRef.Entity)f.Document.Elements[0].Space).SteamId)
                .IsEqualTo(expected);
        });
    }

    /// <summary>
    ///     D2 §2.2, through the real pointer plumbing. <c>ToolPointerEvent.Button</c> was resolved
    ///     correctly from day one and read by nothing, so a right-drag drew ink identical to a left-drag.
    ///     This is the case that proves the host actually hands the button to the router.
    /// </summary>
    [Test]
    public async Task RightDrag_DrawsTheSecondaryInk()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.Vm.Annotations.InkColor = Avalonia.Media.Color.FromRgb(0xFF, 0xC1, 0x07);
            f.Vm.Annotations.SecondaryInkColor = Avalonia.Media.Color.FromRgb(0x29, 0xB6, 0xF6);
            f.Vm.Annotations.SelectTool(ToolKind.Draw);
            Playback2DTimelineHarness.Pump();

            f.Window.MouseDown(f.HostPoint(300, 300), MouseButton.Right);
            f.Window.MouseMove(f.HostPoint(350, 310));
            f.Window.MouseUp(f.HostPoint(350, 310), MouseButton.Right);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Document.Elements.Count).IsEqualTo(1);
            await Assert.That(f.Document.Elements[0].Style.ColorArgb).IsEqualTo(0xFF29B6F6u);

            f.Window.MouseDown(f.HostPoint(300, 340), MouseButton.Left);
            f.Window.MouseMove(f.HostPoint(350, 350));
            f.Window.MouseUp(f.HostPoint(350, 350), MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Document.Elements[1].Style.ColorArgb).IsEqualTo(0xFFFFC107u)
                .Because("the left button is untouched by any of this");
        });
    }

    /// <summary>
    ///     The right button can be bound to the eraser instead — the ask this unlocks cheaply. Not the
    ///     shipped default: item 2.2 asked for two PENS, and an out-of-the-box eraser would leave the
    ///     second colour inert with nothing to hint that it exists.
    /// </summary>
    [Test]
    public async Task RightDrag_WithTheEraseBinding_ErasesInstead()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.DrawStroke();
            await Assert.That(f.Document.Elements.Count).IsEqualTo(1);

            f.Vm.Annotations.RightButtonErases = true;
            Playback2DTimelineHarness.Pump();

            f.Window.MouseDown(f.HostPoint(300, 300), MouseButton.Right);
            f.Window.MouseMove(f.HostPoint(350, 310));
            f.Window.MouseUp(f.HostPoint(350, 310), MouseButton.Right);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Document.Elements).IsEmpty()
                .Because("the binding is read at press time, so a toolbar click takes effect immediately");
        });
    }

    /// <summary>
    ///     D2 §2.3, through the real pointer plumbing: the pen may not take the view away. The pane the
    ///     drag begins on is the one that moves, exactly as under the pan tool.
    /// </summary>
    [Test]
    public async Task MiddleDrag_PansWhileTheDrawToolIsActive()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.Vm.Annotations.SelectTool(ToolKind.Draw);
            Playback2DTimelineHarness.Pump();

            double panBefore = f.Host.PrimaryCameraTransform.PanX;

            f.Window.MouseDown(f.HostPoint(300, 300), MouseButton.Middle);
            f.Window.MouseMove(f.HostPoint(360, 300));
            f.Window.MouseUp(f.HostPoint(360, 300), MouseButton.Middle);
            Playback2DTimelineHarness.Pump();

            await Assert.That(f.Document.Elements).IsEmpty();
            await Assert.That(f.Vm.Annotations.Session.Wet.IsActive).IsFalse();
            await Assert.That(f.Host.PrimaryCameraTransform.PanX).IsNotEqualTo(panBefore);
        });
    }

    /// <summary>
    ///     D2 §2.1: the recent-colour strip. <c>AnnotationRecentColors</c> was persisted, WASM-flattened
    ///     and round-trip tested since B2 — and displayed nowhere. This walks the whole chain the fix
    ///     added: a committed stroke pushes its colour, the panel mirrors it, and the toolbar realises a
    ///     button for it.
    /// </summary>
    [Test]
    public async Task DrawnStroke_PutsItsColourInTheRecentStrip()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            AnnotationToolbar toolbar = f.View.GetVisualDescendants().OfType<AnnotationToolbar>().Single();
            ItemsControl strip = toolbar.FindControl<ItemsControl>("RecentColorsStrip")
                                 ?? throw new InvalidOperationException("RecentColorsStrip not found");

            await Assert.That(f.Vm.Annotations.HasRecentColors).IsFalse();
            await Assert.That(strip.IsEffectivelyVisible).IsFalse()
                .Because("an empty strip is chrome with nothing in it");

            f.Vm.Annotations.InkColor = Avalonia.Media.Color.FromRgb(0x11, 0x22, 0x33);
            f.DrawStroke();
            Playback2DTimelineHarness.Pump(3);

            await Assert.That(f.Vm.Annotations.RecentColors.Count).IsEqualTo(1);
            await Assert.That(f.Vm.Annotations.RecentColors[0].Hex).IsEqualTo("#FF112233");
            await Assert.That(strip.IsEffectivelyVisible).IsTrue();
            await Assert.That(strip.GetVisualDescendants().OfType<Button>().Count()).IsEqualTo(1)
                .Because("the swatch template has to realise, not merely bind");
        });
    }

    /// <summary>The ink actually reaches the surface — a captured frame, for eyeball review too.</summary>
    [Test]
    public async Task DrawnStroke_RendersOnTheSurface()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Fixture f = Fixture.Create();
            f.Vm.Annotations.InkColor = Avalonia.Media.Color.FromRgb(0xFF, 0x00, 0xFF);
            f.Vm.Annotations.InkWidth = 40;
            f.DrawStroke();
            Playback2DTimelineHarness.Pump(6);

            WriteableBitmap? captured = f.Window.CaptureRenderedFrame();
            await Assert.That(captured).IsNotNull();

            string path = Path.Combine(HeadlessSession.ArtifactDir, "annotations-stroke.png");
            Directory.CreateDirectory(HeadlessSession.ArtifactDir);
            captured!.Save(path);

            int magenta = CountMagenta(captured);
            Console.WriteLine($"[annotations] {path} magenta={magenta}");
            await Assert.That(magenta).IsGreaterThan(200);
        });
    }

    // BGRA8888, copied out through Marshal rather than pointer arithmetic so the suite needs no
    // unsafe blocks.
    private static int CountMagenta(WriteableBitmap bitmap)
    {
        PixelSize size = bitmap.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];

        using (ILockedFramebuffer framebuffer = bitmap.Lock())
        {
            Marshal.Copy(framebuffer.Address, buffer, 0, buffer.Length);
        }

        int count = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            byte b = buffer[i];
            byte g = buffer[i + 1];
            byte r = buffer[i + 2];
            if (r > 170 && b > 170 && g < 90)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>An activated tab, a shown window with the v2 host, and the pointer helpers.</summary>
    private sealed class Fixture : IDisposable
    {
        private Fixture(Playback2DTabViewModel vm, Playback2DFakeContext ctx, Window window,
            Playback2DView view)
        {
            Vm = vm;
            Ctx = ctx;
            Window = window;
            View = view;
            Host = Playback2DTimelineHarness.SceneHost(view);
        }

        public Playback2DTabViewModel Vm { get; }

        public Playback2DFakeContext Ctx { get; }

        public Window Window { get; }

        public Playback2DView View { get; }

        public Scene2DHost Host { get; }

        public AnnotationDocument Document => Vm.Annotations.Document;

        public static Fixture Create()
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushMarkers((0, 2, -800f, 600f, 64f, 90f), (1, 3, 900f, -500f, 64f, 270f));

            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            Fixture fixture = new(vm, ctx, window, view);
            fixture.Host.FitToExtent();
            Playback2DTimelineHarness.Pump();
            return fixture;
        }

        /// <summary>
        ///     Focuses the view so the tunnelling key handlers see the keyboard. Nothing focuses it on
        ///     its own until the user clicks the surface, and a key test that never clicks would
        ///     otherwise silently assert nothing.
        /// </summary>
        public void FocusForKeys()
        {
            View.Focus();
            Playback2DTimelineHarness.Pump();
        }

        /// <summary>Host-local point → window coordinates, for headless input.</summary>
        /// <param name="x">Host X.</param>
        /// <param name="y">Host Y.</param>
        public Point HostPoint(double x, double y) =>
            Playback2DTimelineHarness.ToWindow(Host, Window, x, y);

        /// <summary>A world point projected through the primary pane's camera into window coordinates.</summary>
        /// <param name="worldX">World X.</param>
        /// <param name="worldY">World Y.</param>
        public Point WorldToWindow(double worldX, double worldY)
        {
            (double sx, double sy) = Host.PrimaryCameraTransform.WorldToScreen(worldX, worldY);
            return HostPoint(sx, sy);
        }

        /// <summary>Selects the draw tool and drags a three-sample stroke well clear of the toolbar.</summary>
        public void DrawStroke()
        {
            Vm.Annotations.SelectTool(ToolKind.Draw);
            Playback2DTimelineHarness.Pump();

            Window.MouseDown(HostPoint(300, 300), MouseButton.Left);
            Window.MouseMove(HostPoint(350, 310));
            Window.MouseMove(HostPoint(400, 320));
            Window.MouseUp(HostPoint(400, 320), MouseButton.Left);
            Playback2DTimelineHarness.Pump();
        }

        public void Dispose()
        {
            Window.Close();
            Vm.Dispose();
        }
    }
}
