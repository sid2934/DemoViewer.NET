#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Settings;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions.StratBook;

/// <summary>
///     Boots the real composition root with the pack disabled (pack.stratbook = false) and asserts
///     that nothing pack-owned initializes, no pack jobs queue, and the pending-path union excludes
///     pack evaluators. "Off" means: background work stops, resident indexes
///     are never loaded, and startup queues are never enqueued.
///     <see cref="NotInParallelAttribute" /> because the container cases pin the process-global config dir.
/// </summary>
[NotInParallel]
public class PackOffCompositionTests
{
    /// <summary>Pins the config dir to a throwaway folder for the body, with pack.stratbook off.</summary>
    private static Task WithProvider(Func<ServiceProvider, Task> body) =>
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
        }), seedSettingsJson: """
                                {
                                  "Features": { "Overrides": { "pack.stratbook": false } }
                                }
                                """);

    [Test]
    public async Task PackOff_PackGateIsOff()
    {
        await WithProvider(async provider =>
        {
            IFeatureGate gate = provider.GetRequiredService<IFeatureGate>();
            await Assert.That(gate.IsEnabled(StratBookPack.PackFeatureId)).IsFalse()
                .Because("the pack is disabled in this test's settings");
        });
    }

    [Test]
    public async Task PackOff_StratBookPackInstancesAllFieldsAreNull()
    {
        await WithProvider(async provider =>
        {
            StratBookPackInstances instances = provider.GetRequiredService<StratBookPackInstances>();

            using (Assert.Multiple())
            {
                await Assert.That(instances.Situations).IsNull()
                    .Because("the pack is off: situation index is not loaded at startup");
                await Assert.That(instances.Grenades).IsNull()
                    .Because("the pack is off: grenade index is not loaded at startup");
                await Assert.That(instances.Teams).IsNull()
                    .Because("the pack is off: Team Identity does not rebuild at startup");
                await Assert.That(instances.Lineups).IsNull()
                    .Because("the pack is off: Lineup Clip service does not start");
                await Assert.That(instances.TagFacts).IsNull()
                    .Because("the pack is off: Tag Facts Refresher does not start");
                await Assert.That(instances.Watched).IsNull()
                    .Because("the pack is off: Watched Situations service does not start");
                await Assert.That(instances.RoundIndex).IsNull()
                    .Because("the pack is off: Round Index evaluator is not constructed at startup");
                await Assert.That(instances.SuggestedTags).IsNull()
                    .Because("the pack is off: Suggested Tags service is not constructed at startup");
                await Assert.That(instances.GrenadeWalk).IsNull()
                    .Because("the pack is off: Grenade Index evaluator is not constructed at startup");
            }
        });
    }

    // Opening Settings must not build a pack-off page's VM. SuggestedTagsTuningViewModel pulls
    // SuggestedTagsService (which the pack's factory records onto StratBookPackInstances.SuggestedTags),
    // so that field staying null here is the proof the page's factory never ran.
    [Test]
    public async Task PackOff_OpeningSettings_DoesNotBuildTheContributedPages()
    {
        await WithProvider(async provider =>
        {
            SettingsViewModel vm = provider.GetRequiredService<Func<SettingsViewModel>>()();
            try
            {
                StratBookPackInstances instances = provider.GetRequiredService<StratBookPackInstances>();
                await Assert.That(instances.SuggestedTags).IsNull()
                    .Because("the pack is off: Settings must not have resolved SuggestedTagsService");
                await Assert.That(vm.ContributedSettingsPages.Any(p => p.IsBuilt)).IsFalse()
                    .Because("no contributed page builds while its own gate is off");
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    // The pass registry is what is actually responsible for the four fields above staying null: it reads
    // PackContributionSet lazily and never invokes a disabled pack's evaluator factory. Resolving the pass
    // list here (PassIds), something the test above never does, proves the gate itself rather than merely
    // "nothing happened to construct them yet".
    [Test]
    public async Task PackOff_ResolvingThePasses_StillNeverConstructsThePacksThreeEvaluators()
    {
        await WithProvider(async provider =>
        {
            DemoScheduler coordinator = provider.GetRequiredService<DemoScheduler>();

            await Assert.That(coordinator.PassIds).IsEquivalentTo(["library", "highlights", "roundfacts"])
                .Because("the pack is off: its three evaluators are never in the fan-out");

            StratBookPackInstances instances = provider.GetRequiredService<StratBookPackInstances>();
            using (Assert.Multiple())
            {
                await Assert.That(instances.RoundIndex).IsNull();
                await Assert.That(instances.SuggestedTags).IsNull();
                await Assert.That(instances.GrenadeWalk).IsNull();
            }
        });
    }

    // MainViewModel's Match Overview wiring used to resolve GrenadeIndexEvaluator unconditionally
    // (desktop-only, not gated on the pack): building the shell with the pack off must not construct it.
    [Test]
    public async Task PackOff_BuildingMainViewModel_StillDoesNotConstructTheGrenadeEvaluator()
    {
        await WithProvider(async provider =>
        {
            MainViewModel _ = provider.GetRequiredService<MainViewModel>();

            StratBookPackInstances instances = provider.GetRequiredService<StratBookPackInstances>();
            await Assert.That(instances.GrenadeWalk).IsNull()
                .Because("the pack is off: Match Overview's grenades action resolves lazily now");
        });
    }

    [Test]
    public async Task PackOff_PackEvaluatorsReturnFalseOnWants()
    {
        await WithProvider(async provider =>
        {
            const string demoPath = "/test/demo.dem";

            // When the pack is off, these evaluators want nothing because their Wants() predicate
            // gates on the feature flag; Round Facts is the host's, gated on the pack's claim of its ruleset.
            DemoViewer.NET.Services.Facts.RoundFactsEvaluator roundFacts = provider.GetRequiredService<DemoViewer.NET.Services.Facts.RoundFactsEvaluator>();
            DemoViewer.NET.Extensions.StratBook.Services.RoundIndex.RoundIndexEvaluator roundIndex = provider.GetRequiredService<DemoViewer.NET.Extensions.StratBook.Services.RoundIndex.RoundIndexEvaluator>();
            DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags.SuggestedTagsService suggestedTags = provider.GetRequiredService<DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags.SuggestedTagsService>();
            DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook.GrenadeIndexEvaluator grenades = provider.GetRequiredService<DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook.GrenadeIndexEvaluator>();

            using (Assert.Multiple())
            {
                await Assert.That(roundFacts.Wants(demoPath)).IsFalse()
                    .Because("the pack is off: Round Facts evaluator wants nothing");
                await Assert.That(roundIndex.Wants(demoPath)).IsFalse()
                    .Because("the pack is off: Round Index evaluator wants nothing");
                await Assert.That(suggestedTags.Wants(demoPath)).IsFalse()
                    .Because("the pack is off: Suggested Tags service wants nothing");
                await Assert.That(grenades.Wants(demoPath)).IsFalse()
                    .Because("the pack is off: Grenade Index evaluator wants nothing");
            }
        });
    }

    [Test]
    public async Task PackOff_NoPackJobKindsQueued_AtStartup()
    {
        await WithProvider(async provider =>
        {
            IDemoProcessingQueue queue = provider.GetRequiredService<IDemoProcessingQueue>();

            // These are the pack job kinds from QueueJobKind. Only core kinds should be in the queue.
            string[] packKinds = ["StratMining", "StratPreview", "LineupClips", "SuggestionsInbox", "SectionCompute", "TeamsCommand"];

            var queuedItems = queue.Items.ToList();
            foreach (var item in queuedItems)
            {
                string kindString = item.Kind.ToString();
                await Assert.That(packKinds.Contains(kindString)).IsFalse()
                    .Because($"the pack is off: no {kindString} job should be enqueued at startup");
            }
        });
    }

    // ── Helpers (copied from StratBookPackTests) ──────────────────────────────

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

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvpackoff_" + Guid.NewGuid().ToString("N"));
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
}
