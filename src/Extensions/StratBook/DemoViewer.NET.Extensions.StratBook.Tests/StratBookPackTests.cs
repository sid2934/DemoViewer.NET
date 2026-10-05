#region

using DemoViewer.NET.AppTests.Extensions;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.ViewModels.StratBook;
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
        // The extension updater, registered when a config root exists to stage into.
        "DemoViewer.NET.Extensions.Updates.ExtensionUpdateService",
        "DemoViewer.NET.Features.IFeatureGate",
        // The process's extension fault tracker, which the gate takes.
        "DemoViewer.NET.Extensions.ExtensionFaults",
        "DemoViewer.NET.Extensions.Sdk.IExtensionLifecycle",
        // The extension host: the job-kind registry, the shell hub and the pack's own context.
        "DemoViewer.NET.Extensions.JobKindRegistry",
        "DemoViewer.NET.Extensions.ExtensionShellHub",
        "DemoViewer.NET.Extensions.Sdk.IExtensionContext",
        // Every pack's Contribute collected once, read by the module registry and MergedRulesBuild.
        "DemoViewer.NET.Extensions.PackContributionSet",
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
        "DemoViewer.NET.Modules.IStratCapture | Transient",
        "DemoViewer.NET.Modules.StratBook.IStratExport | Transient",
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
        "DemoViewer.NET.Services.DemoProcessing.DemoScheduler",
        // The Strat Book export chip's mount point, shared by the StatusChip
        // contribution and the IStratExport factory's mount callback.
        "DemoViewer.NET.Extensions.StratBook.StratBookExportChipSlot",
        "DemoViewer.NET.Extensions.PackSwitch",
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

    // The two desktop-only settings pages, the one status-chip slot and the one re-index estimate,
    // every one stamped with the pack's own feature id by the collector.
    [Test]
    public async Task ThePack_ContributesTheSettingsPagesTheStatusChipAndTheReindexEstimate()
    {
        await WithProvider(null, async provider =>
        {
            PackContributions pack = provider.GetRequiredService<PackContributionSet>().Packs.Single();

            using (Assert.Multiple())
            {
                await Assert.That(pack.SettingsPages.Select(p => p.Id)).IsEquivalentTo(
                    ["stratbook.suggested-tags-tuning", "stratbook.grenade-index"],
                    TUnit.Assertions.Enums.CollectionOrdering.Matching);
                await Assert.That(pack.SettingsPages.All(p => p.FeatureId == StratBookPack.PackFeatureId)).IsTrue();

                StatusChipContribution chip = pack.StatusChips.Single();
                await Assert.That(chip.Id).IsEqualTo("stratbook.export");
                await Assert.That(chip.FeatureId).IsEqualTo(StratBookPack.PackFeatureId);
                await Assert.That(chip.Source).IsTypeOf<StratBookExportChipSlot>();

                IReindexEstimate estimate = pack.ReindexEstimates.Single();
                await Assert.That(estimate.FeatureId).IsEqualTo(StratBookPack.PackFeatureId);
            }
        });
    }

    // End to end through the real container: GetService<IStratCapture> resolves with the pack
    // on, and a live toggle (no provider rebuild) is enough to make it resolve null again, because the
    // pack's registration reads the gate fresh on every call rather than caching the first answer.
    [Test]
    public async Task TheCaptureService_ResolvesWithThePackOn_AndStopsResolvingOnceToggledOffLive()
    {
        await WithProvider(null, async provider =>
        {
            MainViewModel vm = provider.GetRequiredService<MainViewModel>();
            IModuleContext? context = vm.ModuleContext;

            await Assert.That(context?.GetService<IStratCapture>()).IsNotNull();

            provider.GetRequiredService<SettingsService>().Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);

            await Assert.That(context?.GetService<IStratCapture>()).IsNull();
        });
    }

    // The round_facts ruleset is the pack's. With the pack on the merged set is the whole read
    // (the forward pass is byte-identical to before the pack); off, the ruleset alone leaves it.
    [Test]
    public async Task ThePack_ClaimsTheRoundFactsRuleset_WhichLeavesTheMergedSetOnlyWhenOff()
    {
        await WithProvider(null, async provider =>
        {
            PackContributions pack = provider.GetRequiredService<PackContributionSet>().Packs.Single();
            Modules.Highlights.MergedRulesBuild build = provider.GetRequiredService<Modules.Highlights.MergedRulesBuild>();
            // The app's own read: the shipped rules under the user's overlay, which the locator keys off
            // the real config root, not the test's config dir. The build has already provisioned it.
            string shipped = CS2DemoKit.Analysis.Yaml.RuleSetLocator.ResolveShippedRulesDirectory();
            string read = string.Join(",", CS2DemoKit.Analysis.Yaml.YamlConfigLoader
                .LoadWithOverlay(shipped, CS2DemoKit.Analysis.Yaml.RuleSetLocator.EnsureUserRulesDirectory(shipped))
                .Rulesets.Select(r => r.Id));

            using (Assert.Multiple())
            {
                await Assert.That(pack.Pack.Id).IsEqualTo("net.demoviewer.pack.stratbook");
                await Assert.That(pack.Rulesets.Select(r => r.RulesetId)).IsEquivalentTo(["round_facts"]);
                await Assert.That(build.PackRulesets.Select(r => r.RulesetId)).IsEquivalentTo(["round_facts"]);
                await Assert.That(read).Contains("round_facts");
                await Assert.That(string.Join(",", build.Docs.Select(d => d.Id))).IsEqualTo(read)
                    .Because("pack on: the merged set is the whole read, in read order");
            }
        });

        const string packOff = """{ "Features": { "Overrides": { "pack.stratbook": false } } }""";
        await WithProvider(packOff, async provider =>
        {
            Modules.Highlights.MergedRulesBuild build = provider.GetRequiredService<Modules.Highlights.MergedRulesBuild>();
            using (Assert.Multiple())
            {
                await Assert.That(build.Docs.Select(d => d.Id)).DoesNotContain("round_facts");
                await Assert.That(build.CoreDocs.Select(d => d.Id)).DoesNotContain("round_facts");
                await Assert.That(build.EnabledDoc("round_facts")).IsNull();
            }
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

    // Reads the pack's own contributed module list, not a hand-written one, so an id a module declares
    // but StratBookPack.Features never registered fails the catalog-entry check below, instead of
    // drifting along as a fail-open tab.
    [Test]
    public async Task EveryModule_DeclaresAFeatureId_InTheCatalog_OwnedByThePack()
    {
        await WithProvider(null, async provider =>
        {
            PackContributions pack = provider.GetRequiredService<PackContributionSet>().Packs.Single();
            FakeHost host = new();
            List<string> declaredIds = [];

            foreach (IWorkspaceModule module in pack.Modules)
            {
                foreach (WorkspaceTabDescriptor descriptor in module.CreateTabs(host))
                {
                    await Assert.That(descriptor.FeatureId).IsNotNull()
                        .Because($"{module.Id}'s '{descriptor.TabId}' must declare its own feature id");

                    FeatureDescriptor? catalogEntry = FeatureCatalog.ById(descriptor.FeatureId!);
                    await Assert.That(catalogEntry).IsNotNull()
                        .Because($"'{descriptor.FeatureId}' (declared by {module.Id}) has no catalog entry");
                    await Assert.That(catalogEntry!.OwnerPackId).IsEqualTo(StratBookPack.PackFeatureId)
                        .Because($"'{descriptor.FeatureId}' must be owned by the pack that owns {module.Id}");

                    declaredIds.Add(descriptor.FeatureId!);
                }
            }

            string[] packTabIds = [.. new StratBookPack().Features.Where(f => f.Kind == ExtensionFeatureKind.Tab).Select(f => f.Id)];
            await Assert.That(declaredIds).IsEquivalentTo(packTabIds)
                .Because("every tab id the pack registers must be declared by exactly one module, and vice versa");
        });
    }

    [Test]
    public async Task ThePack_JobKinds_AreTheSixStratBookKinds_UnderTheExtensionsPrefix()
    {
        ExtensionJobKind[] kinds = [.. new StratBookPack().JobKinds];

        await Assert.That(kinds.Select(k => k.Id)).IsEquivalentTo(
        [
            StratBookJobKinds.Mining, StratBookJobKinds.Preview, StratBookJobKinds.LineupClips,
            StratBookJobKinds.SuggestionsInbox, StratBookJobKinds.Teams, StratBookJobKinds.Tuning
        ]);
        await Assert.That(kinds.All(k => k.Id.StartsWith("stratbook.", StringComparison.Ordinal))).IsTrue();
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
            // An explicit override does not survive the pack going off: cascade beats override, through
            // ParentId (tab.dossier) and through OwnerPackId alone (playback2d.tagger, whose ParentId is the
            // core tab.playback2d and so never reaches the pack that way).
            svc.Write(s => s.Features.Overrides["tab.dossier"] = true);
            svc.Write(s => s.Features.Overrides["playback2d.tagger"] = true);
            svc.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);

            await Assert.That(gate.IsEnabled(StratBookPack.PackFeatureId)).IsFalse();
            foreach ((string id, string parent) in _movedIds)
            {
                await Assert.That(gate.IsEnabled(id)).IsFalse()
                    .Because($"{id} is owned by the pack and goes off with it, whatever {parent} resolves to");
            }

            await Assert.That(gate.IsEnabled("playback2d.tagger")).IsFalse()
                .Because("the owning-pack rule beats an explicit override=true, the same as ParentId cascade does");

            // Core tabs are untouched, and so is a core SUB-feature sharing tab.playback2d with the two
            // pack-owned ones that just went off above: the rule discriminates by owner, not by ParentId.
            await Assert.That(gate.IsEnabled("tab.playback2d")).IsTrue();
            await Assert.That(gate.IsEnabled("tab.library")).IsTrue();
            await Assert.That(gate.IsEnabled("playback2d.annotations")).IsTrue()
                .Because("core, not pack-owned, even though its parent is the same tab.playback2d");

            svc.Write(s => s.Features.Overrides.Remove(StratBookPack.PackFeatureId));
            await Assert.That(gate.IsEnabled("tab.stratbook")).IsTrue().Because("the pack back on lets the tabs through");
            await Assert.That(gate.IsEnabled("stratbook.export")).IsTrue();
            await Assert.That(gate.IsEnabled("playback2d.tagger")).IsTrue()
                .Because("the owning-pack rule only forces off; it never forces on past the tab's own state");
            await Assert.That(gate.IsEnabled("playback2d.suggestedtags")).IsTrue();
        });
    }

    // The owning-pack rule (7.2/7.6): a sub-feature docked in a CORE tab still carries its contributing
    // pack's id and cascades off with it, independent of ParentId. Structural half of the rule (the stamp
    // itself); the behavioural half is PackOff_CascadesEveryTab_AndEverySubFeatureUnderThem_Off above.
    [Test]
    public async Task Build_StampsEveryPackDescriptor_WithItsOwnerPackId_ExceptThePackRowItself()
    {
        FeatureDescriptor[] built = FeatureCatalog.Build(FeaturePacks.Default);
        HashSet<string> packIds = [.. new StratBookPack().Features.Select(f => f.Id)];

        FeatureDescriptor packRow = built.Single(d => d.Id == StratBookPack.PackFeatureId);
        await Assert.That(packRow.OwnerPackId).IsNull().Because("a pack is not owned by itself");

        foreach (FeatureDescriptor d in built.Where(d => d.Id != StratBookPack.PackFeatureId))
        {
            if (packIds.Contains(d.Id))
            {
                await Assert.That(d.OwnerPackId).IsEqualTo(StratBookPack.PackFeatureId)
                    .Because($"{d.Id} is contributed by the pack");
            }
            else
            {
                await Assert.That(d.OwnerPackId).IsNull().Because($"{d.Id} is core, not pack-owned");
            }
        }

        // playback2d.tagger and playback2d.suggestedtags are the two that prove the rule earns its keep:
        // their ParentId is the core tab.playback2d, not the pack.
        await Assert.That(built.Single(d => d.Id == "playback2d.tagger").ParentId).IsEqualTo("tab.playback2d");
        await Assert.That(built.Single(d => d.Id == "playback2d.tagger").OwnerPackId).IsEqualTo(StratBookPack.PackFeatureId);
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
                await Assert.That(vm.StratBookHub().Sections.Sections).IsEmpty();
                await Assert.That(vm.LibraryTab.HasTeamsView).IsFalse();
                await Assert.That(vm.LibraryTab.Sections.Sections).IsEmpty();
                await Assert.That(vm.LibraryTab.Filters).IsEmpty()
                    .Because("the real cascade through pack.stratbook hides it the same way FakeGate does in StratBookShellTests");
                await Assert.That(vm.LibraryTab.HasBadge).IsFalse();
            }
        });

        // The control: the same container with the pack on has the hub and every section.
        await WithProvider(null, async provider =>
        {
            MainViewModel vm = provider.GetRequiredService<MainViewModel>();
            using (Assert.Multiple())
            {
                await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains(StratBookHubViewModel.TabId);
                await Assert.That(vm.StratBookHub().Sections.Sections.Count).IsEqualTo(7);
                await Assert.That(vm.LibraryTab.HasTeamsView).IsTrue();
                await Assert.That(vm.LibraryTab.Filters).IsNotEmpty();
                await Assert.That(vm.LibraryTab.HasBadge).IsTrue();
            }
        });
    }

    private static readonly string[] _railOrder =
    [
        "stratbook.browser", "situations.search", "tagger.matrix", "utilitybook.browser", "review.queue", "dossier.browser",
        "suggested.inbox"
    ];

    private static readonly string[] _railHeaders = ["Strats", "Situations", "Tags", "Utility", "Review", "Dossier", "Suggested"];

    // The hub is a contribution now, and the rail must read exactly as it did when the shell built it.
    // The rail's entries are the modules' own descriptors, so the badge a module moves is the badge the rail shows.
    [Test]
    public async Task TheHub_IsThePacksHostTab_AndTheRailKeepsItsSevenSectionsInOrder()
    {
        await WithProvider(null, async provider =>
        {
            PackContributions pack = provider.GetRequiredService<PackContributionSet>().Packs.Single();
            MainViewModel vm = provider.GetRequiredService<MainViewModel>();
            HostTabContribution host = pack.HostTabs.Single();
            IReadOnlyList<WorkspaceTabDescriptor> rail = vm.StratBookHub().Sections.Sections;
            WorkspaceTabDescriptor hubTab = vm.Tabs.Single(t => t.TabId == StratBookHubViewModel.TabId);

            using (Assert.Multiple())
            {
                await Assert.That(host.HostId).IsEqualTo(StratBookHubViewModel.HostId);
                await Assert.That(host.FeatureId).IsEqualTo(StratBookPack.PackFeatureId);
                await Assert.That(host.RailLabel).IsEqualTo("STRAT BOOK");
                await Assert.That(vm.StratBookHub().RailLabel).IsEqualTo("STRAT BOOK")
                    .Because("the shell hands the contribution's label to the VM the view binds");
                await Assert.That(hubTab.Header).IsEqualTo("Strat Book");
                await Assert.That(hubTab.Order).IsEqualTo(4).Because("after 2D Playback, before Authoring");
                await Assert.That(hubTab.FeatureId).IsEqualTo(StratBookPack.PackFeatureId);
                await Assert.That(hubTab.ViewModelFactory!()).IsSameReferenceAs(vm.StratBookHub())
                    .Because("the strip tab's VM is the one the contribution built, not a second hub");
                await Assert.That(rail.Select(s => s.TabId)).IsEquivalentTo(_railOrder, TUnit.Assertions.Enums.CollectionOrdering.Matching);
                await Assert.That(rail.Select(s => s.Header)).IsEquivalentTo(_railHeaders, TUnit.Assertions.Enums.CollectionOrdering.Matching);
                await Assert.That(rail.All(s => s.HostId == StratBookHubViewModel.HostId)).IsTrue()
                    .Because("every rail entry is a pack module's own descriptor, so the badge a module moves is the badge the rail shows");
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

    // The embedded manifest names this pack, and the shipped build passes its own check.
    [Test]
    public async Task ThePack_Manifest_NamesItself_AndIsCompatibleWithThisBuild()
    {
        StratBookPack pack = new();
        ExtensionManifest manifest = ExtensionManifests.Of(pack);
        using (Assert.Multiple())
        {
            await Assert.That(manifest.Id).IsEqualTo(StratBookPack.PackId);
            // Stamped from src/Extensions/StratBook/version.json at build; the 0.x line until the first major.
            await Assert.That(manifest.Version.Major).IsEqualTo(0);
            await Assert.That(manifest.EntryType).IsEqualTo(typeof(StratBookPack).FullName);
            await Assert.That(PackStatus.Evaluate(pack, ExtensionHost.Current).IsCompatible).IsTrue();
        }
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

    // An SDK feature has no Required flag and no group, so neither core rule can be broken from an extension.
    [Test]
    public async Task Build_ComposesTheShippedPack_WithoutMovingTheCoreGroupLeaders()
    {
        await Assert.That(FeatureCatalog.Build(FeaturePacks.Default).Count(d => d.Scope == FeatureScope.Pack)).IsEqualTo(1);
        await Assert.That(FeatureCatalog.Build(FeaturePacks.Default).Where(d => d.OwnerPackId is not null).All(d => !d.Required)).IsTrue();
        await Assert.That(FeatureCatalog.GroupLeader(FeatureCatalog.GroupParserDeepDive)!.Id).IsEqualTo("parser.hex");
    }

    [Test]
    public async Task Build_RefusesTheParentRules_ATabUnderATab_AndASubFeatureUnderAPack()
    {
        FakePack tabUnderTab = new("pack.tt", [Pack("pack.tt"), Tab("tab.tt", "tab.library")]);
        FakePack subUnderPack = new("pack.sp",
            [Pack("pack.sp"), Tab("tab.sp", "pack.sp") with { Id = "sub.sp", Kind = ExtensionFeatureKind.SubFeature }]);
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

    private static ExtensionFeature Pack(string id) =>
        new(id, ExtensionFeatureKind.Extension, id, id, null, AudienceDefaults.Everyone);

    private static ExtensionFeature Tab(string id, string? parent) =>
        new(id, ExtensionFeatureKind.Tab, id, id, parent, AudienceDefaults.Everyone);

    // CreateTabs never reads Context or logs; this is only here to satisfy the parameter.
    private sealed class FakeHost : IModuleHost
    {
        public IModuleContext Context => null!;
        public bool HasCapability(string capability) => true;

        public void Log(ModuleLogLevel level, string message)
        {
        }
    }

    private sealed class FakePack(string featureId, ExtensionFeature[] features) : IExtension, IManifestSource
    {
        public string Id => "net.demoviewer.test." + featureId;
        public string FeatureId => featureId;
        public ExtensionManifest Manifest => FakeManifests.For(Id);
        public IEnumerable<ExtensionFeature> Features => features;

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }
}
