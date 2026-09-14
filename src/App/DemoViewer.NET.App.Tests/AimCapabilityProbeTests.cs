#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The probe's judgement, on hand-built demos where the signal set is known exactly.
///     <para>
///         <b>The failure mode.</b> A capability check that answers "present or absent" reports a
///         substitute signal as if it were the real one, and every consumer downstream ships the
///         resulting number without a caveat. <c>player_hurt</c> standing in for <c>bullet_damage</c>
///         still produces an accuracy figure, and a hitgroup field that never leaves zero still
///         produces a headshot rate: both are wrong, neither throws. These tests pin the degraded
///         verdicts, so a future simplification of the probe to a boolean breaks here rather than in
///         somebody's stats page.
///     </para>
///     <para>
///         Synthetic throughout: no demo file is read, so this class stays in the fast tier. The
///         real-fixture assertions live in <see cref="AimCapabilityProbeRealDemoTests" />.
///     </para>
/// </summary>
public class AimCapabilityProbeTests
{
    [Test]
    public async Task Probe_WithNoBulletDamageButPlayerHurtPresent_DegradesAccuracyRatherThanSupportingIt()
    {
        List<GameEvent> events = [];
        events.AddRange(Shots(40));
        events.AddRange(Hurts(12, varyHitGroup: true));
        events.AddRange(Rounds(4, closed: true));

        AimCapabilityReport report = AimCapabilityProbe.Scan(SyntheticParsedDemo.Create(allGameEvents: events));

        using (Assert.Multiple())
        {
            await Assert.That(report.Observe(AimSignal.BulletDamage).Present).IsFalse();
            await Assert.That(report.Verdict(AimMetric.HitAccuracy).Support).IsEqualTo(MetricSupport.Degraded)
                .Because("player_hurt computes an accuracy, so absent is the wrong answer and supported is a lie");
            await Assert.That(report.Supports(AimMetric.HitAccuracy)).IsFalse()
                .Because("a degraded metric must not read as supported at the call site");
            await Assert.That(report.Verdict(AimMetric.HitAccuracy).Reason).Contains("bullet_damage never fires");
            await Assert.That(report.Verdict(AimMetric.DamagePerShot).Support).IsEqualTo(MetricSupport.Degraded);
        }
    }

    [Test]
    public async Task Probe_WithBulletDamagePresent_SupportsAccuracyAndDamagePerShot()
    {
        List<GameEvent> events = [];
        events.AddRange(Shots(40));
        events.AddRange(BulletDamages(18));
        events.AddRange(Hurts(12, varyHitGroup: true));
        events.AddRange(Rounds(4, closed: true));

        AimCapabilityReport report = AimCapabilityProbe.Scan(SyntheticParsedDemo.Create(allGameEvents: events));

        using (Assert.Multiple())
        {
            await Assert.That(report.Supports(AimMetric.HitAccuracy)).IsTrue();
            await Assert.That(report.Supports(AimMetric.DamagePerShot)).IsTrue();
            await Assert.That(report.Verdict(AimMetric.HitAccuracy).Reason).Contains("bullet_damage fires 18 times");
        }
    }

    [Test]
    public async Task Probe_WhenTheHitgroupFieldNeverVaries_ReportsHeadshotRateUnsupported()
    {
        List<GameEvent> constant = [.. Shots(40), .. Hurts(20, varyHitGroup: false), .. Rounds(4, closed: true)];
        List<GameEvent> varying = [.. Shots(40), .. Hurts(20, varyHitGroup: true), .. Rounds(4, closed: true)];

        AimCapabilityReport flat = AimCapabilityProbe.Scan(SyntheticParsedDemo.Create(allGameEvents: constant));
        AimCapabilityReport real = AimCapabilityProbe.Scan(SyntheticParsedDemo.Create(allGameEvents: varying));

        using (Assert.Multiple())
        {
            // Present-but-constant is the case a presence check cannot see: the field is there, the
            // metric computes, and the answer is a constant rather than a measurement.
            await Assert.That(flat.Observe(AimSignal.PlayerHurt).Present).IsTrue();
            await Assert.That(flat.HitGroups.DistinctValues).IsEqualTo(1);
            await Assert.That(flat.Verdict(AimMetric.HeadshotRate).Support).IsEqualTo(MetricSupport.Unsupported);
            await Assert.That(flat.Verdict(AimMetric.HeadshotRate).Reason).Contains("never varies");

            await Assert.That(real.HitGroups.DistinctValues).IsGreaterThan(1);
            await Assert.That(real.Supports(AimMetric.HeadshotRate)).IsTrue();
        }
    }

