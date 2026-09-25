#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="Scene2DHost" /> bound to an <see cref="ISceneFrameHost" /> that is not the 2D Playback
///     tab: the seam the strat canvas mounts through (step-authoring.md §3.10). Every test here runs with
///     no <see cref="Playback2DTabViewModel" />, no <c>IModuleContext</c> and no window-level
///     DataContext, so a host that quietly still needed the tab would render
///     <see cref="Scene2DFrame.Empty" /> and fail here rather than on the Strat Book tab.
///     <para>
///         The 2D tab's own behaviour is pinned by the suites that already mount through the tab
///         (<c>Scene2DHostTests</c>, <c>SceneLayerListParityTests</c>, the annotation, level strip and
///         keybind suites); they are the regression gate for the rebind and are not duplicated here.
///     </para>
/// </summary>
[NotInParallel]
public class Scene2DHostFrameHostTests
{
    private const byte BgR = 0x15, BgG = 0x18, BgB = 0x1C;

    private static readonly WorldBounds Mirage = new(-3230, -3410, 1860, 1680);

    [Test]
    public async Task FakeHost_MountsAndRendersMarkers_WithoutATabViewModel()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeSceneFrameHost fake = new();
            fake.Publish(Frame(1, true, TenTokens()));

            (Window window, Scene2DHost host) = Mount(fake);
            host.ForceLeaseUnavailableForTest();
            host.FitToExtent();
            Playback2DTimelineHarness.Pump();

            await Assert.That(host.FrameHost).IsSameReferenceAs(fake);
            await Assert.That(host.CurrentSceneFrame).IsSameReferenceAs(fake.CurrentFrame);
            await Assert.That(host.LeaseUnavailable).IsTrue();

            WriteableBitmap? captured = window.CaptureRenderedFrame();
            await Assert.That(captured).IsNotNull();
            (int nonBg, bool team) = ScanPixels(captured!);
            Console.WriteLine($"[frame-host] nonBg={nonBg} team={team} " +
                              $"panes={host.Compositor.Stats.PanesRendered}");

            await Assert.That(nonBg).IsGreaterThan(100);
            await Assert.That(team).IsTrue();

