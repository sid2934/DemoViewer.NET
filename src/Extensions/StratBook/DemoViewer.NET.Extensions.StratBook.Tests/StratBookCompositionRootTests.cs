#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The pack-reaching half of the composition-root smoke test, split out of
///     <c>AppCompositionRootTests</c> in App.Tests: the hub layout, the evaluator fan-out order (on and
///     off), the Situation/Grenade index coordinator wiring, and the pack-off evaluator/badge case. Each
///     case builds the REAL container the same way the core file does; see that class for why
///     <see cref="NotInParallelAttribute" /> and the per-case temp config dir.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class StratBookCompositionRootTests
{
    // Builds a real container against a throwaway config dir, runs the assertion body on the UI thread, and
    // disposes the provider (and thus MainViewModel: detaches the static parser event, stops its timer).
    // Duplicated from AppCompositionRootTests rather than shared: the two classes live in different
    // assemblies and neither references the other's test project.
    private static async Task WithProvider(IWindowService windowService, Func<ServiceProvider, Task> body,
        string? seedSettingsJson = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvcomproot_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Seeded BEFORE the container is built: BuildServices reads the config, and ValidateOnBuild
        // eagerly constructs the shell, so a persisted session has to already be on disk to influence it.
        if (seedSettingsJson is not null)
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), seedSettingsJson);
        }

        string? prev = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                ServiceProvider provider = App.BuildServices(windowService);
                try
                {
                    await body(provider);
                }
                finally
                {
                    provider.Dispose();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, prev);
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                /* best-effort cleanup */
            }
        }
    }

    // The hub rail and the Strats section's list collapse through one object, the one the session file keeps.
    [Test]
    public async Task TheStratBookHub_AndTheStratsSection_ShareOneLayout()
    {
        await WithProvider(new DesktopWindowService(() => null), async provider =>
        {
            MainViewModel vm = provider.GetRequiredService<MainViewModel>();
            ViewModels.StratBook.StratBookTabViewModel strats = provider.GetRequiredService<ViewModels.StratBook.StratBookTabViewModel>();
            await Assert.That(strats.Layout).IsSameReferenceAs(vm.StratBookHub().Layout);
        });
    }

    // The fan-out order is a contract: an evaluator may read what the one before
    // it wrote in the same pass, so the round index, when it lands, goes after round facts and reads them.
    [Test]
    public async Task EvaluatorFanOutOrder_IsLibraryThenHighlightsThenRoundFactsThenRoundIndexThenSuggestedTagsThenGrenades()
    {
        await WithProvider(new DesktopWindowService(() => null), async provider =>
        {
            Services.DemoProcessing.DemoEvaluationCoordinator coordinator =
                provider.GetRequiredService<Services.DemoProcessing.DemoEvaluationCoordinator>();
            await Assert.That(coordinator.EvaluatorIds)
                .IsEquivalentTo(new[]
                {
                    "library", "highlights", Services.RoundFacts.RoundFactsEvaluator.EvaluatorId,
                    Services.RoundIndex.RoundIndexEvaluator.EvaluatorId,
                    Modules.SuggestedTags.SuggestedTagsService.EvaluatorId,
                    Modules.UtilityBook.GrenadeIndexEvaluator.EvaluatorId
                });
            await Assert.That(coordinator.EvaluatorIds[2]).IsEqualTo("roundfacts");
            // The index reads the rows Round Facts wrote in the same pass, so it must come after it.
            await Assert.That(coordinator.EvaluatorIds[3]).IsEqualTo("roundindex");
            // Suggested Tags reads the index written in the same pass, so it comes last.
            await Assert.That(coordinator.EvaluatorIds[4]).IsEqualTo("suggestedtags");
            // The grenade walk reads nothing the others write; last so it never delays one that does.
            await Assert.That(coordinator.EvaluatorIds[5]).IsEqualTo("grenades");

            // The order came from the registry's resolve: confirm it actually built the four pack
            // evaluators, not merely listed their ids.
            StratBookPackInstances instances =
                provider.GetRequiredService<StratBookPackInstances>();
            using (Assert.Multiple())
            {
                await Assert.That(instances.RoundFacts).IsNotNull();
                await Assert.That(instances.RoundIndex).IsNotNull();
                await Assert.That(instances.SuggestedTags).IsNotNull();
                await Assert.That(instances.GrenadeWalk).IsNotNull();
            }

            await Assert.That(provider.GetRequiredService<Modules.UtilityBook.GrenadeIndexEvaluator>().Coordinator)
                .IsSameReferenceAs(coordinator);
            await Assert.That(provider.GetRequiredService<Modules.SuggestedTags.SuggestedTagsService>().Coordinator)
                .IsSameReferenceAs(coordinator);
            await Assert.That(provider.GetRequiredService<Services.RoundFacts.IRoundFactsSource>()).IsNotNull();
            await Assert.That(provider.GetRequiredService<Services.RoundIndex.ISituationIndex>()).IsNotNull();
            await Assert.That(provider.GetRequiredService<Services.Provenance.IDemoProvenanceSource>()).IsNotNull();
            await Assert.That(provider.GetRequiredService<Services.Tags.TagFactsRefresher>()).IsNotNull();
            await Assert.That(provider.GetRequiredService<Services.RoundIndex.RoundIndexEvaluator>().Coordinator)
                .IsSameReferenceAs(coordinator);
        });
    }

    // The converse: with the pack off, the registry's gate keeps the four pack evaluators out of the
    // resolved order, and their factories are never invoked. Never GetRequiredService a pack evaluator
    // directly here, which would construct it regardless of the gate.
    [Test]
    public async Task EvaluatorFanOutOrder_WithThePackOff_IsOnlyLibraryAndHighlights_AndBuildsNothingPackOwned()
    {
        const string packOff = """{ "Features": { "Overrides": { "pack.stratbook": false } } }""";
        await WithProvider(new DesktopWindowService(() => null), async provider =>
        {
            Services.DemoProcessing.DemoEvaluationCoordinator coordinator =
                provider.GetRequiredService<Services.DemoProcessing.DemoEvaluationCoordinator>();

            await Assert.That(coordinator.EvaluatorIds).IsEquivalentTo(["library", "highlights"])
                .Because("the pack is off: its four evaluators are never in the fan-out");

            StratBookPackInstances instances =
                provider.GetRequiredService<StratBookPackInstances>();
            using (Assert.Multiple())
            {
                await Assert.That(instances.RoundFacts).IsNull()
                    .Because("the pack is off: Round Facts evaluator was never constructed, not merely excluded");
                await Assert.That(instances.RoundIndex).IsNull()
                    .Because("the pack is off: Round Index evaluator was never constructed, not merely excluded");
                await Assert.That(instances.SuggestedTags).IsNull()
                    .Because("the pack is off: Suggested Tags service was never constructed, not merely excluded");
                await Assert.That(instances.GrenadeWalk).IsNull()
                    .Because("the pack is off: Grenade Index evaluator was never constructed, not merely excluded");
            }
        }, packOff);
    }

    // RoundIndexEvaluator and GrenadeIndexEvaluator can be built through SituationIndex/GrenadeIndex, at
    // StartPacks time, before the coordinator has ever polled. Their own factory must set .Coordinator;
    // the registry's lazy wrapper is too late for this path.
    [Test]
    public async Task SituationIndexAndGrenadeIndex_SetTheirEvaluatorsCoordinator_WithoutEverPollingTheCoordinator()
    {
        await WithProvider(new DesktopWindowService(() => null), async provider =>
        {
            Services.RoundIndex.SituationIndex _ = provider.GetRequiredService<Services.RoundIndex.SituationIndex>();
            Modules.UtilityBook.GrenadeIndex __ = provider.GetRequiredService<Modules.UtilityBook.GrenadeIndex>();

            await Assert.That(provider.GetRequiredService<Services.RoundIndex.RoundIndexEvaluator>().Coordinator)
                .IsNotNull();
            await Assert.That(provider.GetRequiredService<Modules.UtilityBook.GrenadeIndexEvaluator>().Coordinator)
                .IsNotNull();
        });
    }

    // Wired end to end: with the pack off, the real container's own factories (not a test double) leave
    // every pack-owned evaluator wanting nothing, and the Suggested badge unread, even for a demo that
    // would otherwise qualify and even for a forced Match Overview request.
    [Test]
    public async Task PackOff_NoEvaluatorWantsAnything_AndTheSuggestedBadgeStaysZero()
    {
        const string demo = "/d/pack-root.dem";
        // Every background opt-in ON, so a demo that qualifies does so under the gate's own default
        // (Situations) and against it (Grenades, Playback2D): the pack-off asserts below are not
        // vacuously true because nothing else wanted the demo either.
        const string packOff = """
                               {
                                 "Features": { "Overrides": { "pack.stratbook": false } },
                                 "Situations": { "BackgroundIndex": true },
                                 "Grenades": { "BackgroundIndex": true },
                                 "Playback2D": { "SuggestedTagsBackground": true }
                               }
                               """;
        const string packOn = """
                              {
                                "Situations": { "BackgroundIndex": true },
                                "Grenades": { "BackgroundIndex": true },
                                "Playback2D": { "SuggestedTagsBackground": true }
                              }
                              """;

        await WithProvider(new DesktopWindowService(() => null), async provider =>
        {
            SeedQualifyingDemo(provider, demo);
            Services.RoundFacts.RoundFactsEvaluator roundFacts = provider.GetRequiredService<Services.RoundFacts.RoundFactsEvaluator>();
            Services.RoundIndex.RoundIndexEvaluator roundIndex = provider.GetRequiredService<Services.RoundIndex.RoundIndexEvaluator>();
            Modules.SuggestedTags.SuggestedTagsService suggestedTags = provider.GetRequiredService<Modules.SuggestedTags.SuggestedTagsService>();
            Modules.UtilityBook.GrenadeIndexEvaluator grenades = provider.GetRequiredService<Modules.UtilityBook.GrenadeIndexEvaluator>();

            // The Match Overview chip's own path: a forced request must not leave a stale forced path
            // behind for the pack to pick up unasked once it comes back on.
            grenades.Request(demo);

            using (Assert.Multiple())
            {
                // The seeded rows carry a fake fingerprint, so they are stale: the control below shows the
                // pack-on build wants them, which is what makes this assertion non-vacuous.
                await Assert.That(roundFacts.Wants(demo)).IsFalse();
                await Assert.That(roundFacts.PendingPaths()).IsEmpty();
                await Assert.That(roundIndex.Wants(demo)).IsFalse();
                await Assert.That(roundIndex.PendingPaths()).IsEmpty();
                await Assert.That(suggestedTags.Wants(demo)).IsFalse();
                await Assert.That(suggestedTags.PendingPaths()).IsEmpty();
                await Assert.That(grenades.Wants(demo)).IsFalse();
                await Assert.That(grenades.PendingPaths()).IsEmpty();
                // Wants is gated either way (forced or not); PriorityFor is not, so this is the assertion
                // that actually proves Request did nothing rather than merely agreeing with Wants.
                await Assert.That(grenades.PriorityFor(demo)).IsEqualTo(Services.DemoProcessing.DemoJobPriority.Background)
                    .Because("a real forced path reads UserRequested; Request must have been a no-op");
            }

            await Assert.That(SuggestedBadge(provider)).IsNull().Because("the pack is off: the pending suggestions are never read");
            // The ruleset itself leaves the merged set: the Library and Highlights passes run less. Read
            // after the evaluator asserts, since the first rules read collects the pack contributions.
            await Assert.That(provider.GetRequiredService<Modules.Highlights.MergedRulesBuild>().Docs.Select(d => d.Id))
                .DoesNotContain(Services.RoundFacts.RoundFactsFingerprint.RulesetId);
        }, packOff);

        // Control: the same seed and the same demo, pack on (its default). Without this, the asserts
        // above would pass just as well if the gate wiring were deleted outright.
        await WithProvider(new DesktopWindowService(() => null), async provider =>
        {
            SeedQualifyingDemo(provider, demo);
            Services.RoundFacts.RoundFactsEvaluator roundFacts = provider.GetRequiredService<Services.RoundFacts.RoundFactsEvaluator>();
            Services.RoundIndex.RoundIndexEvaluator roundIndex = provider.GetRequiredService<Services.RoundIndex.RoundIndexEvaluator>();
            Modules.SuggestedTags.SuggestedTagsService suggestedTags = provider.GetRequiredService<Modules.SuggestedTags.SuggestedTagsService>();
            Modules.UtilityBook.GrenadeIndexEvaluator grenades = provider.GetRequiredService<Modules.UtilityBook.GrenadeIndexEvaluator>();

            grenades.Request(demo);

            using (Assert.Multiple())
            {
                await Assert.That(roundIndex.Wants(demo)).IsTrue();
                await Assert.That(suggestedTags.Wants(demo)).IsTrue();
                await Assert.That(grenades.Wants(demo)).IsTrue();
                await Assert.That(grenades.PriorityFor(demo)).IsEqualTo(Services.DemoProcessing.DemoJobPriority.UserRequested);
            }

            await Assert.That(SuggestedBadge(provider)).IsEqualTo("7");
            // Last: the first rules read collects the pack contributions, which queues the Review load and
            // wakes the coordinator, and a job on the seeded demo would clear the forced grenade path above.
            using (Assert.Multiple())
            {
                await Assert.That(roundFacts.Wants(demo)).IsTrue().Because("the seeded rows carry a stale fingerprint");
                await Assert.That(provider.GetRequiredService<Modules.Highlights.MergedRulesBuild>().Docs.Select(d => d.Id))
                    .Contains(Services.RoundFacts.RoundFactsFingerprint.RulesetId);
            }
        }, packOn);
    }

    // A demo whose row carries everything RoundIndexEvaluator, SuggestedTagsService and
    // GrenadeIndexEvaluator each need to want it, and 7 pending suggestions for the badge.
    private static void SeedQualifyingDemo(ServiceProvider provider, string path)
    {
        Services.DemoCache.DemoCacheStore cache = provider.GetRequiredService<Services.DemoCache.DemoCacheStore>();
        cache.Upsert(RoundIndexTestData.ParsedRecord(path, facts: RoundIndexTestData.Facts(RoundIndexTestData.Round(1, 1000, 2000))));
        cache.UpdateExisting(path, r => r.SetSuggestionCount(7));
    }

    private static string? SuggestedBadge(ServiceProvider provider) =>
        provider.GetRequiredService<Modules.ModuleRegistry>().Modules
            .OfType<Modules.SuggestedTags.SuggestedInboxModule>().Single()
            .CreateTabs(null!).Single().Badge;
}