    [Test]
    public async Task Probe_WithTooFewShotsPerRound_DegradesTheShotCountRatherThanReportingItPresent()
    {
        // Four shots across four rounds: one per round, under the MinShotsPerRound floor.
        List<GameEvent> events = [.. Shots(4), .. Rounds(4, closed: true)];

        AimCapabilityReport report = AimCapabilityProbe.Scan(SyntheticParsedDemo.Create(allGameEvents: events));

        using (Assert.Multiple())
        {
            await Assert.That(report.Observe(AimSignal.WeaponFire).Present).IsTrue();
            await Assert.That(report.Observe(AimSignal.WeaponFire).PerRound).IsEqualTo(1.0);
            await Assert.That(report.Verdict(AimMetric.ShotsFired).Support).IsEqualTo(MetricSupport.Degraded)
                .Because("density, not presence, is what decides whether a per-player rate means anything");
        }
    }

    [Test]
    public async Task Probe_WithNoClosingRoundMarker_SegmentsOnTheNextStartAndDegradesPerRoundAim()
    {
        List<GameEvent> open = [.. Shots(40), .. Rounds(3, closed: false)];
        List<GameEvent> closed = [.. Shots(40), .. Rounds(3, closed: true)];

        AimCapabilityReport openReport = AimCapabilityProbe.Scan(SyntheticParsedDemo.Create(allGameEvents: open));
        AimCapabilityReport closedReport = AimCapabilityProbe.Scan(SyntheticParsedDemo.Create(allGameEvents: closed));

        using (Assert.Multiple())
        {
            await Assert.That(openReport.Rounds.StartEvent).IsEqualTo("round_freeze_end");
            await Assert.That(openReport.Rounds.EndEvent).IsNull();
            await Assert.That(openReport.Rounds.RoundCount).IsEqualTo(3)
                .Because("a demo with opening markers is still segmentable without closing ones");
            await Assert.That(openReport.Verdict(AimMetric.PerRoundAim).Support).IsEqualTo(MetricSupport.Degraded);

            await Assert.That(closedReport.Rounds.EndEvent).IsEqualTo("round_officially_ended");
            await Assert.That(closedReport.Supports(AimMetric.PerRoundAim)).IsTrue();
        }
    }

    [Test]
    public async Task Probe_WithNoRoundBoundaryAtAll_ReportsTickDensityAndCallsPerRoundAimUnsupported()
    {
        // No boundary events of any kind: the state every source tested reaches if you segment on
        // round_start / round_end, which fire zero times everywhere.
        AimCapabilityReport report = AimCapabilityProbe.Scan(
            SyntheticParsedDemo.Create(allGameEvents: [.. Shots(64)], tickCount: 6400));

        SignalObservation shots = report.Observe(AimSignal.WeaponFire);
        using (Assert.Multiple())
        {
            await Assert.That(report.Rounds.Segmentable).IsFalse();
            await Assert.That(report.Verdict(AimMetric.PerRoundAim).Support).IsEqualTo(MetricSupport.Unsupported);
            await Assert.That(shots.PerRound).IsEqualTo(0)
                .Because("a per-round density with no rounds must read 0, never a divide-by-zero infinity");
            await Assert.That(shots.PerThousandTicks).IsEqualTo(10.0)
                .Because("the tick denominator survives a demo whose boundary events are missing");
        }
    }

    [Test]
    public async Task Probe_WithNoSubtickPayloads_ReportsTheStreamUnsupportedRatherThanEmpty()
    {
        AimCapabilityReport report = AimCapabilityProbe.Scan(
            SyntheticParsedDemo.Create(allGameEvents: [.. Shots(40), .. Rounds(4, closed: true)]));

        using (Assert.Multiple())
        {
            await Assert.That(report.Subtick.MessageCount).IsEqualTo(0);
            await Assert.That(report.Subtick.YieldPerMessage).IsEqualTo(0);
            await Assert.That(report.Verdict(AimMetric.SubtickAimTiming).Support)
                .IsEqualTo(MetricSupport.Unsupported);
            await Assert.That(report.Observe(AimSignal.UserCmds).Present).IsFalse()
                .Because("svc_UserCmds is a net message, and it must still appear in the signal map");
        }
    }

    [Test]
    public async Task Probe_WithPlayerSoundStandingInForFootsteps_DegradesAudioCuedAiming()
    {
        List<GameEvent> events = [.. Shots(40), .. Sounds(200), .. Rounds(4, closed: true)];

        AimCapabilityReport report = AimCapabilityProbe.Scan(SyntheticParsedDemo.Create(allGameEvents: events));

        using (Assert.Multiple())
        {
            await Assert.That(report.Observe(AimSignal.PlayerFootstep).Present).IsFalse();
            await Assert.That(report.Observe(AimSignal.PlayerSound).Count).IsEqualTo(200);
            await Assert.That(report.Verdict(AimMetric.AudioCuedAiming).Support).IsEqualTo(MetricSupport.Degraded);
            await Assert.That(report.Verdict(AimMetric.AudioCuedAiming).Reason).Contains("player_footstep never fires");
        }
    }

