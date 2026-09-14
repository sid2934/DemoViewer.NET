#region

using System.Globalization;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.Parser.Models;

#endregion

namespace DemoViewer.NET.ViewModels.Stats;

/// <summary>
///     Counts the aim-relevant signals a demo actually carries, and turns that count into a per-metric
///     supported / degraded / unsupported verdict.
///     <para>
///         <b>Why this is observational.</b> Capability belongs to the demo source, not to CS2, and no
///         declaration reproduces it. Three real demos probed with
///         <c>tools/DemoViewer.NET.DemoSourceDetails</c>: a trimmed pro GOTV recording carries zero
///         <c>bullet_damage</c>, zero <c>svc_UserCmds</c> and zero <c>player_footstep</c> while carrying
///         2,249 <c>player_sound</c>; the two Valve matchmaking demos carry the mirror image. The demo
///         trimmer strips animation frames and <c>svc_UserCmds</c> but never game events, and the pro
///         sample is contemporaneous with the newer matchmaking demo, so neither trimming nor version
///         drift accounts for the split. Meanwhile the engine's per-profile coverage declarations
///         advertise the <c>shot_landed</c> view on all five profiles, which that data contradicts, and
///         <c>rules/highlights_aim_pro.rules.yaml</c> ships today built entirely on <c>bullet_damage</c>.
///         A consumer trusting the declaration emits zeros and says nothing about it.
///     </para>
///     <para>
///         <b>Why density and not presence.</b> Two Valve matchmaking demos eight months apart yielded
///         0.026 and 0.88 sub-tick events per <c>svc_UserCmds</c> payload, same parser and same code
///         path: a 39x spread, with the message present in both. Presence answers the wrong question, so
///         every signal here carries a density and every threshold is a named constant with the
///         measurement behind it written down.
///     </para>
/// </summary>
public static class AimCapabilityProbe
{
    /// <summary>
    ///     Sub-tick events per <c>svc_UserCmds</c> payload below which
    ///     <see cref="AimMetric.SubtickAimTiming" /> is reported degraded rather than supported.
    ///     <para>
    ///         Sits between the two measured yields (0.026 and 0.88) and nearer the low one, so the
    ///         sparse demo is flagged while a source that merely dips below the richer demo's yield is
    ///         not. A threshold on observed data, not a spec value: move it when a new measurement says
    ///         to, and record that measurement here when you do.
    ///     </para>
    /// </summary>
    public const double MinSubtickYield = 0.10;

    /// <summary>
    ///     <c>weapon_fire</c> fires per round below which shot-derived metrics are reported degraded. A
    ///     demo can carry the event and still hold too few fires to rate anyone's aim, which is the
    ///     usual shape of a warmup-only or heavily trimmed recording. Five shots per round is roughly
    ///     one player firing one burst, the floor at which a per-player rate stops being noise.
    /// </summary>
    public const double MinShotsPerRound = 5.0;

    /// <summary>
    ///     Frames sampled when measuring the sub-tick yield. A full matchmaking demo carries over 1.5
    ///     million <c>svc_UserCmds</c> payloads, and decoding all of them to learn a ratio would cost
    ///     more than the analysis this probe guards. The sample is strided evenly across the carrier
    ///     frames so it spans the whole match rather than its opening. Pass 0 to <see cref="Scan" /> to
    ///     decode every payload instead.
    /// </summary>
    public const int DefaultSubtickSampleFrames = 512;

    /// <summary>Signals carried as game events, paired with the wire name the probe counts.</summary>
    private static readonly (AimSignal Signal, string Wire)[] _gameEventSignals =
    [
        (AimSignal.BulletDamage, "bullet_damage"),
        (AimSignal.BulletImpact, "bullet_impact"),
        (AimSignal.WeaponFire, "weapon_fire"),
        (AimSignal.PlayerHurt, "player_hurt"),
        (AimSignal.PlayerBlind, "player_blind"),
        (AimSignal.WeaponZoom, "weapon_zoom"),
        (AimSignal.PlayerFootstep, "player_footstep"),
        (AimSignal.PlayerSound, "player_sound")
    ];

    /// <summary>Wire names of <see cref="_gameEventSignals" />, for the per-fire membership test.</summary>
    private static readonly HashSet<string> _trackedWires =
        new(_gameEventSignals.Select(pair => pair.Wire), StringComparer.Ordinal);

