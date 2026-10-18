#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Modules;

/// <summary>
///     One open's <c>clockBase</c> (see <see cref="GameClock" />), read off the open's rules run instead of a
///     replay of its own. <see cref="Providers" /> wraps the engine's freeze-period poll, the one game-rules read
///     every run makes, so the run's own scan settles the clock when it folds the first <c>round_freeze_end</c>
///     frame. <see cref="Resolve" /> replays only when nothing settled it.
/// </summary>
internal sealed class GameClockSource
{
    private readonly int _frame;
    private readonly int _serverTick;
    private readonly int _tickRate;
    private readonly object _gate = new();
    private IReadOnlyList<DemoFrame>? _frames;
    private double _clockBase;
    private bool _settled;

    public GameClockSource(IReadOnlyList<DemoFrame> frames, int firstFreezeEndFrame, int tickRate)
    {
        _frame = firstFreezeEndFrame;
        _tickRate = tickRate;
        if (frames.Count == 0 || firstFreezeEndFrame < 0 || firstFreezeEndFrame >= frames.Count || tickRate <= 0)
        {
            // GameClock's own answer for a demo it cannot calibrate, with no replay.
            _settled = true;
            return;
        }

        _frames = frames;
        _serverTick = frames[firstFreezeEndFrame].ServerTick;
    }

    /// <summary>The clock base the run settled, or null while it has not.</summary>
    public double? Settled
    {
        get
        {
            lock (_gate)
            {
                return _settled ? _clockBase : null;
            }
        }
    }

    /// <summary>The clock base, replaying to the first freeze end when no run settled it.</summary>
    public double Resolve()
    {
        IReadOnlyList<DemoFrame>? frames;
        lock (_gate)
        {
            if (_settled)
            {
                return _clockBase;
            }

            frames = _frames!;
        }

        (double clockBase, bool _) = GameClock.ComputeClockBase(frames, _frame, _tickRate);
        Settle(clockBase);
        return clockBase;
    }

    /// <summary>
    ///     The default singleton providers with the freeze-period poll wrapped to settle this clock, or null when
    ///     the clock is settled or the engine has no freeze-period poll to wrap.
    /// </summary>
    public EntityValueProviderRegistry? Providers()
    {
        if (Settled is not null)
        {
            return null;
        }

        EntityValueProviderRegistry registry = EntityValueProviderRegistry.CreateDefault();
        if (registry.Get(FreezePeriodContext) is not { } freezePeriod)
        {
            return null;
        }

        registry.Register(new SettlingProvider(freezePeriod, this));
        return registry;
    }

    private const string FreezePeriodContext = "entity.game.freeze_period";

    private void Settle(double clockBase)
    {
        lock (_gate)
        {
            if (_settled)
            {
                return;
            }

            _clockBase = clockBase;
            _settled = true;
            _frames = null;
        }
    }

    // A scan folds every frame of a tick before it reads, so its tracker can stand past the freeze end frame.
    // The state is the replay's only when the frames between carry no messages for the tracker to apply.
    private void Observe(EntityTracker tracker, int at)
    {
        IReadOnlyList<DemoFrame>? frames;
        lock (_gate)
        {
            frames = _frames;
        }

        if (frames is null || at >= frames.Count)
        {
            return;
        }

        for (int i = _frame + 1; i <= at; i++)
        {
            if (!Inert(frames[i]))
            {
                return;
            }
        }

        (double clockBase, bool _) = GameClock.Read(tracker, _serverTick, _tickRate);
        Settle(clockBase);
    }

    // EntityTracker applies only these payloads, and resyncs from a full packet.
    private static bool Inert(DemoFrame frame)
    {
        if (frame.CommandKind == EDemoCommands.DemFullPacket)
        {
            return false;
        }

        foreach (NetMessage message in frame.DecodedMessages)
        {
            if (message.Payload is CDemoSendTables or CDemoClassInfo or CSVCMsg_ClassInfo or CSVCMsg_ServerInfo
                or CSVCMsg_FlattenedSerializer or CSVCMsg_CreateStringTable or CSVCMsg_UpdateStringTable
                or CSVCMsg_PacketEntities)
            {
                return false;
            }
        }

        return true;
    }

    // Reads exactly what it wraps; settling the clock is its only addition. One per scan worker, each judging
    // the first frame it reads at or past the freeze end.
    private sealed class SettlingProvider(IEntityValueProvider inner, GameClockSource clock)
        : IEntityValueProvider, IWorkerCloneable<IEntityValueProvider>
    {
        private bool _passed;

        public string ContextName => inner.ContextName;
        public object? DefaultValue => inner.DefaultValue;
        public ChangeDirection EmitOn => inner.EmitOn;
        public string EntityClass => inner.EntityClass;
        public string FieldName => inner.FieldName;
        public Type MarkerType => inner.MarkerType;
        public Type ValueType => inner.ValueType;

        public object? Read(EntityStateLayer layer)
        {
            object? value = inner.Read(layer);
            if (!_passed && layer.Tracker.CurrentFrameIndex >= clock._frame)
            {
                _passed = true;
                clock.Observe(layer.Tracker, layer.Tracker.CurrentFrameIndex);
            }

            return value;
        }

        // A worker clone reads its own copy of the inner provider and settles the same clock.
        public IEntityValueProvider CloneForWorker() =>
            new SettlingProvider(
                inner is IWorkerCloneable<IEntityValueProvider> cloneable
                    ? cloneable.CloneForWorker()
                    : (IEntityValueProvider)Activator.CreateInstance(inner.GetType())!,
                clock);
    }
}
