#region

using System.Numerics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="ThrowerCollector" /> over synthetic frames: the observer's <c>Removed</c> rows, run through
///     <see cref="ProjectileThrowerMatch" />, name the same throwers as the sampler's <c>Removed</c> samples.
/// </summary>
public class ThrowerCollectorTests
{
    private static ProjectileSample Sample(int frame, string className, int slot, Vector3 position, bool removed) =>
        new(frame, frame * 2, 20 + slot, 1, className, slot, position, null, null, 0, false, removed);

    private static ObservedProjectile Row(ProjectileSample sample) =>
        new(sample.EntityIndex, sample.Serial, sample.ClassName, sample.ThrowerSlot, 2, sample.Position, null, null, 0, 0, null, null);

    private static List<RemovedProjectile> Collect(IEnumerable<ProjectileSample> samples)
    {
        IFrameCollector collector = ThrowerCollector.Contribution.Create();
        foreach (IGrouping<int, ProjectileSample> frame in samples.GroupBy(s => s.FrameIndex).OrderBy(g => g.Key))
        {
            collector.OnFrame(new View(frame.Key, frame.First().Tick, [.. frame.Where(s => s.Removed).Select(Row)]));
        }

        Sink sink = new();
        collector.Finish(sink);
        return (List<RemovedProjectile>)sink.Result!;
    }

    [Test]
    public async Task RemovedRowsIntoTheMatch_EqualTheSamplersRemovedSamples()
    {
        List<ProjectileSample> samples =
        [
            Sample(10, GrenadeProjectileClasses.Molotov, 3, new Vector3(100, 200, 0), false),
            Sample(40, GrenadeProjectileClasses.Molotov, 3, new Vector3(110, 210, 0), true),
            Sample(41, GrenadeProjectileClasses.Decoy, 7, new Vector3(-500, 40, 8), true),
            Sample(41, GrenadeProjectileClasses.Smoke, 1, new Vector3(-500, 40, 8), true),
            Sample(90, GrenadeProjectileClasses.Decoy, 4, new Vector3(900, 900, 0), true)
        ];
        List<RemovedProjectile> rows = Collect(samples);
        (string Class, int Tick, Vector3 At)[] detonations =
        [
            (GrenadeProjectileClasses.Molotov, 82, new Vector3(112, 212, 0)),
            (GrenadeProjectileClasses.Decoy, 83, new Vector3(-490, 40, 8)),
            (GrenadeProjectileClasses.Decoy, 180, new Vector3(905, 900, 0)),
            (GrenadeProjectileClasses.Decoy, 120, new Vector3(905, 900, 0))
        ];

        foreach ((string className, int tick, Vector3 at) in detonations)
        {
            await Assert.That(ProjectileThrowerMatch.Nearest(rows, className, tick, at)?.ThrowerSlot)
                .IsEqualTo(ProjectileThrowerMatch.Nearest(samples, className, tick, at)?.ThrowerSlot);
        }

        await Assert.That(rows).IsEquivalentTo(samples
            .Where(s => s.Removed && s.ClassName is GrenadeProjectileClasses.Molotov or GrenadeProjectileClasses.Decoy)
            .Select(RemovedProjectile.From));
    }

    [Test]
    public async Task TheContribution_DeclaresNoProvidersAndTheTwoUnnamedClasses()
    {
        CollectorContribution contribution = ThrowerCollector.Contribution;

        using (Assert.Multiple())
        {
            await Assert.That(contribution.PassId).IsEqualTo(SuggestedTagsService.EvaluatorId);
            await Assert.That(contribution.Providers).IsEmpty();
            await Assert.That(contribution.ProjectileClasses)
                .IsEquivalentTo([GrenadeProjectileClasses.Molotov, GrenadeProjectileClasses.Decoy]);
        }
    }

    private sealed class Sink : ICollectorSink
    {
        public object? Result { get; private set; }

        public void Put<T>(T result) where T : class => Result = result;
    }

    private sealed class View(int frameIndex, int tick, IReadOnlyList<ObservedProjectile> removed) : IFrameReadView
    {
        public DemoFrame Frame => throw new NotSupportedException();

        public int FrameIndex => frameIndex;

        public int Tick => tick;

        public IReadOnlyList<ObservedProjectile> Projectiles => [];

        public IReadOnlyList<ObservedProjectile> Created => [];

        public IReadOnlyList<ObservedProjectile> Removed => removed;

        public ObservedRead Provider(string providerName) => throw new NotSupportedException();

        public object? Read(string providerName, int playerSlot) => throw new NotSupportedException();

        public bool TryReadInt(ObservedRead provider, int playerSlot, out int value) => throw new NotSupportedException();

        public bool TryReadFloat(ObservedRead provider, int playerSlot, out float value) => throw new NotSupportedException();

        public bool TryReadString(ObservedRead provider, int playerSlot, out string? value) => throw new NotSupportedException();

        public int Team(int playerSlot) => throw new NotSupportedException();
    }
}
