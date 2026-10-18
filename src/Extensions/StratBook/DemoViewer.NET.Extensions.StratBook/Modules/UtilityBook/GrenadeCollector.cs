#region

using System.Numerics;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;
using CS2OpenSchema.Protos;
using DemoViewer.NET.Extensions.Sdk;
using Google.Protobuf.Collections;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;

/// <summary>
///     The grenade walk on the rules run: the projectile lifecycle, the throwers' pawns and commands and the
///     grenade events, read frame by frame through the observer instead of a projectile walk and a tracker
///     pass of their own. Puts a <see cref="GrenadeCollected" />, which <see cref="GrenadeWalker.Assemble(GrenadeCollected, IEnumerable{PlayerInfo}, int, int)" />
///     turns into rows once the pass adds the roster.
///     <para>
///         Pawn and command reads are kept in rings bounded to the look-back the plan can reach behind a
///         spawn; when a projectile appears, the ring entries its thrower could need are copied out and the
///         ring moves on. The thrower is unknown at the spawn when the chain has not resolved, so then every
///         slot's entries are kept and the plan picks among them.
///     </para>
/// </summary>
public sealed class GrenadeCollector : IFrameCollector
{
    /// <summary>The collector's id, which the grenade pass reads the collection by.</summary>
    public const string CollectorId = "stratbook.grenades.walk";

    /// <summary>Bumped when what it puts changes.</summary>
    public const int Version = 1;

    private const int Slots = 64;

    // The release may sit ReleaseLookbackTicks before the spawn and the speed read SpeedWindowTicks before that.
    private const int PawnWindowTicks = GrenadeRules.ReleaseLookbackTicks + GrenadeRules.SpeedWindowTicks;

    private const string PosX = "entity.pawn.pos_x";
    private const string PosY = "entity.pawn.pos_y";
    private const string PosZ = "entity.pawn.pos_z";
    private const string EyePitch = "entity.pawn.eye_pitch";
    private const string EyeYaw = "entity.pawn.eye_yaw";
    private const string Walking = "entity.pawn.is_walking";
    private const string Flags = "entity.pawn.flags";
    private const string ThrowStrength = "entity.weapon.throw_strength";
    private const string WeaponClass = "entity.pawn.active_weapon_class";

    /// <summary>The pawn reads, the walker's <c>ReadPawn</c> as provider columns.</summary>
    public static IReadOnlyList<string> Providers { get; } =
        [PosX, PosY, PosZ, EyePitch, EyeYaw, Walking, Flags, ThrowStrength, WeaponClass];

    /// <summary>The events the plan and the detonation join read, plus the round starts.</summary>
    public static IReadOnlyList<string> Events { get; } =
    [
        "weapon_fire", "grenade_thrown", GrenadeEventIndex.HeDetonate, GrenadeEventIndex.FlashDetonate,
        GrenadeEventIndex.SmokeDetonate, GrenadeEventIndex.InfernoStart, GrenadeEventIndex.DecoyStarted, "round_freeze_end"
    ];

    private readonly int _stride;
    private readonly List<(int Index, int Tick)> _frames = [];
    private readonly List<(int Frame, PawnRead Read)>?[] _pawns = new List<(int, PawnRead)>?[Slots];
    private readonly List<CommandSeen>?[] _commands = new List<CommandSeen>?[Slots];
    private readonly CSGOUserCmdPB?[] _lastCommand = new CSGOUserCmdPB?[Slots];
    private readonly int[] _lastClientTick = new int[Slots];
    private readonly ulong[] _lastButtons = new ulong[Slots];
    private readonly List<GameEvent> _events = [];
    private readonly List<ProjectileTrack> _tracks = [];
    private readonly Dictionary<(int Index, int Serial), Live> _live = [];
    private readonly List<Kept> _kept = [];
    private readonly List<int> _read = new(Slots);
    private Handles? _handles;
    private int _framesWithPayloads;
    private int _framesWithCommands;

    /// <param name="trajectoryStride">Keep every n-th moved sample of each flight.</param>
    public GrenadeCollector(int trajectoryStride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(trajectoryStride, 1);
        _stride = trajectoryStride;
    }

    /// <summary>The registration: every projectile class, the pawn reads, the commands and the events.</summary>
    /// <param name="trajectoryStride">The live stride setting, read when a run starts.</param>
    public static CollectorContribution Contribution(Func<int> trajectoryStride)
    {
        ArgumentNullException.ThrowIfNull(trajectoryStride);
        return new CollectorContribution(CollectorId, GrenadeIndexEvaluator.EvaluatorId, Version, Providers,
            GrenadeProjectileClasses.All, () => new GrenadeCollector(Math.Max(1, trajectoryStride())))
        {
            ReadsUserCommands = true,
            GameEvents = Events
        };
    }

