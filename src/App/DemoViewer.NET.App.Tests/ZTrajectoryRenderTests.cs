#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Visual gate for the A4 grenade flight-trail overlay: feeds a smoke projectile moving across several
///     pushes, renders the bare <see cref="Playback2DViewport" />, and asserts the viewport contains BRIGHT
///     pixels (the gray smoke trail #B0BEC5) that neither the dark background (#15181C) nor the dark grid
///     lines (#22272E/#2E3742) can produce — i.e. the comet line actually DREW, not just accumulated in the
///     VM. Z-named so it sorts late (the headless platform / fonts are fully initialised by then).
/// </summary>
[NotInParallel]
public class ZTrajectoryRenderTests
{
    [Test]
    public async Task GrenadeTrail_DrawsAsBrightLine_InViewport()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DTabViewModel vm = new();
            Ctx ctx = new();
            ctx.Roster.Add(new PlayerRosterEntry
            {
                Slot = 0,
                Name = "Neo",
                SteamId = 1
            });
            vm.OnActivated(ctx);

            View view = new();
            Ent nade = new("CSmokeGrenadeProjectile", 1);
            view.Add(nade);

            // Sweep the smoke diagonally across the framed extent so the polyline spans many pixels.
            (float X, float Y)[] path =
            {
                (-800, -400), (-500, -250), (-200, -80), (120, 70), (450, 240), (800, 400)
            };
            for (int i = 0; i < path.Length; i++)
            {
                SetPos(nade, path[i].X, path[i].Y, 0);
                ctx.Push(new Snap(i + 1, view));
            }

            await Assert.That(vm.GrenadeTrails.Count).IsEqualTo(1);
            await Assert.That(vm.GrenadeTrails[0].Points.Count).IsGreaterThanOrEqualTo(5);

