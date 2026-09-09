#region

using CS2DemoKit.Parser;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Stats;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The probe against <c>assets/tour/sample-de_nuke.dem</c>, the demo this repository ships.
///     <para>
///         <b>The failure mode.</b> This fixture genuinely lacks <c>bullet_damage</c>,
///         <c>bullet_impact</c>, <c>player_blind</c>, <c>weapon_zoom</c>, <c>player_footstep</c> and
///         every <c>svc_UserCmds</c> payload, while its own <see cref="DemoFeatureSet" /> advertises
///         three of those as available. <c>rules/highlights_aim_pro.rules.yaml</c> ships today built
///         entirely on <c>bullet_damage</c>, so on this demo it produces zeros and reports success. A
///         probe that answers from the declaration, or that treats a substitute signal as the real
///         thing, passes a synthetic suite and still gets this demo wrong. These assertions are the
///         reason the probe counts rather than asks.
///     </para>
///     <para>
///         Deliberately not routed through <c>TourDemoLocator</c>: that resolver honours the
///         <c>DEMOVIEWER_TOUR_DEMO</c> override, and every count below belongs to this one file.
///     </para>
/// </summary>
[Category("RealDemo")]
public class AimCapabilityProbeRealDemoTests
{
    private const string SampleRelativePath = "assets/tour/sample-de_nuke.dem";

    [Test]
    public async Task Probe_OnTheCommittedSample_ReportsBulletDamageAbsentAndTheMetricsOnItUnsupported()
    {
        AimCapabilityReport report = ScanSample();

        using (Assert.Multiple())
        {
            await Assert.That(report.Observe(AimSignal.BulletDamage).Count).IsEqualTo(0)
                .Because("this fixture genuinely carries no bullet_damage, which is the whole point of it");
            await Assert.That(report.Observe(AimSignal.BulletImpact).Count).IsEqualTo(0);

            // Spray tracing needs per-bullet landings and has no substitute, so it is flatly out.
            await Assert.That(report.Verdict(AimMetric.SprayTrace).Support).IsEqualTo(MetricSupport.Unsupported);

            // Accuracy and damage-per-shot can still be computed off player_hurt, and that is exactly
            // the number a consumer should not present as the real one.
            await Assert.That(report.Verdict(AimMetric.HitAccuracy).Support).IsEqualTo(MetricSupport.Degraded);
            await Assert.That(report.Verdict(AimMetric.DamagePerShot).Support).IsEqualTo(MetricSupport.Degraded);
            await Assert.That(report.Supports(AimMetric.HitAccuracy)).IsFalse();
            await Assert.That(report.Supports(AimMetric.DamagePerShot)).IsFalse();
        }
    }

    [Test]
    public async Task Probe_OnTheCommittedSample_ReportsWeaponFireAndPlayerHurtWithTheirFields()
    {
        AimCapabilityReport report = ScanSample();

        SignalObservation shots = report.Observe(AimSignal.WeaponFire);
        SignalObservation hurt = report.Observe(AimSignal.PlayerHurt);

        using (Assert.Multiple())
        {
            await Assert.That(shots.Present).IsTrue();
            await Assert.That(shots.PerThousandTicks).IsGreaterThan(0)
                .Because("the tick denominator must work on a demo whose round markers are incomplete");
            await Assert.That(report.Supports(AimMetric.ShotsFired)).IsTrue();

            await Assert.That(hurt.Present).IsTrue();
            await Assert.That(hurt.Fields).Contains("HitGroup")
                .Because("a metric reading a field the source omits is as broken as one whose event is missing");

            // The hitgroup is populated here, so the headshot rate is real on this demo even though
            // almost everything else built on bullet_damage is not.
            await Assert.That(report.HitGroups.Fires).IsEqualTo(hurt.Count);
            await Assert.That(report.HitGroups.DistinctValues).IsGreaterThan(1);
            await Assert.That(report.HitGroups.NonZeroFires).IsGreaterThan(0);
            await Assert.That(report.Supports(AimMetric.HeadshotRate)).IsTrue();
        }
    }

