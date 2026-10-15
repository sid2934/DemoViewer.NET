#region

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
    private readonly List<PositionSample> _samples = [];
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
        foreach ((_, int slot) in _order)
        {
            if (!view.TryReadFloat(h.X, slot, out float x) || !view.TryReadFloat(h.Y, slot, out float y)
                                                          || !view.TryReadFloat(h.Z, slot, out float z))
            {
                continue;
            }

            view.TryReadString(h.Place, slot, out string? place);
            bool alive = view.TryReadInt(h.Alive, slot, out int a) && a != 0;
            _samples.Add(new PositionSample(view.FrameIndex, tick, slot, new Vector3(x, y, z), place, view.Team(slot), alive));
        }
    }

    /// <inheritdoc />
    public void Finish(ICollectorSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.Put(new RoundIndexSamples(_samples, _lastFrameTick));
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

/// <summary>What <see cref="RoundIndexCollector" /> read off one demo's rules run.</summary>
/// <param name="Samples">The position samples in frame order, pawns in entity-index order within a frame.</param>
/// <param name="LastFrameTick">The last frame's frame clock; a round with no end runs to one past it.</param>
public sealed record RoundIndexSamples(IReadOnlyList<PositionSample> Samples, int LastFrameTick);
