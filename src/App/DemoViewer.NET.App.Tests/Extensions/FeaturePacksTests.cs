#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Services.DemoProcessing;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The compiled-in pack list's contract: empty until a head configures it, configured once, frozen
///     by the first read. <see cref="FeaturePacks" /> is a static over one <see cref="FrozenList{T}" />
///     that <c>CompiledInPacks</c> already configured for this assembly, so the mechanism is proven on
///     fresh instances and the static only has to show the result of that one configuration.
/// </summary>
public class FeaturePacksTests
{
    [Test]
    public async Task Default_IsEmpty_UntilConfigured()
    {
        FrozenList<string> list = new();

        using (Assert.Multiple())
        {
            await Assert.That(list.IsSet).IsFalse();
            await Assert.That(list.Value).IsEmpty();
        }
    }

    [Test]
    public async Task Configure_SetsTheList_Once()
    {
        FrozenList<string> list = new();
        list.Set(["a", "b"]);

        using (Assert.Multiple())
        {
            await Assert.That(list.IsSet).IsTrue();
            await Assert.That(list.Value).IsEquivalentTo(["a", "b"]);
            // A second configuration would leave an earlier reader disagreeing with the container.
            Assert.Throws<InvalidOperationException>(() => list.Set(["c"]));
            await Assert.That(list.Value).IsEquivalentTo(["a", "b"]);
        }
    }

    [Test]
    public async Task Configure_AfterTheListWasRead_Throws()
    {
        FrozenList<string> list = new();
        _ = list.Value;

        using (Assert.Multiple())
        {
            // The first read freezes an unset list: the registries build from what they read.
            Assert.Throws<InvalidOperationException>(() => list.Set(["a"]));
            await Assert.That(list.IsSet).IsFalse();
            await Assert.That(list.Value).IsEmpty();
        }
    }

    [Test]
    public async Task Configure_WithAnEmptyList_IsAccepted()
    {
        FrozenList<string> list = new();
        list.Set([]);

        using (Assert.Multiple())
        {
            await Assert.That(list.IsSet).IsTrue();
            await Assert.That(list.Value).IsEmpty();
            // An empty list is a configuration, not the absence of one.
            Assert.Throws<InvalidOperationException>(() => list.Set(["a"]));
        }
    }

    [Test]
    public async Task ConfigureIfUnset_SetsOnce_ThenIsANoOp_AndStillRefusesAFrozenUnsetList()
    {
        FrozenList<string> list = new();

        using (Assert.Multiple())
        {
            await Assert.That(list.SetIfUnset(["a"])).IsTrue();
            await Assert.That(list.SetIfUnset(["b"])).IsFalse().Because("the previewer path yields to Main's call");
            await Assert.That(list.Value).IsEquivalentTo(["a"]);
        }

        FrozenList<string> frozen = new();
        _ = frozen.Value;
        // Reading before any configuration is the ordering mistake the freeze exists to catch.
        Assert.Throws<InvalidOperationException>(() => frozen.SetIfUnset(["a"]));
    }

    [Test]
    public async Task Default_CarriesTheAssemblyConfiguration_AndRefusesAnother()
    {
        using (Assert.Multiple())
        {
            await Assert.That(FeaturePacks.Default.Select(p => p.FeatureId)).IsEquivalentTo([StratBookPack.PackFeatureId]);
            Assert.Throws<InvalidOperationException>(() => FeaturePacks.Configure([]));
            await Assert.That(FeaturePacks.ConfigureIfUnset([])).IsFalse();
            await Assert.That(FeaturePacks.Default.Select(p => p.FeatureId)).IsEquivalentTo([StratBookPack.PackFeatureId]);
        }
    }

    // ── Item 33: the compatibility check runs at configuration ─────────────────────────────────────

    [Test]
    public async Task TheShippedPack_IsCompatibleWithThisBuild_SoCompatibleEqualsDefault()
    {
        using (Assert.Multiple())
        {
            await Assert.That(FeaturePacks.Statuses.Count).IsEqualTo(1);
            await Assert.That(FeaturePacks.Statuses[0].IsCompatible).IsTrue().Because(FeaturePacks.Statuses[0].Problem ?? "compatible");
            await Assert.That(FeaturePacks.Statuses[0].Manifest!.Id).IsEqualTo(StratBookPack.PackId);
            await Assert.That(FeaturePacks.Compatible).IsEquivalentTo(FeaturePacks.Default);
        }
    }

