#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A4 grenade flight trails: each in-flight grenade projectile (the five <c>C*Projectile</c> classes)
///     accumulates a Serial-keyed trail of its reconstructed flight path, coloured by grenade kind. The trail
///     fades + prunes after the projectile detonates, and — the one real correctness guard — is cleared
///     wholesale on a discontinuous frame jump so a polyline never streaks from a pre-seek point to a
///     post-seek point (a live-accumulate teleport guard, mirroring marker-snap).
/// </summary>
[NotInParallel]
public class Playback2DTrajectoryTests
{
    [Test]
    public async Task MovingProjectile_AccumulatesTrail_OfTheRightKind()
    {
        Playback2DTabViewModel vm = new();
        Ctx ctx = NewCtx();
        vm.OnActivated(ctx);

        View view = new();
        Ent nade = new("CHEGrenadeProjectile", 7);
        SetPos(nade, 100, 50, 0);
        view.Add(nade);

        ctx.Push(new Snap(1, view)); // 1 point — not yet a visible line
        await Assert.That(vm.GrenadeTrails.Count).IsEqualTo(0);

        SetPos(nade, 200, 50, 0);
        ctx.Push(new Snap(2, view)); // 2 points — now a visible trail
        SetPos(nade, 300, 50, 0);
        ctx.Push(new Snap(3, view)); // 3 points

        await Assert.That(vm.GrenadeTrails.Count).IsEqualTo(1);
        GrenadeTrail trail = vm.GrenadeTrails[0];
        await Assert.That(trail.Kind).IsEqualTo(GrenadeKind.He);
        await Assert.That(trail.Points.Count).IsEqualTo(3);
        await Assert.That(trail.Points[0].X).IsEqualTo(100f);
        await Assert.That(trail.Points[^1].X).IsEqualTo(300f); // head at the latest sampled spot
        await Assert.That(trail.Alpha).IsEqualTo(1.0); // live → full opacity
    }

