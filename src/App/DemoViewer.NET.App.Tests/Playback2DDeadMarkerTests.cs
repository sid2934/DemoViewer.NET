#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Deterministic (synthetic, no demo) coverage of the death-marker PERSISTENCE path: when a player's
///     pawn orphans on death (no live position this tick), the module must hold a gray marker at the
///     last-known death spot with the correct roster label until respawn — instead of the icon vanishing
///     or showing a garbage slot. Complements the real-demo render (single-frame) with the multi-push
///     cache behavior the single seek can't exercise.
/// </summary>
[NotInParallel]
public class Playback2DDeadMarkerTests
{
    [Test]
    public async Task DeadPawn_HoldsGrayMarkerAtLastKnownSpot()
    {
        Playback2DTabViewModel vm = new();
        FakeCtx ctx = new();
        ctx.Roster.Add(new PlayerRosterEntry
        {
            Slot = 0,
            Name = "Neo",
            SteamId = 1
        });
        vm.OnActivated(ctx);

        // Frame 1: slot 0 ALIVE at a known spot → populates the last-known-position cache + draws a marker.
        ctx.Push(new FakeSnap(1, new[]
        {
            Alive(0, 2, 1200, -800, 64, 100)
        }));
        await Assert.That(vm.Markers.Count).IsEqualTo(1);
        await Assert.That(vm.Markers[0].IsAlive).IsTrue();

        // Frame 2: slot 0's pawn has orphaned — no live pawn, no position (HasLivePawn=false).
        ctx.Push(new FakeSnap(2, new[]
        {
            Dead(0, 2)
        }));

        await Assert.That(vm.Markers.Count).IsEqualTo(1);
        PlayerMarker held = vm.Markers[0];
        await Assert.That(held.IsAlive).IsFalse();
        await Assert.That(held.Ring).IsEqualTo(RingState.Dead);
        await Assert.That(held.WorldX).IsEqualTo(1200f); // held at the last-known death spot
        await Assert.That(held.WorldY).IsEqualTo(-800f);
        await Assert.That(held.Label).IsEqualTo("NE"); // correct roster label — NOT "16383"
        await Assert.That(held.Slot).IsEqualTo(0);

        // The attributes row persists, grayed (RowOpacity < 1), with the player still in-match.
        PlayerAttributes row = vm.Attributes.Single(a => a.Slot == 0);
        await Assert.That(row.InMatch).IsTrue();
        await Assert.That(row.IsAlive).IsFalse();
        await Assert.That(row.RowOpacity).IsLessThan(1.0);
    }

    /// <summary>
    ///     The multi-frame cache behavior a single alive→dead transition can't exercise: across a sequence
    ///     of pushes the held death marker must (a) sit at the LATEST alive spot — not the first — proving the
    ///     cache is refreshed every live frame; (b) persist unchanged across SEVERAL consecutive dead frames
    ///     (never drifting, never vanishing, never duplicating); and (c) be replaced by a live marker on
    ///     respawn, which re-arms the cache so a later death holds at the respawn spot.
    /// </summary>
    [Test]
    public async Task DeadMarker_TracksLatestSpot_PersistsAcrossFrames_AndClearsOnRespawn()
    {
        Playback2DTabViewModel vm = new();
        FakeCtx ctx = new();
        ctx.Roster.Add(new PlayerRosterEntry
        {
            Slot = 0,
            Name = "Neo",
            SteamId = 1
        });
        vm.OnActivated(ctx);

        // Frames 1→2 ALIVE and MOVING (spot A then spot B). The last-known-position cache must end on B.
        ctx.Push(new FakeSnap(1, new[]
        {
            Alive(0, 2, 100, 100, 64, 100)
        }));
        ctx.Push(new FakeSnap(2, new[]
        {
            Alive(0, 2, 500, -300, 64, 100)
        }));
        await Assert.That(vm.Markers.Single().IsAlive).IsTrue();
        await Assert.That(vm.Markers.Single().WorldX).IsEqualTo(500f);
        await Assert.That(vm.Markers.Single().WorldY).IsEqualTo(-300f);

        // Frames 3→5 DEAD/orphaned (no live pawn, no position). The gray marker must hold at spot B on
        // EVERY frame — not just the first dead frame — and never drift back to the stale A.
        foreach (int f in new[]
                 {
                     3, 4, 5
                 })
        {
            ctx.Push(new FakeSnap(f, new[]
            {
                Dead(0, 2)
            }));
            PlayerMarker held = vm.Markers.Single(); // exactly one — no vanish, no duplicate
            await Assert.That(held.IsAlive).IsFalse();
            await Assert.That(held.Ring).IsEqualTo(RingState.Dead);
            await Assert.That(held.WorldX).IsEqualTo(500f); // latest alive spot (B), held steady
            await Assert.That(held.WorldY).IsEqualTo(-300f);
            await Assert.That(held.Label).IsEqualTo("NE"); // correct roster label, not a garbage slot
            await Assert.That(held.Slot).IsEqualTo(0);
        }

        // Frame 6 RESPAWN at a fresh spot C → live marker there, ring back to the team colour (the death
        // gap must not leak a stale flash), cache re-armed to C.
        ctx.Push(new FakeSnap(6, new[]
        {
            Alive(0, 2, -900, 1200, 64, 100)
        }));
        PlayerMarker live = vm.Markers.Single();
        await Assert.That(live.IsAlive).IsTrue();
        await Assert.That(live.Ring).IsEqualTo(RingState.Team);
        await Assert.That(live.WorldX).IsEqualTo(-900f);
        await Assert.That(live.WorldY).IsEqualTo(1200f);

        // Frame 7 DEAD again → now held at the RESPAWN spot C, proving the cache refreshed on respawn
        // rather than reverting to the pre-death spot B.
        ctx.Push(new FakeSnap(7, new[]
        {
            Dead(0, 2)
        }));
        PlayerMarker held2 = vm.Markers.Single();
        await Assert.That(held2.IsAlive).IsFalse();
        await Assert.That(held2.Ring).IsEqualTo(RingState.Dead);
        await Assert.That(held2.WorldX).IsEqualTo(-900f);
        await Assert.That(held2.WorldY).IsEqualTo(1200f);
    }

    private static DeadCapablePlayer Alive(int slot, int team, float x, float y, float z, int hp)
    {
        Ent pawn = new("CCSPlayerPawn");
        pawn.F["m_iHealth"] = hp;
        pawn.F["m_lifeState"] = 0;
        Ent ctrl = new("CCSPlayerController");
        return new DeadCapablePlayer(slot, team, pawn, ctrl, (x, y, z));
    }

    private static DeadCapablePlayer Dead(int slot, int team)
    {
        Ent ctrl = new("CCSPlayerController");
        return new DeadCapablePlayer(slot, team, null, ctrl, null);
    }

    // ── Minimal doubles (a PlayerState that can be pawn-less; the smoke test's fakes are always live) ──

    private sealed class DeadCapablePlayer(
        int slot,
        int team,
        Ent? pawn,
        Ent? ctrl,
        (float X, float Y, float Z)? worldPosition) : IPlayerState
    {
        public int Slot => slot;
        public int Team => team;
        public bool HasLivePawn => pawn is not null;
        public IReadOnlyEntity? Pawn => pawn;
        public IReadOnlyEntity? Controller => ctrl;
        public (float X, float Y, float Z)? WorldPosition => worldPosition;
    }

    private sealed class FakeCtx : IModuleContext
    {
        private readonly List<IPlayerState> _current = new();
        public List<PlayerRosterEntry> Roster { get; } = new();

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
