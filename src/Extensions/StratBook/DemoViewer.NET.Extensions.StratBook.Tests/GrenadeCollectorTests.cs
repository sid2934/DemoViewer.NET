#region

using System.Numerics;
using System.Reflection;
using System.Text.Json;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;
using CS2OpenSchema.Protos;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoProcessing.Collectors;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="GrenadeCollector" /> over synthetic frames: the view's projectile rows, pawn reads, commands and
///     events, streamed frame by frame through bounded rings, assemble the rows the walker's pure steps build
///     from the same samples, reads and commands in one go. The ducking read is the flags bit alone.
/// </summary>
public class GrenadeCollectorTests
{
    private const int Thrower = 3;
    private const int Joined = 5;
    private const int HeIndex = 120;
    private const int SmokeIndex = 130;
    private const int Frames = 420;

    private static readonly JsonSerializerOptions Json = new(GrenadeSidecar.JsonOptions) { WriteIndented = true };

    private static readonly PlayerInfo[] Players =
    [
        new(Thrower, "thrower", 76561198000000001UL, 12, 2, false),
        new(Joined, "joiner", 76561198000000005UL, 14, 3, false)
    ];

    // ── the scripted demo ────────────────────────────────────────────────────────────────────────────

    // Slot 3 throws an HE at tick 100 (release 93) running east, crouched at the release, jumping at 94. Slot 5
    // throws a smoke at tick 307 (release 300) whose thrower chain never resolves, standing still and walking.
    private static Script Demo(bool inPlace = false)
    {
        Script s = new(inPlace);
        for (int t = 0; t < Frames; t++)
        {
            s.Pawn(t, Thrower, new Vector3(-40 + (t - 85) * 5, 0, 0), -12.5f, 90f,
                flags: t == 93 ? 3u : t is >= 94 and <= 110 ? 0u : 1u, walking: false,
                weapon: t <= 100 ? "CHEGrenade" : "CAK47", strength: 1.0f);
            s.Pawn(t, Joined, new Vector3(500, 500, 0), 0f, 180f, flags: 1u, walking: true, weapon: "CSmokeGrenade", strength: 0.0f);
            s.Command(t, Thrower, t is >= 94 and <= 96 ? GrenadeRules.JumpButton : 0, subtickJump: false);
            s.Command(t, Joined, 0, subtickJump: t == 290);
        }

        s.Event(10, TestGameEvents.RoundFreezeEnd(frameNumber: 10, serverTick: 10, gameTick: 10));
        s.Event(90, TestGameEvents.RoundFreezeEnd(frameNumber: 90, serverTick: 90, gameTick: 90));
        s.Event(93, Fire(Thrower, 93, "weapon_hegrenade"));
        s.Event(204, Detonate(204, HeIndex, new Vector3(460, 0, 64)));
        s.Event(300, Fire(Joined, 300, "weapon_smokegrenade"));

        // The HE: created 100, bounces at 140 and 170, at rest from 190, removed on 204.
        int bounces = 0;
        for (int t = 100; t <= 203; t++)
        {
            if (t is 140 or 170)
            {
                bounces++;
            }

            float x = t <= 190 ? 10 + (t - 100) * 5 : 10 + 90 * 5;
            s.Projectile(t, HeIndex, 7, GrenadeProjectileClasses.HEGrenade, Thrower, new Vector3(x, 0, 64), bounces,
                created: t == 100, explode: t >= 200 ? 9000 : null);
        }

        s.Removed(204, HeIndex, 7, GrenadeProjectileClasses.HEGrenade, Thrower, new Vector3(460, 0, 64), bounces, explode: 9000);

        // The smoke: unresolved thrower, pops at 380 with its fields, still live at the end.
        for (int t = 307; t < Frames; t++)
        {
            float y = t <= 360 ? 500 + (t - 307) * 2 : 500 + 53 * 2;
            s.Projectile(t, SmokeIndex, 9, GrenadeProjectileClasses.Smoke, -1, new Vector3(500, y, 8), 0,
                created: t == 307, smokeTick: t >= 380 ? 9380 : 0, smokeAt: t >= 380 ? new Vector3(600, 600, 8) : null);
        }

        return s;
    }

    private static GameEvent Fire(int slot, int tick, string weapon) =>
        new("weapon_fire", -1, tick, tick, tick, new WeaponFireEvent { UserId = slot, UserIdPawn = 0, Weapon = weapon, Silenced = false });

