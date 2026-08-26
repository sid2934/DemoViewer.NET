#region

using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>An in-memory entity: a class name, a serial, and a bag of networked field values.</summary>
internal sealed class FakeEntity : IReadOnlyEntity
{
    public FakeEntity(string className, int serial = 1)
    {
        ClassName = className;
        Serial = serial;
    }

    public Dictionary<string, object?> Fields { get; } = [];
    public string ClassName { get; }
    public int Serial { get; }
    public bool IsInPvs => true;
    public object? this[string fieldPath] => Fields.GetValueOrDefault(fieldPath);

    public bool TryGet<T>(string fieldPath, out T value)
    {
        if (Fields.TryGetValue(fieldPath, out object? v) && v is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Sets a field and returns this, so a fake reads as one expression.</summary>
    /// <param name="path">The flattened field path.</param>
    /// <param name="value">The networked value.</param>
    public FakeEntity With(string path, object? value)
    {
        Fields[path] = value;
        return this;
    }

    /// <summary>
    ///     Sets the CBodyComponent cell + offset coordinates that reconstruct to a world position.
    ///     Mirrors the wire encoding: <c>world = (cell − 32) × 512 + offset</c>.
    /// </summary>
    /// <param name="x">Target world X.</param>
    /// <param name="y">Target world Y.</param>
    /// <param name="z">Target world Z.</param>
    public FakeEntity AtWorld(float x, float y, float z)
    {
        SetAxis("X", x);
        SetAxis("Y", y);
        SetAxis("Z", z);
        return this;

        void SetAxis(string axis, float world)
        {
            // Keep the cell fixed at the origin cell so the offset carries the whole value; the builder
            // reconstructs through PositionUtil.Axis, which is the formula under test elsewhere.
            Fields[$"CBodyComponent.m_cell{axis}"] = (ushort)32;
            Fields[$"CBodyComponent.m_vec{axis}"] = world;
        }
    }
}

/// <summary>
///     An in-memory entity view over a fixed set of entities.
///     <para>
///         <c>OfClass</c> hands back a cached per-class array, and an empty array for a class the view
///         does not hold. That is not premature tidiness: the builder calls <c>OfClass</c> ten times per
///         frame, so a <c>Where</c> iterator per call would allocate more than the builder does and make
///         the steady-state allocation gate measure the fake instead of the code under test. An empty
///         array's enumerator is a cached singleton, so absent classes cost nothing at all.
///     </para>
/// </summary>
internal sealed class FakeEntityView : IReadOnlyEntityView
{
    private static readonly IReadOnlyEntity[] _none = [];

    private readonly Dictionary<string, List<IReadOnlyEntity>> _byClass = new(StringComparer.Ordinal);
    private readonly List<IReadOnlyEntity> _entities = [];
    private readonly Dictionary<ulong, IReadOnlyEntity> _byHandle = [];
    private Dictionary<string, IReadOnlyEntity[]>? _frozen;

    public IEnumerable<IReadOnlyEntity> All() => _entities;

    public IEnumerable<IReadOnlyEntity> OfClass(string className)
    {
        _frozen ??= _byClass.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
        return _frozen.TryGetValue(className, out IReadOnlyEntity[]? entities) ? entities : _none;
    }

    public IReadOnlyEntity? BySerial(int serial) => _entities.FirstOrDefault(e => e.Serial == serial);
    public IReadOnlyEntity? ByIndex(int entityIndex) => null;

    // Only the handles a test explicitly seated resolve. The real view decodes an entity index out of the
    // handle's low bits; reproducing that here would test the fake's arithmetic, not the builder's reads.
    public IReadOnlyEntity? ResolveHandle(ulong handle) => _byHandle.GetValueOrDefault(handle);

    /// <summary>Seats an entity at a handle, so a one-hop read (the active weapon) resolves.</summary>
    /// <param name="handle">The handle value a field will carry.</param>
    /// <param name="entity">What it points at. Also added to the view.</param>
    public FakeEntityView AddHandle(ulong handle, IReadOnlyEntity entity)
    {
        _byHandle[handle] = entity;
        return Add(entity);
    }

    /// <summary>Adds an entity and returns this, so a view reads as one expression.</summary>
    /// <param name="entity">The entity to add.</param>
    public FakeEntityView Add(IReadOnlyEntity entity)
    {
        _entities.Add(entity);
        if (!_byClass.TryGetValue(entity.ClassName, out List<IReadOnlyEntity>? bucket))
        {
            bucket = [];
            _byClass[entity.ClassName] = bucket;
        }

        bucket.Add(entity);
        _frozen = null;
        return this;
    }
}

/// <summary>An in-memory player state.</summary>
internal sealed class FakePlayer : IPlayerState
{
    public int Slot { get; init; }
    public int Team { get; init; } = 2;
    public bool HasLivePawn { get; init; } = true;
    public IReadOnlyEntity? Pawn { get; init; }
    public IReadOnlyEntity? Controller { get; init; }
    public (float X, float Y, float Z)? WorldPosition { get; init; }
}
