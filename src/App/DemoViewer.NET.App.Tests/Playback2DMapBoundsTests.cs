#region

using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Playback;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Map mode: verifies the VM reads the REAL networked playable-map bounds from
///     CCSGameRulesProxy.m_pGameRules.m_vMinimapMins / m_vMinimapMaxs (the radar bounding box) — so Map mode
///     frames the actual map instead of the observed-positions approximation. Sync parse path (no render).
/// </summary>
[NotInParallel]
[Category("Integration")]
public class Playback2DMapBoundsTests
{
    [Test]
    public async Task MapBounds_ReadsRealRadarBoundingBox_PlayersInside()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        int mid = frames.Count / 2;
        EntityTracker tracker = new();
        tracker.AdvanceToIndex(mid, frames);

        PlaybackController controller = new();
        controller.LoadDemo(frames, 64);
        controller.SyncPositionFromShell(mid);
        controller.PublishTracker(tracker);

        ModuleContext context = new(controller, () => path);
        context.SetRoster(demo.Players.Values.Select(p =>
            new PlayerRosterEntry
            {
                Slot = p.Slot,
                SteamId = p.SteamId64,
                Name = p.Name
            }));

        Playback2DTabViewModel vm = new();
        vm.OnActivated(context); // UpdateGameInfo reads the map bounds once

        (double MinX, double MinY, double MaxX, double MaxY)? mb = vm.MapBounds;
        Console.WriteLine($"[mapbounds] {Path.GetFileName(path)} MapBounds={(mb is { } b ? $"X[{b.MinX:F0}..{b.MaxX:F0}] Y[{b.MinY:F0}..{b.MaxY:F0}]" : "null")}");

        // The radar bounding box is published on the game rules.
        await Assert.That(mb).IsNotNull();
        (double MinX, double MinY, double MaxX, double MaxY) box = mb!.Value;
        await Assert.That(box.MaxX).IsGreaterThan(box.MinX);
        await Assert.That(box.MaxY).IsGreaterThan(box.MinY);

        // Every live player at this frame sits INSIDE the published map bounds (the sanity gate that the
        // box is real world-space, not garbage). Markers are reconstructed world positions.
        await Assert.That(vm.Markers.Count).IsGreaterThan(0);
        foreach (PlayerMarker m in vm.Markers)
        {
            await Assert.That(m.WorldX).IsGreaterThanOrEqualTo((float)box.MinX - 64);
            await Assert.That(m.WorldX).IsLessThanOrEqualTo((float)box.MaxX + 64);
            await Assert.That(m.WorldY).IsGreaterThanOrEqualTo((float)box.MinY - 64);
            await Assert.That(m.WorldY).IsLessThanOrEqualTo((float)box.MaxY + 64);
        }
    }
}
