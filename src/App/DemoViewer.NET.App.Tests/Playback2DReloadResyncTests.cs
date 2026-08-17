#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     State-restoration parity: opening a NEW demo while the 2D Playback tab stays active must fully resync
///     the module (map image, marker labels, trails) to the new demo — not keep the previous demo's state.
///     <para>
///         The bug this guards: <c>PlaybackController.LoadDemo</c> resets the clock WITHOUT emitting an
///         <c>Advanced</c> push, and the module's roster re-seed was gated on a player-COUNT change — so two
///         different-map demos with the same 10-player count would leave the old map + names on screen. The
///         host now raises <see cref="IModuleContext.DemoReset" /> after a (re)load, which an active module
///         resyncs on. Verified deterministically (synthetic, no demo, no baked bundle needed).
///     </para>
/// </summary>
[NotInParallel]
public class Playback2DReloadResyncTests
{
    [Test]
    public async Task DemoReloadWhileActive_ResyncsMapAndRoster_EvenWhenPlayerCountUnchanged()
    {
        Playback2DTabViewModel vm = new();
        ReloadCtx ctx = new()
        {
            MapName = "de_dust2",
            Roster =
            {
                new PlayerRosterEntry
                {
                    Slot = 0,
                    Name = "Alice",
                    SteamId = 1
                }
            }
        };

        vm.OnActivated(ctx);
        ctx.Push(new FakeSnap(1, new[]
        {
            Alive(0, 2, 100, 200)
        }));

        // Baseline: seeded to demo A (its map + its roster).
        await Assert.That(vm.LoadedMapNameForTest).IsEqualTo("de_dust2");
        await Assert.That(vm.Attributes.Single(a => a.Slot == 0).Name).IsEqualTo("Alice");
        await Assert.That(vm.Markers.Count).IsEqualTo(1);

        // A NEW demo loads while the tab stays active: DIFFERENT map, DIFFERENT roster, SAME player count
        // (the case the old count-based re-seed guard silently missed). LoadDemo emits NO Advanced push —
        // the shell raises DemoReset instead, and there is no authoritative tracker until the first seek
        // (CurrentPlayers empty), exactly as after a real reload.
        ctx.MapName = "de_mirage";
        ctx.Roster = new List<PlayerRosterEntry>
        {
            new()
            {
                Slot = 0,
                Name = "Bob",
                SteamId = 2
            }
        };
        ctx.SetCurrent(Array.Empty<IPlayerState>());
        ctx.RaiseDemoReset();

        // The map image + roster labels now reflect demo B — full restoration, no manual seek required...
        await Assert.That(vm.LoadedMapNameForTest).IsEqualTo("de_mirage");
        await Assert.That(vm.Attributes.Single(a => a.Slot == 0).Name).IsEqualTo("Bob");
        // ...and no marker glides in from demo A's position (markers/trails cleared on resync).
        await Assert.That(vm.Markers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DemoReset_AfterDeactivation_IsIgnored()
    {
        Playback2DTabViewModel vm = new();
        ReloadCtx ctx = new()
        {
            MapName = "de_dust2",
            Roster =
            {
                new PlayerRosterEntry
                {
                    Slot = 0,
                    Name = "Alice",
                    SteamId = 1
                }
            }
        };

        vm.OnActivated(ctx);
        await Assert.That(vm.LoadedMapNameForTest).IsEqualTo("de_dust2");

        vm.OnDeactivated();

        // A reload after deactivation must NOT touch this now-inactive module (zero work while
        // inactive) — it resyncs on its next OnActivated instead. The DemoReset subscription is dropped.
        ctx.MapName = "de_mirage";
        ctx.RaiseDemoReset();

        await Assert.That(vm.LoadedMapNameForTest).IsEqualTo("de_dust2"); // unchanged — module didn't react
    }

    private static ResyncPlayer Alive(int slot, int team, float x, float y)
    {
        Ent pawn = new("CCSPlayerPawn");
        pawn.F["m_iHealth"] = 100;
        pawn.F["m_lifeState"] = 0;
        return new ResyncPlayer(slot, team, pawn, new Ent("CCSPlayerController"), (x, y, 64f));
    }

    private sealed class ResyncPlayer(
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

    private sealed class ReloadCtx : IModuleContext
    {
        private readonly List<IPlayerState> _current = new();
        public List<PlayerRosterEntry> Roster { get; set; } = new();

        public bool HasDemo => true;
        public string? DemoPath => null;
        public string? MapName { get; set; }
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
        public event Action? DemoReset;
        public IReadOnlyEntityView Entities { get; } = new View();
        public IReadOnlyList<PlayerRosterEntry> Players => Roster;
        public IReadOnlyList<IPlayerState> CurrentPlayers => _current;

        /// <summary>Set the current-tick player-join list (empty = no tracker yet, i.e. pre-seek).</summary>
        public void SetCurrent(IEnumerable<IPlayerState> players)
        {
            _current.Clear();
            _current.AddRange(players);
        }

        public void Push(FakeSnap snap)
        {
            SetCurrent(snap.Players);
            Advanced?.Invoke(snap);
        }

        /// <summary>Host-side raise of the demo-reload signal (only the host may raise it in production).</summary>
        public void RaiseDemoReset() => DemoReset?.Invoke();
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