    /// <inheritdoc />
    public void OnFrame(IFrameReadView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        int frame = view.FrameIndex;
        int tick = view.Frame.ServerTick;
        Handles h = _handles ??= new Handles(view);

        _frames.Add((frame, tick));
        Trim(_frames, static e => e.Tick, tick - PawnWindowTicks, true);

        _read.Clear();
        for (int slot = 0; slot < Slots; slot++)
        {
            if (!view.TryReadFloat(h.X, slot, out float x))
            {
                continue;
            }

            _read.Add(slot);
            Vector3? position = view.TryReadFloat(h.Y, slot, out float y) && view.TryReadFloat(h.Z, slot, out float z)
                ? new Vector3(x, y, z)
                : null;
            uint? flags = view.TryReadInt(h.Flags, slot, out int f) ? (uint)f : null;
            bool held = view.TryReadString(h.Weapon, slot, out string? weapon) && GrenadeRules.IsGrenadeWeaponClass(weapon);
            int team = view.Team(slot);
            PawnRead read = new(tick,
                position,
                view.TryReadFloat(h.Pitch, slot, out float pitch) ? pitch : null,
                view.TryReadFloat(h.Yaw, slot, out float yaw) ? yaw : null,
                flags,
                flags is { } bits ? (bits & GrenadeRules.DuckingFlag) != 0 : null,
                view.TryReadInt(h.Walking, slot, out int walking) ? walking != 0 : null,
                team > 0 ? team : 0,
                held && view.TryReadFloat(h.Strength, slot, out float strength) ? strength : null,
                held);
            List<(int Frame, PawnRead Read)> ring = _pawns[slot] ??= [];
            ring.Add((frame, read));
            Trim(ring, static e => e.Read.Tick, tick - PawnWindowTicks, true);
        }

        ReadCommands(view);
        ReadEvents(view);
        ReadProjectiles(view, frame, tick);
    }

    /// <inheritdoc />
    public void Finish(ICollectorSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        foreach (Live live in _live.Values)
        {
            _kept.Add(live.Kept);
        }

        _live.Clear();

        GrenadeEventIndex events = GrenadeEventIndex.From(_events);
        IReadOnlyList<ClipRound> rounds = ClipRounds.Derive(_events);
        (int Index, int Tick)[] frames = [.. _kept.SelectMany(k => k.Frames).Distinct().OrderBy(f => f.Index)];
        List<ThrowPlan> plans = GrenadeWalker.Plan(_tracks, events, t => FrameAtOrBefore(frames, t));

        GrenadeReads reads = new();
        LaneInputSource inputs = new(_framesWithPayloads == 0 ? 0 : (double)_framesWithCommands / _framesWithPayloads);
        foreach (Kept kept in _kept)
        {
            foreach (((int f, int slot), PawnRead read) in kept.Pawns)
            {
                reads.Pawns.TryAdd((f, slot), read);
            }

            foreach ((int slot, List<CommandSeen> seen) in kept.Commands)
            {
                inputs.Add(slot, seen);
            }

            reads.Projectiles[kept.Key] = kept.Projectile;
        }

        inputs.Seal();
        sink.Put(new GrenadeCollected(plans, reads, events, inputs, rounds, _stride));
    }

    private void ReadCommands(IFrameReadView view)
    {
        bool any = false;
        foreach (int slot in _read)
        {
            CSGOUserCmdPB? command = view.Command(slot);
            if (command is null)
            {
                continue;
            }

            // The lane applies a delta to the slot's instance in place, so the same reference with a
            // new client tick is a new command; a keyframe is a new instance.
            CBaseUserCmdPB? cmd = command.Base;
            int clientTick = cmd?.ClientTick ?? 0;
            if (ReferenceEquals(command, _lastCommand[slot]) && clientTick == _lastClientTick[slot])
            {
                continue;
            }

            _lastCommand[slot] = command;
            _lastClientTick[slot] = clientTick;
            any = true;
            ulong buttons = cmd?.ButtonsPb?.Buttonstate1 ?? 0;
            bool subtick = false;
            if (cmd is not null)
            {
                // Indexed: the repeated field's enumerator is an allocation per command.
                RepeatedField<CSubtickMoveStep> moves = cmd.SubtickMoves;
                for (int i = 0; i < moves.Count; i++)
                {
                    CSubtickMoveStep step = moves[i];
                    if (step.Button == GrenadeRules.JumpButton && step.Pressed)
                    {
                        subtick = true;
                        break;
                    }
                }
            }

            ulong previous = _lastButtons[slot];
            _lastButtons[slot] = buttons;
            bool rose = subtick || ((buttons & GrenadeRules.JumpButton) != 0 && (previous & GrenadeRules.JumpButton) == 0);
            List<CommandSeen> ring = _commands[slot] ??= [];
            ring.Add(new CommandSeen(view.FrameIndex, view.Tick, rose));
            Trim(ring, static e => e.Tick, view.Tick - GrenadeRules.JumpWindowTicks, false);
        }

        if (view.Frame.UserCmdsPayloadCount > 0)
        {
            _framesWithPayloads++;
            if (any)
            {
                _framesWithCommands++;
            }
        }
    }