    /// <summary>
    ///     Opening markers in preference order. <c>round_start</c> is last because it was absent from
    ///     every source tested and is carried here only so the report can say so.
    /// </summary>
    private static readonly string[] _startMarkers = ["round_prestart", "round_freeze_end", "round_start"];

    /// <summary>
    ///     Closing markers in preference order. The trimmed pro demo carries neither, which is why the
    ///     no-closing-marker branch below exists rather than being treated as unreachable.
    /// </summary>
    private static readonly string[] _endMarkers = ["round_officially_ended", "round_end"];

    /// <summary>Every boundary event the report enumerates, zeros included, so absence is visible.</summary>
    private static readonly string[] _boundaryCandidates =
    [
        "round_prestart", "round_freeze_end", "round_start", "round_end", "round_officially_ended",
        "round_poststart"
    ];

    /// <summary>
    ///     The advertised capability flags that map cleanly onto one wire event, for the drift check.
    ///     <c>HasHltvCameraEvents</c> is deliberately absent: it covers several events
    ///     (<c>hltv_chase</c>, <c>hltv_fixed</c>, and others), so a drift line about it would be a guess
    ///     at which one the flag meant.
    /// </summary>
    private static readonly (DemoFeatureSet Flag, string Wire)[] _declaredFeatures =
    [
        (DemoFeatureSet.HasPlayerBlind, "player_blind"),
        (DemoFeatureSet.HasRoundOfficiallyEnded, "round_officially_ended"),
        (DemoFeatureSet.HasWeaponReload, "weapon_reload"),
        (DemoFeatureSet.HasWeaponZoom, "weapon_zoom"),
        (DemoFeatureSet.HasGrenadeThrown, "grenade_thrown"),
        (DemoFeatureSet.HasEntityKilled, "entity_killed"),
        (DemoFeatureSet.HasPlayerSound, "player_sound"),
        (DemoFeatureSet.HasCsPreRestart, "cs_pre_restart")
    ];

    /// <summary>
    ///     Probes one parsed demo. Reads only what the parse already produced (the event list and the
    ///     per-frame sub-tick payload counts), so it costs one walk of the events plus a bounded decode
    ///     of sub-tick payloads. No entity replay.
    /// </summary>
    /// <param name="demo">The parsed demo to count.</param>
    /// <param name="subtickSampleFrames">
    ///     How many <c>svc_UserCmds</c>-carrying frames to decode when measuring the yield. 0 means all
    ///     of them, which is exact and expensive. Defaults to <see cref="DefaultSubtickSampleFrames" />.
    /// </param>
    /// <returns>What the demo contains, and what that permits.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="demo" /> is null.</exception>
    public static AimCapabilityReport Scan(ParsedDemo demo, int subtickSampleFrames = DefaultSubtickSampleFrames)
    {
        ArgumentNullException.ThrowIfNull(demo);

        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<string>> fieldsByName = new(StringComparer.Ordinal);
        HashSet<int> hitGroups = [];
        int hurtFires = 0;
        int hurtNonZero = 0;

        foreach (GameEvent fire in demo.AllGameEvents)
        {
            counts[fire.Name] = counts.GetValueOrDefault(fire.Name) + 1;

            // Field names only for the signals the report actually carries, and only off the first
            // fire of each: the projection is reflection over the payload record, and a demo holds
            // some 46 distinct event names of which this reports 8.
            if (_trackedWires.Contains(fire.Name) && !fieldsByName.ContainsKey(fire.Name))
            {
                fieldsByName[fire.Name] = DecodedFieldNames(fire);
            }

            // Read the hitgroup off the typed payload rather than the decoded-field strings: what
            // matters is whether the VALUE varies, and a formatted string would turn that into a
            // question about the formatter.
            if (fire.Payload is PlayerHurtEvent hurt)
            {
                hurtFires++;
                hitGroups.Add(hurt.HitGroup);
                if (hurt.HitGroup != 0)
                {
                    hurtNonZero++;
                }
            }
        }

        RoundBoundaryObservation rounds = SegmentRounds(counts);
        SubtickObservation subtick = MeasureSubtick(demo, subtickSampleFrames);

        Dictionary<AimSignal, SignalObservation> signals = [];
        foreach ((AimSignal signal, string wire) in _gameEventSignals)
        {
            int count = counts.GetValueOrDefault(wire);
            signals[signal] = new SignalObservation(
                signal,
                wire,
                count,
                PerRound(count, rounds.RoundCount),
                PerThousandTicks(count, demo.TickCount),
                fieldsByName.GetValueOrDefault(wire, []));
        }

        // svc_UserCmds is a net message, not a game event, so it gets the same shape by hand. Its
        // field list stays empty on purpose: the payload's shape is fixed, and what decides whether it
        // is usable is the yield in `subtick`, not which fields it declares.
        signals[AimSignal.UserCmds] = new SignalObservation(
            AimSignal.UserCmds,
            "svc_UserCmds",
            subtick.MessageCount,
            PerRound(subtick.MessageCount, rounds.RoundCount),
            PerThousandTicks(subtick.MessageCount, demo.TickCount),
            []);

        HitGroupObservation hitGroupObservation = new(hurtFires, hitGroups.Count, hurtNonZero);

        return new AimCapabilityReport(
            demo.Profile.SourceKind,
            counts.Count,
            demo.AllGameEvents.Count,
            signals,
            subtick,
            hitGroupObservation,
            rounds,
            BuildVerdicts(signals, subtick, hitGroupObservation, rounds),
            BuildDeclarationDrift(demo.Profile.Features, counts));
    }

