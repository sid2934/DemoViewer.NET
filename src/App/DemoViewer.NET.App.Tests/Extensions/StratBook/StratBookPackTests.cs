#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests.Extensions.StratBook;

/// <summary>
///     The Strat Book pack is plumbing, not behaviour: with it on, the composition root registers the same
///     services and the same modules in the same order as before it existed, and every former catalog id
///     is still there under <c>pack.stratbook</c>. With it off, the gate cascades every section and the
///     shell shows no hub. The service and module lists were captured on the commit before the pack and
///     are pinned here; a change to either is a deliberate change to what the app composes.
///     <see cref="NotInParallelAttribute" /> because the container cases pin the process-global config
///     dir, as <c>AppCompositionRootTests</c> does.
/// </summary>
[NotInParallel]
public class StratBookPackTests
{
    // Every registration the composition root made before the pack: Type.ToString() and its lifetime.
    // Every row is a singleton apart from the two options rows marked otherwise.
    private static readonly string[] _serviceTypesBeforeThePack =
    [
        "DemoViewer.NET.Configuration.SettingsService",
        "Microsoft.Extensions.Options.IOptions`1[TOptions]",
        "Microsoft.Extensions.Options.IOptionsSnapshot`1[TOptions] | Scoped",
        "Microsoft.Extensions.Options.IOptionsMonitor`1[TOptions]",
        "Microsoft.Extensions.Options.IOptionsFactory`1[TOptions] | Transient",
        "Microsoft.Extensions.Options.IOptionsMonitorCache`1[TOptions]",
        "Microsoft.Extensions.Options.IOptionsChangeTokenSource`1[DemoViewer.NET.Configuration.AppSettings]",
        "Microsoft.Extensions.Options.IConfigureOptions`1[DemoViewer.NET.Configuration.AppSettings]",
        "DemoViewer.NET.Features.IFeatureGate",
        "DemoViewer.NET.Extensions.PackLifecycleRegistry",
        "DemoViewer.NET.Extensions.IPackLifecycle",
        "DemoViewer.NET.Extensions.StratBook.StratBookPackInstances",
        "DemoViewer.NET.Theming.ThemeRegistry",
        "DemoViewer.NET.Services.IWindowService",
        "System.Func`1[DemoViewer.NET.ViewModels.Settings.SettingsViewModel]",
        "System.Func`1[DemoViewer.NET.ViewModels.Setup.FirstRunWizardViewModel]",
        "DemoViewer.NET.Services.HeavyJobGate",
        "DemoViewer.NET.Modules.Highlights.MergedRulesBuild",
        "DemoViewer.NET.Services.DemoProcessing.DemoProcessingQueue",
        "DemoViewer.NET.Services.DemoProcessing.IDemoProcessingQueue",
        "DemoViewer.NET.Services.DemoCache.DemoCacheStore",
        "DemoViewer.NET.Modules.Library.DemoLibraryService",
        "DemoViewer.NET.Modules.Highlights.IHighlightHarvester",
        "DemoViewer.NET.ViewModels.Highlights.HighlightsTabViewModel",
        "DemoViewer.NET.ViewModels.Highlights.HighlightScanStatusViewModel",
        "DemoViewer.NET.Modules.Highlights.HighlightScanService",
        "DemoViewer.NET.Services.RoundFacts.RulesRoundFactsRulesetIdentity",
        "DemoViewer.NET.Services.RoundFacts.IRoundFactsRulesetIdentity",
        "DemoViewer.NET.Services.RoundFacts.IRoundFactsRowSource",
        "DemoViewer.NET.Services.RoundFacts.RoundFactsEvaluator",
        "DemoViewer.NET.Services.RoundFacts.IRoundFactsSource",
        "DemoViewer.NET.Services.RoundIndex.IZonePlaceResolverSource",
        "DemoViewer.NET.Services.RoundIndex.RoundIndexPlaceSources",
        "DemoViewer.NET.Services.RoundIndex.RoundIndexStore",
        "DemoViewer.NET.Services.RoundIndex.RoundIndexEvaluator",
        "DemoViewer.NET.Services.RoundIndex.SituationIndex",
        "DemoViewer.NET.Services.RoundIndex.ISituationIndex",
        "DemoViewer.NET.Modules.Situations.ISituationPlayback",
        "DemoViewer.NET.ViewModels.Situations.SituationsTabViewModel",
        "DemoViewer.NET.Services.Review.ReviewQueue",
        "DemoViewer.NET.ViewModels.Review.ReviewQueueTabViewModel",
        "DemoViewer.NET.Modules.SuggestedTags.SuggestedInboxService",
        "DemoViewer.NET.ViewModels.SuggestedTags.SuggestedInboxViewModel",
        "DemoViewer.NET.Modules.Situations.WatchedSituationsService",
        "DemoViewer.NET.Services.Tags.TagStore",
        "DemoViewer.NET.Services.Tags.TagFactsRefresher",
        "DemoViewer.NET.ViewModels.RoundTagger.TagMatrixTabViewModel",
        "DemoViewer.NET.Modules.SuggestedTags.ProposalStore",
        "DemoViewer.NET.Modules.SuggestedTags.SiteRegionStore",
        "DemoViewer.NET.Modules.SuggestedTags.ProfileStore",
        "DemoViewer.NET.Modules.SuggestedTags.SuggestedTagsService",
        "DemoViewer.NET.Modules.SuggestedTags.SuggestedTagsTuningService",
        "DemoViewer.NET.Services.Tags.TagPaletteStore",
        "DemoViewer.NET.Services.Teams.TeamIdentityService",
        "DemoViewer.NET.Services.Provenance.DemoProvenanceSource",
        "DemoViewer.NET.Services.Provenance.IDemoProvenanceSource",
        "DemoViewer.NET.ViewModels.Teams.TeamsTabViewModel",
        "DemoViewer.NET.Services.Strats.StratStore",
        "DemoViewer.NET.Services.Strats.CalloutResolverSource",
        "DemoViewer.NET.Services.Strats.StratEvidenceService",
        "DemoViewer.NET.Services.Strats.Mining.StratMiningService",
        "DemoViewer.NET.ViewModels.StratBook.StratBookLayout",
        "DemoViewer.NET.ViewModels.StratBook.StratBookTabViewModel",
        "DemoViewer.NET.Modules.Situations.ISituationResultWalk",
        "DemoViewer.NET.Modules.Situations.IFindRoundsLikeThis",
        "DemoViewer.NET.Modules.UtilityBook.GrenadeIndexEvaluator",
        "DemoViewer.NET.Modules.UtilityBook.GrenadeIndex",
        "DemoViewer.NET.ViewModels.UtilityBook.UtilityBookTabViewModel",
        "DemoViewer.NET.Services.Teams.VetoHistoryStore",
        "DemoViewer.NET.Services.Teams.DossierNotesStore",
        "DemoViewer.NET.ViewModels.Dossier.DossierTabViewModel",
        "DemoViewer.NET.Modules.UtilityBook.LineupClipService",
        "DemoViewer.NET.Services.DemoProcessing.DemoEvaluationCoordinator",
        "DemoViewer.NET.Services.RecentFilesStore",
        "DemoViewer.NET.Modules.ModuleRegistry",
        "DemoViewer.NET.ViewModels.Shell.MainViewModel"
    ];

