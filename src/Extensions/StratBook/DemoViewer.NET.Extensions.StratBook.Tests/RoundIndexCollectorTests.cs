#region

using System.Numerics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Services.DemoProcessing.Collectors;
using TUnit.Assertions.Enums;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="RoundIndexCollector" /> over synthetic frames: it keeps the walk's stride, pawn order and fields,
///     so the fold over its samples equals the fold over the walk's.
/// </summary>
public class RoundIndexCollectorTests
{
    private static readonly string[] Declared = [.. RoundIndexCollector.Contribution.Providers];

    private static RoundIndexSamples Collect(IEnumerable<View> frames)
    {
        RoundIndexCollector collector = new(4);
        foreach (View frame in frames)
        {
            collector.OnFrame(frame);
        }

        Sink sink = new();
        collector.Finish(sink);
        return (RoundIndexSamples)sink.Result!;
    }

    [Test]
    public async Task OnlyEveryFourthFrame_IsSampled_InEntityIndexOrder_AtTheServerTick()
    {
        View Frame(int index) => new View(index, 1000 + index, 2000 + index)
            .Pawn(5, entityIndex: 30, new Vector3(1, 2, 3), "BombsiteA", team: 3, alive: true)
            .Pawn(2, entityIndex: 40, new Vector3(4, 5, 6), null, team: 2, alive: false)
            .Pawn(9, entityIndex: 12, new Vector3(7, 8, 9), "Ramp", team: 2, alive: true);

        RoundIndexSamples collected = Collect([.. Enumerable.Range(0, 6).Select(Frame)]);

        using (Assert.Multiple())
        {
            await Assert.That(collected.Samples.Select(s => (s.FrameIndex, s.PlayerSlot)))
                .IsEquivalentTo([(0, 9), (0, 5), (0, 2), (4, 9), (4, 5), (4, 2)], CollectionOrdering.Matching);
            await Assert.That(collected.Samples[0]).IsEqualTo(new PositionSample(0, 2000, 9, new Vector3(7, 8, 9), "Ramp", 2, true));
            await Assert.That(collected.Samples[2]).IsEqualTo(new PositionSample(0, 2000, 2, new Vector3(4, 5, 6), null, 2, false));
            await Assert.That(collected.LastFrameTick).IsEqualTo(1005);
        }
    }

    [Test]
    public async Task ASlotWithoutAPosition_IsNotSampled()
    {
        View frame = new View(0, 64, 64).Pawn(1, 20, new Vector3(1, 1, 1), "Mid", 3, true);
        frame.Values[(Declared[0], 7)] = 5f;

        RoundIndexSamples collected = Collect([frame]);

        await Assert.That(collected.Samples.Select(s => s.PlayerSlot)).IsEquivalentTo([1]);
    }

    [Test]
    public async Task TheFoldOverCollectedSamples_EqualsTheFoldOverTheSameWalk()
    {
        // Two rounds of ten seconds at 64 ticks, a frame every two ticks; slot 3 dies halfway through round 1.
        List<View> frames = [];
        List<PositionSample> walk = [];
        for (int i = 0; i < 1400; i++)
        {
            int tick = 1000 + 2 * i;
            View frame = new(i, tick, tick);
            for (int slot = 0; slot < 4; slot++)
            {
                Vector3 at = new(slot * 100 + i * 0.25f, -slot * 50, 8);
                string place = (i / 128 + slot) % 2 == 0 ? "Long" : "Short";
                bool alive = slot != 3 || tick < 1300;
                int team = slot < 2 ? 3 : 2;
                frame.Pawn(slot, 10 + (3 - slot), at, place, team, alive);
                if (i % 4 == 0)
                {
                    walk.Add(new PositionSample(i, tick, slot, at, place, team, alive));
                }
            }

            frames.Add(frame);
        }

        walk = [.. walk.OrderBy(s => s.FrameIndex).ThenBy(s => 3 - s.PlayerSlot)];
        RoundIndexSamples collected = Collect(frames);
        RoundFactsRows facts = Facts(Round(1, 1000, 1640), Round(2, 1800, 3790));
        RoundIndexDemoValues values = new(64, collected.LastFrameTick, new RoundFactsClock { TickRate = 64 }, "de_nuke");

        RoundIndexBuild fromWalk = RoundIndexBuilder.BuildWithPositions(values, facts, RoundIndexOptions.Default, PawnPlaceSource.Instance, walk);
        RoundIndexBuild fromCollector = RoundIndexBuilder.BuildWithPositions(values, facts, RoundIndexOptions.Default,
            PawnPlaceSource.Instance, collected.Samples);

        using (Assert.Multiple())
        {
            await Assert.That(collected.Samples).IsEquivalentTo(walk, CollectionOrdering.Matching);
            await Assert.That(fromCollector.Index.Serialize()).IsEqualTo(fromWalk.Index.Serialize());
            await Assert.That(fromCollector.Positions.Serialize()).IsEqualTo(fromWalk.Positions.Serialize());
            await Assert.That(fromWalk.Index.Rounds.Sum(r => r.Runs.Count)).IsGreaterThan(0);
        }
    }