    private static List<string> DecodedFieldNames(GameEvent fire)
    {
        List<string> names = [];
        foreach ((string field, _, _) in fire.GetDecodedFields())
        {
            names.Add(field);
        }

        return names;
    }

    private static double PerRound(int count, int roundCount) =>
        roundCount <= 0 ? 0 : (double)count / roundCount;

    private static double PerThousandTicks(int count, int tickCount) =>
        tickCount <= 0 ? 0 : count * 1000.0 / tickCount;

    private static RoundBoundaryObservation SegmentRounds(Dictionary<string, int> counts)
    {
        Dictionary<string, int> boundary = new(StringComparer.Ordinal);
        foreach (string candidate in _boundaryCandidates)
        {
            boundary[candidate] = counts.GetValueOrDefault(candidate);
        }

        string? startEvent = FirstThatFires(_startMarkers, counts);
        string? endEvent = FirstThatFires(_endMarkers, counts);
        int startCount = startEvent is null ? 0 : counts[startEvent];
        int endCount = endEvent is null ? 0 : counts[endEvent];

        // Count rounds off the opening marker whenever there is one: a demo cut mid-round holds a
        // start without its end, and counting ends would drop that round entirely.
        int roundCount = startCount > 0 ? startCount : endCount;

        string strategy;
        if (startEvent is not null && endEvent is not null)
        {
            strategy = string.Create(
                CultureInfo.InvariantCulture,
                $"on {startEvent} to {endEvent} ({startCount} starts, {endCount} ends)");
        }
        else if (startEvent is not null)
        {
            strategy = string.Create(
                CultureInfo.InvariantCulture,
                $"on {startEvent} to the next {startEvent} ({startCount} starts): no closing marker fires " +
                $"in this demo, so the last round runs to the end of the recording and post-round activity " +
                $"falls inside it");
        }
        else if (endEvent is not null)
        {
            strategy = string.Create(
                CultureInfo.InvariantCulture,
                $"on {endEvent} back to the previous {endEvent} ({endCount} ends): no opening marker fires " +
                $"in this demo, so the first round starts at the beginning of the recording");
        }
        else
        {
            strategy = "not segmentable: every candidate boundary event fires zero times";
        }

        return new RoundBoundaryObservation(
            startEvent, startCount, endEvent, endCount, roundCount, strategy, boundary);
    }

