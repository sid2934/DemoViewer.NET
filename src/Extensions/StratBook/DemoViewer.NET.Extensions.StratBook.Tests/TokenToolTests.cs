#region

using DemoViewer.NET.Extensions.StratBook.Playback2D.Input;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Cameras;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The token tool over a fake <see cref="ITokenEditor" /> (step-authoring.md §3.7, §7): a miss or a
///     missing editor is not a gesture, a drag writes the pointer's pane level, release and Esc close it
///     one way or the other, the heading grip turns, and the router's pan diversions still win.
/// </summary>
public class TokenToolTests
{
    [Test]
    public async Task Press_WithNoEditor_ReturnsFalse()
    {
        Harness h = new(editor: false);

        await Assert.That(h.Press(0, 0)).IsFalse()
            .Because("the 2D Playback tab has no token editor, so the press falls through");
        await Assert.That(h.Tool.IsDragging).IsFalse();
    }

    [Test]
    public async Task Press_AwayFromEveryToken_ReturnsFalse_AndOpensNothing()
    {
        Harness h = new();
        h.Editor.Tokens["A"] = (0, 0, 0);

        await Assert.That(h.Press(500, 500)).IsFalse();
        await Assert.That(h.Editor.Calls).IsEmpty();
        await Assert.That(h.Editor.LastRadius).IsGreaterThan(0f)
            .Because("the editor was asked, with a radius in world units");
    }

    [Test]
    public async Task Drag_WritesMoveTo_WithThePanesQuantizedLevel_AndReleaseClosesIt()
    {
        Harness h = new(zMin: -447.5);
        h.Editor.Tokens["B"] = (0, 0, 0);

        await Assert.That(h.Press(0, 0)).IsTrue();
        h.Move(40, 10);
        h.Move(80, 20);
        h.Release(120, 30);

        await Assert.That(string.Join("|", h.Editor.Calls))
            .IsEqualTo("begin B Body|move B 40,10 @-448|move B 80,20 @-448|move B 120,30 @-448|end B None");
        await Assert.That(h.Tool.IsDragging).IsFalse();
        await Assert.That(h.Services.RenderRequests).IsGreaterThan(0);
    }

    /// <summary>
    ///     The pane under the pointer supplies the level, sample by sample, which is how a token is dragged
    ///     onto another floor of a stacked map.
    /// </summary>
    [Test]
    public async Task Drag_AcrossPanes_TakesEachPanesLevel()
    {
        Harness h = new();
        h.Editor.Tokens["C"] = (0, 0, 0);
        LevelPane upper = LocalFakes.Pane(600, 400, zMin: -384, zMax: -128);

        h.Press(0, 0);
        h.Tool.OnMoved(Harness.Event(upper, h.Services, 30, 30), h.Services);
        h.Tool.OnMoved(Harness.Event(null, h.Services, 40, 40), h.Services);

        await Assert.That(string.Join("|", h.Editor.Calls)).IsEqualTo("begin C Body|move C 30,30 @-384")
            .Because("a sample over no pane is skipped rather than guessed");
    }

    [Test]
    public async Task Esc_CallsCancelDrag_AndTheReleaseAfterItIsANoOp()
    {
        Harness h = new();
        h.Editor.Tokens["D"] = (0, 0, 0);

        h.Press(0, 0);
        h.Move(50, 0);
        h.Tool.OnCancelled(h.Services);
        h.Release(60, 0);

        await Assert.That(string.Join("|", h.Editor.Calls)).IsEqualTo("begin D Body|move D 50,0 @0|cancel D");
    }

    [Test]
    public async Task EverySample_CarriesTheKeysHeld_AndTheRelease_ClosesWithThem()
    {
        Harness h = new();
        h.Editor.Tokens["A"] = (0, 0, 0);

        h.Press(0, 0);
        h.Tool.OnMoved(Harness.Event(h.Pane, h.Services, 0, 50, ToolModifiers.Alt), h.Services);
        h.Release(0, 100, ToolModifiers.Shift);

        await Assert.That(string.Join("|", h.Editor.Calls))
            .IsEqualTo("begin A Body|move A 0,50 @0 Alt|move A 0,100 @0 Shift|end A Shift")
            .Because("Shift stores a point and Alt pins; no key snaps a facing any more");
    }

