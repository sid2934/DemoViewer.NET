#region

using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Parser;
using CS2OpenSchema.Protos;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing.Collectors;

/// <summary>
///     The one <see cref="IFrameObserver" /> a run takes, fanned out to every collector of its
///     <see cref="CollectorSet" />. The engine aborts a run whose observer throws, so each collector's throw is
///     caught here, kept as that collector's result and reported to its extension; the others and the run go on.
/// </summary>
internal sealed class CollectorObserver : IFrameObserver
{
    private readonly Slot[] _slots;
    private readonly ReadView _view;
    private bool _finished;

    public CollectorObserver(CollectorSet set)
    {
        _slots = [.. set.All.Select(c => new Slot(c))];
        _view = new ReadView(set.Providers, set.ReadsUserCommands);
    }

    /// <summary>What each collector put, or why it failed. Complete after <see cref="Finish" />.</summary>
    public CollectorResults Results { get; } = new();

    public void OnFrame(FrameView frame)
    {
        _view.Bind(frame);
        foreach (Slot slot in _slots)
        {
            if (slot.Failure is not null)
            {
                continue;
            }

            try
            {
                slot.Collector ??= slot.Registered.Contribution.Create();
                slot.Collector.OnFrame(_view);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Fail(slot, ex, "frame");
            }
        }
    }

    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        foreach (Slot slot in _slots)
        {
            if (slot.Failure is not null)
            {
                Results.Fail(slot.Registered.Id, slot.Failure);
                continue;
            }

            try
            {
                slot.Collector ??= slot.Registered.Contribution.Create();
                Sink sink = new();
                slot.Collector.Finish(sink);
                Results.Put(slot.Registered.Id, sink.Result);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Fail(slot, ex, "finish");
                Results.Fail(slot.Registered.Id, ex);
            }
        }
    }

    private static void Fail(Slot slot, Exception ex, string where)
    {
        slot.Failure = ex;
        slot.Registered.Guard?.Report("collector " + slot.Registered.Id + " " + where, ex);
    }

    private sealed class Slot(RegisteredCollector registered)
    {
        public RegisteredCollector Registered { get; } = registered;
        public IFrameCollector? Collector { get; set; }
        public Exception? Failure { get; set; }
    }

    private sealed class Sink : ICollectorSink
    {
        public object? Result { get; private set; }

        public void Put<T>(T result) where T : class => Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    // One per run. Provider handles resolve against the engine view on first use, and again when the engine
    // hands a different view, so a handle never reads another run's columns.
    private sealed class ReadView(IReadOnlyList<string> declared, bool commands) : IFrameReadView
    {
        private readonly string[] _declared = [.. declared];
        private readonly Dictionary<string, int> _indexOf = Index(declared);
        private ObservedProvider?[] _handles = [];
        private FrameView? _frame;
        private ObservedProjectile[]? _projectiles;
        private ObservedProjectile[]? _created;
        private ObservedProjectile[]? _removed;

        public DemoFrame Frame => View.Frame;

        public int FrameIndex => View.FrameIndex;

        public int Tick => View.Tick;

        public IReadOnlyList<ObservedProjectile> Projectiles => _projectiles ??= Copy(View.Projectiles);

        public IReadOnlyList<ObservedProjectile> Created => _created ??= Copy(View.Created);

        public IReadOnlyList<ObservedProjectile> Removed => _removed ??= Copy(View.Removed);

        private FrameView View => _frame ?? throw new InvalidOperationException("the view is readable only inside OnFrame");

        public void Bind(FrameView frame)
        {
            if (!ReferenceEquals(frame, _frame))
            {
                _handles = new ObservedProvider?[_declared.Length];
            }

            _frame = frame;
            _projectiles = _created = _removed = null;
        }

        public ObservedRead Provider(string providerName) =>
            _indexOf.TryGetValue(providerName, out int index)
                ? new ObservedRead(_declared[index], index)
                : throw new ArgumentException($"provider '{providerName}' is not declared by any collector on this run", nameof(providerName));

        public object? Read(string providerName, int playerSlot) => View.Read(Handle(Provider(providerName)), playerSlot);

        public bool TryReadInt(ObservedRead provider, int playerSlot, out int value) => View.TryReadInt(Handle(provider), playerSlot, out value);

        public bool TryReadFloat(ObservedRead provider, int playerSlot, out float value) => View.TryReadFloat(Handle(provider), playerSlot, out value);

        public bool TryReadString(ObservedRead provider, int playerSlot, out string? value) => View.TryReadString(Handle(provider), playerSlot, out value);

        public int Team(int playerSlot) => View.Team(playerSlot);

        // Declared, not merely available: a rule reading player.cmd.* would build the lane for a run the
        // collector did not ask for, and its stamp would not say so.
        public CSGOUserCmdPB? Command(int playerSlot) =>
            commands
                ? View.Command(playerSlot)
                : throw new InvalidOperationException("no collector on this run declared ReadsUserCommands");

        private ObservedProvider Handle(ObservedRead provider)
        {
            if ((uint)provider.Index >= (uint)_declared.Length || !string.Equals(_declared[provider.Index], provider.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"'{provider.Name}' was not resolved by this view", nameof(provider));
            }

            return _handles[provider.Index] ??= View.Provider(_declared[provider.Index]);
        }

        private static Dictionary<string, int> Index(IReadOnlyList<string> declared)
        {
            Dictionary<string, int> index = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < declared.Count; i++)
            {
                index.TryAdd(declared[i], i);
            }

            return index;
        }

        private static ObservedProjectile[] Copy(ReadOnlySpan<ProjectileRow> rows)
        {
            if (rows.IsEmpty)
            {
                return [];
            }

            ObservedProjectile[] copy = new ObservedProjectile[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                ProjectileRow r = rows[i];
                copy[i] = new ObservedProjectile(r.EntityIndex, r.Serial, r.ClassName, r.ThrowerSlot, r.Team, r.Position,
                    r.InitialPosition, r.InitialVelocity, r.Bounces, r.SmokeEffectTickBegin, r.SmokeDetonationPosition, r.IsIncendiary);
            }

            return copy;
        }
    }
}

/// <summary>Each collector's result from one run, by collector id.</summary>
internal sealed class CollectorResults
{
    private readonly Dictionary<string, (object? Result, Exception? Failure)> _byId = new(StringComparer.Ordinal);

    public static CollectorResults None { get; } = new();

    public bool Has(string collectorId) => _byId.ContainsKey(collectorId);

    public void Put(string collectorId, object? result) => _byId[collectorId] = (result, null);

    public void Fail(string collectorId, Exception failure) => _byId[collectorId] = (null, failure);

    /// <summary>What <paramref name="collectorId" /> put. Throws its failure, or when it was not on the run.</summary>
    public T? Get<T>(string collectorId) where T : class
    {
        if (!_byId.TryGetValue(collectorId, out (object? Result, Exception? Failure) entry))
        {
            throw new InvalidOperationException($"collector {collectorId} did not run on this demo's rules run");
        }

        if (entry.Failure is { } failure)
        {
            throw new InvalidOperationException($"collector {collectorId} failed on this demo", failure);
        }

        return entry.Result switch
        {
            null => null,
            T typed => typed,
            _ => throw new InvalidCastException($"collector {collectorId} put a {entry.Result.GetType().Name}, not a {typeof(T).Name}")
        };
    }
}