    private static string? FirstThatFires(string[] preference, Dictionary<string, int> counts)
    {
        foreach (string candidate in preference)
        {
            if (counts.GetValueOrDefault(candidate) > 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static SubtickObservation MeasureSubtick(ParsedDemo demo, int sampleFrames)
    {
        int messageCount = 0;
        List<DemoFrame> carriers = [];
        foreach (DemoFrame frame in demo.Frames)
        {
            int payloads = frame.UserCmdsPayloadCount;
            if (payloads == 0)
            {
                continue;
            }

            messageCount += payloads;
            carriers.Add(frame);
        }

        if (carriers.Count == 0)
        {
            return new SubtickObservation(0, 0, 0, 0, 0);
        }

        int target = sampleFrames <= 0 ? carriers.Count : Math.Min(sampleFrames, carriers.Count);
        List<DemoFrame> sample = new(target);
        double stride = (double)carriers.Count / target;
        for (int i = 0; i < target; i++)
        {
            int index = Math.Min((int)(i * stride), carriers.Count - 1);
            sample.Add(carriers[index]);
        }

        int sampledMessages = 0;
        foreach (DemoFrame frame in sample)
        {
            sampledMessages += frame.UserCmdsPayloadCount;
        }

        int sampledEvents = SubTickExtractor.Extract(sample).Count;
        double yield = sampledMessages == 0 ? 0 : (double)sampledEvents / sampledMessages;

        return new SubtickObservation(messageCount, carriers.Count, sampledMessages, sampledEvents, yield);
    }

    private static List<string> BuildDeclarationDrift(
        DemoFeatureSet declared, Dictionary<string, int> counts)
    {
        List<string> drift = [];
        foreach ((DemoFeatureSet flag, string wire) in _declaredFeatures)
        {
            bool advertised = declared.HasFlag(flag);
            int observed = counts.GetValueOrDefault(wire);

            if (advertised && observed == 0)
            {
                drift.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{flag} is declared but {wire} never fires: a consumer gated on the declaration " +
                    $"computes zeros"));
            }
            else if (!advertised && observed > 0)
            {
                drift.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{flag} is not declared but {wire} fires {observed} times: a consumer gated on the " +
                    $"declaration leaves the signal unused"));
            }
        }

