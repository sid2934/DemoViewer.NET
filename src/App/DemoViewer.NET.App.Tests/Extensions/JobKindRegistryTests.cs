#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Manifest;
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
            (QueueJobKind.ExtensionUpdate, "extension update", 4, true),
            (QueueJobKind.DemoOpen, "open", 4, false),
            (QueueJobKind.Extension, "extension", 4, false),
            (QueueJobKind.Scheduling, "scheduling", 4, true),
            (QueueJobKind.RecordPass, "records", 4, true),
            (QueueJobKind.LibraryListing, "library", 4, true),
            (QueueJobKind.CacheSweep, "cleanup", 4, true)
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
            }
        }
    }

    [Test]
    public async Task Build_WithNoExtensions_CoversEveryCoreKind()
    {
        JobKindRegistry registry = JobKindRegistry.Build([]);

        await Assert.That(Enum.GetValues<QueueJobKind>().All(k => registry.Descriptor(k).Kind == k)).IsTrue();
    }

    [Test]
    public async Task ADeclaredKind_LabelsRanksAndLightensItsJobs_AndAnUnknownOneFallsBackToTheExtensionRow()
    {
        FakePack pack = new("pack.fake", [new ExtensionJobKind("fake.walk", "walk", true, 2)]);
        JobKindRegistry registry = JobKindRegistry.Build([pack]);

        using (Assert.Multiple())
        {
            await Assert.That(registry.Label(QueueJobKind.Extension, "fake.walk")).IsEqualTo("walk");
            await Assert.That(registry.Rank(QueueJobKind.Extension, "fake.walk")).IsEqualTo(2);
            await Assert.That(registry.IsLight(QueueJobKind.Extension, "fake.walk")).IsTrue();
            await Assert.That(registry.IsDeclared("fake.walk")).IsTrue();
            await Assert.That(registry.Label(QueueJobKind.Extension, "fake.unknown")).IsEqualTo("extension");
        }
    }

    [Test]
    public async Task Build_AnExtensionDeclaringABuiltInKindId_Throws()
    {
        FakePack pack = new("pack.fake", [new ExtensionJobKind(BuiltInJobKinds.Save, "fake save", false, 9)]);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => JobKindRegistry.Build([pack]));

        await Assert.That(ex.Message).Contains(BuiltInJobKinds.Save);
        await Assert.That(ex.Message).Contains(pack.Id);
    }

    [Test]
    public async Task Build_TwoExtensionsDeclaringTheSameKind_Throws()
    {
        FakePack first = new("pack.fake1", [new ExtensionJobKind("shared.mining", "mining", false, 2)]);
        FakePack second = new("pack.fake2", [new ExtensionJobKind("shared.mining", "dup", false, 2)]);

        InvalidOperationException ex =
            Assert.Throws<InvalidOperationException>(() => JobKindRegistry.Build([first, second]));

        await Assert.That(ex.Message).Contains("shared.mining");
        await Assert.That(ex.Message).Contains(second.Id);
    }

    private sealed class FakePack(string featureId, ExtensionJobKind[] jobKinds) : IExtension, IManifestSource
    {
        public string Id => "net.demoviewer.test." + featureId;
        public string FeatureId => featureId;
        public ExtensionManifest Manifest => FakeManifests.For(Id);

        public IEnumerable<ExtensionFeature> Features =>
        [
            new(featureId, ExtensionFeatureKind.Extension, "Fake pack", "a test pack", null, AudienceDefaults.Everyone)
        ];

        public IEnumerable<ExtensionJobKind> JobKinds => jobKinds;

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }
}
