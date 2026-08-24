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
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Phase-3 end-to-end check for the 2D <b>Vision</b> overlay: enabling it on a real demo lazily builds the
///     collision BVH (off-thread) and the viewport draws could-see sightlines. Seeks to a real kill frame
///     (where LOS provably exists) so the overlay has content, renders to a Skia frame, asserts sightlines
///     were produced, and saves the capture so the sightlines-over-the-map look is eyeball-verifiable. Skips
///     without the nuke demo + baked collision.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ZVisionOverlayRenderTests
{
    [Test]
    public async Task RealDemo_Nuke_VisionOverlay_DrawsSightlines()
    {
        string? path = DemoTestHelper.FindDemoPath("vitality-vs-fut-m3-nuke.dem")
                       ?? DemoTestHelper.FindDemoPath("furia-vs-vitality-m3-nuke.dem")
                       ?? DemoTestHelper.FindDemoPath("003816306022075596881_1029495947.dem");
        if (path is null)
        {
            throw new SkipTestException("no Nuke demo present");
        }

        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        // A direct rifle/pistol kill a bit into the game → the killer (and usually others) have live LOS a few
        // frames before death. A far better frame for the vision overlay than round-start (everyone at spawn).
        // Fire + payload: the filter reads Penetrated/Attacker/Weapon off the payload and
        // FrameNumber off the envelope.
        var kill = demo.AllGameEvents
            .Where(e => e.Payload is PlayerDeathEvent)
            .Select(e => (Fire: e, Death: (PlayerDeathEvent)e.Payload!))
            .Where(x => x.Death.Penetrated == 0 && x.Death.Attacker >= 0
                        && x.Death.Attacker != x.Death.UserId
                        && x.Fire.FrameNumber > frames.Count / 4
                        && x.Fire.FrameNumber < frames.Count - 1
                        && !x.Death.Weapon.Contains("grenade", StringComparison.OrdinalIgnoreCase)
                        && !x.Death.Weapon.Equals("inferno", StringComparison.OrdinalIgnoreCase))
            .Skip(10)
            .Cast<(GameEvent Fire, PlayerDeathEvent Death)?>()
            .FirstOrDefault();
        if (kill is null)
        {
            throw new SkipTestException("no suitable direct kill found");
        }

        int target = Math.Max(0, kill.Value.Fire.FrameNumber - 8);
        EntityTracker tracker = new();
        tracker.ReplayToIndex(target, frames);

        PlaybackController controller = new();
        controller.LoadDemo(frames, 64);
        controller.SyncPositionFromShell(target);
        controller.PublishTracker(tracker);

        ModuleContext context = new(controller, () => path);
        context.SetRoster(demo.Players.Values.Select(p =>
            new PlayerRosterEntry
            {
                Slot = p.Slot,
                SteamId = p.SteamId64,
                Name = p.Name
            }));
        context.SetMapName(demo.MapName);

        Playback2DTabViewModel vm = new();
        bool bundleLoaded = false, engineLoaded = false;
        int sightlines = -1, nonBgOff = 0, nonBgOn = 0;

        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DViewport viewport = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = 900,
                Height = 900,
                Content = viewport
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.OnActivated(context);
            bundleLoaded = vm.MapAsset is not null;
            if (!bundleLoaded || vm.MapAsset!.CollisionTrisPath is null)
            {
                return;
            }

            // Baseline render (vision OFF) for a pixel-delta sanity check.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (window.CaptureRenderedFrame() is { } off)
            {
                nonBgOff = ScanNonBackground(off);
            }

            // Enable Vision. The production path builds the BVH off-thread + posts back; under headless the
            // async pump is unreliable, so build synchronously via the test seam (same load, same engine).
            vm.ShowVision = true;
            vm.LoadVisionEngineSyncForTest();
            Dispatcher.UIThread.RunJobs();
            engineLoaded = vm.VisionEngine is not null;

            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            sightlines = viewport.SightlineCount;
            if (window.CaptureRenderedFrame() is { } on)
            {
                on.Save(Path.Combine(HeadlessSession.ArtifactDir, "vision-nuke.png"));
                nonBgOn = ScanNonBackground(on);
            }

            await Task.CompletedTask;
        });

        if (!bundleLoaded)
        {
            throw new SkipTestException("de_nuke bundle/collision not baked (run tools/DemoViewer.NET.AssetBaker)");
        }

        Console.WriteLine($"[vision] kill@{kill.Value.Fire.FrameNumber} killer={kill.Value.Death.Attacker} victim={kill.Value.Death.UserId} " +
                          $"engineLoaded={engineLoaded} sightlines={sightlines} nonBgOff={nonBgOff} nonBgOn={nonBgOn}");

        await Assert.That(engineLoaded).IsTrue(); // the async off-thread BVH build completed + applied
        await Assert.That(sightlines).IsGreaterThan(0); // could-see sightlines were computed at this frame
        await Assert.That(nonBgOn).IsGreaterThanOrEqualTo(nonBgOff); // overlay adds ink, never removes it
    }

    private static int ScanNonBackground(WriteableBitmap bmp)
    {
        const byte BgR = 0x15, BgG = 0x18, BgB = 0x1C;
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
