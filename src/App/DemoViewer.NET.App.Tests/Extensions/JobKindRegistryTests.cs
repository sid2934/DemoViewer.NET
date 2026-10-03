#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Features;
using DemoViewer.NET.Services.DemoProcessing;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="JobKindRegistry" />: the queue's scheduling rank, light-slot flag and chip label per
///     <see cref="QueueJobKind" />, core table union every pack's kinds. Built with a fake pack rather
///     than <see cref="FeaturePacks.Default" /> so a collision or a missing descriptor proves something
///     without touching the Strat Book's own table (that is <c>StratBookPackTests</c>' job).
/// </summary>
public class JobKindRegistryTests
{
    [Test]
    public async Task Default_ResolvesLabelRankAndLight_ForEveryEnumMember()
    {
        using (Assert.Multiple())
        {
            foreach (QueueJobKind kind in Enum.GetValues<QueueJobKind>())
            {
                JobKindDescriptor descriptor = JobKindRegistry.Default.Descriptor(kind);
                await Assert.That(descriptor.Kind).IsEqualTo(kind);
                await Assert.That(JobKindRegistry.Default.Label(kind)).IsEqualTo(descriptor.Label);
                await Assert.That(JobKindRegistry.Default.Rank(kind)).IsEqualTo(descriptor.Rank);
                await Assert.That(JobKindRegistry.Default.IsLight(kind)).IsEqualTo(descriptor.IsLight);
            }
        }
    }

    // Pins the values the old DemoProcessingQueue.KindRank/IsLight switches and DemoQueueRowViewModel's
    // KindLabel switch returned for the kinds core keeps, so a change here is a deliberate change to
    // queue scheduling or the flyout's chip text, not a side effect of the registry's plumbing.
    [Test]
    public async Task CoreDescriptors_PinRankLightAndLabel_UnchangedFromBeforeTheRegistry()
    {
        (QueueJobKind Kind, string Label, int Rank, bool IsLight)[] expected =
        [
            (QueueJobKind.DemoProcessing, "", 0, false),
            (QueueJobKind.PackExport, "pack export", 0, false),
            (QueueJobKind.SidecarMigration, "migration", 1, false),
            (QueueJobKind.HeapCompaction, "memory", 4, false),
            (QueueJobKind.StoreSave, "save", 4, true),
            (QueueJobKind.StoreLoad, "load", 4, true),
            (QueueJobKind.SectionCompute, "section", 4, true),
            (QueueJobKind.LibraryScan, "library", 4, false),
            (QueueJobKind.DemoOpen, "open", 4, false)
        ];

        await Assert.That(JobKindRegistry.CoreDescriptors.Select(d => d.Kind))
            .IsEquivalentTo(expected.Select(e => e.Kind));

        using (Assert.Multiple())
        {
            foreach (var (kind, label, rank, isLight) in expected)
            {
                JobKindDescriptor descriptor = JobKindRegistry.CoreDescriptors.Single(d => d.Kind == kind);
                await Assert.That(descriptor.Label).IsEqualTo(label).Because($"{kind}'s chip label");
                await Assert.That(descriptor.Rank).IsEqualTo(rank).Because($"{kind}'s scheduling rank");
                await Assert.That(descriptor.IsLight).IsEqualTo(isLight).Because($"{kind}'s light-slot flag");
                await Assert.That(descriptor.Owner).IsNull().Because($"{kind} has no single owner");
            }
        }
    }

    [Test]
    public async Task Build_WithNoPacks_ThrowsNamingAMissingKind()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => JobKindRegistry.Build([]));

        // No pack contributed the kinds core does not own.
        await Assert.That(ex.Message).Contains("StratMining");
    }

    [Test]
    public async Task Build_APackRedeclaringACoreKind_Throws()
    {
        FakePack pack = new("pack.fake", [new JobKindDescriptor(QueueJobKind.StoreSave, "fake save", 9, false)]);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => JobKindRegistry.Build([pack]));

        await Assert.That(ex.Message).Contains("StoreSave");
        await Assert.That(ex.Message).Contains(pack.Id);
    }

    [Test]
    public async Task Build_TwoPacksContributingTheSameKind_Throws()
    {
        FakePack first = new("pack.fake1", [new JobKindDescriptor(QueueJobKind.StratMining, "mining", 2, false, "x")]);
        FakePack second = new("pack.fake2", [new JobKindDescriptor(QueueJobKind.StratMining, "dup", 2, false, "y")]);

        InvalidOperationException ex =
            Assert.Throws<InvalidOperationException>(() => JobKindRegistry.Build([first, second]));

        await Assert.That(ex.Message).Contains("StratMining");
        await Assert.That(ex.Message).Contains(second.Id);
    }

    private sealed class FakePack(string featureId, JobKindDescriptor[] jobKinds) : IFeaturePack
    {
        public string Id => "net.demoviewer.test." + featureId;
        public string FeatureId => featureId;

        public IEnumerable<FeatureDescriptor> Features =>
        [
            new(featureId, FeatureScope.Pack, "Fake pack", "a test pack", null, null, false,
                FeatureCatalog.Defaults(true, true, true))
        ];

        public IEnumerable<JobKindDescriptor> JobKinds => jobKinds;

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IPackContributions contributions, IServiceProvider sp)
        {
        }
    }
}
