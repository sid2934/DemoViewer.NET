#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Deterministic (synthetic, no demo) coverage of the ADR / match-damage panel stat. Verifies the
///     wiring the arithmetic depends on: <c>UpdateGameInfo</c> caches <c>m_totalRoundsPlayed</c> ONCE per
///     frame BEFORE the per-player <c>UpdateAttributes</c> loop reads it (the ordering invariant), ADR =
///     round(total damage / rounds played), the opening-round floor (0 completed → divide by 1, not zero),
///     in-place recompute across frames, and the null-controller fallback to "—".
///     <para>
///         The real <c>m_pActionTrackingServices.m_iDamage</c> / <c>m_totalRoundsPlayed</c> field paths are
///         already probe-confirmed on real data and exercised by the real-demo render tests; this isolates
///         the new computation so a regression in the formula or the once-per-frame ordering is caught fast.
///     </para>
/// </summary>
[NotInParallel]
public class Playback2DAdrTests
{
    [Test]
    public async Task Adr_IsDamageOverRoundsPlayed_FlooredAtOneRound_RecomputedEachFrame()
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

        // Frame 1: 4 rounds completed (5th in progress), 500 total damage → ADR = round(500/4) = 125.
        ctx.Push(Snap(1, 4, Player(0, 2, 500)));
        PlayerAttributes row = vm.Attributes.Single(a => a.Slot == 0);
        await Assert.That(row.Damage).IsEqualTo("500");
        await Assert.That(row.Adr).IsEqualTo("125");

        // Frame 2: in-place recompute — 5 rounds done, 700 damage → round(700/5) = 140 (NOT a stale 125).
        ctx.Push(Snap(2, 5, Player(0, 2, 700)));
        await Assert.That(row.Damage).IsEqualTo("700");
        await Assert.That(row.Adr).IsEqualTo("140");

        // Frame 3: opening round (0 completed). Denominator floors to 1 → ADR == damage, never a divide-by-0.
        ctx.Push(Snap(3, 0, Player(0, 2, 73)));
        await Assert.That(row.Damage).IsEqualTo("73");
        await Assert.That(row.Adr).IsEqualTo("73");
    }

    [Test]
    public async Task Adr_FallsBackToDash_WhenControllerMissing()
    {
        Playback2DTabViewModel vm = new();
        FakeCtx ctx = new();
        ctx.Roster.Add(new PlayerRosterEntry
        {
            Slot = 0,
            Name = "Trinity",
            SteamId = 2
        });
        vm.OnActivated(ctx);

        // A roster slot present this frame but with no live controller (pre-spawn / disconnect) renders
        // placeholders, never crashes, never shows a bogus 0 ADR off a null controller.
        ctx.Push(Snap(1, 3, new AttrPlayer(0, 2, null, null)));
        PlayerAttributes row = vm.Attributes.Single(a => a.Slot == 0);
        await Assert.That(row.Damage).IsEqualTo("—");
        await Assert.That(row.Adr).IsEqualTo("—");
    }

    // ── Builders ──

    private static AttrPlayer Player(int slot, int team, int damage)
    {
        Ent pawn = new("CCSPlayerPawn");
        pawn.F["m_iHealth"] = 100;
        pawn.F["m_lifeState"] = 0;
        Ent ctrl = new("CCSPlayerController");
        ctrl.F["m_pActionTrackingServices.m_iDamage"] = damage;
        return new AttrPlayer(slot, team, pawn, ctrl);
    }

    private static FakeSnap Snap(int frameIndex, int roundsPlayed, params IPlayerState[] players)
    {
        Ent rules = new("CCSGameRulesProxy");
        rules.F["m_pGameRules.m_totalRoundsPlayed"] = roundsPlayed;
        return new FakeSnap(frameIndex, players, new View(rules));
    }

    // ── Minimal doubles (a controller-bearing player + a view that yields the game-rules entity) ──

    private sealed class AttrPlayer(int slot, int team, Ent? pawn, Ent? ctrl) : IPlayerState
    {
        public int Slot => slot;
        public int Team => team;
        public bool HasLivePawn => pawn is not null;
        public IReadOnlyEntity? Pawn => pawn;
        public IReadOnlyEntity? Controller => ctrl;

        // Attributes update independently of position; a fixed spot keeps a marker drawable when alive.
        public (float X, float Y, float Z)? WorldPosition => pawn is null ? null : (0f, 0f, 0f);
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
        public IReadOnlyEntityView Entities { get; private set; } = new View(null);

        public IReadOnlyList<PlayerRosterEntry> Players => Roster;
        public IReadOnlyList<IPlayerState> CurrentPlayers => _current;

        public void Push(FakeSnap snap)
        {
            _current.Clear();
            _current.AddRange(snap.Players);
            Entities = snap.Entities;
            Advanced?.Invoke(snap);
        }
    }

    private sealed class FakeSnap(int frameIndex, IReadOnlyList<IPlayerState> players, IReadOnlyEntityView view)
        : IPlaybackSnapshot
    {
        public int FrameIndex => frameIndex;
        public int Tick => frameIndex * 64;
        public IReadOnlyEntityView Entities { get; } = view;
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

    private sealed class View(Ent? rules) : IReadOnlyEntityView
    {
        public IEnumerable<IReadOnlyEntity> All() => rules is null
            ? Array.Empty<IReadOnlyEntity>()
            : new[]
            {
                rules
            };

        public IEnumerable<IReadOnlyEntity> OfClass(string className) =>
            rules is not null && className == rules.ClassName
                ? new[]
                {
                    rules
                }
                : Array.Empty<IReadOnlyEntity>();

        public IReadOnlyEntity? BySerial(int serial) => null;
        public IReadOnlyEntity? ByIndex(int entityIndex) => null;
        public IReadOnlyEntity? ResolveHandle(ulong handle) => null;
    }
}