    [Test]
    public async Task DetonatedProjectile_FadesOut_ThenPrunes()
    {
        Playback2DTabViewModel vm = new();
        Ctx ctx = NewCtx();
        vm.OnActivated(ctx);

        View view = new();
        Ent nade = new("CSmokeGrenadeProjectile", 11);
        SetPos(nade, 0, 0, 0);
        view.Add(nade);

        ctx.Push(new Snap(1, view));
        SetPos(nade, 64, 0, 0);
        ctx.Push(new Snap(2, view));
        await Assert.That(vm.GrenadeTrails.Count).IsEqualTo(1);

        // Projectile detonates → gone from the entity view. The trail should fade over ~TrailFadeSeconds
        // (2s @ 64t = 128 ticks; each push advances 64 ticks here), then be pruned.
        view.Remove(nade);

        ctx.Push(new Snap(3, view)); // age 64 → ~half faded, still present
        await Assert.That(vm.GrenadeTrails.Count).IsEqualTo(1);
        await Assert.That(vm.GrenadeTrails[0].Alpha).IsLessThan(1.0);
        await Assert.That(vm.GrenadeTrails[0].Alpha).IsGreaterThan(0.0);

        ctx.Push(new Snap(5, view)); // age 192 ≥ fade window → pruned
        await Assert.That(vm.GrenadeTrails.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LandedButStillAlive_Projectile_FadesAndPrunes()
    {
        // A smoke/decoy projectile lingers as a LIVE entity for many seconds after it lands. Its flight line
        // must still fade out (driven by time-since-last-MOVE), not hold at full opacity for the whole life.
        Playback2DTabViewModel vm = new();
        Ctx ctx = NewCtx();
        vm.OnActivated(ctx);

        View view = new();
        Ent nade = new("CSmokeGrenadeProjectile", 21);
        SetPos(nade, 0, 0, 0);
        view.Add(nade);
        ctx.Push(new Snap(1, view));
        SetPos(nade, 64, 0, 0);
        ctx.Push(new Snap(2, view)); // 2 points, last move @ tick 128
        await Assert.That(vm.GrenadeTrails.Count).IsEqualTo(1);

        // Projectile has landed but stays in the entity view, STATIONARY. Advance time without moving it.
        ctx.Push(new Snap(3, view)); // age 64 → fading, still shown
        await Assert.That(vm.GrenadeTrails.Count).IsEqualTo(1);
        await Assert.That(vm.GrenadeTrails[0].Alpha).IsLessThan(1.0);

        ctx.Push(new Snap(5, view)); // age 192 ≥ fade window → pruned even though the entity is still present
        await Assert.That(vm.GrenadeTrails.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BackwardMicroStep_HoldsTrail_NoKinkNoPrune()
    {
        // A 1-frame backward step (within the jump threshold, so NO wholesale clear) must hold the trail as-is
        // — neither append a backward point (kink) nor prune it.
        Playback2DTabViewModel vm = new();
        Ctx ctx = NewCtx();
        vm.OnActivated(ctx);

        View view = new();
        Ent nade = new("CHEGrenadeProjectile", 9);
        SetPos(nade, 100, 0, 0);
        view.Add(nade);
        ctx.Push(new Snap(1, view));
        SetPos(nade, 200, 0, 0);
        ctx.Push(new Snap(2, view));
        SetPos(nade, 300, 0, 0);
        ctx.Push(new Snap(3, view));
        await Assert.That(vm.GrenadeTrails.Single().Points.Count).IsEqualTo(3);

        // Step back one frame: the projectile is at its earlier position now.
        SetPos(nade, 200, 0, 0);
        ctx.Push(new Snap(2, view));

        GrenadeTrail trail = vm.GrenadeTrails.Single(); // still present (not pruned)
        await Assert.That(trail.Points.Count).IsEqualTo(3); // no backward point appended
        await Assert.That(trail.Points[^1].X).IsEqualTo(300f); // head unchanged — no kink back to 200
        await Assert.That(trail.Alpha).IsEqualTo(1.0); // held at full opacity
    }

    [Test]
    public async Task DiscontinuousJump_ClearsTrails_NoStreak()
    {
        Playback2DTabViewModel vm = new();
        Ctx ctx = NewCtx();
        vm.OnActivated(ctx);

        View view = new();
        Ent nade = new("CMolotovProjectile", 3);
        SetPos(nade, 100, 0, 0);
        view.Add(nade);

        ctx.Push(new Snap(1, view));
        SetPos(nade, 200, 0, 0);
        ctx.Push(new Snap(2, view));
        SetPos(nade, 300, 0, 0);
        ctx.Push(new Snap(3, view));
        await Assert.That(vm.GrenadeTrails.Count).IsEqualTo(1); // a real trail built up

        // Seek far forward (Δframe ≫ TrailJumpThreshold). The trail must be cleared so the next sample does
        // NOT connect the pre-seek arc (x≈100..300) to the post-seek position with a streak.
        SetPos(nade, 900, 0, 0);
        ctx.Push(new Snap(1000, view));
        await Assert.That(vm.GrenadeTrails.Count).IsEqualTo(0); // single post-jump point — no visible line yet

        SetPos(nade, 910, 0, 0);
        ctx.Push(new Snap(1001, view));
        GrenadeTrail trail = vm.GrenadeTrails.Single();
        await Assert.That(trail.Points.Count).IsEqualTo(2);
        await Assert.That(trail.Points[0].X).IsGreaterThanOrEqualTo(900f); // post-jump only — pre-jump arc gone
    }

    [Test]
    public async Task EachGrenadeKind_GetsItsOwnTrail()
    {
        Playback2DTabViewModel vm = new();
        Ctx ctx = NewCtx();
        vm.OnActivated(ctx);

        View view = new();
        Ent[] nades = new[]
        {
            new Ent("CHEGrenadeProjectile", 1), new Ent("CFlashbangProjectile", 2), new Ent("CSmokeGrenadeProjectile", 3), new Ent("CMolotovProjectile", 4), new Ent("CDecoyProjectile", 5)
        };
        for (int i = 0; i < nades.Length; i++)
        {
            SetPos(nades[i], 100 * (i + 1), 0, 0);
            view.Add(nades[i]);
        }

        ctx.Push(new Snap(1, view));
        for (int i = 0; i < nades.Length; i++)
        {
            SetPos(nades[i], 100 * (i + 1) + 30, 0, 0); // each moves → 2 points
        }

        ctx.Push(new Snap(2, view));

        await Assert.That(vm.GrenadeTrails.Count).IsEqualTo(5);
        GrenadeKind[] kinds = vm.GrenadeTrails.Select(t => t.Kind).OrderBy(k => k).ToArray();
        await Assert.That(kinds).IsEquivalentTo(new[]
        {
            GrenadeKind.He, GrenadeKind.Flash, GrenadeKind.Smoke, GrenadeKind.Molotov, GrenadeKind.Decoy
        });
    }

    // ── Builders ──

    private static Ctx NewCtx()
    {
        Ctx ctx = new();
        ctx.Roster.Add(new PlayerRosterEntry
        {
            Slot = 0,
            Name = "Neo",
            SteamId = 1
        });
        return ctx;
    }

    // Axis(32, off) = (32-32)*512 + off = off, so the world position equals the offset directly.
    private static void SetPos(Ent e, float x, float y, float z)
    {
        e.F["CBodyComponent.m_cellX"] = 32;
        e.F["CBodyComponent.m_cellY"] = 32;
        e.F["CBodyComponent.m_cellZ"] = 32;
        e.F["CBodyComponent.m_vecX"] = x;
        e.F["CBodyComponent.m_vecY"] = y;
        e.F["CBodyComponent.m_vecZ"] = z;
    }

    // ── Doubles ──

    private sealed class Ctx : IModuleContext
    {
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
        public IReadOnlyList<IPlayerState> CurrentPlayers { get; } = new List<IPlayerState>();
        public void Push(Snap snap) => Advanced?.Invoke(snap);
    }

    private sealed class Snap : IPlaybackSnapshot
    {
        public Snap(int frameIndex, IReadOnlyEntityView entities)
        {
            FrameIndex = frameIndex;
            Entities = entities;
        }

        public int FrameIndex { get; }
        public int Tick => FrameIndex * 64;
        public IReadOnlyEntityView Entities { get; }
        public IReadOnlyList<IPlayerState> Players { get; } = new List<IPlayerState>();
    }

    private sealed class Ent : IReadOnlyEntity
    {
        public Ent(string className, int serial)
        {
            ClassName = className;
            Serial = serial;
        }

        public Dictionary<string, object?> F { get; } = new();
        public string ClassName { get; }
        public int Serial { get; }
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
        private readonly List<IReadOnlyEntity> _ents = new();
        public IEnumerable<IReadOnlyEntity> All() => _ents;
        public IEnumerable<IReadOnlyEntity> OfClass(string className) => _ents.Where(e => e.ClassName == className);
        public IReadOnlyEntity? BySerial(int serial) => null;
        public IReadOnlyEntity? ByIndex(int entityIndex) => null;
        public IReadOnlyEntity? ResolveHandle(ulong handle) => null;
        public void Add(IReadOnlyEntity e) => _ents.Add(e);
        public void Remove(IReadOnlyEntity e) => _ents.Remove(e);
    }
}