            window.Close();
        });
    }

    /// <summary>
    ///     A discontinuity snaps the smoothed marker onto its sample once per <c>(FrameIndex, Tick)</c>
    ///     identity. A frame republished under the same identity with the flag still up glides instead:
    ///     the strat transport raises the flag on a seek and the animation loop re-renders that frame
    ///     many times, and re-applying it would freeze every token on its raw position.
    /// </summary>
    [Test]
    public async Task FrameUpdated_Invalidates_AndADiscontinuityIsConsumedOnce()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeSceneFrameHost fake = new();
            fake.Publish(Frame(10, true, OneToken(0, 2, 0f, 0f)));

            (Window window, Scene2DHost host) = Mount(fake);
            Playback2DTimelineHarness.Pump();
            await Assert.That(host.SmoothedMarkerPosition(0)).IsEqualTo((0f, 0f));

            // Under the smoother's snap distance, so only the flag can make it jump.
            fake.Publish(Frame(20, true, OneToken(0, 2, 200f, 0f)));
            Playback2DTimelineHarness.Pump(1);
            await Assert.That(host.SmoothedMarkerPosition(0)).IsEqualTo((200f, 0f))
                .Because("a fresh discontinuity snaps");

            fake.Publish(Frame(20, true, OneToken(0, 2, 400f, 0f)));
            Playback2DTimelineHarness.Pump(1);
            (float x, float _) = host.SmoothedMarkerPosition(0)!.Value;
            Console.WriteLine($"[frame-host] same identity, flag still up: x={x:F1}");
            await Assert.That(x).IsGreaterThan(200f);
            await Assert.That(x).IsLessThan(400f)
                .Because("a repeated identity must not snap a second time");

            window.Close();
        });
    }

    /// <summary>
    ///     The one-shot fit frames <see cref="SceneMapInfo.ObservedBounds" />, while the export path
    ///     prefers <see cref="SceneMapInfo.NetworkedBounds" />. A frame host that fills only one of them
    ///     frames the live canvas and the export differently, which is why the strat canvas fills both.
    /// </summary>
    [Test]
    public async Task ObservedBounds_DrivesTheFirstFit()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            ViewportTransform both = FirstFit(Mirage, Mirage);
            ViewportTransform observedOnly = FirstFit(Mirage, null);
            ViewportTransform networkedOnly = FirstFit(WorldBounds.Default, Mirage);

            Console.WriteLine($"[fit] scale both={both.EffectiveScale:F6} observedOnly={observedOnly.EffectiveScale:F6} " +
                              $"networkedOnly={networkedOnly.EffectiveScale:F6}");

            // Zoom and pan are identity straight after a fit; the fitted rectangle lives in the base
            // scale and the centre, so those are what is compared, through a world point's screen spot.
            (double bx, double by) = both.WorldToScreen(0, 0);
            (double ox, double oy) = observedOnly.WorldToScreen(0, 0);
            await Assert.That(observedOnly.EffectiveScale).IsEqualTo(both.EffectiveScale).Within(1e-9);
            await Assert.That(ox).IsEqualTo(bx).Within(1e-6);
            await Assert.That(oy).IsEqualTo(by).Within(1e-6);
            await Assert.That(Math.Abs(networkedOnly.EffectiveScale - both.EffectiveScale)).IsGreaterThan(1e-6)
                .Because("NetworkedBounds alone does not move the first fit");
        });
    }

    [Test]
    public async Task TokenEditor_ReachesTheToolServices()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            RecordingTokenEditor editor = new();
            FakeSceneFrameHost fake = new() { TokenEditor = editor };
            fake.Publish(Frame(1, true, TenTokens()));

            (Window window, Scene2DHost host) = Mount(fake);
            host.FitToExtent();
            Playback2DTimelineHarness.Pump();
            host.SetActiveTool(ToolKind.Token);
            await Assert.That(host.Router.ActiveKind).IsEqualTo(ToolKind.Token)
                .Because("the host registers the token tool, or selecting it would fall back to pan");

            Point start = Playback2DTimelineHarness.ToWindow(host, window, 300, 300);
            Point end = Playback2DTimelineHarness.ToWindow(host, window, 340, 320);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end);
            window.MouseUp(end, MouseButton.Left);
            Playback2DTimelineHarness.Pump();

            Console.WriteLine($"[token] {string.Join(", ", editor.Calls)}");
            await Assert.That(editor.Calls).Contains("Begin T1 Body");
            await Assert.That(editor.Calls.Count(c => c.StartsWith("Move", StringComparison.Ordinal)))
                .IsGreaterThanOrEqualTo(1);
            await Assert.That(editor.Calls[^1]).StartsWith("End");

            window.Close();
        });
    }

    /// <summary>
    ///     The 2D Playback tab is a frame host with no token editor, so the token tool falls through there
    ///     and the press opens nothing.
    /// </summary>
    [Test]
    public async Task TabViewModel_HasNoTokenEditor_AndTheTokenToolFallsThrough()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushMarkers((0, 2, -800f, 600f, 64f, 90f), (1, 3, 900f, -500f, 64f, 270f));

            (Window window, Scene2DHost host) = Mount(vm);
            host.FitToExtent();
            Playback2DTimelineHarness.Pump();

            await Assert.That(host.FrameHost).IsSameReferenceAs(vm);
            await Assert.That(((ISceneFrameHost)vm).TokenEditor).IsNull();

            host.SetActiveTool(ToolKind.Token);
            window.MouseDown(Playback2DTimelineHarness.ToWindow(host, window, 300, 300), MouseButton.Left);
            await Assert.That(host.Router.IsGestureOpen).IsFalse();
            window.MouseUp(Playback2DTimelineHarness.ToWindow(host, window, 300, 300), MouseButton.Left);

            window.Close();
        });
    }

    /// <summary>
    ///     Re-binding from the tab to another frame host mid-gesture cancels the gesture, drops the
    ///     smoothing and rebuilds the panes: half a stroke from the demo's document must not be committed
    ///     into the strat's, and a token must not glide in from a player's position.
    /// </summary>
    [Test]
    public async Task SwappingHosts_CancelsTheGesture_AndClearsSmoothing()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushMarkers((0, 2, -800f, 600f, 64f, 90f), (1, 3, 900f, -500f, 64f, 270f));

            (Window window, Scene2DHost host) = Mount(vm);
            host.FitToExtent();
            Playback2DTimelineHarness.Pump();
            await Assert.That(host.SmoothedMarkerPosition(0)).IsNotNull();

            // A pan drag left open: pressed and moved, never released.
            window.MouseDown(Playback2DTimelineHarness.ToWindow(host, window, 200, 200), MouseButton.Left);
            window.MouseMove(Playback2DTimelineHarness.ToWindow(host, window, 240, 220));
            await Assert.That(host.Router.IsGestureOpen).IsTrue();

            FakeSceneFrameHost fake = new();
            fake.Publish(Frame(5, true, OneToken(7, 3, 500f, 500f)));
            host.DataContext = fake;

            await Assert.That(host.Router.IsGestureOpen).IsFalse();
            await Assert.That(host.SmoothedMarkerPosition(0)).IsNull();
            await Assert.That(host.PaneCountForTest).IsEqualTo(0);
            await Assert.That(host.CurrentSceneFrame).IsSameReferenceAs(fake.CurrentFrame);

            Playback2DTimelineHarness.Pump();
            await Assert.That(host.PaneCountForTest).IsGreaterThan(0);
            await Assert.That(host.SmoothedMarkerPosition(7)).IsEqualTo((500f, 500f));

            window.MouseUp(Playback2DTimelineHarness.ToWindow(host, window, 240, 220), MouseButton.Left);
            window.Close();
        });
    }

    /// <summary>A token drag open when the host is re-bound is rolled back, not committed.</summary>
    [Test]
    public async Task SwappingHosts_MidTokenDrag_CancelsTheDrag()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            RecordingTokenEditor editor = new();
            FakeSceneFrameHost fake = new() { TokenEditor = editor };
            fake.Publish(Frame(1, true, TenTokens()));

            (Window window, Scene2DHost host) = Mount(fake);
            host.FitToExtent();
            Playback2DTimelineHarness.Pump();
            host.SetActiveTool(ToolKind.Token);

            window.MouseDown(Playback2DTimelineHarness.ToWindow(host, window, 300, 300), MouseButton.Left);
            window.MouseMove(Playback2DTimelineHarness.ToWindow(host, window, 320, 300));
            await Assert.That(host.Router.IsGestureOpen).IsTrue();

            host.DataContext = new FakeSceneFrameHost();

            Console.WriteLine($"[token-swap] {string.Join(", ", editor.Calls)}");
            await Assert.That(host.Router.IsGestureOpen).IsFalse();
            await Assert.That(editor.Calls[^1]).IsEqualTo("Cancel");
            await Assert.That(editor.Calls.Any(c => c.StartsWith("End", StringComparison.Ordinal))).IsFalse();

            window.MouseUp(Playback2DTimelineHarness.ToWindow(host, window, 320, 300), MouseButton.Left);
            window.Close();
        });
    }

    /// <summary>
    ///     The host re-reads the toggles only when <see cref="ISceneFrameHost.FrameUpdated" /> fires. A
    ///     frame host whose setter forgets to raise it never reaches the compositor, which is the
    ///     contract the strat canvas's toggles have to keep.
    /// </summary>
    [Test]
    public async Task Toggles_ReachTheCompositor_OnFrameUpdated()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeSceneFrameHost fake = new();
            fake.Publish(Frame(1, true, TenTokens()));

            (Window window, Scene2DHost host) = Mount(fake);
            Playback2DTimelineHarness.Pump();
            await Assert.That(LayerEnabled(host, SceneLayerIds.Trails)).IsTrue();

            fake.ShowTrails = false;
            await Assert.That(LayerEnabled(host, SceneLayerIds.Trails)).IsTrue()
                .Because("nothing was raised yet");

            fake.Raise();
            await Assert.That(LayerEnabled(host, SceneLayerIds.Trails)).IsFalse();
            await Assert.That(LayerEnabled(host, SceneLayerIds.Vision)).IsFalse()
                .Because("the fake, like the strat canvas, never shows cones");

            window.Close();
        });
    }

    /// <summary>
    ///     A level-set rebuild that moves a band hands the old-to-new ZMin map to whatever the host is
    ///     bound to, so ink the strat anchored to a floor follows that floor.
    /// </summary>
    [Test]
    public async Task LevelRebuild_ForwardsToTheHost()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            // Two Z clusters far enough apart for the splitter's density-valley rule. Section heights are
            // not adopted as floors (FloorSplitter.ComputeSlices), so the band is moved the way a demo
            // moves it: the upper floor's dwell mass shifts and the valley boundary follows.
            FakeSceneFrameHost fake = new();
            fake.Publish(Frame(1, true, TwoFloors(704f)));

            (Window window, Scene2DHost host) = Mount(fake);
            for (int i = 0; i < 30; i++)
            {
                fake.Publish(Frame(2 + i, false, TwoFloors(704f)));
                Playback2DTimelineHarness.Pump(1);
            }

            await Assert.That(host.Levels.Levels.Count).IsEqualTo(2);
            double before = host.Levels.Levels[1].ZMin;
            fake.Rebuilds.Clear();

            for (int i = 0; i < 400 && fake.Rebuilds.Count == 0; i++)
            {
                fake.Publish(Frame(100 + i, false, TwoFloors(576f)));
                Playback2DTimelineHarness.Pump(1);
            }

            double after = host.Levels.Levels[1].ZMin;
            Console.WriteLine($"[levels] upper ZMin {before} -> {after} rebuilds=" +
                              string.Join(" | ", fake.Rebuilds.Select(m =>
                                  string.Join(", ", m.Select(p => $"{p.Key}->{p.Value}")))));

            await Assert.That(fake.Rebuilds.Count).IsGreaterThanOrEqualTo(1);
            IReadOnlyDictionary<double, double> moved = fake.Rebuilds[0];
            await Assert.That(moved.TryGetValue(MapSpace.QuantizeZ(before), out double to)).IsTrue();
            await Assert.That(to).IsNotEqualTo(MapSpace.QuantizeZ(before));

            window.Close();
        });
    }

    /// <summary>
    ///     <c>SceneLayerListParityTests</c>' rule with the host off the 2D tab: the seven catalog scene
    ///     layers plus, at most, the ink. A strat-only layer added to the host fails here.
    /// </summary>
    [Test]
    [Category("Render")]
    public async Task FakeHost_MountsTheCatalogsSceneLayers_PlusTheInk()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeSceneFrameHost fake = new()
            {
                AnnotationSession = new AnnotationSession(new AnnotationDocument())
            };
            fake.Publish(Frame(1, true, TenTokens()));

            (Window window, Scene2DHost host) = Mount(fake);
            Playback2DTimelineHarness.Pump();

            string[] mounted = [.. host.Compositor.Layers.Select(l => l.Id).Order()];
            Console.WriteLine($"[layers] frame host: {string.Join(", ", mounted)}");

            string[] expected =
            [
                .. SceneLayerCatalog.SceneStackIds.Where(id => !SceneLayerIds.OptIn.Contains(id)),
                SceneLayerIds.Annotations
            ];
            await Assert.That(mounted).IsEquivalentTo(expected.Order().ToArray());
            await Assert.That(LayerEnabled(host, SceneLayerIds.Annotations)).IsTrue();

            window.Close();
        });
    }

    private static ViewportTransform FirstFit(WorldBounds observed, WorldBounds? networked)
    {
        FakeSceneFrameHost fake = new();
        fake.Publish(new Scene2DFrame
        {
            Time = new SceneTime(1, 1, 1 / 64.0, 1 / 64.0, true),
            Markers = TenTokens(),
            Map = new SceneMapInfo
            {
                MapName = "de_mirage",
                ObservedBounds = observed,
                NetworkedBounds = networked
            }
        });

        (Window window, Scene2DHost host) = Mount(fake);
        Playback2DTimelineHarness.Pump();
        ViewportTransform fit = host.PrimaryCameraTransform;
        window.Close();
        return fit;
    }

    // The host alone, as the Strat Book view mounts it: DataContext set on the host, nothing on the
    // window, so inheritance cannot be what binds it.
    private static (Window Window, Scene2DHost Host) Mount(object frameHost)
    {
        Scene2DHost host = new() { DataContext = frameHost };
        Window window = new()
        {
            Width = 1000,
            Height = 700,
            Content = host
        };
        window.Show();
        Playback2DTimelineHarness.Pump();
        return (window, host);
    }

    private static bool LayerEnabled(Scene2DHost host, string id) =>
        host.Compositor.Layers.First(l => l.Id == id).IsEnabled;

    private static Scene2DFrame Frame(int tick, bool discontinuity, IReadOnlyList<PlayerMarker> markers) => new()
    {
        Time = new SceneTime(tick, tick, tick / 64.0, 1 / 64.0, discontinuity),
        Markers = markers,
        Map = new SceneMapInfo
        {
            MapName = "de_mirage",
            ObservedBounds = Mirage,
            NetworkedBounds = Mirage
        }
    };

    private static PlayerMarker[] OneToken(int slot, int team, float x, float y) => [Token(slot, team, x, y, 64f)];

    private static PlayerMarker Token(int slot, int team, float x, float y, float z) =>
        new(slot, team, x, y, z, 0f, RingState.Team, 1, team == 2 ? $"T{slot + 1}" : $"CT{slot - 4}", true,
            SteamId: (ulong)(slot + 1));

    private static PlayerMarker[] TwoFloors(float upperZ) =>
    [
        Token(0, 2, -800f, 600f, 64f), Token(1, 2, -700f, 500f, 64f),
        Token(5, 3, 900f, -500f, upperZ), Token(6, 3, 800f, -400f, upperZ)
    ];

    // Ten tokens spread across the map, T on the left and CT on the right. The editor's hit test is a
    // recording stub, so their exact positions only matter for the pixel scan.
    private static PlayerMarker[] TenTokens()
    {
        PlayerMarker[] markers = new PlayerMarker[10];
        for (int i = 0; i < 10; i++)
        {
            int team = i < 5 ? 2 : 3;
            float x = team == 2 ? -2500f + i * 150f : 500f + (i - 5) * 150f;
            markers[i] = Token(i, team, x, -1500f + i * 300f, 64f);
        }

        return markers;
    }

    private static (int NonBackground, bool SawTeamColour) ScanPixels(WriteableBitmap bitmap)
    {
        PixelSize size = bitmap.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4]; // BGRA8888

        using (ILockedFramebuffer framebuffer = bitmap.Lock())
        {
            Marshal.Copy(framebuffer.Address, buffer, 0, buffer.Length);
        }

        int nonBg = 0;
        bool team = false;
        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            byte b = buffer[i], g = buffer[i + 1], r = buffer[i + 2];
            if (Math.Abs(r - BgR) > 6 || Math.Abs(g - BgG) > 6 || Math.Abs(b - BgB) > 6)
            {
                nonBg++;
            }

            bool amber = r > 170 && g is > 110 and < 200 && b < 110;
            bool blue = b > 150 && g is > 100 and < 200 && r < 130;
            if (amber || blue)
            {
                team = true;
            }
        }

        return (nonBg, team);
    }

    /// <summary>
    ///     The smallest frame host: a frame, the push signal, settable toggles, and a record of every
    ///     level rebuild the host forwarded. Vision is off for the same reason it is on the strat canvas:
    ///     there is no demo to solve against.
    /// </summary>
    private sealed class FakeSceneFrameHost : ISceneFrameHost
    {
        public List<IReadOnlyDictionary<double, double>> Rebuilds { get; } = [];
        public Scene2DFrame CurrentFrame { get; private set; } = Scene2DFrame.Empty;
        public LoadedMapAsset? MapAsset => null;
        public VisibilityEngine? VisionEngine => null;
        public AnnotationSession? AnnotationSession { get; init; }
        public bool IsAnnotationsEnabled => AnnotationSession is not null;
        public bool ShowRadar { get; set; } = true;
        public bool ShowTrails { get; set; } = true;
        public bool ShowAreaEffects { get; set; } = true;
        public bool ShowVision => false;
        public bool ShowBombRing { get; set; } = true;
        public bool ShowZones => false;
        public PlaceResolver? Zones => null;
        public ITokenEditor? TokenEditor { get; init; }

        public event Action? FrameUpdated;

        public void ApplyAnnotationLevelRebuild(IReadOnlyDictionary<double, double> zMinMap) =>
            Rebuilds.Add(new Dictionary<double, double>(zMinMap));

        public bool TryTagPositionAt(MapLevel level, double worldX, double worldY) => false;

        // A fresh frame per publish, never a mutated one: the render thread replays the submitted frame.
        public void Publish(Scene2DFrame frame)
        {
            CurrentFrame = frame;
            FrameUpdated?.Invoke();
        }

        public void Raise() => FrameUpdated?.Invoke();
    }

    /// <summary>Hits a token on every press and records each call in order.</summary>
    private sealed class RecordingTokenEditor : ITokenEditor
    {
        public List<string> Calls { get; } = [];
        public int ActiveTick => 0;

        public bool TryHitToken(LevelPane pane, SKPoint world, float worldRadius, out string slot,
            out TokenGrip grip)
        {
            slot = "T1";
            grip = TokenGrip.Body;
            return true;
        }

        public void BeginDrag(string slot, TokenGrip grip) => Calls.Add($"Begin {slot} {grip}");

        public void MoveTo(string slot, SKPoint world, double levelMinZ) =>
            Calls.Add($"Move {slot} {world.X:F0},{world.Y:F0} z={levelMinZ}");

        public void EndDrag(float? yawDegrees) => Calls.Add($"End {yawDegrees}");

        public void CancelDrag() => Calls.Add("Cancel");
    }
}