    private void ReadEvents(IFrameReadView view)
    {
        foreach (NetMessage message in view.Frame.DecodedMessages)
        {
            if (message is not GameEventMessage { DecodedEvent: { } e })
            {
                continue;
            }

            switch (e.Payload)
            {
                case WeaponFireEvent fire when GrenadeRules.ProjectileClassOfWeapon(fire.Weapon) is not null:
                case GrenadeThrownEvent:
                case HegrenadeDetonateEvent:
                case FlashbangDetonateEvent:
                case SmokeGrenadeDetonateEvent:
                case InfernoStartburnEvent:
                case DecoyStartedEvent:
                case RoundFreezeEndEvent:
                    _events.Add(e);
                    break;
            }
        }
    }

    // Rings first, then projectiles: the spawn frame's pawn read is one the plan asks for.
    private void ReadProjectiles(IFrameReadView view, int frame, int tick)
    {
        foreach (ObservedProjectile row in view.Removed)
        {
            if (_live.Remove((row.EntityIndex, row.Serial), out Live? live))
            {
                live.Track.Observe(Sample(row, frame, tick, false, true));
                _kept.Add(live.Kept);
            }
        }

        IReadOnlyList<ObservedProjectile> created = view.Created;
        foreach (ObservedProjectile row in created.Count > 1 ? [.. created.OrderBy(r => r.EntityIndex)] : created)
        {
            (int, int) key = (row.EntityIndex, row.Serial);
            if (_live.ContainsKey(key))
            {
                continue;
            }

            ProjectileTrack track = new(Sample(row, frame, tick, true, false), _stride);
            _tracks.Add(track);
            Live live = new(track, new Kept(key));
            _live[key] = live;
            Read(live, row);
            Keep(live.Kept, row.ThrowerSlot);
        }

        foreach (ObservedProjectile row in view.Projectiles)
        {
            if (!_live.TryGetValue((row.EntityIndex, row.Serial), out Live? live) || live.Track.SpawnFrame == frame)
            {
                continue;
            }

            live.Track.Observe(Sample(row, frame, tick, false, false));
            Read(live, row);
        }
    }

    // The walker reads the incendiary flag on the spawn frame and the effect fields on the last one the
    // projectile existed; a flag never changes, so the first value seen is the spawn's when it had one.
    private static void Read(Live live, ObservedProjectile row)
    {
        ProjectileRead current = live.Kept.Projectile;
        live.Kept.Projectile = new ProjectileRead(
            current.IsIncendiary ?? row.IsIncendiary,
            row.ExplodeEffectTickBegin,
            row.SmokeEffectTickBegin > 0 ? row.SmokeEffectTickBegin : null,
            row.SmokeDetonationPosition);
    }

    private void Keep(Kept kept, int thrower)
    {
        kept.Frames.AddRange(_frames);
        if (thrower >= 0)
        {
            Keep(kept, thrower, _pawns[thrower], _commands[thrower]);
            return;
        }

        for (int slot = 0; slot < Slots; slot++)
        {
            Keep(kept, slot, _pawns[slot], _commands[slot]);
        }
    }

    private static void Keep(Kept kept, int slot, List<(int Frame, PawnRead Read)>? pawns, List<CommandSeen>? commands)
    {
        if (pawns is not null)
        {
            foreach ((int frame, PawnRead read) in pawns)
            {
                kept.Pawns[(frame, slot)] = read;
            }
        }

        if (commands is { Count: > 0 })
        {
            kept.Commands[slot] = [.. commands];
        }
    }

    private static ProjectileSample Sample(ObservedProjectile row, int frame, int tick, bool created, bool removed) =>
        new(frame, tick, row.EntityIndex, row.Serial, row.ClassName, row.ThrowerSlot, row.Position, row.InitialPosition,
            row.InitialVelocity, row.Bounces, created, removed);

    // Drops the front while the entries behind it are old enough on their own; with keepOlder the newest entry
    // below the floor stays, as the frame at or before a tick inside the window can be that one.
    private static void Trim<T>(List<T> ring, Func<T, int> tick, int floor, bool keepOlder)
    {
        int drop = 0;
        if (keepOlder)
        {
            while (drop + 1 < ring.Count && tick(ring[drop + 1]) < floor)
            {
                drop++;
            }
        }
        else
        {
            while (drop < ring.Count && tick(ring[drop]) < floor)
            {
                drop++;
            }
        }

        if (drop > 0)
        {
            ring.RemoveRange(0, drop);
        }
    }

