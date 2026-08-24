#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CS2DemoKit.Analysis.Visibility;
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
///     Second increment of the map-asset consumption: the 2D viewport draws the baked radar bitmap under the markers, per
///     floor band, placed via the bundle transform. Renders a REAL demo + baked bundle to a Skia frame,
///     asserts the radar fills the viewport, and saves the capture so world→radar alignment (players over the
///     map) is eyeball-verifiable — Nuke (two floors) and dust2 (single floor, carries rotate/zoom). Skips
///     when a demo or its baked bundle is absent.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ZRadarRenderTests
{
    private const byte BgR = 0x15, BgG = 0x18, BgB = 0x1C;

    [Test]
    public async Task RealDemo_Nuke_DrawsRadarBackground_PerFloor()
    {
        string? path = DemoTestHelper.FindDemoPath("003816306022075596881_1029495947.dem")
                       ?? DemoTestHelper.FindDemoPath("match730_003826256877184877003_0981591541_410.dem");
        if (path is null)
        {
            throw new SkipTestException("no Nuke demo present");
        }

        CaptureResult r = await RenderMapCapture(path, "radar-nuke.png");
        if (!r.BundleLoaded)
        {
            throw new SkipTestException("de_nuke bundle not baked (run tools/DemoViewer.NET.AssetBaker)");
        }

        LogCapture("nuke", r);
        await Assert.That(r.Bitmaps).IsEqualTo(2);
        await Assert.That(r.Floors).IsEqualTo(2);
        await Assert.That(r.NonBgOn).IsGreaterThan(400_000);
        await Assert.That(File.Exists(Path.Combine(HeadlessSession.ArtifactDir, "radar-nuke.png"))).IsTrue();
    }

    [Test]
    public async Task RealDemo_Dust2_DrawsRadarBackground_SingleFloor()
    {
        string? path = DemoTestHelper.FindDemoPath("003801777854962729156_0256036251.dem");
        if (path is null)
        {
            throw new SkipTestException("no dust2 demo present");
        }

        CaptureResult r = await RenderMapCapture(path, "radar-dust2.png");
        if (!r.BundleLoaded)
        {
            throw new SkipTestException("de_dust2 bundle not baked");
        }

        LogCapture("dust2", r);
        await Assert.That(r.Bitmaps).IsGreaterThanOrEqualTo(1);
        await Assert.That(r.Floors).IsEqualTo(1);
        await Assert.That(r.NonBgOn).IsGreaterThan(100_000);
        await Assert.That(File.Exists(Path.Combine(HeadlessSession.ArtifactDir, "radar-dust2.png"))).IsTrue();
    }

    private static void LogCapture(string map, CaptureResult r)
    {
        Console.WriteLine($"[radar] {map} floors={r.Floors} bitmaps={r.Bitmaps} nonBgOn={r.NonBgOn} " +
                          $"images=[{string.Join(",", r.Images)}]");
        Console.WriteLine($"  transform={r.Transform}  bounds={r.Bounds}");
        Console.WriteLine("  markers: " + string.Join("  ", r.Markers.Select(m => $"{m.Slot}:({m.X:F0},{m.Y:F0},{m.Z:F0})")));
    }

    private static async Task<CaptureResult> RenderMapCapture(string path, string outName)
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        // A round-start frame (just after freeze end) has ~10 players alive, spread CT-spawn ↔ T-spawn, so the
        // camera fit frames most of the map — the right shape for judging world→radar alignment.
        int target = FindRoundStartFrame(frames);
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

        bool bundleLoaded = false;
        int floors = 0, bitmaps = 0, nonBgOn = 0;
        IReadOnlyList<string> images = Array.Empty<string>();
        RadarTransform? transform = null;
        WorldBoundsDto? bounds = null;
        List<(int, float, float, float)> markers = new();

        // Render the VIEWPORT alone (background is the known #15181C, so radar coverage is measurable).
        // Activate ON the UI thread AFTER the viewport attaches: the loader decodes PNGs via Avalonia imaging
        // (unavailable off-thread), and OnActivated's FrameUpdated drives the viewport's floor+radar pull.
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
            if (!bundleLoaded)
            {
                return;
            }

            floors = vm.AuthoritativeFloors?.Count ?? 0;
            bitmaps = vm.MapAsset!.RadarBitmaps.Count;
            images = vm.MapAsset.Bundle.RadarImages;
            transform = vm.MapAsset.Bundle.Transform;
            bounds = vm.MapAsset.Bundle.Bounds;
            foreach (PlayerMarker m in vm.Markers.Take(5))
            {
                markers.Add((m.Slot, m.WorldX, m.WorldY, m.WorldZ));
            }

            vm.ShowRadar = true;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap? on = window.CaptureRenderedFrame();
            if (on is not null)
            {
                on.Save(Path.Combine(HeadlessSession.ArtifactDir, outName));
                nonBgOn = ScanNonBackground(on);
            }

            await Task.CompletedTask;
        });

        return new CaptureResult(bundleLoaded, floors, bitmaps, nonBgOn, images, transform, bounds,
            markers.Select(m => (m.Item1, m.Item2, m.Item3, m.Item4)).ToList());
    }

    // First round_freeze_end (past warmup) + a few frames → all players alive, still near their spawns.
    private static int FindRoundStartFrame(IReadOnlyList<DemoFrame> frames)
    {
        int start = frames.Count / 8, end = frames.Count * 3 / 4;
        for (int i = start; i < end; i++)
        {
            bool freezeEnd = frames[i].InnerMessages.Any(m =>
                m is GameEventMessage gem && gem.DecodedEvent.Name.Equals("round_freeze_end", StringComparison.OrdinalIgnoreCase));
            if (freezeEnd)
            {
                return Math.Min(i + 12, frames.Count - 1);
            }
        }

        return frames.Count / 2;
    }

    // Counts pixels that differ from the viewport background (BGRA8888 buffer). Safe Marshal.Copy path
    // (matches Playback2DRealDemoRenderTests — the test project doesn't enable unsafe blocks).
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

    private sealed record CaptureResult(
        bool BundleLoaded,
        int Floors,
        int Bitmaps,
        int NonBgOn,
        IReadOnlyList<string> Images,
        RadarTransform? Transform,
        WorldBoundsDto? Bounds,
        IReadOnlyList<(int Slot, float X, float Y, float Z)> Markers);
}
