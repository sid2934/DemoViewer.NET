#region

using System.Numerics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoProcessing.Collectors;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="GrenadeTrajectoryCollector" /> over synthetic frames: each projectile's rows, frame by frame,
///     fold to the polyline <see cref="TrajectoryBuilder" /> builds from the same positions, the spawn frame's
///     live row is not offered twice, a projectile with no release point starts at its first read, each flight
///     keeps its spawn fields, rest tick and end, and the contribution declares the classes and nothing else.
/// </summary>
public class GrenadeTrajectoryCollectorTests
{
    private const int HeIndex = 120;
    private const int SmokeIndex = 130;

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    public async Task TheCollectedFlights_EqualTheBuildersPolylines(int stride)
    {
        GrenadeTrajectoryCollector collector = new(stride);
        TrajectoryBuilder he = new(stride);
        TrajectoryBuilder smoke = new(stride);
        he.Start(100, new Vector3(0, 0, 64));
        int bounces = 0;
        for (int t = 100; t <= 210; t++)
        {
            List<ObservedProjectile> created = [], live = [], removed = [];
            if (t is >= 100 and <= 203)
            {
                if (t is 140 or 170)
                {
                    bounces++;
                }

                float x = t <= 190 ? 10 + (t - 100) * 5 : 10 + 90 * 5;
                ObservedProjectile row = Row(HeIndex, 7, GrenadeProjectileClasses.HEGrenade, new Vector3(x, 0, 64), bounces,
                    t == 100 ? new Vector3(0, 0, 64) : null);
                (t == 100 ? created : live).Add(row);
                if (t == 100)
                {
                    // The spawn frame's live row is the created row; a view can only ever repeat it.
                    live.Add(row with { Position = new Vector3(999, 0, 64) });
                }

                he.Offer(t, new Vector3(x, 0, 64), bounces);
            }
            else if (t == 204)
            {
                removed.Add(Row(HeIndex, 7, GrenadeProjectileClasses.HEGrenade, new Vector3(460, 0, 64), bounces, null));
            }

            // The smoke has no release point and no position on its first frame: the line starts at its first read.
            if (t >= 150)
            {
                Vector3? at = t == 150 ? null : new Vector3(500, 500 + Math.Min(t - 151, 40) * 2, 8);
                ObservedProjectile row = Row(SmokeIndex, 9, GrenadeProjectileClasses.Smoke, at, 0, null);
                (t == 150 ? created : live).Add(row);
                if (t == 150)
                {
                    live.Add(row);
                }

                if (at is { } p)
                {
                    smoke.Offer(t, p, 0);
                }
            }

            collector.OnFrame(new View(t, created, live, removed));
        }

        Sink sink = new();
        collector.Finish(sink);
        GrenadeTrajectories flights = (GrenadeTrajectories)sink.Result!;

        using (Assert.Multiple())
        {
            await Assert.That(flights.Stride).IsEqualTo(stride);
            await Assert.That(flights.Flights.Keys).IsEquivalentTo([(HeIndex, 7), (SmokeIndex, 9)], CollectionOrdering.Matching);
            await Assert.That(flights.Of(HeIndex, 7)).IsEquivalentTo(he.Finish(), CollectionOrdering.Matching);
            await Assert.That(flights.Of(HeIndex, 7)[0]).IsEqualTo(new TrajectoryPoint(100, 0, 0, 64, 0));
            await Assert.That(flights.Of(HeIndex, 7)[^1].Tick).IsEqualTo(190).Because("at rest from 190: the last moved sample ends the line");
            await Assert.That(flights.Of(HeIndex, 7).Any(p => p.X == 999)).IsFalse().Because("the spawn frame's live row is not offered again");
            await Assert.That(flights.Of(SmokeIndex, 9)).IsEquivalentTo(smoke.Finish(), CollectionOrdering.Matching);
            await Assert.That(flights.Of(SmokeIndex, 9)[0].Tick).IsEqualTo(151);
            await Assert.That(flights.Of(1, 1)).IsEmpty();
            await Assert.That(flights.Facts[(HeIndex, 7)]).IsEqualTo(new FlightFacts(GrenadeProjectileClasses.HEGrenade, 100, new Vector3(0, 0, 64),
                new Vector3(500, 0, 100), null, 204, true, new Vector3(460, 0, 64), 190, 2));
            await Assert.That(flights.Facts[(SmokeIndex, 9)]).IsEqualTo(new FlightFacts(GrenadeProjectileClasses.Smoke, 150, null, null, null, 210,
                false, new Vector3(500, 580, 8), 191, 0)).Because("still live when the run ended");
        }
    }

    [Test]
    public async Task TheContribution_DeclaresEveryClass_NoReadsNoCommandsNoEvents()
    {
        int asked = 0;
        CollectorContribution contribution = GrenadeTrajectoryCollector.Contribution(() =>
        {
            asked++;
            return 2;
        });
        IFrameCollector made = contribution.Create();

        using (Assert.Multiple())
        {
            await Assert.That(contribution.Id).IsEqualTo(GrenadeTrajectoryCollector.CollectorId);
            await Assert.That(contribution.PassId).IsEqualTo(GrenadeIndexEvaluator.EvaluatorId);
            await Assert.That(contribution.ProjectileClasses).IsEquivalentTo(GrenadeProjectileClasses.All, CollectionOrdering.Matching);
            await Assert.That(contribution.Providers).IsEmpty();
            await Assert.That(contribution.ReadsUserCommands).IsFalse();
            await Assert.That(contribution.GameEvents).IsEmpty();
            await Assert.That(RegisteredCollector.Refusal(contribution)).IsNull();
            await Assert.That(made).IsTypeOf<GrenadeTrajectoryCollector>();
            await Assert.That(asked).IsEqualTo(1).Because("the stride is read when the run's collector is made");
        }
    }

    private static ObservedProjectile Row(int index, int serial, string cls, Vector3? at, int bounces, Vector3? initial) =>
        new(index, serial, cls, 3, 2, at, initial, initial is null ? null : new Vector3(500, 0, 100), bounces, 0, null, null);

    private sealed class Sink : ICollectorSink
    {
        public object? Result { get; private set; }

        public void Put<T>(T result) where T : class => Result = result;
    }

    private sealed class View(int tick, List<ObservedProjectile> created, List<ObservedProjectile> live, List<ObservedProjectile> removed) : IFrameReadView
    {
        public DemoFrame Frame => throw new NotSupportedException();

        public int FrameIndex => tick;

        public int Tick => tick;

        public IReadOnlyList<ObservedProjectile> Projectiles => live;

        public IReadOnlyList<ObservedProjectile> Created => created;

        public IReadOnlyList<ObservedProjectile> Removed => removed;

        public ObservedRead Provider(string providerName) => throw new ArgumentException(providerName);

        public object? Read(string providerName, int playerSlot) => null;

        public bool TryReadInt(ObservedRead provider, int playerSlot, out int value)
        {
            value = 0;
            return false;
        }

        public bool TryReadFloat(ObservedRead provider, int playerSlot, out float value)
        {
            value = 0;
            return false;
        }

        public bool TryReadString(ObservedRead provider, int playerSlot, out string? value)
        {
            value = null;
            return false;
        }

        public int Team(int playerSlot) => -1;

        public CSGOUserCmdPB? Command(int playerSlot) => null;
    }
}