        return drift;
    }

    private static List<MetricVerdict> BuildVerdicts(
        Dictionary<AimSignal, SignalObservation> signals,
        SubtickObservation subtick,
        HitGroupObservation hitGroups,
        RoundBoundaryObservation rounds)
    {
        SignalObservation shots = signals[AimSignal.WeaponFire];
        SignalObservation bulletDamage = signals[AimSignal.BulletDamage];
        SignalObservation bulletImpact = signals[AimSignal.BulletImpact];
        SignalObservation hurt = signals[AimSignal.PlayerHurt];

        return
        [
            ShotsFiredVerdict(shots),
            HitAccuracyVerdict(shots, bulletDamage, hurt),
            HeadshotRateVerdict(hurt, hitGroups),
            DamagePerShotVerdict(shots, bulletDamage, hurt),
            SprayTraceVerdict(bulletImpact, bulletDamage),
            PresenceVerdict(
                AimMetric.FlashedAiming,
                signals[AimSignal.PlayerBlind],
                "nothing records who was flashed or for how long, so a shot cannot be attributed to a " +
                "flashed shooter"),
            PresenceVerdict(
                AimMetric.ScopedAiming,
                signals[AimSignal.WeaponZoom],
                "scope state is never announced, so scoped and unscoped shots cannot be told apart"),
            AudioCuedVerdict(signals[AimSignal.PlayerFootstep], signals[AimSignal.PlayerSound]),
            SubtickVerdict(subtick),
            PerRoundVerdict(rounds)
        ];
    }

    private static MetricVerdict ShotsFiredVerdict(SignalObservation shots)
    {
        if (!shots.Present)
        {
            return new MetricVerdict(
                AimMetric.ShotsFired,
                MetricSupport.Unsupported,
                "weapon_fire never fires, so there is no shot count and nothing to divide any rate by");
        }

        if (shots.PerRound > 0 && shots.PerRound < MinShotsPerRound)
        {
            return new MetricVerdict(
                AimMetric.ShotsFired,
                MetricSupport.Degraded,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"weapon_fire fires {shots.Count} times ({shots.PerRound:0.##} per round), below the " +
                    $"{MinShotsPerRound:0.##} per round a per-player shot count needs to be more than noise"));
        }

        return new MetricVerdict(
            AimMetric.ShotsFired,
            MetricSupport.Supported,
            string.Create(
                CultureInfo.InvariantCulture,
                $"weapon_fire fires {shots.Count} times ({shots.PerRound:0.##} per round, " +
                $"{shots.PerThousandTicks:0.##} per 1000 ticks)"));
    }

    private static MetricVerdict HitAccuracyVerdict(
        SignalObservation shots, SignalObservation bulletDamage, SignalObservation hurt)
    {
        if (!shots.Present)
        {
            return new MetricVerdict(
                AimMetric.HitAccuracy,
                MetricSupport.Unsupported,
                "weapon_fire never fires, so accuracy has no denominator");
        }

        if (bulletDamage.Present)
        {
            return new MetricVerdict(
                AimMetric.HitAccuracy,
                MetricSupport.Supported,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"bullet_damage fires {bulletDamage.Count} times against {shots.Count} weapon_fire, one " +
                    $"fire per damaging bullet"));
        }

        if (hurt.Present)
        {
            return new MetricVerdict(
                AimMetric.HitAccuracy,
                MetricSupport.Degraded,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"bullet_damage never fires, so player_hurt ({hurt.Count} fires) is the only hit signal. " +
                    $"It reports damage applied to a victim rather than bullets landed, which collapses a " +
                    $"shotgun spread or two same-tick hits into one fire and reads accuracy low"));
        }

        return new MetricVerdict(
            AimMetric.HitAccuracy,
            MetricSupport.Unsupported,
            string.Create(
                CultureInfo.InvariantCulture,
                $"neither bullet_damage nor player_hurt fires, so the {shots.Count} weapon_fire events " +
                $"cannot be split into hits and misses"));
    }

    private static MetricVerdict HeadshotRateVerdict(SignalObservation hurt, HitGroupObservation hitGroups)
    {
        if (!hurt.Present)
        {
            return new MetricVerdict(
                AimMetric.HeadshotRate,
                MetricSupport.Unsupported,
                "player_hurt never fires, so no hit carries a hitgroup");
        }

        if (hitGroups.DistinctValues <= 1)
        {
            return new MetricVerdict(
                AimMetric.HeadshotRate,
                MetricSupport.Unsupported,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"player_hurt fires {hurt.Count} times but its hitgroup never varies " +
                    $"({hitGroups.DistinctValues} distinct value), so every hit reads as the same body part"));
        }

        return new MetricVerdict(
            AimMetric.HeadshotRate,
            MetricSupport.Supported,
            string.Create(
                CultureInfo.InvariantCulture,
                $"player_hurt fires {hurt.Count} times across {hitGroups.DistinctValues} distinct hitgroups, " +
                $"{hitGroups.NonZeroFires} of them naming a specific body part"));
    }

    private static MetricVerdict DamagePerShotVerdict(
        SignalObservation shots, SignalObservation bulletDamage, SignalObservation hurt)
    {
        if (!shots.Present)
        {
            return new MetricVerdict(
                AimMetric.DamagePerShot,
                MetricSupport.Unsupported,
                "weapon_fire never fires, so damage cannot be divided by shots");
        }

        if (bulletDamage.Present)
        {
            return new MetricVerdict(
                AimMetric.DamagePerShot,
                MetricSupport.Supported,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"bullet_damage fires {bulletDamage.Count} times, carrying per-bullet damage against " +
                    $"{shots.Count} weapon_fire"));
        }

        if (hurt.Present)
        {
            return new MetricVerdict(
                AimMetric.DamagePerShot,
                MetricSupport.Degraded,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"bullet_damage never fires, so damage comes from player_hurt ({hurt.Count} fires). Its " +
                    $"damage is clamped to the victim's remaining health, so overkill is invisible and the " +
                    $"per-shot figure reads low"));
        }

        return new MetricVerdict(
            AimMetric.DamagePerShot,
            MetricSupport.Unsupported,
            "neither bullet_damage nor player_hurt fires, so no damage is recorded at all");
    }

    private static MetricVerdict SprayTraceVerdict(SignalObservation bulletImpact, SignalObservation bulletDamage)
    {
        if (bulletImpact.Present)
        {
            return new MetricVerdict(
                AimMetric.SprayTrace,
                MetricSupport.Supported,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"bullet_impact fires {bulletImpact.Count} times ({bulletImpact.PerRound:0.##} per " +
                    $"round), one per bullet landing"));
        }

        string substitute = bulletDamage.Present
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"bullet_damage ({bulletDamage.Count} fires) is not a substitute: it reports only bullets " +
                $"that hit a player, which is the half of a spray a spray trace is not about")
            : "bullet_damage does not fire either, so no per-bullet record exists at all";

        return new MetricVerdict(
            AimMetric.SprayTrace,
            MetricSupport.Unsupported,
            string.Create(
                CultureInfo.InvariantCulture,
                $"bullet_impact never fires, so misses leave no trace. {substitute}"));
    }

    private static MetricVerdict PresenceVerdict(AimMetric metric, SignalObservation signal, string consequence)
    {
        if (!signal.Present)
        {
            return new MetricVerdict(
                metric,
                MetricSupport.Unsupported,
                string.Create(CultureInfo.InvariantCulture, $"{signal.Wire} never fires: {consequence}"));
        }

        return new MetricVerdict(
            metric,
            MetricSupport.Supported,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{signal.Wire} fires {signal.Count} times ({signal.PerRound:0.##} per round)"));
    }

    private static MetricVerdict AudioCuedVerdict(SignalObservation footstep, SignalObservation sound)
    {
        if (footstep.Present)
        {
            return new MetricVerdict(
                AimMetric.AudioCuedAiming,
                MetricSupport.Supported,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"player_footstep fires {footstep.Count} times ({footstep.PerRound:0.##} per round), one " +
                    $"per step, attributed to the player who took it"));
        }

        if (sound.Present)
        {
            return new MetricVerdict(
                AimMetric.AudioCuedAiming,
                MetricSupport.Degraded,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"player_footstep never fires, so cues come from player_sound ({sound.Count} fires). That " +
                    $"event covers every sound a player emits, so footsteps have to be filtered out of it by " +
                    $"its step flag and the audible radius read rather than assumed"));
        }

        return new MetricVerdict(
            AimMetric.AudioCuedAiming,
            MetricSupport.Unsupported,
            "neither player_footstep nor player_sound fires, so nothing records what a shooter could hear");
    }

    private static MetricVerdict SubtickVerdict(SubtickObservation subtick)
    {
        if (!subtick.Present)
        {
            return new MetricVerdict(
                AimMetric.SubtickAimTiming,
                MetricSupport.Unsupported,
                "no svc_UserCmds payloads survive in this demo, so there is no input stream below tick " +
                "granularity to read (the demo trimmer strips exactly this message)");
        }

        if (subtick.YieldPerMessage < MinSubtickYield)
        {
            return new MetricVerdict(
                AimMetric.SubtickAimTiming,
                MetricSupport.Degraded,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"svc_UserCmds carries {subtick.MessageCount} payloads but yields only " +
                    $"{subtick.YieldPerMessage:0.###} sub-tick events per payload ({subtick.SampledEvents} " +
                    $"events from {subtick.SampledMessages} sampled payloads), below the " +
                    $"{MinSubtickYield:0.##} floor: the stream is present but mostly empty"));
        }

        return new MetricVerdict(
            AimMetric.SubtickAimTiming,
            MetricSupport.Supported,
            string.Create(
                CultureInfo.InvariantCulture,
                $"svc_UserCmds carries {subtick.MessageCount} payloads yielding " +
                $"{subtick.YieldPerMessage:0.###} sub-tick events each ({subtick.SampledEvents} events from " +
                $"{subtick.SampledMessages} sampled payloads)"));
    }

    private static MetricVerdict PerRoundVerdict(RoundBoundaryObservation rounds)
    {
        if (!rounds.Segmentable)
        {
            return new MetricVerdict(
                AimMetric.PerRoundAim,
                MetricSupport.Unsupported,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the demo cannot be cut into rounds: {rounds.Strategy}"));
        }

        // A missing closing marker still segments, but the last round has no boundary and everything
        // after the final kill lands inside it, so the per-round slice is real yet skewed.
        MetricSupport support = rounds.EndEvent is null ? MetricSupport.Degraded : MetricSupport.Supported;

        return new MetricVerdict(
            AimMetric.PerRoundAim,
            support,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{rounds.RoundCount} rounds, segmented {rounds.Strategy}"));
    }
}
