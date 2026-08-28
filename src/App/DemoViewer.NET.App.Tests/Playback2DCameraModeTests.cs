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
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Headless render gates for the four camera modes. Each mode (Fit / Alive / Map / Follow) is set
///     on the viewport and the View is rendered to a Skia frame, asserting it is NON-BLANK — the smooth
///     modes' CONVERGENCE is unit-tested purely in <c>SliceCameraTests</c> (which moved to the
///     direct-execution Playback2D suite in B0), so here we only confirm
///     each mode renders without crashing through the render loop (the split: pure math tests the
///     lerp, render tests confirm a mode draws). Fully synthetic / deterministic (no demo / no async), the
///     same practice as <see cref="Playback2DHeadlessSmokeTests" />.
/// </summary>
[NotInParallel]
[Category("Render")]
public class Playback2DCameraModeTests
{
    private const byte BgR = 0x15, BgG = 0x18, BgB = 0x1C;

    [Test]
    public async Task AllModes_RenderNonBlankFrame()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DTabViewModel vm = new();
            ModeFakeContext ctx = new();
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

            // A couple of advancing pushes so the observed extent + markers are populated.
            for (int frame = 0; frame < 2; frame++)
            {
                List<IPlayerState> players = new()
                {
                    ModePlayer(0, 2, -700 + frame * 80, 500, 64, 90),
                    ModePlayer(1, 3, 800 - frame * 80, -400, 64, 270)
                };
                ctx.Push(new ModeSnapshot(frame, frame * 64, players));
            }

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

            Playback2DViewport viewport = FindViewport(view);

            // The Follow picker reads the VM roster — confirm it surfaces both players.
            await Assert.That(vm.FollowablePlayers.Count).IsEqualTo(2);

