#region

using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Headless Skia smoke test for the 2D Playback module (the project's src/App/DemoViewer.NET.App.Tests
///     practice). Activates the VM through its pure surface, feeds a few ADVANCING synthetic snapshots, and
///     renders the View to a Skia frame — asserting it is NON-BLANK with team-coloured marker pixels.
///     <para>
///         <b>Fully synthetic / deterministic.</b> The headless harness can't reliably
///         complete the fire-and-forget async demo load (Task.Run parse + Analysis.RunAsync). So this test
///         feeds the VM hand-built <see cref="IPlaybackSnapshot" />s directly via fake context interfaces —
///         no demo parsing, no async, no DispatcherTimer. It owns exactly the markers/panels → PIXELS leg;
///         the real-data → markers leg is pinned by Playback2DModuleLifecycleTests, real-data → WorldPosition
///         by PositionUtilGateTests, and the game-info field paths by GameInfoFieldProbeTests.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class Playback2DHeadlessSmokeTests
{
    // Background fill of the viewport (#15181C) — markers must introduce pixels distinct from it.
    private const byte BgR = 0x15, BgG = 0x18, BgB = 0x1C;

    [Test]
    public async Task ActivatedTab_AdvancingSnapshots_RenderNonBlankFrame_WithMarkers()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DTabViewModel vm = new();

            // Two synthetic players at known on-radar world coords (T at slot 0, CT at slot 1).
            FakeContext ctx = new();
            ctx.Roster.Add(new PlayerRosterEntry
            {
                Slot = 0,
                Name = "Alpha",
                SteamId = 1
            });
            ctx.Roster.Add(new PlayerRosterEntry
            {
                Slot = 1,
                Name = "Bravo",
                SteamId = 2
            });

            vm.OnActivated(ctx);

            // Feed a few ADVANCING snapshots (players shift toward each other), to exercise the per-push
            // build + delta cache + redraw path.
            for (int frame = 0; frame < 3; frame++)
            {
                List<IPlayerState> players = new()
                {
                    Player(0, 2, -800 + frame * 100, 600, 64, 90, 100, frame),
                    Player(1, 3, 900 - frame * 100, -500, 64, 270, 100 - frame * 5, 0)
                };
                ctx.Push(new FakeSnapshot(frame, frame * 64, players, new FakeEntityView()));
            }

            await Assert.That(vm.PushCount).IsEqualTo(3);
            await Assert.That(vm.Markers.Count).IsEqualTo(2);

            // Render the View standalone.
            // Carried-forward suite: pin the LEGACY surface. Mounting the surface happens in
            // code, so a view built without this would get the v2 host.
            Playback2DRenderer.ResetForTest(Playback2DRendererKind.Legacy);
            Playback2DView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = 1000,
                Height = 600,
                Content = view
            };
            window.Show();

            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            WriteableBitmap? frameBmp = window.CaptureRenderedFrame();
            await Assert.That(frameBmp).IsNotNull();

            string path = Path.Combine(HeadlessSession.ArtifactDir, "playback2d.png");
            frameBmp!.Save(path);
            (int nonBg, bool sawTeamColour) = ScanPixels(frameBmp);
            Console.WriteLine($"[capture] {path}  markers={vm.Markers.Count}  nonBg={nonBg}  team={sawTeamColour}");

            await Assert.That(nonBg).IsGreaterThan(100); // not a blank frame (grid + markers drew)
            await Assert.That(sawTeamColour).IsTrue(); // at least one team-coloured marker rendered
        });

        await Assert.That(File.Exists(Path.Combine(HeadlessSession.ArtifactDir, "playback2d.png"))).IsTrue();
    }

    private static FakePlayerState Player(int slot, int team, double x, double y, double z, float yaw,
        int health, int shots)
    {
        FakeEntity pawn = new("CCSPlayerPawn");
        pawn.Fields["m_iHealth"] = health;
        pawn.Fields["m_iShotsFired"] = shots;
        pawn.Fields["m_lifeState"] = 0;
        pawn.Fields["m_flFlashDuration"] = 0f;
        pawn.Fields["m_angEyeAngles"] = new Vector3(0, yaw, 0);
        pawn.Fields["m_ArmorValue"] = 100;
        pawn.Fields["m_unCurrentEquipmentValue"] = 3500u;

        FakeEntity ctrl = new("CCSPlayerController");
        ctrl.Fields["m_pInGameMoneyServices.m_iAccount"] = 2400;
        ctrl.Fields["m_iScore"] = 12;
        ctrl.Fields["m_pActionTrackingServices.m_iNumRoundKills"] = 1;

        return new FakePlayerState(slot, team, pawn, ctrl, ((float)x, (float)y, (float)z));
    }

    private static (int NonBackground, bool SawTeamColour) ScanPixels(WriteableBitmap bmp)
    {
        PixelSize size = bmp.PixelSize;
        int w = size.Width, h = size.Height;
        byte[] buffer = new byte[w * h * 4]; // BGRA8888

        using (ILockedFramebuffer fb = bmp.Lock())
        {
            Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
        }

        int nonBg = 0;
        bool sawTeam = false;
        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            byte b = buffer[i], g = buffer[i + 1], r = buffer[i + 2];

            if (Diff(r, BgR) > 6 || Diff(g, BgG) > 6 || Diff(b, BgB) > 6)
            {
                nonBg++;
            }

            // Amber T fill ≈ (224,160,48); blue CT fill ≈ (74,144,217). Loose hue checks.
            bool amber = r > 170 && g is > 110 and < 200 && b < 110;
            bool blue = b > 150 && g is > 100 and < 200 && r < 130;
            if (amber || blue)
            {
                sawTeam = true;
            }
        }

        return (nonBg, sawTeam);
    }

    private static int Diff(byte a, byte b) => Math.Abs(a - b);

    // ── Minimal test doubles for the Abstractions interfaces (no demo, no async) ──

    private sealed class FakeContext : IModuleContext
    {
        public List<PlayerRosterEntry> Roster { get; } = new();
        public List<IPlayerState> Current { get; } = new();

        public bool HasDemo => true;
        public string? DemoPath => null;
        public int TickRate => 64;
        public int CurrentFrameIndex => 0;
        public int CurrentTick => 0;
        public bool IsPlaying => false;
        public double Speed => 1;
        public double CurtimeSeconds(int tick) => tick / 64.0;

        public void RequestSeekToFrame(int frameIndex)
        {
        }

        public void RequestSeekToTick(int tick)
        {
        }

        public void RequestPlay()
        {
        }

        public void RequestPause()
        {
        }

        public event Action<IPlaybackSnapshot>? Advanced;
        public IReadOnlyEntityView Entities { get; } = new FakeEntityView();
        public IReadOnlyList<PlayerRosterEntry> Players => Roster;
        public IReadOnlyList<IPlayerState> CurrentPlayers => Current;

        public void Push(IPlaybackSnapshot snapshot)
        {
            Current.Clear();
            Current.AddRange(snapshot.Players);
            Advanced?.Invoke(snapshot);
        }
    }


    private sealed class FakeSnapshot : IPlaybackSnapshot
    {
        public FakeSnapshot(int frameIndex, int tick, IReadOnlyList<IPlayerState> players, IReadOnlyEntityView entities)
        {
            FrameIndex = frameIndex;
            Tick = tick;
            Players = players;
            Entities = entities;
        }

        public int FrameIndex { get; }
        public int Tick { get; }
        public IReadOnlyEntityView Entities { get; }
        public IReadOnlyList<IPlayerState> Players { get; }
    }

    private sealed class FakePlayerState : IPlayerState
    {
        public FakePlayerState(int slot, int team, IReadOnlyEntity pawn, IReadOnlyEntity ctrl,
            (float X, float Y, float Z) pos)
        {
            Slot = slot;
            Team = team;
            Pawn = pawn;
            Controller = ctrl;
            WorldPosition = pos;
        }

        public int Slot { get; }
        public int Team { get; }
        public bool HasLivePawn => true;
        public IReadOnlyEntity? Pawn { get; }
        public IReadOnlyEntity? Controller { get; }
        public (float X, float Y, float Z)? WorldPosition { get; }
    }

    private sealed class FakeEntity : IReadOnlyEntity
    {
        public FakeEntity(string className) => ClassName = className;

        public Dictionary<string, object?> Fields { get; } = new();
        public string ClassName { get; }
        public int Serial => 1;
        public bool IsInPvs => true;

        public object? this[string fieldPath] => Fields.TryGetValue(fieldPath, out object? v) ? v : null;

        public bool TryGet<T>(string fieldPath, out T value)
        {
            if (Fields.TryGetValue(fieldPath, out object? v) && v is T t)
            {
                value = t;
                return true;
            }

            value = default!;
            return false;
        }
    }

    private sealed class FakeEntityView : IReadOnlyEntityView
    {
        public IEnumerable<IReadOnlyEntity> All() => Array.Empty<IReadOnlyEntity>();
        public IEnumerable<IReadOnlyEntity> OfClass(string className) => Array.Empty<IReadOnlyEntity>();
        public IReadOnlyEntity? BySerial(int serial) => null;
        public IReadOnlyEntity? ByIndex(int entityIndex) => null;
        public IReadOnlyEntity? ResolveHandle(ulong handle) => null;
    }
}
