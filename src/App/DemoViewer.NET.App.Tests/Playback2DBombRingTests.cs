#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Playback;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     De-risks the bomb-timer ring (A4): the planted-C4 world position must reconstruct from its
///     CBodyComponent cell coords (the open question — does CPlantedC4 carry the same m_cell* fields as a
///     pawn?). Activates a real VM at a frame shortly after a real bomb_planted and asserts the
///     <see cref="Playback2DTabViewModel.Bomb" /> draw-state is present, in-bounds, and counting down near
///     full (just planted).
/// </summary>
[NotInParallel]
[Category("Integration")]
public class Playback2DBombRingTests
{
    [Test]
    public async Task Bomb_DrawState_HasReconstructedPosition_AtAPlant()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        int tickRate = demo.TickRate > 0 ? demo.TickRate : 64;

        int plantFrame = FirstEventFrame(frames, "bomb_planted");
        if (plantFrame < 0)
        {
            throw new SkipTestException("no bomb_planted event in demo");
        }

        int gateFrame = Math.Min(plantFrame + 8, frames.Count - 1); // C4 entity exists + m_flC4Blow populated

        EntityTracker tracker = new();
        tracker.ReplayToIndex(gateFrame, frames);

        PlaybackController controller = new();
        controller.LoadDemo(frames, tickRate);
        controller.SyncPositionFromShell(gateFrame);
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
        vm.OnActivated(context);

        BombMarker? bomb = vm.Bomb;
        Console.WriteLine($"[bomb-ring] plantFrame={plantFrame} gateFrame={gateFrame} bomb={bomb}");

        await Assert.That(bomb).IsNotNull();
        BombMarker b = bomb!.Value;

        // Position reconstructed and inside the world (|axis| < WORLD_HALF_EXTENT); not the (0,0) failure spot.
        await Assert.That(Math.Abs(b.WorldX)).IsLessThan(PositionUtil.WorldHalfExtent);
        await Assert.That(Math.Abs(b.WorldY)).IsLessThan(PositionUtil.WorldHalfExtent);
        await Assert.That(Math.Abs(b.WorldX) + Math.Abs(b.WorldY)).IsGreaterThan(1f);

        // Just planted → detonation ring near full.
        await Assert.That(b.DetonationFraction).IsGreaterThan(0.8);
        await Assert.That(b.DetonationFraction).IsLessThanOrEqualTo(1.0);
    }

    private static int FirstEventFrame(IReadOnlyList<DemoFrame> frames, string name)
    {
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].InnerMessages.Any(m => m is GameEventMessage gem &&
                                                 gem.DecodedEvent.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return -1;
    }
}
