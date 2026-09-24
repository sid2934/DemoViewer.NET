#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.ViewModels.Situations;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Situations module's persisted ids, its feature-catalog row, the settings section's
///     <c>WriteInMemory</c> rows, and the status strip's lines on both hosts.
/// </summary>
public class SituationsModuleTests
{
    [Test]
    public async Task TheModule_ContributesOneMainTab_UnderThePersistedIds()
    {
        SituationsModule module = new(() => throw new InvalidOperationException("never built here"));
        WorkspaceTabDescriptor tab = module.CreateTabs(null!).Single();

        using (Assert.Multiple())
        {
            await Assert.That(module.Id).IsEqualTo("net.demoviewer.situations");
            await Assert.That(tab.TabId).IsEqualTo("situations.search");
            await Assert.That(tab.Header).IsEqualTo("Situations");
            await Assert.That(tab.Placement).IsEqualTo(TabPlacement.Main);
            await Assert.That(tab.ViewModelFactory is not null).IsTrue().Because("lazy and retained, never DataContext");
            await Assert.That(tab.DataContext).IsNull();
        }
    }

    [Test]
    public async Task TheFeatureId_IsATab_VisibleToEveryCategory()
    {
        FeatureDescriptor? feature = FeatureCatalog.ById("tab.situations");

        using (Assert.Multiple())
        {
            await Assert.That(feature).IsNotNull();
            await Assert.That(feature!.Scope).IsEqualTo(FeatureScope.Tab);
            await Assert.That(feature.ParentId).IsNull();
            await Assert.That(feature.GroupId).IsNull().Because("it must not disturb the leader-lock ordering");
            foreach (UserCategory category in Enum.GetValues<UserCategory>())
            {
                await Assert.That(feature.Defaults[category]).IsTrue().Because($"{category} sees the flagship");
            }

            await Assert.That(ShellModuleFeatureGate.DesktopOnlyIds).DoesNotContain("tab.situations")
                .Because("the tab renders on the browser and says what it cannot do");
        }
    }

    [Test]
    public async Task EverySituationsSetting_SurvivesAFilelessWrite()
    {
        SettingsService svc = new(null);
        await Assert.That(svc.Current.Situations.BackgroundIndex).IsTrue().Because("on by default: the flagship needs coverage");
        await Assert.That(svc.Current.Situations.TokenSource).IsEqualTo(RoundIndexTokenSource.Pawn);

        svc.Write(s =>
        {
            s.Situations.BackgroundIndex = false;
            s.Situations.TokenSource = RoundIndexTokenSource.Zones;
        });

        using (Assert.Multiple())
        {
            await Assert.That(svc.Current.Situations.BackgroundIndex).IsFalse()
                .Because("a Situations property with no WriteInMemory row forgets itself on WASM");
            await Assert.That(svc.Current.Situations.TokenSource).IsEqualTo(RoundIndexTokenSource.Zones);
        }
    }

    [Test]
    public async Task TheStrip_CountsFromTheRows_AndOffersRetryOnlyWhenSomethingFailed()
    {
        DemoCacheStore cache = new(null);
        using RoundIndexStore sidecars = new(null, cache);
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
        RoundIndexEvaluator evaluator = new(cache, sidecars, sources, () => true, walk: _ => []);
        using SituationIndex index = new(cache, sidecars, sources, evaluator: evaluator);
        Indexed(cache, sidecars, "/d/a.dem", Document("de_nuke", sources.FingerprintFor("de_nuke")));
        cache.Upsert(ParsedRecord("/d/b.dem", facts: Facts(Round(1, 1000, 2000))));
        cache.Upsert(ParsedRecord("/d/c.dem"));
        index.Load();

        using SituationsTabViewModel vm = new(index, evaluator, cache, sources, () => RoundIndexTokenSource.Pawn, isBrowser: false);

        using (Assert.Multiple())
        {
            await Assert.That(vm.LibraryCount).IsEqualTo(3);
            await Assert.That(vm.IndexedCount).IsEqualTo(1);
            await Assert.That(vm.PendingCount).IsEqualTo(1).Because("b has rows and no index; c has no rows yet");
            await Assert.That(vm.StatusLine).IsEqualTo("Indexed 1 of 3 demos · 1 queued");
            await Assert.That(vm.HasFailed).IsFalse();
            await Assert.That(vm.CanRebuild).IsTrue();
            await Assert.That(vm.TokenSourceLine).Contains("pawn");
        }

        cache.UpdateExisting("/d/b.dem", r => r.RoundIndexState = RoundIndexState.Failed);
        using (Assert.Multiple())
        {
            await Assert.That(vm.FailedCount).IsEqualTo(1);
            await Assert.That(vm.HasFailed).IsTrue();
            await Assert.That(vm.StatusLine).IsEqualTo("Indexed 1 of 3 demos · 1 failed");
        }

        vm.RetryFailedCommand.Execute(null);
        await Assert.That(cache.TryGetIndex("/d/b.dem")!.RoundIndexState).IsEqualTo(RoundIndexState.Pending);
        await Assert.That(vm.PendingCount).IsEqualTo(1);

        vm.RebuildIndexCommand.Execute(null);
        await Assert.That(vm.StaleCount).IsEqualTo(1).Because("a's rows keep answering under a cleared fingerprint");
        await Assert.That(vm.StatusLine).Contains("1 stale");
    }

    [Test]
    public async Task OnTheBrowser_TheStripSaysThereIsNoLibraryIndex()
    {
        DemoCacheStore cache = new(null);
        using RoundIndexStore sidecars = new(null, cache);
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
        using SituationIndex index = new(cache, sidecars, sources);
        index.Load();

        using SituationsTabViewModel vm = new(index, null, cache, sources, () => RoundIndexTokenSource.Zones, isBrowser: true);

        using (Assert.Multiple())
        {
            await Assert.That(vm.StatusLine).IsEqualTo("session only: no library index in the browser");
            await Assert.That(vm.CanRebuild).IsFalse().Because("no queue to rebuild with");
            await Assert.That(vm.IsBrowser).IsTrue();
        }
    }

    [Test]
    public async Task TheZonesMode_NamesTheMapsThatFellBackToThePawn()
    {
        DemoCacheStore cache = new(null);
        cache.Upsert(ParsedRecord("/d/a.dem", map: "de_nuke"));
        cache.Upsert(ParsedRecord("/d/b.dem", map: "de_dust2"));
        using RoundIndexStore sidecars = new(null, cache);
        RoundIndexBuilderTests.FakeZoneResolver nuke = new("zv-1", _ => null);
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Zones,
            new RoundIndexEvaluatorTests.MapZones(("de_nuke", nuke)));
        using SituationIndex index = new(cache, sidecars, sources);
        index.Load();

        using SituationsTabViewModel vm = new(index, null, cache, sources, () => RoundIndexTokenSource.Zones, isBrowser: false);

        await Assert.That(vm.TokenSourceLine).Contains("no zones for de_dust2");
        await Assert.That(vm.TokenSourceLine).DoesNotContain("de_nuke");
    }
}