    private static GameEvent Detonate(int tick, int entity, Vector3 p) =>
        new("hegrenade_detonate", -1, tick, tick, tick,
            new HegrenadeDetonateEvent { EntityId = (short)entity, UserId = Thrower, UserIdPawn = 0, X = p.X, Y = p.Y, Z = p.Z });

    private static GrenadeCollected Collect(Script script, int stride = 4)
    {
        GrenadeCollector collector = new(stride);
        for (int t = 0; t < Frames; t++)
        {
            collector.OnFrame(script.View(t));
        }

        Sink sink = new();
        collector.Finish(sink);
        return (GrenadeCollected)sink.Result!;
    }

    // The walker's own steps over the same script: samples folded, every pawn read kept, every command fed.
    private static GrenadeWalk Oracle(Script script, int stride = 4)
    {
        List<ProjectileTrack> tracks = ProjectileTrack.Collect(script.Samples(), stride);
        GrenadeEventIndex events = GrenadeEventIndex.From(script.Events);
        List<ThrowPlan> plans = GrenadeWalker.Plan(tracks, events, t => t < 0 ? -1 : Math.Min(t, Frames - 1));
        GrenadeReads reads = new();
        foreach (((int frame, int slot), PawnRead read) in script.Reads)
        {
            reads.Pawns[(frame, slot)] = read;
        }

        foreach (((int index, int serial), ProjectileRead read) in script.ProjectileReads)
        {
            reads.Projectiles[(index, serial)] = read;
        }

        ReconstructedInputSource inputs = new(plans.Where(p => p.ThrowerSlot >= 0)
            .Select(p => (p.ThrowerSlot, p.Track.SpawnTick - GrenadeRules.JumpWindowTicks, p.Track.SpawnTick)));
        foreach ((int tick, int slot, ulong buttons, bool subtick) in script.Commands)
        {
            inputs.Observe(slot, tick, buttons, subtick);
        }

        inputs.SetCoverage(1);
        List<GrenadeRow> rows = GrenadeWalker.Assemble(plans, reads, events, inputs, ClipRounds.Derive(script.Events), Players, 64, 0);
        return new GrenadeWalk(rows, 1, inputs.Decoder, stride);
    }

    private static string Serialize(IEnumerable<GrenadeRow> rows) =>
        JsonSerializer.Serialize(rows.Select(r => new { row = r, trajectory = r.Trajectory }), Json);

    // ── the rows ─────────────────────────────────────────────────────────────────────────────────────

    // In place: one instance per slot, changed under the collector between frames, as the lane applies deltas.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TheCollectedRows_EqualTheWalkersRows_OverTheSameFramesReadsAndCommands(bool inPlace)
    {
        Script script = Demo(inPlace);
        GrenadeCollected collected = Collect(script);
        GrenadeWalk walk = GrenadeWalker.Assemble(collected, Players, 64, 0);
        GrenadeWalk oracle = Oracle(script);

        GrenadeRow he = walk.Rows.Single(r => r.Kind == GrenadeKind.He);
        GrenadeRow smoke = walk.Rows.Single(r => r.Kind == GrenadeKind.Smoke);
        using (Assert.Multiple())
        {
            await Assert.That(walk.Rows.Count).IsEqualTo(2);
            await Assert.That(Serialize(walk.Rows)).IsEqualTo(Serialize(oracle.Rows));
            await Assert.That(walk.InputCoverage).IsEqualTo(1).Because("every frame carried a payload and advanced a slot");
            await Assert.That(walk.InputDecoder).IsEqualTo(GrenadeCollector.InputDecoderName);
            await Assert.That(walk.TrajectoryStride).IsEqualTo(4);

            await Assert.That(he.ThrowerSteamId64).IsEqualTo("76561198000000001");
            await Assert.That(he.ThrowerSource).IsEqualTo(ThrowerSource.Entity);
            await Assert.That(he.RoundNumber).IsEqualTo(2);
            await Assert.That(he.ReleaseTick).IsEqualTo(93);
            await Assert.That(he.ReleaseCrouched).IsEqualTo(true).Because("the flags bit at the release frame");
            await Assert.That(he.ReleaseOnGround).IsEqualTo(true);
            await Assert.That(he.ThrowStrengthClass).IsEqualTo(ThrowStrengthClass.Full);
            await Assert.That(he.SpeedAtRelease).IsEqualTo(320f);
            await Assert.That(he.Movement).IsEqualTo(MovementClass.Running);
            await Assert.That(he.ThrowerOnGroundAtSpawn).IsEqualTo(false);
            await Assert.That(he.JumpThrow).IsTrue();
            await Assert.That(he.JumpThrowSource).IsEqualTo(JumpThrowSource.Inputs);
            await Assert.That(he.JumpPressTick).IsEqualTo(94);
            await Assert.That(he.DetonationTick).IsEqualTo(204);
            await Assert.That(he.DetonationSource).IsEqualTo(DetonationSource.Event);
            await Assert.That(he.BounceCount).IsEqualTo(2);
            await Assert.That(he.EndKind).IsEqualTo(GrenadeEndKind.Detonated);

            await Assert.That(smoke.ThrowerSlot).IsEqualTo(Joined).Because("the plan joined the unresolved smoke to the slot that fired one");
            await Assert.That(smoke.ThrowerSource).IsEqualTo(ThrowerSource.WeaponFire);
            await Assert.That(smoke.ThrowerSteamId64).IsEqualTo("76561198000000005");
            await Assert.That(smoke.ReleaseTick).IsEqualTo(300);
            await Assert.That(smoke.ReleasePosition).IsEqualTo(new WorldPoint(500, 500, 0)).Because("every slot's reads were kept while the thrower was unknown");
            await Assert.That(smoke.Movement).IsEqualTo(MovementClass.Stationary);
            await Assert.That(smoke.ThrowStrengthClass).IsEqualTo(ThrowStrengthClass.Underhand);
            await Assert.That(smoke.JumpThrow).IsTrue().Because("the sub-tick press at 290 sits inside the window");
            await Assert.That(smoke.JumpPressTick).IsEqualTo(290);
            await Assert.That(smoke.DetonationSource).IsEqualTo(DetonationSource.Entity).Because("no event: the pop fields read on the last frame decide");
            await Assert.That(smoke.DetonationTick).IsEqualTo(9380);
            await Assert.That(smoke.DetonationPosition).IsEqualTo(new WorldPoint(600, 600, 8));
            await Assert.That(smoke.EndKind).IsEqualTo(GrenadeEndKind.Detonated);
        }
    }

