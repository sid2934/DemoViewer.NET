#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Pins that the spray plot's data actually reaches the player drilldown.
///     <para>
///         The plot is drawn rather than templated, so a view-model that never populated renders as a
///         tidy empty card with nothing in the tree saying why. This asserts on the view-model instead
///         of on pixels: weapons offered, runs listed, a pattern to draw against, and the two origin
///         modes producing genuinely different geometry.
///     </para>
/// </summary>
[Category("RealDemo")]
[NotInParallel]
public class SprayDrilldownTests
{
    [Test]
    public async Task Drilldown_OffersWeaponsAndRuns_ForAPlayerWhoSprayed()
    {
        (StatsTabViewModel vm, _) = BuildTab();

        // Pick a player the sampler actually found sprays for, rather than assuming row 0 did.
        SprayModel model = SpraySampler.Sample(vm.LoadedDemo!, SprayOrigin.FirstBullet);
        await Assert.That(model.Players.Count).IsGreaterThan(0)
            .Because("a full match contains sprays");

        int slot = model.Players.OrderByDescending(p => p.Runs.Count).First().Slot;
        StatsRow? row = vm.GameRows.FirstOrDefault(r => r.PlayerSlot == slot);
        await Assert.That(row).IsNotNull().Because("the sprayer must appear on the scoreboard");

        vm.OpenPlayerDetailsCommand.Execute(row);
        PlayerDetailsViewModel details = vm.PlayerDetails!;

        await Wait(() => !details.Spray.IsBusy && details.Spray.Weapons.Count > 0);

        await Assert.That(details.Spray.HasData).IsTrue()
            .Because($"slot {slot} has {model.Players.First(p => p.Slot == slot).Runs.Count} runs");
        await Assert.That(details.Spray.Weapons.Count).IsGreaterThan(0);
        await Assert.That(details.Spray.SelectedWeapon).IsNotNull();
        await Assert.That(details.Spray.Runs.Count).IsGreaterThan(0);
        await Assert.That(details.Spray.Shots.Count).IsGreaterThanOrEqualTo(SpraySampler.MinRunShots);
        await Assert.That(details.Spray.Pattern.Count).IsGreaterThan(0)
            .Because("the reference trace is what the player's spray is read against");
    }

    [Test]
    public async Task Drilldown_TrackTarget_ChangesTheGeometry()
    {
        (StatsTabViewModel vm, _) = BuildTab();

        SprayModel anchored = SpraySampler.Sample(vm.LoadedDemo!, SprayOrigin.FirstBullet);
        int slot = anchored.Players.OrderByDescending(p => p.Runs.Count).First().Slot;
        vm.OpenPlayerDetailsCommand.Execute(vm.GameRows.First(r => r.PlayerSlot == slot));
        PlayerDetailsViewModel details = vm.PlayerDetails!;

        await Wait(() => !details.Spray.IsBusy && details.Spray.Weapons.Count > 0);
        float anchoredFirst = details.Spray.Shots[0].OffsetYawDeg;

        details.Spray.TrackTarget = true;
        await Wait(() => !details.Spray.IsBusy && details.Spray.Weapons.Count > 0);

        // The anchored mode's first shot IS the origin, so its offset is zero by construction. The
        // tracking mode measures from the victim, so it cannot also be zero unless the bullet landed
        // exactly on the chest anchor, which a whole match of them will not do.
        await Assert.That(anchoredFirst).IsEqualTo(0f);
        await Assert.That(details.Spray.Shots.Count).IsGreaterThan(0)
            .Because("tracking mode still has sprays, measured from a different origin");
        await Assert.That(details.Spray.Shots.Any(s => Math.Abs(s.OffsetYawDeg) > 0.001f)).IsTrue()
            .Because("a moving origin cannot leave every offset at zero");

        // The load-bearing one. Every assertion above also holds of the ANCHORED run, so a reload
        // that resampled but never rebuilt the run list would satisfy them while the plot still drew
        // the old geometry under the new label. Only the first bullet separates the two modes: it is
        // the origin under FirstBullet and so exactly zero, and it is measured from the victim under
        // TargetCentre and so never is.
        await Assert.That(Math.Abs(details.Spray.Shots[0].OffsetYawDeg)).IsGreaterThan(0.001f)
            .Because("the run list must be rebuilt from the tracking model, not left on the anchored one");
    }

    private static async Task Wait(Func<bool> until)
    {
        for (int i = 0; i < 200 && !until(); i++)
        {
            await Task.Delay(25);
        }
    }

    private static (StatsTabViewModel Vm, ParsedDemo Demo) BuildTab()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);

        string rulesDir = RuleSetLocator.ResolveShippedRulesDirectory();
        RuleConfigLoadResult loaded = YamlConfigLoader.TryLoadDirectory(rulesDir);

        string? tris = CollisionAssetLocator.FindCollisionTris(demo.MapName);
        BuildResult build = DemoAnalysis.Build(demo, loaded.Rulesets, new AnalysisOptions
        {
            VisibilityEngine = tris is null ? null : VisibilityEngine.Load(tris)
        });

        AnalysisRun run = DemoAnalysis.Evaluate(demo, build, new AnalysisOptions());
        StatsTabViewModel vm = new(null, () => path);
        vm.UpdateFromRun(run, demo);
        return (vm, demo);
    }
}
