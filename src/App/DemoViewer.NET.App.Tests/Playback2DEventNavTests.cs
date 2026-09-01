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
        RecordingCtx ctx = new("player_death", "bomb_planted");
        ctx.Roster.Add(new PlayerRosterEntry
        {
            Slot = 0,
            Name = "Neo",
            SteamId = 1
        });

        vm.OnActivated(ctx);
        await Assert.That(vm.HasKillEvents).IsTrue();

        vm.NextKillCommand.Execute(null);
        await Assert.That(ctx.LastNextFilter).IsNotNull();
        await Assert.That(ctx.LastNextFilter!).Contains("player_death");

        vm.PrevKillCommand.Execute(null);
        await Assert.That(ctx.LastPrevFilter).IsNotNull();
        await Assert.That(ctx.LastPrevFilter!).Contains("player_death");
    }

    [Test]
    public async Task KillNav_Hidden_WhenDemoHasNoPlayerDeath()
    {
        Playback2DTabViewModel vm = new();
        RecordingCtx ctx = new("round_start", "bomb_planted"); // no player_death
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
        await Assert.That(ctx.LastNextFilter!).Contains("player_death");
    }

    // ── Recording double: exposes a configurable event-name set and captures the forward-nav filters ──

    private sealed class RecordingCtx(params string[] eventNames) : IModuleContext
    {
        public List<PlayerRosterEntry> Roster { get; } = new();
        public IReadOnlyCollection<string>? LastNextFilter { get; private set; }
        public IReadOnlyCollection<string>? LastPrevFilter { get; private set; }

        public IReadOnlyCollection<string> AvailableEventNames { get; } = eventNames;
        public void RequestNextEvent(IReadOnlyCollection<string>? names) => LastNextFilter = names;
        public void RequestPrevEvent(IReadOnlyCollection<string>? names) => LastPrevFilter = names;

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
        public IReadOnlyEntityView Entities { get; } = new EmptyView();
        public IReadOnlyList<PlayerRosterEntry> Players => Roster;
        public IReadOnlyList<IPlayerState> CurrentPlayers { get; } = new List<IPlayerState>();

        // Advanced is never raised in this test (commands are driven directly); reference it to satisfy the
        // "event is never used" analyzer without changing behavior.
        public void Unused() => Advanced?.Invoke(null!);
    }


    private sealed class EmptyView : IReadOnlyEntityView
    {
        public IEnumerable<IReadOnlyEntity> All() => Array.Empty<IReadOnlyEntity>();
        public IEnumerable<IReadOnlyEntity> OfClass(string className) => Array.Empty<IReadOnlyEntity>();
        public IReadOnlyEntity? BySerial(int serial) => null;
        public IReadOnlyEntity? ByIndex(int entityIndex) => null;
        public IReadOnlyEntity? ResolveHandle(ulong handle) => null;
    }
}
