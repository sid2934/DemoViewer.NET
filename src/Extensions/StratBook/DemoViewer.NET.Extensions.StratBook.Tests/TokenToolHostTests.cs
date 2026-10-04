#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Input;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="TokenTool" /> over a real <see cref="Scene2DHost" />, through <see cref="Scene2DHost.AddTool" />:
///     split out of <c>Scene2DHostFrameHostTests</c>, which stayed in App.Tests and carries no
///     extension import, because these two are the only cases there that construct the extension's
///     <see cref="TokenTool" /> directly. The fakes here are this class's own, not shared with
///     <c>Scene2DHostFrameHostTests</c>: the two test projects do not reference each other,
///     each project's own test infrastructure is compile-linked rather than referenced, for the same
///     reason.
/// </summary>
[NotInParallel]
[Category("Render")]
public class TokenToolHostTests
{
    private static readonly WorldBounds Mirage = new(-3230, -3410, 1860, 1680);

    [Test]
    public async Task TokenEditor_ReachesTheToolServices()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            RecordingTokenEditor editor = new();
            FakeSceneFrameHost fake = new() { TokenEditor = editor };
            fake.Publish(Frame(1, true, TenTokens()));

            (Window window, Scene2DHost host) = Mount(fake);
            host.AddTool(new TokenTool());
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
            host.AddTool(new TokenTool());
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

    // The host alone, as the Strat Book view mounts it: DataContext set on the host, nothing on the
    // window, so inheritance cannot be what binds it. Same shape as Scene2DHostFrameHostTests.Mount.
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

    private static PlayerMarker Token(int slot, int team, float x, float y, float z) =>
        new(slot, team, x, y, z, 0f, RingState.Team, 1, team == 2 ? $"T{slot + 1}" : $"CT{slot - 4}", true,
            SteamId: (ulong)(slot + 1));

    // Ten tokens spread across the map, T on the left and CT on the right. The editor's hit test is a
    // recording stub, so their exact positions only matter for the press to land on one.
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

    /// <summary>The smallest frame host: a frame, the push signal, and a settable token editor.</summary>
    private sealed class FakeSceneFrameHost : ISceneFrameHost, ITokenEditingHost
    {
        public Scene2DFrame CurrentFrame { get; private set; } = Scene2DFrame.Empty;
        public LoadedMapAsset? MapAsset => null;
        public VisibilityEngine? VisionEngine => null;
        public AnnotationSession? AnnotationSession => null;
        public bool IsAnnotationsEnabled => false;
        public bool ShowRadar => true;
        public bool ShowTrails => true;
        public bool ShowAreaEffects => true;
        public bool ShowVision => false;
        public bool ShowBombRing => true;
        public bool ShowZones => false;
        public PlaceResolver? Zones => null;
        public ITokenEditor? TokenEditor { get; init; }

        public event Action? FrameUpdated;

        public void ApplyAnnotationLevelRebuild(IReadOnlyDictionary<double, double> zMinMap)
        {
        }

        // A fresh frame per publish, never a mutated one: the render thread replays the submitted frame.
        public void Publish(Scene2DFrame frame)
        {
            CurrentFrame = frame;
            FrameUpdated?.Invoke();
        }
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

        public void MoveTo(string slot, SKPoint world, double levelMinZ, ToolModifiers modifiers = ToolModifiers.None, double worldUnitsPerPixel = 0) =>
            Calls.Add($"Move {slot} {world.X:F0},{world.Y:F0} z={levelMinZ}");

        public void EndDrag(ToolModifiers modifiers = ToolModifiers.None) => Calls.Add("End ");

        public void CancelDrag() => Calls.Add("Cancel");
    }
}