            const int Width = 800, Height = 600;
            Playback2DViewport viewport = new()
            {
                DataContext = vm
            };
            // Pin the canvas to Dark: CountBright's "R,G,B all > 0x80" heuristic assumes a DARK canvas (dark bg
            // + bright trail). The central theme system made ThemeVariant.Default resolve to LIGHT headless, so
            // without this the light canvas bg counts as bright and the trail-hidden assertion fails. This test
            // is about the trail drawing, not the theme — pin the variant it always implicitly needed.
            Window window = new()
            {
                Width = Width,
                Height = Height,
                Content = viewport,
                RequestedThemeVariant = ThemeVariant.Dark
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            WriteableBitmap? bmp = window.CaptureRenderedFrame();
            await Assert.That(bmp).IsNotNull();
            bmp!.Save(Path.Combine(HeadlessSession.ArtifactDir, "trajectory.png"));

            int bright = CountBright(bmp);
            Console.WriteLine($"[trajectory] trail pts={vm.GrenadeTrails[0].Points.Count}  brightPixels={bright}");
            await Assert.That(bright).IsGreaterThan(60); // the gray comet line clearly drew

            // Toggle the overlay OFF — the viewport must stop drawing the trail (the VM data is unchanged).
            vm.ShowTrails = false;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            WriteableBitmap? hidden = window.CaptureRenderedFrame();
            int brightHidden = CountBright(hidden!);
            Console.WriteLine($"[trajectory] after ShowTrails=false  brightPixels={brightHidden}");
            await Assert.That(brightHidden).IsLessThan(5); // the comet line is gone
            await Assert.That(vm.GrenadeTrails.Count).IsEqualTo(1); // ...but the underlying trail data is intact
        });
    }

    [Test]
    public async Task ZZ_FullView_WithToggleStrip_Captures()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DTabViewModel vm = new();
            Ctx ctx = new();
            ctx.Roster.Add(new PlayerRosterEntry
            {
                Slot = 0,
                Name = "Neo",
                SteamId = 1
            });
            vm.OnActivated(ctx);

            View view = new();
            Ent nade = new("CSmokeGrenadeProjectile", 1);
            view.Add(nade);
            (float X, float Y)[] path =
            {
                (-800, -400), (-400, -200), (0, 0), (400, 200), (800, 400)
            };
            for (int i = 0; i < path.Length; i++)
            {
                SetPos(nade, path[i].X, path[i].Y, 0);
                ctx.Push(new Snap(i + 1, view));
            }

            const int Width = 1100, Height = 650;
            Playback2DView fullView = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = Width,
                Height = Height,
                Content = fullView
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            WriteableBitmap? bmp = window.CaptureRenderedFrame();
            await Assert.That(bmp).IsNotNull();
            bmp!.Save(Path.Combine(HeadlessSession.ArtifactDir, "trajectory-fullview.png"));
        });
    }

    // Counts "bright" pixels (every channel well above the dark bg/grid range) — only the smoke trail + head
    // dot can produce these, so a non-trivial count proves the line rendered.
    private static int CountBright(WriteableBitmap bmp)
    {
        PixelSize size = bmp.PixelSize;
        int w = size.Width, h = size.Height;
        byte[] buffer = new byte[w * h * 4]; // BGRA8888
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
        }

        int count = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            byte b = buffer[i], g = buffer[i + 1], r = buffer[i + 2];
            if (r > 0x80 && g > 0x80 && b > 0x80)
            {
                count++;
            }
        }

        return count;
    }

    // Axis(32, off) = off, so the world position equals the offset directly.
    private static void SetPos(Ent e, float x, float y, float z)
    {
        e.F["CBodyComponent.m_cellX"] = 32;
        e.F["CBodyComponent.m_cellY"] = 32;
        e.F["CBodyComponent.m_cellZ"] = 32;
        e.F["CBodyComponent.m_vecX"] = x;
        e.F["CBodyComponent.m_vecY"] = y;
        e.F["CBodyComponent.m_vecZ"] = z;
    }

    // ── Doubles ──

    private sealed class Ctx : IModuleContext
    {
        public List<PlayerRosterEntry> Roster { get; } = new();

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
        public IReadOnlyEntityView Entities { get; } = new View();
        public IReadOnlyList<PlayerRosterEntry> Players => Roster;
        public IReadOnlyList<IPlayerState> CurrentPlayers { get; } = new List<IPlayerState>();
        public void Push(Snap snap) => Advanced?.Invoke(snap);
    }

    private sealed class Snap : IPlaybackSnapshot
    {
        public Snap(int frameIndex, IReadOnlyEntityView entities)
        {
            FrameIndex = frameIndex;
            Entities = entities;
        }

        public int FrameIndex { get; }
        public int Tick => FrameIndex * 64;
        public IReadOnlyEntityView Entities { get; }
        public IReadOnlyList<IPlayerState> Players { get; } = new List<IPlayerState>();
    }

    private sealed class Ent : IReadOnlyEntity
    {
        public Ent(string className, int serial)
        {
            ClassName = className;
            Serial = serial;
        }

        public Dictionary<string, object?> F { get; } = new();
        public string ClassName { get; }
        public int Serial { get; }
        public bool IsInPvs => true;
        public object? this[string fieldPath] => F.GetValueOrDefault(fieldPath);

        public bool TryGet<T>(string fieldPath, out T value)
        {
            if (F.TryGetValue(fieldPath, out object? v) && v is T t)
            {
                value = t;
                return true;
            }

            value = default!;
            return false;
        }
    }

    private sealed class View : IReadOnlyEntityView
    {
        private readonly List<IReadOnlyEntity> _ents = new();
        public IEnumerable<IReadOnlyEntity> All() => _ents;
        public IEnumerable<IReadOnlyEntity> OfClass(string className) => _ents.Where(e => e.ClassName == className);
        public IReadOnlyEntity? BySerial(int serial) => null;
        public IReadOnlyEntity? ByIndex(int entityIndex) => null;
        public IReadOnlyEntity? ResolveHandle(ulong handle) => null;
        public void Add(IReadOnlyEntity e) => _ents.Add(e);
    }
}