    [Test]
    public async Task Probe_OnTheCommittedSample_ReportsPlayerSoundInsteadOfFootsteps()
    {
        AimCapabilityReport report = ScanSample();

        using (Assert.Multiple())
        {
            await Assert.That(report.Observe(AimSignal.PlayerFootstep).Count).IsEqualTo(0);
            await Assert.That(report.Observe(AimSignal.PlayerSound).Count).IsGreaterThan(0)
                .Because("the two events substitute for each other, and this source ships the second");
            await Assert.That(report.Verdict(AimMetric.AudioCuedAiming).Support).IsEqualTo(MetricSupport.Degraded);
        }
    }

    [Test]
    public async Task Probe_OnTheCommittedSample_FindsNoSubtickInputStreamAtAll()
    {
        AimCapabilityReport report = ScanSample();

        using (Assert.Multiple())
        {
            // The trimmer strips svc_UserCmds, and SubTickExtractor.Extract returns zero events with
            // no exception and no warning. The probe is where that silence becomes a stated verdict.
            await Assert.That(report.Subtick.MessageCount).IsEqualTo(0);
            await Assert.That(report.Subtick.CarrierFrames).IsEqualTo(0);
            await Assert.That(report.Subtick.YieldPerMessage).IsEqualTo(0);
            await Assert.That(report.Verdict(AimMetric.SubtickAimTiming).Support)
                .IsEqualTo(MetricSupport.Unsupported);
        }
    }

    [Test]
    public async Task Probe_OnTheCommittedSample_SegmentsOnFreezeEndBecauseTheOtherMarkersAreMissing()
    {
        AimCapabilityReport report = ScanSample();

        using (Assert.Multiple())
        {
            // round_start and round_end were absent from every source tested, and this demo carries
            // neither round_prestart nor round_officially_ended either, so freeze-end is all there is.
            await Assert.That(report.Rounds.Counts["round_start"]).IsEqualTo(0);
            await Assert.That(report.Rounds.Counts["round_end"]).IsEqualTo(0);
            await Assert.That(report.Rounds.Counts["round_officially_ended"]).IsEqualTo(0);
            await Assert.That(report.Rounds.StartEvent).IsEqualTo("round_freeze_end");
            await Assert.That(report.Rounds.EndEvent).IsNull();

            await Assert.That(report.Rounds.Segmentable).IsTrue();
            await Assert.That(report.Rounds.RoundCount).IsEqualTo(report.Rounds.StartCount);
            await Assert.That(report.Verdict(AimMetric.PerRoundAim).Support).IsEqualTo(MetricSupport.Degraded)
                .Because("segmenting on start-to-next-start leaves the last round unbounded");
        }
    }

    [Test]
    public async Task Probe_OnTheCommittedSample_ContradictsTheSourcesOwnCapabilityDeclaration()
    {
        AimCapabilityReport report = ScanSample();
        ParsedDemo demo = DemoTestHelper.GetOrParse(RequireSample());

        // Advertised, and never sent: a consumer gated on the flag emits zeros for all three.
        bool blindDeclared = demo.Profile.Features.HasFlag(DemoFeatureSet.HasPlayerBlind);
        bool zoomDeclared = demo.Profile.Features.HasFlag(DemoFeatureSet.HasWeaponZoom);

        using (Assert.Multiple())
        {
            await Assert.That(blindDeclared).IsTrue()
                .Because("the declaration is what this test exists to contradict; if it changed, re-derive");
            await Assert.That(report.Observe(AimSignal.PlayerBlind).Count).IsEqualTo(0);
            await Assert.That(report.Verdict(AimMetric.FlashedAiming).Support).IsEqualTo(MetricSupport.Unsupported);

            await Assert.That(zoomDeclared).IsTrue();
            await Assert.That(report.Observe(AimSignal.WeaponZoom).Count).IsEqualTo(0);
            await Assert.That(report.Verdict(AimMetric.ScopedAiming).Support).IsEqualTo(MetricSupport.Unsupported);

            await Assert.That(report.DeclarationDrift).IsNotEmpty()
                .Because("this demo's advertisement and its contents disagree, and that must be reported");
        }
    }

    private static AimCapabilityReport ScanSample() =>
        AimCapabilityProbe.Scan(DemoTestHelper.GetOrParse(RequireSample()));

    private static string RequireSample()
    {
        string? root = DemoTestHelper.FindRepoRoot();
        if (root is null)
        {
            throw new SkipTestException("repo root not found from the test bin");
        }

        string path = Path.Combine(root, SampleRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new SkipTestException($"the committed tour sample is missing at {path}");
        }

        return path;
    }
}