    /// <summary>
    ///     Every enum member is answered. A new <see cref="AimSignal" /> or <see cref="AimMetric" />
    ///     added without wiring reads as a missing key or a thrown lookup at the consumer, which is a
    ///     worse failure than the silent zeros this probe exists to replace.
    /// </summary>
    [Test]
    public async Task Probe_ForEverySignalAndMetric_ProducesAnEntryWithANonEmptyReason()
    {
        AimCapabilityReport report = AimCapabilityProbe.Scan(
            SyntheticParsedDemo.Create(allGameEvents: [.. Shots(40), .. Rounds(4, closed: true)]));

        List<string> missing = [];
        foreach (AimSignal signal in Enum.GetValues<AimSignal>())
        {
            if (!report.Signals.ContainsKey(signal))
            {
                missing.Add($"signal {signal}");
            }
        }

        foreach (AimMetric metric in Enum.GetValues<AimMetric>())
        {
            MetricVerdict verdict = report.Verdict(metric);
            if (string.IsNullOrWhiteSpace(verdict.Reason))
            {
                missing.Add($"metric {metric} has no reason");
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(missing).IsEmpty();
            await Assert.That(report.Metrics.Count).IsEqualTo(Enum.GetValues<AimMetric>().Length);
        }
    }

    [Test]
    public async Task Probe_WhenTheSourceAdvertisesSignalsItNeverSends_RecordsTheDriftInBothDirections()
    {
        // Advertises a flash event it never sends, and sends a sound event it never advertises: the
        // exact shape the committed tour sample has, reproduced without reading a file.
        DemoProfile profile = new(
            DemoSourceKind.GotvMatchmaking, 0, "csgo", DemoFeatureSet.HasPlayerBlind);

        AimCapabilityReport report = AimCapabilityProbe.Scan(
            SyntheticParsedDemo.Create(
                allGameEvents: [.. Shots(40), .. Sounds(50), .. Rounds(4, closed: true)],
                profile: profile));

        bool overclaims = report.DeclarationDrift.Any(
            line => line.Contains("HasPlayerBlind", StringComparison.Ordinal)
                    && line.Contains("never fires", StringComparison.Ordinal));
        bool underclaims = report.DeclarationDrift.Any(
            line => line.Contains("HasPlayerSound", StringComparison.Ordinal)
                    && line.Contains("is not declared", StringComparison.Ordinal));

        using (Assert.Multiple())
        {
            await Assert.That(overclaims).IsTrue()
                .Because("a declared capability the demo never sends is what makes a consumer emit zeros");
            await Assert.That(underclaims).IsTrue()
                .Because("an undeclared capability the demo does send is a signal left on the floor");
        }
    }

    [Test]
    public async Task Probe_WithANullDemo_Throws()
    {
        ArgumentNullException? thrown = await Assert.ThrowsAsync<ArgumentNullException>(
            () =>
            {
                AimCapabilityProbe.Scan(null!);
                return Task.CompletedTask;
            });

        await Assert.That(thrown).IsNotNull();
    }

    // ── Fixture builders ─────────────────────────────────────────────────────────────────────────

    private static IEnumerable<GameEvent> Shots(int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return new GameEvent(
                "weapon_fire", -1, i, i, i,
                new WeaponFireEvent
                {
                    UserId = i % 10,
                    UserIdPawn = 0,
                    Weapon = "ak47",
                    Silenced = false
                });
        }
    }

    private static IEnumerable<GameEvent> BulletDamages(int count)
    {
        for (int i = 0; i < count; i++)
        {
            // BulletDamageEvent declares 26 required fields (aim punch, inaccuracy, shoot angles) and
            // the probe reads only the wire name and the count, so a payload-free fire keeps the
            // fixture about the thing under test. Field-name reporting is covered against the real
            // fixture, where the payloads are the ones the demo actually carried.
            yield return new GameEvent("bullet_damage", -1, i, i, i, null!);
        }
    }

    private static IEnumerable<GameEvent> Hurts(int count, bool varyHitGroup)
    {
        for (int i = 0; i < count; i++)
        {
            yield return new GameEvent(
                "player_hurt", -1, i, i, i,
                new PlayerHurtEvent
                {
                    UserId = i % 10,
                    UserIdPawn = 0,
                    Attacker = (i + 1) % 10,
                    AttackerPawn = 0,
                    Health = 60,
                    Armor = 90,
                    Weapon = "ak47",
                    DmgHealth = 27,
                    DmgArmor = 4,
                    HitGroup = varyHitGroup ? (byte)(i % 5) : (byte)0
                });
        }
    }

    private static IEnumerable<GameEvent> Sounds(int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return new GameEvent(
                "player_sound", -1, i, i, i,
                new PlayerSoundEvent
                {
                    UserId = i % 10,
                    UserIdPawn = 0,
                    Radius = 800,
                    Duration = 0.4f,
                    Step = true
                });
        }
    }

    private static IEnumerable<GameEvent> Rounds(int count, bool closed)
    {
        for (int i = 0; i < count; i++)
        {
            yield return TestGameEvents.RoundFreezeEnd(frameNumber: i * 100, serverTick: i * 100, gameTick: i * 100);
            if (closed)
            {
                yield return TestGameEvents.RoundOfficiallyEnded(
                    frameNumber: (i * 100) + 50, serverTick: (i * 100) + 50, gameTick: (i * 100) + 50);
            }
        }
    }
}