    // The module registry before the pack, in registration order.
    private const string ModulesBeforeThePack =
        "net.demoviewer.playback2d, net.demoviewer.ruleworkbench, net.demoviewer.highlights, "
        + "net.demoviewer.situations, net.demoviewer.teams, net.demoviewer.review, net.demoviewer.suggested, "
        + "net.demoviewer.roundtagger, net.demoviewer.stratbook, net.demoviewer.utilitybook, net.demoviewer.dossier";

    // Every id the static catalog used to hold for the Strat Book, with the parent it has now.
    private static readonly (string Id, string Parent)[] _movedIds =
    [
        ("tab.situations", StratBookPack.PackFeatureId),
        ("tab.teams", StratBookPack.PackFeatureId),
        ("tab.review", StratBookPack.PackFeatureId),
        ("tab.suggested", StratBookPack.PackFeatureId),
        ("tab.tagger", StratBookPack.PackFeatureId),
        ("tab.stratbook", StratBookPack.PackFeatureId),
        ("tab.utilitybook", StratBookPack.PackFeatureId),
        ("tab.dossier", StratBookPack.PackFeatureId),
        ("playback2d.tagger", "tab.playback2d"),
        ("playback2d.suggestedtags", "tab.playback2d"),
        ("stratbook.export", "tab.stratbook"),
        ("stratbook.routing", "tab.stratbook")
    ];

