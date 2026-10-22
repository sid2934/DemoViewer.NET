#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Phase E (module forward-nav, A3): the 2D tab offers "jump to next/prev kill" and routes it through
///     <see cref="IModuleContext.RequestNextEvent" /> / <c>RequestPrevEvent</c> with the player_death filter,
///     so the seek lands on the shell's shared clock. The buttons are gated on the demo actually carrying
///     player_death (<c>HasKillEvents</c> ← <c>AvailableEventNames</c>) so a demo without kills shows nothing
///     rather than a dead button (asset/demo-independent).
/// </summary>
[NotInParallel]
public class Playback2DEventNavTests
{
    [Test]
    public async Task KillNav_ShownAndRouted_WhenDemoHasPlayerDeath()
    {
        Playback2DTabViewModel vm = new();
        Playback2DFakeContext ctx = NavCtx("player_death", "bomb_planted");
        ctx.Roster.Add(new PlayerRosterEntry
        {
            Slot = 0,
            Name = "Neo",
            SteamId = 1
        });

        vm.OnActivated(ctx);
        await Assert.That(vm.HasKillEvents).IsTrue();

        vm.NextKillCommand.Execute(null);
        await Assert.That(ctx.NextEvents.LastOrDefault()).IsNotNull();
        await Assert.That(ctx.NextEvents.LastOrDefault()!).Contains("player_death");

        vm.PrevKillCommand.Execute(null);
        await Assert.That(ctx.PrevEvents.LastOrDefault()).IsNotNull();
        await Assert.That(ctx.PrevEvents.LastOrDefault()!).Contains("player_death");
    }

    [Test]
    public async Task KillNav_Hidden_WhenDemoHasNoPlayerDeath()
    {
        Playback2DTabViewModel vm = new();
        Playback2DFakeContext ctx = NavCtx("round_start", "bomb_planted"); // no player_death
        ctx.Roster.Add(new PlayerRosterEntry
        {
            Slot = 0,
            Name = "Trinity",
            SteamId = 2
        });

        vm.OnActivated(ctx);

        await Assert.That(vm.HasKillEvents).IsFalse();

        // Commands are still safe to invoke (the button is just hidden): they request the kill filter, which
        // the navigator simply can't satisfy. No throw.
        vm.NextKillCommand.Execute(null);
        await Assert.That(ctx.NextEvents.LastOrDefault()!).Contains("player_death");
    }

    // ── Recording double: exposes a configurable event-name set and captures the forward-nav filters ──

    // The nav filters are recorded in NextEvents / PrevEvents; the names make those commands visible.
    private static Playback2DFakeContext NavCtx(params string[] eventNames)
    {
        Playback2DFakeContext ctx = Playback2DFakeContext.Bare();
        foreach (string name in eventNames)
        {
            ctx.Frames[name] = [];
        }

        return ctx;
    }
}