    [Test]
    public async Task TheRingsKeepOnlyTheLookBack_AndAnUnresolvedThrowerKeepsEverySlot()
    {
        Script script = Demo();
        GrenadeCollected collected = Collect(script);

        // Below the smoke's window: the unresolved smoke keeps every slot's ring at 307, slot 3's included.
        List<int> heFrames = [.. collected.Reads.Pawns.Keys.Where(k => k.Slot == Thrower && k.Frame < 200).Select(k => k.Frame).Order()];
        int[] slotsAtSmoke = [.. collected.Reads.Pawns.Keys.Where(k => k.Frame == 307).Select(k => k.Slot).Order()];
        using (Assert.Multiple())
        {
            await Assert.That(heFrames.Max()).IsEqualTo(100).Because("nothing after the spawn is a read the plan can name");
            await Assert.That(heFrames.Min()).IsGreaterThanOrEqualTo(100 - GrenadeRules.ReleaseLookbackTicks - GrenadeRules.SpeedWindowTicks - 1)
                .Because("the pawn ring holds the look-back plus the one frame before it");
            await Assert.That(heFrames.Count).IsLessThanOrEqualTo(GrenadeRules.ReleaseLookbackTicks + GrenadeRules.SpeedWindowTicks + 2);
            await Assert.That(collected.Reads.Pawns.Keys.Count(k => k.Slot == Joined && k.Frame <= 100)).IsEqualTo(0)
                .Because("a resolved thrower keeps its own slot alone");
            await Assert.That(slotsAtSmoke).IsEquivalentTo([Thrower, Joined]);
            await Assert.That(collected.Inputs.HasCoverage(Thrower, 100 - GrenadeRules.JumpWindowTicks - 1, 100 - GrenadeRules.JumpWindowTicks - 1)).IsFalse()
                .Because("a command before the jump window is dropped");
            await Assert.That(collected.Inputs.HasCoverage(Thrower, 100 - GrenadeRules.JumpWindowTicks, 100)).IsTrue();
        }
    }