    [Test]
    public async Task ThePack_RegistersTheSameServiceTypes_TheCompositionRootDidBeforeIt()
    {
        await WithConfigDir(async _ =>
        {
            ServiceCollection services = App.ComposeServices(new DesktopWindowService(() => null), FeaturePacks.Default);
            // Compared as sorted text: registration order changed (the pack registers last), the set must not.
            string registered = string.Join("\n", services
                .Select(d => d.ServiceType + " | " + d.Lifetime)
                .Order(StringComparer.Ordinal));
            string expected = string.Join("\n", _serviceTypesBeforeThePack
                .Select(row => row.Contains(" | ", StringComparison.Ordinal) ? row : row + " | Singleton")
                .Order(StringComparer.Ordinal));
            await Assert.That(registered).IsEqualTo(expected)
                .Because("moving a registration into the pack must not add, drop, retype or re-scope a service");
        });
    }

    [Test]
    public async Task ThePack_ContributesTheSameModules_InTheSameOrder()
    {
        await WithProvider(null, async provider =>
        {
            string modules = string.Join(", ", provider.GetRequiredService<ModuleRegistry>().Modules.Select(m => m.Id));
            await Assert.That(modules).IsEqualTo(ModulesBeforeThePack)
                .Because("the shell's tab order and section order follow registration order");
        });
    }

    [Test]
    public async Task TheCatalog_HoldsEveryFormerStratBookId_UnderThePack()
    {
        FeatureDescriptor? pack = FeatureCatalog.ById(StratBookPack.PackFeatureId);
        await Assert.That(pack).IsNotNull();
        await Assert.That(pack!.Scope).IsEqualTo(FeatureScope.Pack);
        await Assert.That(pack.ParentId).IsNull();
        await Assert.That(pack.Label).IsEqualTo("Strat Book extension");

        foreach ((string id, string parent) in _movedIds)
        {
            FeatureDescriptor? descriptor = FeatureCatalog.ById(id);
            await Assert.That(descriptor).IsNotNull().Because($"{id} is a persisted override key and must survive the move");
            await Assert.That(descriptor!.ParentId).IsEqualTo(parent).Because($"{id} cascades from {parent}");
            await Assert.That(descriptor.Scope)
                .IsEqualTo(id.StartsWith("tab.", StringComparison.Ordinal) ? FeatureScope.Tab : FeatureScope.SubFeature);
        }

        string[] packFeatureIds = [.. new StratBookPack().Features.Select(f => f.Id)];
        await Assert.That(packFeatureIds)
            .IsEquivalentTo([StratBookPack.PackFeatureId, .. _movedIds.Select(m => m.Id)]);
        await Assert.That(FeatureCatalog.Children(StratBookPack.PackFeatureId).Select(d => d.Id))
            .IsEquivalentTo(_movedIds.Where(m => m.Parent == StratBookPack.PackFeatureId).Select(m => m.Id));
    }

    [Test]
    public async Task ThePack_ResolvesOn_ByDefault_ForEveryCategory()
    {
        await WithGate(async (svc, gate) =>
        {
            foreach (UserCategory category in Enum.GetValues<UserCategory>())
            {
                svc.Write(s => s.UserCategory = category);
                await Assert.That(gate.IsEnabled(StratBookPack.PackFeatureId)).IsTrue()
                    .Because($"the extension is on by default for {category}");
                await Assert.That(gate.IsEnabled("tab.stratbook")).IsTrue();
            }
        });
    }