    private static int FrameAtOrBefore((int Index, int Tick)[] frames, int tick)
    {
        int lo = 0, hi = frames.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (frames[mid].Tick <= tick)
            {
                found = frames[mid].Index;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    private readonly record struct CommandSeen(int Frame, int Tick, bool JumpRose);

    private sealed class Handles(IFrameReadView view)
    {
        public ObservedRead X { get; } = view.Provider(PosX);
        public ObservedRead Y { get; } = view.Provider(PosY);
        public ObservedRead Z { get; } = view.Provider(PosZ);
        public ObservedRead Pitch { get; } = view.Provider(EyePitch);
        public ObservedRead Yaw { get; } = view.Provider(EyeYaw);
        public ObservedRead Walking { get; } = view.Provider(GrenadeCollector.Walking);
        public ObservedRead Flags { get; } = view.Provider(GrenadeCollector.Flags);
        public ObservedRead Strength { get; } = view.Provider(ThrowStrength);
        public ObservedRead Weapon { get; } = view.Provider(WeaponClass);
    }

    // What one projectile's row assembly can read: the frames and pawn reads behind its spawn, its thrower
    // candidates' commands in the jump window, and its own fields as last seen.
    private sealed class Kept((int Index, int Serial) key)
    {
        public (int Index, int Serial) Key => key;
        public List<(int Index, int Tick)> Frames { get; } = [];
        public Dictionary<(int Frame, int Slot), PawnRead> Pawns { get; } = [];
        public Dictionary<int, List<CommandSeen>> Commands { get; } = [];
        public ProjectileRead Projectile { get; set; }
    }

    private sealed class Live(ProjectileTrack track, Kept kept)
    {
        public ProjectileTrack Track => track;
        public Kept Kept => kept;
    }

    /// <summary>
    ///     The throwers' inputs as the run's command lane showed them: one command per slot per frame, the
    ///     last of the frame, at the frame's tick. Coverage is the share of frames carrying command payloads
    ///     on which the lane advanced a slot.
    /// </summary>
    private sealed class LaneInputSource(double coverage) : IThrowerInputSource
    {
        /// <summary>The header's decoder name for this source.</summary>
        public const string DecoderName = "frame-view-command";

        private readonly Dictionary<int, Dictionary<int, CommandSeen>> _bySlot = [];
        private Dictionary<int, CommandSeen[]>? _sealed;

        public string Decoder => DecoderName;

        public double Coverage => coverage;

        public bool HasCoverage(int slot, int fromTick, int toTick) =>
            _sealed!.TryGetValue(slot, out CommandSeen[]? seen) && seen.Any(c => c.Tick >= fromTick && c.Tick <= toTick);

        public int? FindJumpPress(int slot, int fromTick, int toTick)
        {
            if (!_sealed!.TryGetValue(slot, out CommandSeen[]? seen))
            {
                return null;
            }

            int? latest = null;
            foreach (CommandSeen c in seen)
            {
                if (c.JumpRose && c.Tick >= fromTick && c.Tick <= toTick && (latest is null || c.Tick > latest))
                {
                    latest = c.Tick;
                }
            }

            return latest;
        }

        public void Add(int slot, List<CommandSeen> seen)
        {
            if (!_bySlot.TryGetValue(slot, out Dictionary<int, CommandSeen>? byFrame))
            {
                _bySlot[slot] = byFrame = [];
            }

            foreach (CommandSeen c in seen)
            {
                byFrame[c.Frame] = c;
            }
        }

        public void Seal() => _sealed = _bySlot.ToDictionary(kv => kv.Key, kv => kv.Value.Values.OrderBy(c => c.Frame).ToArray());
    }

    /// <summary>The lane input source's header name, for the tests and the sidecar.</summary>
    public static string InputDecoderName => LaneInputSource.DecoderName;
}

/// <summary>
///     What <see cref="GrenadeCollector" /> put: the plans and everything <see cref="GrenadeWalker.Assemble(GrenadeCollected, IEnumerable{PlayerInfo}, int, int)" />
///     reads but the roster, which the pass adds.
/// </summary>
/// <param name="Plans">The throwers and releases, in spawn order.</param>
/// <param name="Reads">The pawn and projectile reads the plans name.</param>
/// <param name="Events">The demo's grenade events.</param>
/// <param name="Inputs">The throwers' inputs.</param>
/// <param name="Rounds">Round starts, frame clock.</param>
/// <param name="TrajectoryStride">The stride the trajectories were thinned at.</param>
public sealed record GrenadeCollected(
    IReadOnlyList<ThrowPlan> Plans,
    GrenadeReads Reads,
    GrenadeEventIndex Events,
    IThrowerInputSource Inputs,
    IReadOnlyList<ClipRound> Rounds,
    int TrajectoryStride);