    [Test]
    public async Task Ducking_IsTheFlagsBit_AndUnknownWithoutFlags()
    {
        Script script = new();
        for (int t = 0; t < Frames; t++)
        {
            script.Pawn(t, Thrower, new Vector3(0, 0, 0), 0f, 0f, flags: t == 93 ? 2u : t == 85 ? null : 1u, walking: false,
                weapon: "CHEGrenade", strength: 1f);
        }

        script.Event(93, Fire(Thrower, 93, "weapon_hegrenade"));
        script.Projectile(100, HeIndex, 7, GrenadeProjectileClasses.HEGrenade, Thrower, new Vector3(0, 0, 64), 0, created: true);
        script.Removed(101, HeIndex, 7, GrenadeProjectileClasses.HEGrenade, Thrower, new Vector3(0, 0, 64), 0);

        GrenadeCollected collected = Collect(script);
        PawnRead release = collected.Reads.Pawns[(93, Thrower)];
        PawnRead before = collected.Reads.Pawns[(85, Thrower)];
        PawnRead spawn = collected.Reads.Pawns[(100, Thrower)];
        using (Assert.Multiple())
        {
            await Assert.That(release.Ducked).IsEqualTo(true);
            await Assert.That(release.OnGround).IsEqualTo(false);
            await Assert.That(spawn.Ducked).IsEqualTo(false);
            await Assert.That(spawn.OnGround).IsEqualTo(true);
            await Assert.That(before.Flags).IsNull();
            await Assert.That(before.Ducked).IsNull().Because("no fallback read: without the flags the crouch is unknown");
            await Assert.That(before.OnGround).IsNull();
        }
    }

    [Test]
    public async Task TheContribution_DeclaresEveryClassThePawnReadsTheCommandsAndTheEvents()
    {
        int asked = 0;
        CollectorContribution contribution = GrenadeCollector.Contribution(() =>
        {
            asked++;
            return 2;
        });
        IFrameCollector made = contribution.Create();

        using (Assert.Multiple())
        {
            await Assert.That(contribution.Id).IsEqualTo(GrenadeCollector.CollectorId);
            await Assert.That(contribution.PassId).IsEqualTo(GrenadeIndexEvaluator.EvaluatorId);
            await Assert.That(contribution.ProjectileClasses).IsEquivalentTo(GrenadeProjectileClasses.All);
            await Assert.That(contribution.Providers).IsEquivalentTo([
                "entity.pawn.pos_x", "entity.pawn.pos_y", "entity.pawn.pos_z", "entity.pawn.eye_pitch", "entity.pawn.eye_yaw",
                "entity.pawn.is_walking", "entity.pawn.flags", "entity.weapon.throw_strength", "entity.pawn.active_weapon_class"
            ]);
            await Assert.That(contribution.ReadsUserCommands).IsTrue();
            await Assert.That(contribution.GameEvents).Contains("weapon_fire");
            await Assert.That(contribution.GameEvents).Contains("decoy_started");
            await Assert.That(contribution.GameEvents).Contains("round_freeze_end");
            await Assert.That(contribution.Stamp).Contains(";u=1");
            await Assert.That(RegisteredCollector.Refusal(contribution)).IsNull().Because("the engine registers every read");
            await Assert.That(made).IsTypeOf<GrenadeCollector>();
            await Assert.That(asked).IsEqualTo(1).Because("the stride is read when the run's collector is made");
        }
    }

    // ── the fakes ────────────────────────────────────────────────────────────────────────────────────

    private sealed class Sink : ICollectorSink
    {
        public object? Result { get; private set; }

        public void Put<T>(T result) where T : class => Result = result;
    }

    // One frame per tick. Frame events go into the frame's message list, as the parser puts them. With inPlace
    // each slot has one command instance that View(tick) rewrites to that tick's inputs, the client tick moving on.
    private sealed class Script(bool inPlace = false)
    {
        private static readonly PropertyInfo MessageList = typeof(DemoFrame).GetProperty("MessageList", BindingFlags.Instance | BindingFlags.NonPublic)
                                                           ?? throw new MissingMemberException(nameof(DemoFrame), "MessageList");

        // Only the count is read: it says the frame carried command payloads, which the coverage divides by.
        private static readonly PropertyInfo UserCmdsCount = typeof(DemoFrame).GetProperty("UserCmdsCount", BindingFlags.Instance | BindingFlags.NonPublic)
                                                             ?? throw new MissingMemberException(nameof(DemoFrame), "UserCmdsCount");

        private readonly Dictionary<int, Dictionary<(string Provider, int Slot), object>> _values = [];
        private readonly Dictionary<int, Dictionary<int, CSGOUserCmdPB>> _commands = [];
        private readonly Dictionary<int, Dictionary<int, (ulong Buttons, bool Subtick)>> _inputs = [];
        private readonly Dictionary<int, CSGOUserCmdPB> _shared = [];
        private readonly Dictionary<int, List<GameEvent>> _events = [];
        private readonly Dictionary<int, List<ObservedProjectile>> _created = [];
        private readonly Dictionary<int, List<ObservedProjectile>> _live = [];
        private readonly Dictionary<int, List<ObservedProjectile>> _removed = [];
        private readonly List<ProjectileSample> _samples = [];

