#region

using System.Numerics;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A4 grenade area effects: active smoke clouds (<c>CSmokeGrenadeProjectile</c>, once
///     <c>m_nSmokeEffectTickBegin &gt; 0</c>) become one big smoke disc at <c>m_vSmokeDetonationPos</c>, and
///     each BURNING inferno cell (<c>CInferno.m_firePositions[i]</c> where <c>m_bFireIsBurning[i]</c>) becomes
///     a small fire disc. A still-flying smoke projectile and non-burning cells are excluded. Field
///     keys/types were probe-confirmed on a real demo.
/// </summary>
[NotInParallel]
public class Playback2DAreaEffectsTests
{
    [Test]
    public async Task ActiveSmokes_AndBurningInfernoCells_BecomeAreaEffects()
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

        View view = new();
        view.Add(Smoke(100, 370, -1058, -389)); // detonated → an area effect
        view.Add(Smoke(0, 999, 999, 0)); // still flying → ignored

        Ent inferno = new("CInferno");
        inferno.F["m_fireCount"] = 3;
        AddCell(inferno, 0, true, 36f, -2212f, -413f);
        AddCell(inferno, 1, false, 5f, 5f, 0f); // not burning → excluded
        AddCell(inferno, 2, true, -46f, -2226f, -416f);
        view.Add(inferno);

        ctx.Push(new FakeSnap(1, view));

        await Assert.That(vm.AreaEffects.Count(a => a.Kind == AreaEffectKind.Smoke)).IsEqualTo(1);
        await Assert.That(vm.AreaEffects.Count(a => a.Kind == AreaEffectKind.Fire)).IsEqualTo(2);

        AreaEffect smoke = vm.AreaEffects.Single(a => a.Kind == AreaEffectKind.Smoke);
        await Assert.That(smoke.WorldX).IsEqualTo(370f);
        await Assert.That(smoke.WorldY).IsEqualTo(-1058f);
        await Assert.That(smoke.WorldRadius).IsGreaterThan(100f); // ~144 standard smoke radius

        // Only the burning cells (0 and 2) — not the unburning middle one.
        await Assert.That(vm.AreaEffects.Any(a => a.Kind == AreaEffectKind.Fire && a.WorldX == 36f)).IsTrue();
        await Assert.That(vm.AreaEffects.Any(a => a.Kind == AreaEffectKind.Fire && a.WorldX == -46f)).IsTrue();
        await Assert.That(vm.AreaEffects.Any(a => a.Kind == AreaEffectKind.Fire && a.WorldX == 5f)).IsFalse();
    }

    [Test]
    public async Task NoGrenades_YieldNoAreaEffects()
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

        ctx.Push(new FakeSnap(1, new View()));
        await Assert.That(vm.AreaEffects.Count).IsEqualTo(0);
    }

    // ── Builders ──

    private static Ent Smoke(int tickBegin, float x, float y, float z)
    {
        Ent e = new("CSmokeGrenadeProjectile");
        e.F["m_nSmokeEffectTickBegin"] = tickBegin;
        e.F["m_vSmokeDetonationPos"] = new Vector3(x, y, z);
        return e;
    }

    private static void AddCell(Ent inferno, int i, bool burning, float x, float y, float z)
    {
        inferno.F[$"m_bFireIsBurning[{i}]"] = burning ? 1 : 0;
        inferno.F[$"m_firePositions[{i}]"] = new Vector3(x, y, z);
    }

    // ── Doubles ──

    private sealed class FakeCtx : IModuleContext
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
        public void Push(FakeSnap snap) => Advanced?.Invoke(snap);
    }

    private sealed class FakeSnap : IPlaybackSnapshot
    {
        public FakeSnap(int frameIndex, IReadOnlyEntityView entities)
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
        public Ent(string className) => ClassName = className;
        public Dictionary<string, object?> F { get; } = new();
        public string ClassName { get; }
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
        private readonly List<IReadOnlyEntity> _ents = new();
        public IEnumerable<IReadOnlyEntity> All() => _ents;
        public IEnumerable<IReadOnlyEntity> OfClass(string className) => _ents.Where(e => e.ClassName == className);
        public IReadOnlyEntity? BySerial(int serial) => null;
        public IReadOnlyEntity? ByIndex(int entityIndex) => null;
        public IReadOnlyEntity? ResolveHandle(ulong handle) => null;
        public void Add(IReadOnlyEntity e) => _ents.Add(e);
    }
}
