#region

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
///     Renders the full 2D view with a populated kill feed and asserts the feed actually draws as a visible
///     HUD overlay in the viewport's TOP-RIGHT — guarding against (a) it not rendering in front and (b) the
///     regression that prompted it: the strip background matching the viewport's own colour (#15181C) so the
///     feed blended into the grid as floating text. The strips are now distinctly DARKER, so the top-right
///     region contains plenty of pixels that differ from the viewport background.
/// </summary>
[NotInParallel]
public class Playback2DKillFeedRenderTests
{
    private const byte BgR = 0x15, BgG = 0x18, BgB = 0x1C; // viewport background

    [Test]
    public async Task KillFeed_DrawsAsVisibleHud_TopRightOfViewport()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DTabViewModel vm = new();
            Kctx ctx = new(
                Kill(4950, 0, 1, "ak47", true),
                Kill(4980, 1, 0, "awp", penetrated: 2, assister: 0, flashAssist: true),
                Kill(5000, 0, 1, "deagle", noscope: true));
            ctx.Roster.Add(new PlayerRosterEntry
            {
                Slot = 0,
                Name = "ZywOo",
                SteamId = 1
            });
            ctx.Roster.Add(new PlayerRosterEntry
            {
                Slot = 1,
                Name = "ropz",
                SteamId = 2
            });

            vm.OnActivated(ctx);
            ctx.Push(new KSnap(5, 5000)); // tick 5000 → all three kills inside the window
            await Assert.That(vm.KillFeed.Count).IsEqualTo(3);

            const int Width = 900, Height = 560;
            Playback2DView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = Width,
                Height = Height,
                Content = view
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            WriteableBitmap? bmp = window.CaptureRenderedFrame();
            await Assert.That(bmp).IsNotNull();
            bmp!.Save(Path.Combine(HeadlessSession.ArtifactDir, "killfeed.png"));

            // Scan the viewport's top-right (left column ends ~x=576; the feed is right-aligned there, top
            // ~90px). Excludes the right-hand game-info panel (x>580).
            int nonBg = CountNonBackground(bmp, 360, 568, 4, 90);
            Console.WriteLine($"[killfeed] rows={vm.KillFeed.Count}  topRightNonBg={nonBg}");
            await Assert.That(nonBg).IsGreaterThan(500); // strips + text clearly drew, not a blended-away feed
        });
    }

    private static int CountNonBackground(WriteableBitmap bmp, int x0, int x1, int y0, int y1)
    {
        PixelSize size = bmp.PixelSize;
        int w = size.Width, h = size.Height;
        byte[] buffer = new byte[w * h * 4]; // BGRA8888
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
        }

        int count = 0;
        for (int y = y0; y < Math.Min(y1, h); y++)
        {
            for (int x = x0; x < Math.Min(x1, w); x++)
            {
                int i = (y * w + x) * 4;
                byte b = buffer[i], g = buffer[i + 1], r = buffer[i + 2];
                if (Math.Abs(r - BgR) > 6 || Math.Abs(g - BgG) > 6 || Math.Abs(b - BgB) > 6)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static GameEventView Kill(int tick, int killer, int victim, string weapon,
        bool hs = false, int penetrated = 0, bool noscope = false, int assister = -1, bool flashAssist = false) =>
        new()
        {
            Name = "player_death",
            Tick = tick,
            Fields = new Dictionary<string, object?>
            {
                ["Attacker"] = killer,
                ["UserId"] = victim,
                ["Assister"] = assister,
                ["Weapon"] = weapon,
                ["Headshot"] = hs,
                ["Penetrated"] = penetrated,
                ["NoScope"] = noscope,
                ["ThruSmoke"] = false,
                ["AttackerBlind"] = false,
                ["AttackerInAir"] = false,
                ["AssistedFlash"] = flashAssist
            }
        };

    private sealed class Kctx : IModuleContext
    {
        private readonly IReadOnlyList<GameEventView> _t;
        public Kctx(params GameEventView[] t) => _t = t;
        public List<PlayerRosterEntry> Roster { get; } = new();

        public IReadOnlyList<GameEventView> GetEventTimeline(string n) =>
            n == "player_death" ? _t : Array.Empty<GameEventView>();

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
        public IReadOnlyEntityView Entities { get; } = new V();
        public IReadOnlyList<PlayerRosterEntry> Players => Roster;
        public IReadOnlyList<IPlayerState> CurrentPlayers { get; } = new List<IPlayerState>();
        public void Push(KSnap s) => Advanced?.Invoke(s);
    }

    private sealed class KSnap : IPlaybackSnapshot
    {
        public KSnap(int f, int t)
        {
            FrameIndex = f;
            Tick = t;
        }

        public int FrameIndex { get; }
        public int Tick { get; }
        public IReadOnlyEntityView Entities { get; } = new V();
        public IReadOnlyList<IPlayerState> Players { get; } = new List<IPlayerState>();
    }


    private sealed class V : IReadOnlyEntityView
    {
        public IEnumerable<IReadOnlyEntity> All() => Array.Empty<IReadOnlyEntity>();
        public IEnumerable<IReadOnlyEntity> OfClass(string className) => Array.Empty<IReadOnlyEntity>();
        public IReadOnlyEntity? BySerial(int serial) => null;
        public IReadOnlyEntity? ByIndex(int entityIndex) => null;
        public IReadOnlyEntity? ResolveHandle(ulong handle) => null;
    }
}