        public Dictionary<(int Frame, int Slot), PawnRead> Reads { get; } = [];
        public Dictionary<(int Index, int Serial), ProjectileRead> ProjectileReads { get; } = [];
        public List<(int Tick, int Slot, ulong Buttons, bool Subtick)> Commands { get; } = [];
        public List<GameEvent> Events { get; } = [];

        public void Pawn(int tick, int slot, Vector3 at, float pitch, float yaw, uint? flags, bool walking, string weapon, float strength)
        {
            Dictionary<(string, int), object> values = _values.TryGetValue(tick, out Dictionary<(string, int), object>? v) ? v : _values[tick] = [];
            values[("entity.pawn.pos_x", slot)] = at.X;
            values[("entity.pawn.pos_y", slot)] = at.Y;
            values[("entity.pawn.pos_z", slot)] = at.Z;
            values[("entity.pawn.eye_pitch", slot)] = pitch;
            values[("entity.pawn.eye_yaw", slot)] = yaw;
            if (flags is { } f)
            {
                values[("entity.pawn.flags", slot)] = (int)f;
            }

            values[("entity.pawn.is_walking", slot)] = walking ? 1 : 0;
            values[("entity.pawn.active_weapon_class", slot)] = weapon;
            values[("entity.weapon.throw_strength", slot)] = strength;
            bool held = GrenadeRules.IsGrenadeWeaponClass(weapon);
            Reads[(tick, slot)] = new PawnRead(tick, at, pitch, yaw, flags, flags is { } b ? (b & GrenadeRules.DuckingFlag) != 0 : null,
                walking, slot == Thrower ? 2 : 3, held ? strength : null, held);
        }

        public void Command(int tick, int slot, ulong buttons, bool subtickJump)
        {
            Commands.Add((tick, slot, buttons, subtickJump));
            if (inPlace)
            {
                (_inputs.TryGetValue(tick, out Dictionary<int, (ulong, bool)>? i) ? i : _inputs[tick] = [])[slot] = (buttons, subtickJump);
                return;
            }

            CSGOUserCmdPB command = new() { Base = new CBaseUserCmdPB { ButtonsPb = new CInButtonStatePB { Buttonstate1 = buttons } } };
            if (subtickJump)
            {
                command.Base.SubtickMoves.Add(new CSubtickMoveStep { Button = GrenadeRules.JumpButton, Pressed = true });
            }

            (_commands.TryGetValue(tick, out Dictionary<int, CSGOUserCmdPB>? c) ? c : _commands[tick] = [])[slot] = command;
        }

        private Dictionary<int, CSGOUserCmdPB> CommandsAt(int tick)
        {
            if (!inPlace)
            {
                return _commands.GetValueOrDefault(tick) ?? [];
            }

            Dictionary<int, CSGOUserCmdPB> at = [];
            foreach ((int slot, (ulong buttons, bool subtick)) in _inputs.GetValueOrDefault(tick) ?? [])
            {
                if (!_shared.TryGetValue(slot, out CSGOUserCmdPB? command))
                {
                    _shared[slot] = command = new CSGOUserCmdPB { Base = new CBaseUserCmdPB { ButtonsPb = new CInButtonStatePB() } };
                }

                command.Base.ClientTick = tick;
                command.Base.ButtonsPb.Buttonstate1 = buttons;
                command.Base.SubtickMoves.Clear();
                if (subtick)
                {
                    command.Base.SubtickMoves.Add(new CSubtickMoveStep { Button = GrenadeRules.JumpButton, Pressed = true });
                }

                at[slot] = command;
            }

            return at;
        }

        public void Event(int tick, GameEvent e)
        {
            (_events.TryGetValue(tick, out List<GameEvent>? list) ? list : _events[tick] = []).Add(e);
            Events.Add(e);
        }