    [Test]
    public async Task HeadingGrip_Rotates_AndTheReleaseOnlyCloses()
    {
        Harness h = new();
        h.Editor.Tokens["E"] = (0, 0, 0);
        h.Editor.ForceGrip = TokenGrip.Heading;

        h.Press(0, 0);
        h.Move(0, 200);
        h.Release(0, 300);

        await Assert.That(h.Editor.Calls[0]).IsEqualTo("begin E Heading");
        await Assert.That(h.Editor.Calls[^1]).IsEqualTo("end E None");
    }

    /// <summary>The disc moves, the stub beyond it turns, and a point off both misses.</summary>
    [Test]
    public async Task HitTest_SplitsTheDiscFromTheHeadingStub()
    {
        const float radius = 9f;

        await Assert.That(TokenHitTest.Classify(0, 0, 0, new SKPoint(4, 3), radius)).IsEqualTo(TokenGrip.Body);
        await Assert.That(TokenHitTest.Classify(0, 0, 0, new SKPoint(14, 0), radius)).IsEqualTo(TokenGrip.Heading);
        await Assert.That(TokenHitTest.Classify(0, 0, 90, new SKPoint(0, 15), radius)).IsEqualTo(TokenGrip.Heading)
            .Because("the stub follows the yaw, world yaw 90 pointing +Y");
        await Assert.That(TokenHitTest.Classify(0, 0, 90, new SKPoint(15, 0), radius)).IsNull();
        await Assert.That(TokenHitTest.Classify(0, 0, 0, new SKPoint(-14, 0), radius)).IsNull()
            .Because("behind the token there is no stub");
        await Assert.That(TokenHitTest.WorldRadius(2)).IsEqualTo(SceneDefaults.MarkerRadius * 2);
    }

    [Test]
    [Arguments(ToolPointerButton.Middle, ToolModifiers.None, false)]
    [Arguments(ToolPointerButton.Left, ToolModifiers.Control, false)]
    [Arguments(ToolPointerButton.Left, ToolModifiers.None, true)]
    public async Task MiddleDragCtrlDragAndHoldSpace_StillPan(ToolPointerButton button, ToolModifiers modifiers,
        bool holdSpace)
    {
        (MapSpace _, PaneSet panes) = LocalFakes.Panes(new SKSize(600, 400),
            new FloorSlice(-448, -384), new FloorSlice(-384, -128));
        FakeToolServices services = new(new AnnotationSession(new AnnotationDocument()), panes);
        FakeTokenEditor editor = new(hitEverything: true);
        services.Tokens = editor;

        InputToolRouter router = new(services, new PanZoomTool());
        router.Register(new TokenTool());
        router.SetActive(ToolKind.Token);
        router.IsSpaceHeld = holdSpace;

        SKPoint start = new(200, 100);
        LevelPane pane = services.PaneAt(start)!;
        router.OnPressed(Harness.ScreenEvent(pane, start, button, modifiers));

        await Assert.That(router.GestureTool).IsTypeOf<PanZoomTool>();
        await Assert.That(editor.Calls).IsEmpty();
    }

    private sealed class Harness
    {
        public Harness(bool editor = true, double zMin = 0)
        {
            Pane = LocalFakes.Pane(600, 400, zMin: zMin, zMax: zMin + 64);
            Services = new FakeToolServices(new AnnotationSession(new AnnotationDocument()), Pane);
            Editor = new FakeTokenEditor();
            if (editor)
            {
                Services.Tokens = Editor;
            }
        }

        public LevelPane Pane { get; }

        public FakeToolServices Services { get; }

        public FakeTokenEditor Editor { get; }

        public TokenTool Tool { get; } = new();

        public bool Press(float x, float y) => Tool.OnPressed(Event(Pane, Services, x, y), Services);

        public void Move(float x, float y) => Tool.OnMoved(Event(Pane, Services, x, y), Services);

        public void Release(float x, float y, ToolModifiers modifiers = ToolModifiers.None) =>
            Tool.OnReleased(Event(Pane, Services, x, y, modifiers), Services);