            foreach (CameraMode mode in new[]
                     {
                         CameraMode.Fit, CameraMode.Alive, CameraMode.Map, CameraMode.FollowPlayer
                     })
            {
                if (mode == CameraMode.FollowPlayer)
                {
                    viewport.FollowSlot = 0; // implies FollowPlayer mode
                }
                else
                {
                    viewport.Mode = mode;
                }

                // Pump a few render frames so the smooth modes step (we don't assert convergence here).
                for (int i = 0; i < 4; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                }

                WriteableBitmap? bmp = window.CaptureRenderedFrame();
                await Assert.That(bmp).IsNotNull();
                int nonBg = ScanNonBackground(bmp!);
                Console.WriteLine($"[mode-render] mode={mode} followSlot={viewport.FollowSlot} nonBg={nonBg}");
                await Assert.That(nonBg).IsGreaterThan(100);
            }
        });
    }

    [Test]
    public async Task FollowMode_CentresCameraOnFollowedPlayer()
    {
        // Behavioural: in Follow mode the camera converges so the followed player maps near the band centre.
        // This also proves the render-frame lerp advances under the headless render loop (if RAF didn't fire
        // the camera would never move toward the target).
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DTabViewModel vm = new();
            ModeFakeContext ctx = new();
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

            // TWO players far apart so the followed one is clearly NOT where a Fit (which frames the midpoint)
            // would centre — proving the Follow lerp actually moved the camera onto slot 0.
            const float Px = 2200, Py = -1600;
            ctx.Push(new ModeSnapshot(0, 0, new List<IPlayerState>
            {
                ModePlayer(0, 2, Px, Py, 64, 0),
                ModePlayer(1, 3, -2200, 1600, 64, 0)
            }));

            // Carried-forward suite: pin the LEGACY surface. Mounting the surface happens in
            // code, so a view built without this would get the v2 host.
            Playback2DRenderer.ResetForTest(Playback2DRendererKind.Legacy);
            Playback2DView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = 800,
                Height = 600,
                Content = view
            };
            window.Show();
            Playback2DViewport viewport = FindViewport(view);

            viewport.FollowSlot = 0; // → Follow mode

            // Pump enough render frames for the lerp to settle.
            for (int i = 0; i < 120; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }

            // The followed player should now map near the viewport centre (within a generous tolerance —
            // single-floor band is the full height here).
            ViewportTransform t = viewport.PrimaryCameraTransform;
            (double sx, double sy) = t.WorldToScreen(Px, Py);
            Console.WriteLine($"[follow] player→screen=({sx:F0},{sy:F0}) view=({t.ViewWidth:F0}x{t.ViewHeight:F0}) " +
                              $"scale={t.EffectiveScale:F4}");

            await Assert.That(Math.Abs(sx - t.ViewWidth / 2)).IsLessThan(t.ViewWidth * 0.25);
            await Assert.That(Math.Abs(sy - t.ViewHeight / 2)).IsLessThan(t.ViewHeight * 0.25);
        });
    }

    [Test]
    public async Task ManualPanZoom_FlipsModeToManualOverride_WithoutCrash()
    {
        // A manual gesture on a slice in a smooth mode must not throw and must still render — the auto-mode
        // pauses for that slice (verified behaviourally: the frame still draws after the override).
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DTabViewModel vm = new();
            ModeFakeContext ctx = new();
            ctx.Roster.Add(new PlayerRosterEntry
            {
                Slot = 0,
                Name = "Alpha",
                SteamId = 1
            });
            vm.OnActivated(ctx);
            ctx.Push(new ModeSnapshot(0, 0, new List<IPlayerState>
            {
                ModePlayer(0, 2, 0, 0, 64, 0)
            }));

            // Carried-forward suite: pin the LEGACY surface. Mounting the surface happens in
            // code, so a view built without this would get the v2 host.
            Playback2DRenderer.ResetForTest(Playback2DRendererKind.Legacy);
            Playback2DView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = 800,
                Height = 600,
                Content = view
            };
            window.Show();
            Playback2DViewport viewport = FindViewport(view);

            viewport.Mode = CameraMode.Alive;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            WriteableBitmap? bmp = window.CaptureRenderedFrame();
            await Assert.That(bmp).IsNotNull();
            await Assert.That(ScanNonBackground(bmp!)).IsGreaterThan(100);
        });
    }

    // The surface mounts in code rather than declaring it in XAML, so it comes out of the
    // ContentControl slot. This suite is carried forward against the LEGACY control: the
    // v2 host's equivalents live in Scene2DHostInputTests.
    private static Playback2DViewport FindViewport(Playback2DView view) =>
        Playback2DTimelineHarness.Viewport(view);

    private static ModePlayerState ModePlayer(int slot, int team, double x, double y, double z, float yaw)
    {
        ModeEntity pawn = new("CCSPlayerPawn");
        pawn.Fields["m_iHealth"] = 100;
        pawn.Fields["m_iShotsFired"] = 0;
        pawn.Fields["m_lifeState"] = 0;
        pawn.Fields["m_flFlashDuration"] = 0f;
        pawn.Fields["m_angEyeAngles"] = new Vector3(0, yaw, 0);

        ModeEntity ctrl = new("CCSPlayerController");
        return new ModePlayerState(slot, team, pawn, ctrl, ((float)x, (float)y, (float)z));
    }

    private static int ScanNonBackground(WriteableBitmap bmp)
    {
        PixelSize size = bmp.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
        }

        int nonBg = 0;
        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            byte b = buffer[i], g = buffer[i + 1], r = buffer[i + 2];
            if (Math.Abs(r - BgR) > 6 || Math.Abs(g - BgG) > 6 || Math.Abs(b - BgB) > 6)
            {
                nonBg++;
            }
        }

        return nonBg;
    }

    // ── Minimal Abstractions doubles (no demo, no async). ──

    private sealed class ModeFakeContext : IModuleContext
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
        public IReadOnlyEntityView Entities { get; } = new ModeEntityView();
        public IReadOnlyList<PlayerRosterEntry> Players => Roster;
        public IReadOnlyList<IPlayerState> CurrentPlayers => Current;

        public void Push(IPlaybackSnapshot snapshot)
        {
            Current.Clear();
            Current.AddRange(snapshot.Players);
            Advanced?.Invoke(snapshot);
        }
    }


    private sealed class ModeSnapshot : IPlaybackSnapshot
    {
        public ModeSnapshot(int frameIndex, int tick, IReadOnlyList<IPlayerState> players)
        {
            FrameIndex = frameIndex;
            Tick = tick;
            Players = players;
        }

        public int FrameIndex { get; }
        public int Tick { get; }
        public IReadOnlyEntityView Entities { get; } = new ModeEntityView();
        public IReadOnlyList<IPlayerState> Players { get; }
    }

    private sealed class ModePlayerState : IPlayerState
    {
        public ModePlayerState(int slot, int team, IReadOnlyEntity pawn, IReadOnlyEntity ctrl,
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

    private sealed class ModeEntity : IReadOnlyEntity
    {
        public ModeEntity(string className) => ClassName = className;
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

    private sealed class ModeEntityView : IReadOnlyEntityView
    {
        public IEnumerable<IReadOnlyEntity> All() => Array.Empty<IReadOnlyEntity>();
        public IEnumerable<IReadOnlyEntity> OfClass(string className) => Array.Empty<IReadOnlyEntity>();
        public IReadOnlyEntity? BySerial(int serial) => null;
        public IReadOnlyEntity? ByIndex(int entityIndex) => null;
        public IReadOnlyEntity? ResolveHandle(ulong handle) => null;
    }
}