        public void Projectile(int tick, int index, int serial, string cls, int thrower, Vector3 at, int bounces, bool created,
            int? explode = null, int smokeTick = 0, Vector3? smokeAt = null)
        {
            ObservedProjectile row = new(index, serial, cls, thrower, 2, at, created ? new Vector3(0, 0, 64) : null,
                created ? new Vector3(500, 0, 100) : null, bounces, smokeTick, smokeAt, null) { ExplodeEffectTickBegin = explode };
            (created ? Add(_created, tick) : Add(_live, tick)).Add(row);
            if (created)
            {
                Add(_live, tick).Add(row);
            }

            _samples.Add(new ProjectileSample(tick, tick, index, serial, cls, thrower, at, row.InitialPosition, row.InitialVelocity, bounces, created, false));
            ProjectileReads[(index, serial)] = new ProjectileRead(null, explode, smokeTick > 0 ? smokeTick : null, smokeAt);
        }

        public void Removed(int tick, int index, int serial, string cls, int thrower, Vector3 lastAt, int bounces, int? explode = null)
        {
            Add(_removed, tick).Add(new ObservedProjectile(index, serial, cls, thrower, 2, lastAt, null, null, bounces, 0, null, null)
                { ExplodeEffectTickBegin = explode });
            _samples.Add(new ProjectileSample(tick, tick, index, serial, cls, thrower, lastAt, null, null, bounces, false, true));
        }

        public IEnumerable<ProjectileSample> Samples() => _samples.OrderBy(s => s.FrameIndex).ThenBy(s => s.EntityIndex).ThenByDescending(s => s.Removed);

        public View View(int tick)
        {
            DemoFrame frame = new()
            {
                CommandKind = EDemoCommands.DemPacket, FrameNumber = tick, ServerTick = tick, GameTick = tick,
                HeaderLength = 0, RawLength = 0, RawStart = 0, IsCompressed = false
            };
            if (_events.TryGetValue(tick, out List<GameEvent>? events))
            {
                List<NetMessage> messages = (List<NetMessage>)MessageList.GetValue(frame)!;
                messages.AddRange(events.Select(GameEventMessage.ForSynthesizedEvent));
            }

            Dictionary<int, CSGOUserCmdPB> commands = CommandsAt(tick);
            if (commands.Count > 0)
            {
                UserCmdsCount.SetValue(frame, 1);
            }

            return new View(frame, tick,
                _values.GetValueOrDefault(tick) ?? [],
                commands,
                _created.GetValueOrDefault(tick) ?? [],
                _live.GetValueOrDefault(tick) ?? [],
                _removed.GetValueOrDefault(tick) ?? []);
        }

        private static List<ObservedProjectile> Add(Dictionary<int, List<ObservedProjectile>> map, int tick) =>
            map.TryGetValue(tick, out List<ObservedProjectile>? list) ? list : map[tick] = [];
    }

    private sealed class View(
        DemoFrame frame,
        int tick,
        Dictionary<(string Provider, int Slot), object> values,
        Dictionary<int, CSGOUserCmdPB> commands,
        List<ObservedProjectile> created,
        List<ObservedProjectile> live,
        List<ObservedProjectile> removed) : IFrameReadView
    {
        private static readonly string[] Declared = [.. GrenadeCollector.Providers];

        public DemoFrame Frame => frame;

        public int FrameIndex => tick;

        public int Tick => tick;

        public IReadOnlyList<ObservedProjectile> Projectiles => live;

        public IReadOnlyList<ObservedProjectile> Created => created;

        public IReadOnlyList<ObservedProjectile> Removed => removed;

        public ObservedRead Provider(string providerName) =>
            Array.IndexOf(Declared, providerName) is var i and >= 0 ? new ObservedRead(providerName, i) : throw new ArgumentException(providerName);

        public object? Read(string providerName, int playerSlot) => values.GetValueOrDefault((providerName, playerSlot));

        public bool TryReadInt(ObservedRead provider, int playerSlot, out int value)
        {
            bool found = values.TryGetValue((provider.Name, playerSlot), out object? v);
            value = found ? (int)v! : 0;
            return found;
        }

        public bool TryReadFloat(ObservedRead provider, int playerSlot, out float value)
        {
            bool found = values.TryGetValue((provider.Name, playerSlot), out object? v);
            value = found ? (float)v! : 0;
            return found;
        }

        public bool TryReadString(ObservedRead provider, int playerSlot, out string? value)
        {
            bool found = values.TryGetValue((provider.Name, playerSlot), out object? v);
            value = found ? (string)v! : null;
            return found;
        }

        public int Team(int playerSlot) => playerSlot == Thrower ? 2 : playerSlot == Joined ? 3 : -1;

        public CSGOUserCmdPB? Command(int playerSlot) => commands.GetValueOrDefault(playerSlot);
    }
}
