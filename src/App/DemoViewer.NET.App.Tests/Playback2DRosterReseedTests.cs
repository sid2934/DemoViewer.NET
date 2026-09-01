#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     When the roster is set AFTER the tab activates (host order), the side cards, stats, and marker
///     INITIALS must appear without needing a tab leave-and-return. Markers (positions) come from the
///     per-tick join and already work; the roster-derived display state (names + attribute rows) must
///     re-seed on the empty→populated transition. Verified deterministically (synthetic, no demo).
/// </summary>
[NotInParallel]
public class Playback2DRosterReseedTests
{
    [Test]
    public async Task RosterArrivingAfterActivation_SeedsCardsAndInitials_WithoutReactivation()
    {
        Playback2DTabViewModel vm = new();
        FakeCtx ctx = new(); // roster starts EMPTY (host hasn't set it yet)

        vm.OnActivated(ctx);

        // Frame 1: a live player exists but the roster is still empty → marker renders (with a NUMBER label),
        // but there is no attributes card and no initials yet.
        ctx.Push(new FakeSnap(1, new[]
        {
            Alive(0, 2, 100, 200)
        }));
        await Assert.That(vm.Markers.Count).IsEqualTo(1);
        await Assert.That(vm.Markers[0].Label).IsEqualTo("1"); // slot+1 fallback, no name
        await Assert.That(vm.Attributes.Count(a => a.InMatch)).IsEqualTo(0); // no cards yet

        // The host now sets the roster (post-load) and another push arrives.
        ctx.Roster.Add(new PlayerRosterEntry
        {
            Slot = 0,
            Name = "Neo",
            SteamId = 1
        });
        ctx.Push(new FakeSnap(2, new[]
        {
            Alive(0, 2, 110, 210)
        }));

        // The cards + initials now appear, no tab re-activation needed.
        await Assert.That(vm.Markers[0].Label).IsEqualTo("NE"); // initials from the roster name
        PlayerAttributes row = vm.Attributes.Single(a => a.Slot == 0);
        await Assert.That(row.InMatch).IsTrue();
        await Assert.That(row.Name).IsEqualTo("Neo");
        await Assert.That(row.HasLivePawn).IsTrue();
    }

    private static DeadCapablePlayer Alive(int slot, int team, float x, float y)
    {
        Ent pawn = new("CCSPlayerPawn");
        pawn.F["m_iHealth"] = 100;
        pawn.F["m_lifeState"] = 0;
        return new DeadCapablePlayer(slot, team, pawn, new Ent("CCSPlayerController"), (x, y, 64f));
    }

    private sealed class DeadCapablePlayer(
        int slot,
        int team,
        Ent? pawn,
        Ent? ctrl,
        (float X, float Y, float Z)? pos) : IPlayerState
    {
        public int Slot => slot;
        public int Team => team;
        public bool HasLivePawn => pawn is not null;
        public IReadOnlyEntity? Pawn => pawn;
        public IReadOnlyEntity? Controller => ctrl;
        public (float X, float Y, float Z)? WorldPosition => pos;
    }

    private sealed class FakeCtx : IModuleContext
    {
        private readonly List<IPlayerState> _current = new();
        public List<PlayerRosterEntry> Roster { get; } = new();

        public bool HasDemo => true;
        public string? DemoPath => null;
        public int TickRate => 64;
        public double CurtimeSeconds(int tick) => tick / 64.0;
        public int CurrentFrameIndex => 0;
        public int CurrentTick => 0;
        public bool IsPlaying => false;
        public double Speed => 1;

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
        public IReadOnlyList<IPlayerState> CurrentPlayers => _current;

        public void Push(FakeSnap snap)
        {
            _current.Clear();
            _current.AddRange(snap.Players);
            Advanced?.Invoke(snap);
        }
    }

    private sealed class FakeSnap(int frameIndex, IReadOnlyList<IPlayerState> players) : IPlaybackSnapshot
    {
        public int FrameIndex => frameIndex;
        public int Tick => frameIndex * 64;
        public IReadOnlyEntityView Entities { get; } = new View();
        public IReadOnlyList<IPlayerState> Players => players;
    }


    private sealed class Ent(string className) : IReadOnlyEntity
    {
        public Dictionary<string, object?> F { get; } = new();
        public string ClassName => className;
        public int Serial => 1;
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
        public IEnumerable<IReadOnlyEntity> All() => Array.Empty<IReadOnlyEntity>();
        public IEnumerable<IReadOnlyEntity> OfClass(string className) => Array.Empty<IReadOnlyEntity>();
        public IReadOnlyEntity? BySerial(int serial) => null;
        public IReadOnlyEntity? ByIndex(int entityIndex) => null;
        public IReadOnlyEntity? ResolveHandle(ulong handle) => null;
    }
}
