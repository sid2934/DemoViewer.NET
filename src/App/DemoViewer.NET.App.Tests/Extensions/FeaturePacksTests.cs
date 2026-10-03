#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;

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
}
