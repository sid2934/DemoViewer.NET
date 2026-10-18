#region

using System.Collections;
using System.Numerics;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

/// <summary>
///     The round index's position samples, read on the rules run instead of a <see cref="PositionSampler.Walk" />
///     of its own. Puts a <see cref="RoundIndexSamples" />: every controller-bound pawn on every
///     <see cref="RoundIndexOptions.FrameStride" />th frame, in pawn entity-index order, the walk's sample set
///     and order, so the fold over it writes the walk's bytes.
/// </summary>
public sealed class RoundIndexCollector : IFrameCollector
{
    /// <summary>The collector's id, which the round index pass reads the samples by.</summary>
    public const string CollectorId = "stratbook.roundindex.positions";

    /// <summary>Bumped when the samples it puts change.</summary>
    public const int Version = 1;

    // Controller slots are entity index minus one, and controllers sit at entity indices 1 to 64.
    private const int Slots = 64;

    private const string PosX = "entity.pawn.pos_x";
    private const string PosY = "entity.pawn.pos_y";
    private const string PosZ = "entity.pawn.pos_z";
    private const string Place = "entity.pawn.place";
    private const string Alive = "entity.pawn.is_alive";
    private const string EntityIndex = "entity.pawn.entity_index";

    private readonly List<(int Index, int Slot)> _order = new(Slots);
    private readonly SampleStore _samples = new();
    private readonly int _stride;
    private Handles? _handles;
    private int _lastFrameTick;

    /// <param name="stride">Every how many frames to sample.</param>
    public RoundIndexCollector(int stride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, 1);
        _stride = stride;
    }

    /// <summary>The registration: the pawn reads the walk's samples are made of, at the shipped stride.</summary>
    public static CollectorContribution Contribution { get; } =
        new(CollectorId, RoundIndexEvaluator.EvaluatorId, Version, [PosX, PosY, PosZ, Place, Alive, EntityIndex], [],
            () => new RoundIndexCollector(RoundIndexOptions.Default.FrameStride));

    /// <inheritdoc />
    public void OnFrame(IFrameReadView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _lastFrameTick = view.Tick;
        if (view.FrameIndex % _stride != 0)
        {
            return;
        }

        Handles h = _handles ??= new Handles(view);
        _order.Clear();
        for (int slot = 0; slot < Slots; slot++)
        {
            if (view.TryReadFloat(h.X, slot, out _))
            {
                _order.Add((view.TryReadInt(h.Index, slot, out int index) ? index : int.MaxValue, slot));
            }
        }

        // The walk visits pawns by entity index; a fold sums floats in arrival order.
        _order.Sort();
        int tick = view.Frame.ServerTick;
        _samples.BeginFrame(view.FrameIndex, tick);
        foreach ((_, int slot) in _order)
        {
            if (!view.TryReadFloat(h.X, slot, out float x) || !view.TryReadFloat(h.Y, slot, out float y)
                                                          || !view.TryReadFloat(h.Z, slot, out float z))
            {
                continue;
            }

            view.TryReadString(h.Place, slot, out string? place);
            bool alive = view.TryReadInt(h.Alive, slot, out int a) && a != 0;
            _samples.Add(slot, new Vector3(x, y, z), place, view.Team(slot), alive);
        }
    }

    /// <inheritdoc />
    public void Finish(ICollectorSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.Put(new RoundIndexSamples(_samples.Seal(), _lastFrameTick));
    }

    private sealed class Handles(IFrameReadView view)
    {
        public ObservedRead X { get; } = view.Provider(PosX);
        public ObservedRead Y { get; } = view.Provider(PosY);
        public ObservedRead Z { get; } = view.Provider(PosZ);
        public ObservedRead Place { get; } = view.Provider(RoundIndexCollector.Place);
        public ObservedRead Alive { get; } = view.Provider(RoundIndexCollector.Alive);
        public ObservedRead Index { get; } = view.Provider(EntityIndex);
    }
}

/// <summary>
///     The collector's samples, held for the whole rules run in chunks under the large object threshold, so
///     growth never copies or abandons an array.
/// </summary>
internal sealed class SampleStore : IReadOnlyList<PositionSample>
{
    // Every chunk stays under the 85,000-byte large object threshold: 3072 rows of 24 bytes, 3072 frames of 12.
    private const int ChunkSize = 3072;

    private readonly List<Row[]> _rows = [];
    private readonly List<Frame[]> _frames = [];
    private readonly Dictionary<string, int> _placeIds = new(StringComparer.Ordinal);

    // Id 0 is a failed place read; "" is an unplaced pawn and gets an id of its own.
    private readonly List<string?> _places = [null];
    private int _frameCount;

    public int Count { get; private set; }

    public PositionSample this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            int lo = 0, hi = _frameCount - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (FrameAt(mid).First <= index)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return Sample(FrameAt(lo), _rows[index / ChunkSize][index % ChunkSize]);
        }
    }

    public void BeginFrame(int frameIndex, int tick)
    {
        Frame frame = new(frameIndex, tick, Count);
        if (_frameCount > 0 && FrameAt(_frameCount - 1).First == Count)
        {
            // A frame with no rows is overwritten: the indexer needs strictly increasing frame starts.
            _frames[(_frameCount - 1) / ChunkSize][(_frameCount - 1) % ChunkSize] = frame;
            return;
        }

        if (_frameCount % ChunkSize == 0)
        {
            _frames.Add(new Frame[ChunkSize]);
        }

        _frames[^1][_frameCount % ChunkSize] = frame;
        _frameCount++;
    }

    public void Add(int slot, Vector3 position, string? place, int team, bool alive)
    {
        if (Count % ChunkSize == 0)
        {
            _rows.Add(new Row[ChunkSize]);
        }

        _rows[^1][Count % ChunkSize] = new Row(position, PlaceId(place), team, (byte)slot, alive);
        Count++;
    }

    public SampleStore Seal()
    {
        if (_frameCount > 0 && FrameAt(_frameCount - 1).First == Count)
        {
            _frameCount--;
        }

        return this;
    }

    public IEnumerator<PositionSample> GetEnumerator()
    {
        int at = 0;
        for (int f = 0; f < _frameCount; f++)
        {
            Frame frame = FrameAt(f);
            int end = f + 1 < _frameCount ? FrameAt(f + 1).First : Count;
            for (; at < end; at++)
            {
                yield return Sample(frame, _rows[at / ChunkSize][at % ChunkSize]);
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private Frame FrameAt(int index) => _frames[index / ChunkSize][index % ChunkSize];

    private PositionSample Sample(Frame frame, Row row) =>
        new(frame.FrameIndex, frame.Tick, row.Slot, row.Position, _places[row.Place], row.Team, row.Alive);

    private int PlaceId(string? place)
    {
        if (place is null)
        {
            return 0;
        }

        if (!_placeIds.TryGetValue(place, out int id))
        {
            id = _places.Count;
            _places.Add(place);
            _placeIds[place] = id;
        }

        return id;
    }

    private readonly record struct Frame(int FrameIndex, int Tick, int First);

    // 24 bytes: the slot is 0 to 63; place and team are kept whole so no read narrows.
    private readonly record struct Row(Vector3 Position, int Place, int Team, byte Slot, bool Alive);
}

/// <summary>What <see cref="RoundIndexCollector" /> read off one demo's rules run.</summary>
/// <param name="Samples">The position samples in frame order, pawns in entity-index order within a frame.</param>
/// <param name="LastFrameTick">The last frame's frame clock; a round with no end runs to one past it.</param>
public sealed record RoundIndexSamples(IReadOnlyList<PositionSample> Samples, int LastFrameTick);