    [Test]
    public async Task PackOff_CascadesEveryTab_AndEverySubFeatureUnderThem_Off()
    {
        await WithGate(async (svc, gate) =>
        {
            svc.Write(s => s.UserCategory = UserCategory.Developer);
            // An explicit per-tab override does not survive the pack going off: cascade beats override.
            svc.Write(s => s.Features.Overrides["tab.dossier"] = true);
            svc.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);

            await Assert.That(gate.IsEnabled(StratBookPack.PackFeatureId)).IsFalse();
            foreach ((string id, string parent) in _movedIds.Where(m => m.Parent != "tab.playback2d"))
            {
                await Assert.That(gate.IsEnabled(id)).IsFalse().Because($"{id} cascades off through {parent}");
            }

            // The two docked in 2D Playback keep tab.playback2d as their parent and so stay on here.
            await Assert.That(gate.IsEnabled("playback2d.tagger")).IsTrue();
            await Assert.That(gate.IsEnabled("playback2d.suggestedtags")).IsTrue();
            // Core tabs are untouched.
            await Assert.That(gate.IsEnabled("tab.playback2d")).IsTrue();
            await Assert.That(gate.IsEnabled("tab.library")).IsTrue();

            svc.Write(s => s.Features.Overrides.Remove(StratBookPack.PackFeatureId));
            await Assert.That(gate.IsEnabled("tab.stratbook")).IsTrue().Because("the pack back on lets the tabs through");
            await Assert.That(gate.IsEnabled("stratbook.export")).IsTrue();
        });
    }

    [Test]
    public async Task PackOff_TheShellShowsNoHub_AndNoStratBookSection()
    {
        const string seed = """
                            {
                              "Features": {
                                "Overrides": { "pack.stratbook": false }
                              }
                            }
                            """;

        await WithProvider(seed, async provider =>
        {
            MainViewModel vm = provider.GetRequiredService<MainViewModel>();
            using (Assert.Multiple())
            {
                await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain(StratBookHubViewModel.TabId)
                    .Because("a rail with nothing on it has no tab");
                await Assert.That(vm.StratBookHub.Sections.Sections).IsEmpty();
                await Assert.That(vm.LibraryTab.HasTeamsView).IsFalse();
                await Assert.That(vm.LibraryTab.Sections.Sections).IsEmpty();
            }
        });

        // The control: the same container with the pack on has the hub and every section.
        await WithProvider(null, async provider =>
        {
            MainViewModel vm = provider.GetRequiredService<MainViewModel>();
            using (Assert.Multiple())
            {
                await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains(StratBookHubViewModel.TabId);
                await Assert.That(vm.StratBookHub.Sections.Sections.Count).IsEqualTo(7);
                await Assert.That(vm.LibraryTab.HasTeamsView).IsTrue();
            }
        });
    }

    [Test]
    public async Task UnknownPackId_ResolvesOff_UnknownOtherId_StillResolvesOn()
    {
        await WithGate(async (_, gate) =>
        {
            await Assert.That(gate.IsEnabled("pack.does.not.exist")).IsFalse()
                .Because("a pack id gates background work, so a typo must not enable it");
            await Assert.That(gate.IsEnabled("does.not.exist")).IsTrue().Because("every other unknown id fails open");
        });
    }

    [Test]
    public async Task ThePackId_IsNotTheModuleId()
    {
        StratBookPack pack = new();
        await Assert.That(pack.Id).IsEqualTo("net.demoviewer.pack.stratbook");
        await Assert.That(pack.Id).IsNotEqualTo(new Modules.StratBook.StratBookModule(() => null!).Id)
            .Because("the pack and its Strats module are different persisted keys");
    }

    [Test]
    public async Task Build_RefusesAPack_WhoseFeatureIdIsNotExactlyOnePackRow()
    {
        FakePack none = new("pack.none", [Tab("tab.none", "pack.none")]);
        FakePack two = new("pack.two", [Pack("pack.two"), Pack("pack.two")]);
        FakePack wrongScope = new("pack.scope", [Tab("pack.scope", null)]);

        await Assert.That(Message(() => FeatureCatalog.Build([none]))).Contains("exactly one Pack-scope descriptor");
        await Assert.That(Message(() => FeatureCatalog.Build([two]))).Contains("exactly one Pack-scope descriptor");
        await Assert.That(Message(() => FeatureCatalog.Build([wrongScope]))).Contains("exactly one Pack-scope descriptor");
    }

    [Test]
    public async Task Build_RefusesRequired_OnAPackRow_OrOnATabUnderAPack()
    {
        FakePack requiredPack = new("pack.req", [Pack("pack.req") with { Required = true }]);
        FakePack requiredTab = new("pack.reqtab", [Pack("pack.reqtab"), Tab("tab.reqtab", "pack.reqtab") with { Required = true }]);

        await Assert.That(Message(() => FeatureCatalog.Build([requiredPack]))).Contains("may not be Required");
        await Assert.That(Message(() => FeatureCatalog.Build([requiredTab]))).Contains("may not be Required");
    }

    [Test]
    public async Task Build_RefusesAPackRow_InACoreGroup()
    {
        FakePack grouped = new("pack.grp",
            [Pack("pack.grp"), Tab("tab.grp", "pack.grp") with { GroupId = FeatureCatalog.GroupParserDeepDive }]);

        await Assert.That(Message(() => FeatureCatalog.Build([grouped]))).Contains("may not join core group");
        // The shipped pack passes every rule, and its rows keep the core leaders where they are.
        await Assert.That(FeatureCatalog.Build(FeaturePacks.Default).Count(d => d.Scope == FeatureScope.Pack)).IsEqualTo(1);
        await Assert.That(FeatureCatalog.GroupLeader(FeatureCatalog.GroupParserDeepDive)!.Id).IsEqualTo("parser.hex");
    }

    [Test]
    public async Task Build_RefusesTheParentRules_ATabUnderATab_AndASubFeatureUnderAPack()
    {
        FakePack tabUnderTab = new("pack.tt", [Pack("pack.tt"), Tab("tab.tt", "tab.library")]);
        FakePack subUnderPack = new("pack.sp",
            [Pack("pack.sp"), Tab("tab.sp", "pack.sp") with { Id = "sub.sp", Scope = FeatureScope.SubFeature }]);
        FakePack unknownParent = new("pack.up", [Pack("pack.up"), Tab("tab.up", "pack.missing")]);

        await Assert.That(Message(() => FeatureCatalog.Build([tabUnderTab]))).Contains("may not have 'tab.library'");
        await Assert.That(Message(() => FeatureCatalog.Build([subUnderPack]))).Contains("may not have 'pack.sp'");
        await Assert.That(Message(() => FeatureCatalog.Build([unknownParent]))).Contains("unknown parent");
    }

    [Test]
    public void TheCatalog_IsComposedOnce_AndRefusesADifferentSet()
    {
        // The default composition is idempotent; a different pack list is a programming error.
        FeatureCatalog.Compose(FeaturePacks.Default);
        Assert.Throws<InvalidOperationException>(() => FeatureCatalog.Compose([]));
    }

    // The live SettingsService to IOptionsMonitor to FeatureGate chain over a throwaway config dir, with
    // Changed raised inline (no dispatcher), as FeatureGateTests does.
    private static async Task WithGate(Func<SettingsService, FeatureGate, Task> body)
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            ServiceCollection services = new();
            services.Configure<AppSettings>(svc.Configuration);
            using ServiceProvider sp = services.BuildServiceProvider();
            using FeatureGate gate = new(sp.GetRequiredService<IOptionsMonitor<AppSettings>>(), false);
            await body(svc, gate);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Pins the config dir to a throwaway folder for the body, seeding settings.json when asked.
    private static async Task WithConfigDir(Func<string, Task> body, string? seedSettingsJson = null)
    {
        string dir = NewTempDir();
        if (seedSettingsJson is not null)
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), seedSettingsJson);
        }

        string? prev = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);
        try
        {
            await body(dir);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, prev);
            Cleanup(dir);
        }
    }

    // The real container on the headless UI thread, disposed after the body (AppCompositionRootTests' shape).
    private static Task WithProvider(string? seedSettingsJson, Func<ServiceProvider, Task> body) =>
        WithConfigDir(_ => HeadlessSession.RunOnUi(async () =>
        {
            ServiceProvider provider = App.BuildServices(new DesktopWindowService(() => null));
            try
            {
                await body(provider);
            }
            finally
            {
                provider.Dispose();
            }
        }), seedSettingsJson);

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvstratpack_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static string Message(Func<object> build) =>
        Assert.Throws<InvalidOperationException>(() => build()).Message;

    private static FeatureDescriptor Pack(string id) =>
        new(id, FeatureScope.Pack, id, id, null, null, false, FeatureCatalog.Defaults(true, true, true));

    private static FeatureDescriptor Tab(string id, string? parent) =>
        new(id, FeatureScope.Tab, id, id, parent, null, false, FeatureCatalog.Defaults(true, true, true));

    private sealed class FakePack(string featureId, FeatureDescriptor[] features) : IFeaturePack
    {
        public string Id => "net.demoviewer.test." + featureId;
        public string FeatureId => featureId;
        public IEnumerable<FeatureDescriptor> Features => features;

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IPackContributions contributions, IServiceProvider sp)
        {
        }
    }
}