        public static ToolPointerEvent Event(LevelPane? pane, FakeToolServices services, float x, float y,
            ToolModifiers modifiers = ToolModifiers.None) =>
            new()
            {
                Pane = pane,
                Screen = pane is null ? default : services.WorldToScreen(pane, new SKPoint(x, y)),
                World = new SKPoint(x, y),
                Pressure = 0.5f,
                Button = ToolPointerButton.Left,
                Modifiers = modifiers
            };

        public static ToolPointerEvent ScreenEvent(LevelPane pane, SKPoint screen, ToolPointerButton button,
            ToolModifiers modifiers)
        {
            (double wx, double wy) = pane.Camera.Current.ScreenToWorld(
                screen.X - pane.ViewportRect.Left, screen.Y - pane.ViewportRect.Top);
            return new ToolPointerEvent
            {
                Pane = pane,
                Screen = screen,
                PaneLocal = new SKPoint(screen.X - pane.ViewportRect.Left, screen.Y - pane.ViewportRect.Top),
                World = new SKPoint((float)wx, (float)wy),
                Pressure = 0.5f,
                Button = button,
                Modifiers = modifiers
            };
        }
    }

    /// <summary>Records every call as a short line, and hit-tests the tokens it holds with the shared geometry.</summary>
    private sealed class FakeTokenEditor(bool hitEverything = false) : ITokenEditor
    {
        public Dictionary<string, (float X, float Y, float Yaw)> Tokens { get; } = [];

        public List<string> Calls { get; } = [];

        public TokenGrip? ForceGrip { get; set; }

        public float LastRadius { get; private set; }

        public int ActiveTick => 0;

        public bool TryHitToken(LevelPane pane, SKPoint world, float worldRadius, out string slot, out TokenGrip grip)
        {
            LastRadius = worldRadius;
            if (hitEverything)
            {
                slot = "A";
                grip = TokenGrip.Body;
                return true;
            }

            foreach ((string key, (float x, float y, float yaw)) in Tokens)
            {
                if (TokenHitTest.Classify(x, y, yaw, world, worldRadius) is { } hit)
                {
                    slot = key;
                    grip = ForceGrip ?? hit;
                    return true;
                }
            }

            slot = "";
            grip = TokenGrip.Body;
            return false;
        }

        public void BeginDrag(string slot, TokenGrip grip) => Calls.Add($"begin {slot} {grip}");

        public void MoveTo(string slot, SKPoint world, double levelMinZ, ToolModifiers modifiers = ToolModifiers.None, double worldUnitsPerPixel = 0) =>
            Calls.Add(FormattableString.Invariant($"move {slot} {world.X},{world.Y} @{levelMinZ}") + (modifiers == ToolModifiers.None ? "" : " " + modifiers));

        public void EndDrag(ToolModifiers modifiers = ToolModifiers.None) => Calls.Add($"end {Active} {modifiers}");

        public void CancelDrag() => Calls.Add($"cancel {Active}");

        private string Active => Calls.FirstOrDefault(c => c.StartsWith("begin ", StringComparison.Ordinal))?.Split(' ')[1] ?? "?";
    }

    // A direct-execution IToolServices and the two pane builders this suite needs, local rather than
    // shared with Playback2D.Tests: that project and this one both link tests/shared/TestTiers.cs, and a
    // ProjectReference between them duplicates every type it declares (CS0436).
    private sealed class FakeToolServices : IToolServices
    {
        private readonly PaneSet? _panes;
        private readonly LevelPane? _single;

        public FakeToolServices(AnnotationSession session, LevelPane pane)
        {
            Session = session;
            _single = pane;
        }

        public FakeToolServices(AnnotationSession session, PaneSet panes)
        {
            Session = session;
            _panes = panes;
        }

        public List<PlayerMarker> Markers { get; } = [];

        public AnnotationSession Session { get; }

        public int CurrentTick { get; set; }

        public long NowMilliseconds { get; set; }

        public LevelPane? PaneAt(SKPoint screen) =>
            _panes is not null ? _panes.PaneAt(screen.X, screen.Y) : _single;

        public SKPoint ScreenToWorld(LevelPane pane, SKPoint screen)
        {
            ArgumentNullException.ThrowIfNull(pane);
            (double x, double y) = pane.Camera.Current.ScreenToWorld(
                screen.X - pane.ViewportRect.Left, screen.Y - pane.ViewportRect.Top);
            return new SKPoint((float)x, (float)y);
        }

