#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Playback;
using DemoViewer.NET.Views.Playback2D;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     End-to-end REAL-DEMO render of the 2D Playback viewport, built on the SYNCHRONOUS parse path
///     (<see cref="PositionUtilGateTests" />-style) rather than the shell's async demo load (which the
///     headless dispatcher can't deterministically pump to completion). Parses a real demo, advances a
///     real <see cref="EntityTracker" /> to a mid-match frame, drives the REAL host player-join through a
///     real <see cref="ModuleContext" />, activates the real <see cref="Playback2DTabViewModel" />, renders
///     the real <see cref="Playback2DView" /> to a Skia frame, and dumps each marker's world position so a
///     Y-flip / X-Y-swap / rotation (which the magnitude-only gate and the synthetic 2-player render both
///     miss) is visible by inspecting the formation against the demo's map.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class Playback2DRealDemoRenderTests
{
    private const byte BgR = 0x15, BgG = 0x18, BgB = 0x1C;

    [Test]
    public async Task RealDemo_MidRound_RendersLivePlayerFormation()
    {
        string path = DemoTestHelper.RequireDemo();

        // ── All heavy work is SYNCHRONOUS and off the UI thread (no async-load pumping needed). ──
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        await Assert.That(frames.Count).IsGreaterThan(100);

        // Find a frame shortly after a player_death so at least one player is reliably dead — exercises
        // the controller-anchored join (dead players keep a grayed row) and the orphaned-pawn guard (no
        // garbage "16383" marker). Deterministic across demos (a fixed fraction can land on a no-death
        // round — that was the old flake).
        int target = FindPostDeathFrame(frames);
        if (target < 0)
        {
            throw new SkipTestException("no player_death event found in demo");
        }

        EntityTracker tracker = new();
        tracker.AdvanceToIndex(target, frames);

        // Drive the controller synchronously: feed frames, publish the already-advanced tracker.
        PlaybackController controller = new();
        controller.LoadDemo(frames, 64);
        controller.SyncPositionFromShell(target);
        controller.PublishTracker(tracker);

        // The REAL host-join: ModuleContext.RebuildPlayerJoin (controller-anchored). Roster = ALL match
        // players (like the shell's parsed.Players), so dead players have stable rows.
        ModuleContext context = new(controller, () => path);
        context.SetRoster(demo.Players.Values.Select(p =>
            new PlayerRosterEntry
            {
                Slot = p.Slot,
                SteamId = p.SteamId64,
                Name = p.Name
            }));

        Playback2DTabViewModel vm = new();
        vm.OnActivated(context); // builds real markers from context.CurrentPlayers (the real join)

        int shownRows = vm.Attributes.Count(a => a.InMatch);
        int deadRows = vm.Attributes.Count(a => a.InMatch && !a.IsAlive);
        Console.WriteLine($"[realdemo] {Path.GetFileName(path)} frame={target}/{frames.Count} " +
                          $"status='{vm.Status}' markers={vm.Markers.Count} shownRows={shownRows}/{vm.Attributes.Count} " +
                          $"deadRows={deadRows} score CT {vm.GameInfo.CtScore}:{vm.GameInfo.TScore} T");
        foreach (PlayerMarker m in vm.Markers.OrderBy(m => m.Team).ThenBy(m => m.Slot))
        {
            Console.WriteLine($"  slot {m.Slot,2} team {m.Team} {m.Label,-4} " +
                              $"world=({m.WorldX,8:F0},{m.WorldY,8:F0},{m.WorldZ,7:F0}) " +
                              $"ring={m.Ring} alive={m.IsAlive}");
        }

        foreach (PlayerAttributes a in vm.Attributes)
        {
            Console.WriteLine($"  attrs slot {a.Slot} alive={a.IsAlive} op={a.RowOpacity:F2}: " +
                              $"HP {a.Health} {a.ActiveWeapon} ${a.Cash} K/D/A {a.Kda}");
        }

        // Credible formation joined (alive + dead-but-mapped markers). Lower bound only — the exact count
        // varies with how many are alive at the chosen post-death frame.
        await Assert.That(vm.Markers.Count).IsGreaterThanOrEqualTo(4);
        // Real-data weapon resolve: at least one live player holds a weapon.
        await Assert.That(vm.Attributes.Any(a => a.IsAlive && a.ActiveWeapon != "—")).IsTrue();
        // Regression: no marker carries the orphaned-pawn garbage slot (16382 → "16383" label).
        await Assert.That(vm.Markers.All(m => m.Slot is >= 0 and < 64)).IsTrue();
        // Regression: dead players are NOT removed — they stay as grayed rows (RowOpacity < 1).
        await Assert.That(deadRows).IsGreaterThan(0);
        await Assert.That(vm.Attributes.Where(a => a.InMatch && !a.IsAlive).All(a => a.RowOpacity < 1.0)).IsTrue();
        // Coach / GOTV roster entries (non T/CT) are filtered out of the panel — clean 10-player list.
        await Assert.That(shownRows).IsLessThanOrEqualTo(10);

        // ── Render the real VM (only the UI leg runs on the dispatcher). ──
        int nonBg = 0;
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = 1100,
                Height = 700,
                Content = view
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            if (frame is null)
            {
                return;
            }

            string outPath = Path.Combine(HeadlessSession.ArtifactDir, "realdemo_playback2d.png");
            frame.Save(outPath);
            nonBg = ScanNonBackground(frame);
            Console.WriteLine($"[capture] {outPath}  nonBg={nonBg}");
            await Task.CompletedTask;
        });

        await Assert.That(nonBg).IsGreaterThan(100);
        await Assert.That(File.Exists(Path.Combine(HeadlessSession.ArtifactDir, "realdemo_playback2d.png"))).IsTrue();
    }

    // First player_death (in the middle 50%) that is NOT immediately followed by a round_end, +8 frames:
    // the killed player is reliably dead and the round hasn't reset (so "some dead" holds). Deterministic
    // regardless of which demo resolves — a fixed fraction could land on a no-death round (the old flake).
    private static int FindPostDeathFrame(IReadOnlyList<DemoFrame> frames)
    {
        int start = frames.Count / 4, end = frames.Count * 3 / 4;
        for (int i = start; i < end; i++)
        {
            if (!HasEvent(frames[i], "player_death"))
            {
                continue;
            }

            bool roundEndsSoon = false;
            for (int j = i; j < Math.Min(i + 96, frames.Count); j++)
            {
                if (HasEvent(frames[j], "round_end"))
                {
                    roundEndsSoon = true;
                    break;
                }
            }

            if (!roundEndsSoon)
            {
                return Math.Min(i + 8, frames.Count - 1);
            }
        }

        return -1;
    }

    private static bool HasEvent(DemoFrame frame, string name) =>
        frame.InnerMessages.Any(m => m is GameEventMessage gem &&
                                     gem.DecodedEvent.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

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
}