    // The check is pure over (packs, host), so it is proven here on fakes against a fixed host; the static
    // only applies it inside Configure. An incompatible pack stays in the declared list (so Settings can
    // name it) and leaves the compatible one, which is all the catalog, the registries and the composition
    // root read: none of them sees its descriptors, job kinds or commands. The real Strat Book pack rides
    // along because the job-kind registry must cover every QueueJobKind to build at all.
    [Test]
    public async Task AnIncompatiblePack_IsDeclaredButNotCompatible_AndNothingBuiltFromCompatibleSeesIt()
    {
        ExtensionHostInfo host = new(SemVersion.Parse("1.0.0"), null, SemVersion.Parse("0.13.0-beta0001"));
        IncompatibleFakePack incompatible = new();
        StratBookPack stratBook = new();

        IReadOnlyList<PackStatus> statuses = PackStatus.Evaluate([incompatible, stratBook], host);
        IReadOnlyList<IFeaturePack> declared = [.. statuses.Select(s => s.Pack)];
        IReadOnlyList<IFeaturePack> compatible = [.. statuses.Where(s => s.IsCompatible).Select(s => s.Pack)];

        using (Assert.Multiple())
        {
            await Assert.That(declared.Select(p => p.Id)).IsEquivalentTo([incompatible.Id, stratBook.Id]);
            await Assert.That(compatible.Select(p => p.Id)).IsEquivalentTo([stratBook.Id]);
            await Assert.That(statuses[0].Compatibility is PackCompatibility.HostContractMismatch).IsTrue();
            await Assert.That(statuses[0].Problem).IsEqualTo("Incompatible 2.0.0 needs app contract ^2.0; this app provides 1.0.0");

            FeatureDescriptor[] catalog = FeatureCatalog.Build(compatible);
            await Assert.That(catalog.Any(d => d.Id == IncompatibleFakePack.PackFeatureId)).IsFalse()
                .Because("the gate then reads the pack id as unknown and resolves it off");
            await Assert.That(catalog.Any(d => d.Id == StratBookPack.PackFeatureId)).IsTrue();

            // Its job kind collides with a core kind and its command names no action, so either registry
            // built over the declared list throws, and built over the compatible list does not: neither
            // ever saw it.
            Assert.Throws<InvalidOperationException>(() => JobKindRegistry.Build(declared));
            _ = JobKindRegistry.Build(compatible);
            Assert.Throws<InvalidOperationException>(() => CommandRegistry.Build(declared));
            _ = CommandRegistry.Build(compatible);
        }
    }

    // Whichever derived view a consumer reads first, the verdicts were fixed by Set, so the two views
    // agree and a configuration after either read is refused as before.
    [Test]
    public async Task TheDerivedViews_AgreeInEitherReadOrder_AndFreezeOnFirstRead()
    {
        ExtensionHostInfo host = new(SemVersion.Parse("1.0.0"), null, SemVersion.Parse("0.13.0-beta0001"));
        IncompatibleFakePack incompatible = new();
        PackCompatibilityTests.ManifestPack fine = new("net.demoviewer.pack.fine", FakeManifests.For("net.demoviewer.pack.fine"));

        FrozenList<PackStatus> compatibleFirst = new();
        compatibleFirst.Set(PackStatus.Evaluate([incompatible, fine], host));
        IReadOnlyList<IFeaturePack> c1 = [.. compatibleFirst.Value.Where(s => s.IsCompatible).Select(s => s.Pack)];
        IReadOnlyList<IFeaturePack> d1 = [.. compatibleFirst.Value.Select(s => s.Pack)];

        FrozenList<PackStatus> defaultFirst = new();
        defaultFirst.Set(PackStatus.Evaluate([incompatible, fine], host));
        IReadOnlyList<IFeaturePack> d2 = [.. defaultFirst.Value.Select(s => s.Pack)];
        IReadOnlyList<IFeaturePack> c2 = [.. defaultFirst.Value.Where(s => s.IsCompatible).Select(s => s.Pack)];

        using (Assert.Multiple())
        {
            await Assert.That(c1.Select(p => p.Id)).IsEquivalentTo(c2.Select(p => p.Id));
            await Assert.That(d1.Select(p => p.Id)).IsEquivalentTo(d2.Select(p => p.Id));
            await Assert.That(c1.Select(p => p.Id)).IsEquivalentTo([fine.Id]);
            Assert.Throws<InvalidOperationException>(() => compatibleFirst.Set([]));
            Assert.Throws<InvalidOperationException>(() => defaultFirst.Set([]));
        }
    }