    [Test]
    public async Task TheContribution_IsTheRoundIndexPassesAndTheEngineKnowsItsReads()
    {
        CollectorContribution contribution = RoundIndexCollector.Contribution;

        using (Assert.Multiple())
        {
            await Assert.That(contribution.PassId).IsEqualTo(RoundIndexEvaluator.EvaluatorId);
            await Assert.That(contribution.Providers).IsEquivalentTo([
                "entity.pawn.pos_x", "entity.pawn.pos_y", "entity.pawn.pos_z", "entity.pawn.place", "entity.pawn.health",
                "entity.pawn.is_alive", "entity.pawn.entity_index"
            ]);
            await Assert.That(contribution.ProjectileClasses).IsEmpty();
            await Assert.That(RegisteredCollector.Refusal(contribution)).IsNull();
        }
    }

    private sealed class Sink : ICollectorSink
    {
        public object? Result { get; private set; }

        public void Put<T>(T result) where T : class => Result = result;
    }

    // Slot reads keyed by provider name; a slot absent from the map never had the value.
    private sealed class View(int frameIndex, int tick, int serverTick) : IFrameReadView
    {
        public Dictionary<(string Provider, int Slot), object> Values { get; } = [];

        private readonly Dictionary<int, int> _teams = [];

        public View Pawn(int slot, int entityIndex, Vector3 at, string? place, int team, bool alive)
        {
            Values[(Declared[0], slot)] = at.X;
            Values[(Declared[1], slot)] = at.Y;
            Values[(Declared[2], slot)] = at.Z;
            if (place is not null)
            {
                Values[(Declared[3], slot)] = place;
            }

            Values[(Declared[4], slot)] = alive ? 100 : 0;
            Values[(Declared[5], slot)] = alive ? 1 : 0;
            Values[(Declared[6], slot)] = entityIndex;
            _teams[slot] = team;
            return this;
        }

        public DemoFrame Frame { get; } = new()
        {
            CommandKind = EDemoCommands.DemPacket, FrameNumber = frameIndex, ServerTick = serverTick, GameTick = tick,
            HeaderLength = 0, RawLength = 0, RawStart = 0, IsCompressed = false
        };

        public int FrameIndex => frameIndex;

        public int Tick => tick;

        public IReadOnlyList<ObservedProjectile> Projectiles => [];

        public IReadOnlyList<ObservedProjectile> Created => [];

        public IReadOnlyList<ObservedProjectile> Removed => [];

        public ObservedRead Provider(string providerName) =>
            Array.IndexOf(Declared, providerName) is var i and >= 0 ? new ObservedRead(providerName, i) : throw new ArgumentException(providerName);

        public object? Read(string providerName, int playerSlot) => Values.GetValueOrDefault((providerName, playerSlot));

        public bool TryReadInt(ObservedRead provider, int playerSlot, out int value)
        {
            bool found = Values.TryGetValue((provider.Name, playerSlot), out object? v);
            value = found ? (int)v! : 0;
            return found;
        }

        public bool TryReadFloat(ObservedRead provider, int playerSlot, out float value)
        {
            bool found = Values.TryGetValue((provider.Name, playerSlot), out object? v);
            value = found ? (float)v! : 0;
            return found;
        }

        public bool TryReadString(ObservedRead provider, int playerSlot, out string? value)
        {
            bool found = Values.TryGetValue((provider.Name, playerSlot), out object? v);
            value = found ? (string)v! : null;
            return found;
        }

        public int Team(int playerSlot) => _teams.GetValueOrDefault(playerSlot, -1);
    }
}