        public SKPoint WorldToScreen(LevelPane pane, SKPoint world)
        {
            ArgumentNullException.ThrowIfNull(pane);
            (double x, double y) = pane.Camera.Current.WorldToScreen(world.X, world.Y);
            return new SKPoint((float)x + pane.ViewportRect.Left, (float)y + pane.ViewportRect.Top);
        }

        public double WorldUnitsPerPixel(LevelPane pane)
        {
            ArgumentNullException.ThrowIfNull(pane);
            double scale = pane.Camera.Current.EffectiveScale;
            return scale > 0 ? 1 / scale : 1;
        }

        public bool TryResolveEntityAnchor(LevelPane pane, SKPoint world, float worldRadius,
            out ulong steamId, out float dx, out float dy)
        {
            steamId = 0;
            dx = 0;
            dy = 0;

            float best = worldRadius * worldRadius;
            bool found = false;
            for (int i = 0; i < Markers.Count; i++)
            {
                PlayerMarker marker = Markers[i];
                if (!marker.IsAlive || marker.SteamId == 0)
                {
                    continue;
                }

                float ddx = world.X - marker.WorldX;
                float ddy = world.Y - marker.WorldY;
                float distance = ddx * ddx + ddy * ddy;
                if (distance > best)
                {
                    continue;
                }

                best = distance;
                steamId = marker.SteamId;
                dx = ddx;
                dy = ddy;
                found = true;
            }

            return found;
        }

        public bool TryResolveDrawOffset(LevelPane pane, AnnotationElement element,
            out float offsetX, out float offsetY)
        {
            ArgumentNullException.ThrowIfNull(pane);
            ArgumentNullException.ThrowIfNull(element);

            offsetX = 0;
            offsetY = 0;

            switch (element.Space)
            {
                case SpaceRef.World world:
                {
                    MapSpace? space = pane.Space;
                    return space is not { Levels.Count: > 1 }
                           || space.IdForAnchor(world.LevelMinZ) == pane.LevelId;
                }

                case SpaceRef.Entity entity:
                {
                    for (int i = 0; i < Markers.Count; i++)
                    {
                        PlayerMarker marker = Markers[i];
                        if (marker.SteamId != entity.SteamId || entity.SteamId == 0)
                        {
                            continue;
                        }

                        if (!marker.IsAlive)
                        {
                            return false;
                        }

                        InkPoint origin = element.Points.Count > 0 ? element.Points[0] : default;
                        offsetX = marker.WorldX + entity.Dx - origin.X;
                        offsetY = marker.WorldY + entity.Dy - origin.Y;
                        return true;
                    }

                    return false;
                }

                default:
                    return false;
            }
        }

        public List<Guid> TextEditRequests { get; } = [];

        public void RequestTextEdit(Guid elementId) => TextEditRequests.Add(elementId);

        /// <summary>The token editor the token tool drives; null, as on the 2D Playback tab, until a test sets it.</summary>
        public ITokenEditor? Tokens { get; set; }

        public int RenderRequests { get; private set; }

        public void RequestRender() => RenderRequests++;
    }

    private static class LocalFakes
    {
        /// <summary>A stacked pane set over the given bands, arranged on a host surface.</summary>
        public static (MapSpace Space, PaneSet Panes) Panes(SKSize host, params FloorSlice[] bands)
        {
            MapSpace space = new();
            space.Rebuild(bands);
            PaneSet panes = new(new StackedLayout());
            panes.Reconcile(space, LevelDisplayMode.Stacked, host, new WorldBounds(-1000, -1000, 1000, 1000));
            return (space, panes);
        }

        /// <summary>A pane framing a world rectangle, with no level set behind it.</summary>
        public static LevelPane Pane(float width, float height, double zMin = 0, double zMax = 64)
        {
            MapLevel level = new()
            {
                Id = MapSpace.IdForZMin(zMin),
                Name = "floor 0",
                ZMin = zMin,
                ZMax = zMax
            };

            return new LevelPane(level,
                new SliceCamera(ViewportTransform.Fit(width, height, -1000, -1000, 1000, 1000)),
                ManualRig.Instance)
            {
                ViewportRect = SKRect.Create(width, height)
            };
        }
    }
}