    // ── Item 34: where each pack came from ──────────────────────────────────────────────────────────

    [Test]
    public async Task AStatus_IsBundled_UnlessTheLoaderSaysOtherwise()
    {
        ExtensionHostInfo host = new(SemVersion.Parse("1.0.0"), null, SemVersion.Parse("0.13.0-beta0001"));
        PackCompatibilityTests.ManifestPack fine = new("net.demoviewer.pack.fine", FakeManifests.For("net.demoviewer.pack.fine"));
        PackStatus evaluated = PackStatus.Evaluate(fine, host);
        LoadOutcome refused = new("/extensions/net.demoviewer.pack.fine/1.1.0", FakeManifests.For(fine.Id, "Fine", "1.1.0"), LoadFailure.Untrusted, "unsigned");
        PackStatus staged = evaluated with { Source = new PackSource.Staged("/extensions/net.demoviewer.pack.fine/1.2.0"), Rejected = [refused] };

        using (Assert.Multiple())
        {
            await Assert.That(evaluated.Source).IsEqualTo(PackSource.Bundled);
            await Assert.That(evaluated.Source.IsStaged).IsFalse();
            await Assert.That(evaluated.Source.Label).IsEqualTo("bundled");
            await Assert.That(evaluated.Rejected).IsEmpty();
            await Assert.That(staged.Source.IsStaged).IsTrue();
            await Assert.That(staged.Source.Label).IsEqualTo("installed update");
            await Assert.That(staged.Rejected.Single().UserMessage).IsEqualTo("Update 1.1.0 was not loaded: unsigned");
            await Assert.That(staged.IsCompatible).IsTrue().Because("the source changes nothing about the verdict");
            await Assert.That(FeaturePacks.Statuses.Single().Source).IsEqualTo(PackSource.Bundled).Because("the test assembly compile-links the pack");
        }
    }

    // The status-list overload is what the Desktop head passes from the loader: the list is taken as is,
    // sources and rejections included, and freezes like the pack-list one.
    [Test]
    public async Task ConfiguringFromStatuses_KeepsTheSourcesTheLoaderAssigned()
    {
        ExtensionHostInfo host = new(SemVersion.Parse("1.0.0"), null, SemVersion.Parse("0.13.0-beta0001"));
        PackCompatibilityTests.ManifestPack fine = new("net.demoviewer.pack.fine", FakeManifests.For("net.demoviewer.pack.fine"));
        PackStatus staged = PackStatus.Evaluate(fine, host) with { Source = new PackSource.Staged("/extensions/x/1.2.0") };

        FrozenList<PackStatus> list = new();
        list.Set([staged]);

        using (Assert.Multiple())
        {
            await Assert.That(list.Value.Single().Source).IsEqualTo(new PackSource.Staged("/extensions/x/1.2.0"));
            Assert.Throws<InvalidOperationException>(() => list.Set([]));
            Assert.Throws<InvalidOperationException>(() => FeaturePacks.ConfigureResolved([staged]));
        }
    }

    // A pack built against contract 2.x on a 1.x host, with a job kind and a command each registry would
    // refuse, so the checks above can tell whether a registry saw it.
    private sealed class IncompatibleFakePack : IFeaturePack
    {
        public const string PackFeatureId = "pack.incompatible";

        public string Id => "net.demoviewer.pack.incompatible";
        public string FeatureId => PackFeatureId;

        public ExtensionManifest Manifest => FakeManifests.For(Id, "Incompatible", "2.0.0", "^2.0", "*");

        public IEnumerable<FeatureDescriptor> Features =>
        [
            new(PackFeatureId, FeatureScope.Pack, "Incompatible", "d", null, null, false, new Dictionary<UserCategory, bool>())
        ];

        public IEnumerable<CommandDescriptor> Commands => [new CommandDescriptor("incompatible.cmd", "Cmd", "playback2d", null, _ => true)];

        public IEnumerable<JobKindDescriptor> JobKinds => [new JobKindDescriptor(QueueJobKind.StoreSave, "collides", 9, false)];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IPackContributions contributions, IServiceProvider sp)
        {
        }
    }
}
