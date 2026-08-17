#region

using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using Cs2DemoKit.Parser;
using Cs2DemoKit.Parser.EntityTracking;
using Cs2DemoKit.Parser.GameEvents;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Playback;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Gates the A4 area-effects overlay end-to-end on a REAL demo: shortly after a real
///     <c>smokegrenade_detonate</c> the VM must emit a Smoke area effect, and shortly after a real
///     <c>inferno_startburn</c> it must emit Fire cells — proving the CSmokeGrenadeProjectile /
///     CInferno field paths (<c>m_vSmokeDetonationPos</c>, <c>m_firePositions[i]</c>, <c>m_fireCount</c>,
///     <c>m_bFireIsBurning[i]</c>) resolve through the live entity view, not just synthetic doubles.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class Playback2DAreaEffectsRealDemoTests
{
    [Test]
    public async Task RealDemo_ActiveSmoke_ProducesSmokeAreaEffect() =>
        await AssertEffectAfter("smokegrenade_detonate", AreaEffectKind.Smoke);

    [Test]
    public async Task RealDemo_BurningInferno_ProducesFireAreaEffects() =>
        await AssertEffectAfter("inferno_startburn", AreaEffectKind.Fire);

    private static async Task AssertEffectAfter(string eventName, AreaEffectKind kind)
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        int tickRate = demo.TickRate > 0 ? demo.TickRate : 64;

        int ev = FirstEventFrame(frames, eventName);
        if (ev < 0)
        {
            throw new SkipTestException($"no {eventName} in demo");
        }

        int target = Math.Min(ev + 16, frames.Count - 1); // a bit after so the entity is live + fields filled

        EntityTracker tracker = new();
        tracker.AdvanceToIndex(target, frames);

        PlaybackController controller = new();
        controller.LoadDemo(frames, tickRate);
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

        Playback2DTabViewModel vm = new();
        vm.OnActivated(context);

        int matching = vm.AreaEffects.Count(a => a.Kind == kind);
        Console.WriteLine($"[area-fx] {eventName} @frame {target}: {kind}={matching} " +
                          $"(total {vm.AreaEffects.Count})");
        await Assert.That(matching).IsGreaterThan(0);

        // The effect is at a sane in-world position (not the (0,0) failure spot, inside the world extent).
        AreaEffect fx = vm.AreaEffects.First(a => a.Kind == kind);
        await Assert.That(Math.Abs(fx.WorldX) + Math.Abs(fx.WorldY)).IsGreaterThan(1f);
        await Assert.That(fx.WorldRadius).IsGreaterThan(0f);
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
