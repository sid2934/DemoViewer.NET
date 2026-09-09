#region

using CS2DemoKit.Parser;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Checks that <see cref="SpraySampler" /> produces spray geometry a plot could actually draw,
///     and prints the derived recoil pattern so a reader can sanity-check its shape against the
///     weapon they know.
///     <para>
///         The load-bearing assertion is that the derived pattern CLIMBS: a rifle's recoil pitch has
///         to grow with the spray index, and a pattern that came out flat or unordered would mean the
///         sampler is reading something other than recoil while still producing a plausible-looking
///         picture.
///     </para>
/// </summary>
[Category("RealDemo")]
[NotInParallel]
public class SpraySamplerProbe
{
    [Test]
    public async Task Sample_ProducesRunsAndAClimbingPattern()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);

        SprayModel model = SpraySampler.Sample(demo);

        Console.WriteLine($"[spray] player/weapon pairs: {model.Players.Count}");
        foreach (PlayerSprays p in model.Players.OrderByDescending(x => x.Runs.Count).Take(6))
        {
            Console.WriteLine($"[spray] slot={p.Slot,2} {p.Weapon,-22} runs={p.Runs.Count}");
        }

        // The richest weapon is the one worth printing a pattern for.
        KeyValuePair<string, IReadOnlyList<SprayPatternPoint>> best = model.IdealByWeapon
            .OrderByDescending(kv => kv.Value.Sum(p => p.Samples))
            .First();

        Console.WriteLine($"[spray] pattern for {best.Key}:");
        foreach (SprayPatternPoint pt in best.Value.Take(12))
        {
            Console.WriteLine($"[spray]   idx={pt.RecoilIndex,2} n={pt.Samples,3} "
                              + $"yaw={pt.PunchYawDeg,8:F3} pitch={pt.PunchPitchDeg,8:F3}");
        }

        SprayRun sample = model.Players.OrderByDescending(p => p.Runs.Count).First().Runs[0];
        Console.WriteLine($"[spray] example run: {sample.Weapon} at tick {sample.StartTick}");
        foreach (SpraySample s in sample.Shots)
        {
            Console.WriteLine($"[spray]   shot={s.ShotIndex} recoil={s.RecoilIndex,2} "
                              + $"dYaw={s.OffsetYawDeg,7:F2} dPitch={s.OffsetPitchDeg,7:F2}");
        }

        // The tracking mode: same demo, origin moving with the victim.
        SprayModel tracked = SpraySampler.Sample(demo, SprayOrigin.TargetCentre);
        List<SpraySample> trackedShots = [.. tracked.Players.SelectMany(p => p.Runs).SelectMany(r => r.Shots)];
        Console.WriteLine($"[track] pairs={tracked.Players.Count} shots={trackedShots.Count} "
                          + $"empty={tracked.Players.Count(p => p.Runs.Count == 0)}");
        if (trackedShots.Count > 0)
        {
            double mean = trackedShots.Average(x =>
                Math.Sqrt((x.OffsetYawDeg * x.OffsetYawDeg) + (x.OffsetPitchDeg * x.OffsetPitchDeg)));
            Console.WriteLine($"[track] mean offset from victim chest = {mean:F2} deg");
            Console.WriteLine($"[track] anchor offsets are NOT zero: {trackedShots.Count(x => x.ShotIndex == 0 && Math.Abs(x.OffsetYawDeg) > 0.001)} of {trackedShots.Count(x => x.ShotIndex == 0)}");
        }

        await Assert.That(model.Players.Count).IsGreaterThan(0)
            .Because("a full match contains sprays");

        // The tracking origin drops any bullet whose victim could not be placed, so a player/weapon
        // pair can lose every run it had. Such a pair must not reach the model at all: the drilldown
        // offers Weapons straight from it, and an entry with no runs is a weapon in the picker that
        // selects nothing and draws a blank plot.
        await Assert.That(tracked.Players.All(p => p.Runs.Count > 0)).IsTrue()
            .Because("a player/weapon pair with no surviving run must not be offered");
        await Assert.That(sample.Shots[0].OffsetYawDeg).IsEqualTo(0f)
            .Because("the anchor is the origin by construction");

        // A rifle's recoil climbs. Compare the mean pitch of the first three pattern points against
        // the next three: recoil is negative pitch (upward kick), so later must be MORE negative.
        List<SprayPatternPoint> pts = [.. best.Value.Where(p => p.Samples >= 3).Take(6)];
        if (pts.Count >= 6)
        {
            double early = pts.Take(3).Average(p => p.PunchPitchDeg);
            double late = pts.Skip(3).Take(3).Average(p => p.PunchPitchDeg);
            Console.WriteLine($"[spray] climb: early={early:F3} late={late:F3}");
            await Assert.That(late).IsLessThan(early)
                .Because("recoil accumulates upward along a spray, so later bullets punch further");
        }
    }
}
