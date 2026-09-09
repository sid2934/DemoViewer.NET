#region

using System.Collections.Immutable;

#endregion

namespace DemoViewer.NET.AppTests.AimParity;

/// <summary>
///     What a comparison against Leetify is allowed to conclude for one stat. The mode is part of
///     the stat's definition, not a knob: two of these columns cannot be compared by value at all,
///     and encoding that here is what stops a future reader from "fixing" a divergence that is a
///     definitional difference rather than a defect.
/// </summary>
public enum AimComparisonMode
{
    /// <summary>
    ///     Both sides measure the same population, so the values are comparable and a large gap is
    ///     a finding.
    /// </summary>
    Comparable,

    /// <summary>
    ///     Both sides measure the same idea over DIFFERENT populations, so only the direction and
    ///     the order of magnitude carry information. See
    ///     <see cref="LeetifyAimRow.AccuracyHead" /> for the worked case.
    /// </summary>
    Directional,

    /// <summary>
    ///     Leetify declares the field and never populates it, so there is no reference at all and
    ///     the stat needs its own oracle. Spray control is here.
    /// </summary>
    NoReference,

    /// <summary>
    ///     Leetify reports it and we deliberately do not produce it yet. Carried so the gap is a
    ///     line in the report rather than an absence nobody notices.
    /// </summary>
    NotImplemented
}

/// <summary>
///     Which of the aim board's three capability tiers a stat sits in. A tier that the demo or the
///     checkout cannot support reads 0.0 rather than blank (see the header of
///     <c>rules/aim_rating.rules.yaml</c>), so a comparison must know the tier before it can tell a
///     score from an empty population.
/// </summary>
public enum AimTier
{
    /// <summary>Needs only <c>weapon_fire</c> and <c>player_hurt</c>. Every source carries these.</summary>
    ShotsAndHits = 1,

    /// <summary>Needs the per-tick shot enrichments (movement, recoil, aim punch).</summary>
    ShotEnrichment = 2,

    /// <summary>
    ///     Needs the synthesized <c>enemy_spotted</c> event, which exists only when the run is
    ///     handed baked map geometry.
    /// </summary>
    Visibility = 3
}

/// <summary>
///     One row of the aim parity table: our stat, the Leetify field it answers, and what a
///     comparison of the two is allowed to conclude.
/// </summary>
/// <param name="Canonical">
///     The key this stat is pinned under in <c>tests/fixtures/&lt;demo-id&gt;/aim.expected.golden.json</c>.
///     snake_case to match the rest of the golden schema.
/// </param>
/// <param name="RuleId">
///     The qualified v2 stat id (<c>aim_rating.&lt;stat&gt;</c>) read out of the materialized graph.
///     Read by rule id rather than by the <c>show:</c> column label so the harness covers the
///     populations that are not on the scoreboard (<c>cs_clean</c> is a numerator, not a column).
/// </param>
/// <param name="LeetifyField">The Leetify wire name, or <c>null</c> when there is no counterpart.</param>
/// <param name="LeetifyScale">
///     What Leetify's value is multiplied by to reach our units. Their ratios are 0..1 and ours are
///     percentages, so this is 100 for every ratio and 1 for every count.
/// </param>
/// <param name="Tier">The capability tier, which decides whether a 0.0 is a score or an empty population.</param>
/// <param name="Mode">What the comparison may conclude.</param>
/// <param name="Note">Why this row reads the way it does. Printed in the divergence report.</param>
public sealed record AimStatDefinition(
    string Canonical,
    string RuleId,
    string? LeetifyField,
    double LeetifyScale,
    AimTier Tier,
    AimComparisonMode Mode,
    string Note);

/// <summary>
///     The aim stat universe the parity gate covers, and the mapping onto Leetify's own columns.
///     <para>
///         <b>Every denominator is named.</b> The aim board's whole design premise (see the header
///         of <c>rules/aim_rating.rules.yaml</c>) is that two tools calling something
///         "counter-strafing percent" usually agree about the shots and disagree about which shots
///         were counted. This table is where that disagreement is made explicit: each row says
///         which Leetify column it is answering and, in <see cref="AimStatDefinition.Mode" />,
///         whether the two populations are the same population.
///     </para>
///     <para>
///         <b>What is deliberately absent.</b> <c>hsp</c> is Leetify's headshot share of KILLS, which
///         the player-stats board already covers and the aim ruleset does not produce, so it is not
///         a row here. <c>reactionTime</c> is a row with no rule id, because the time-to-X ladder is
///         deferred rather than shipped wrong; a row that reads "we do not compute this" is worth
///         more than an absent row nobody notices.
///     </para>
/// </summary>
public static class AimStatCatalogue
{
    /// <summary>The ruleset id every stat below is qualified by.</summary>
    public const string RulesetId = "aim_rating";

    /// <summary>
    ///     The stat table. Ordered by tier so the report degrades readably on a source that
    ///     supports only the first one.
    /// </summary>
    public static ImmutableArray<AimStatDefinition> Stats { get; } =
    [
        // ── Tier 1: shots and hits ────────────────────────────────────────────────────────────
        new("shots_fired", "aim_rating.bullet_shots", "shotsFired", 1.0,
            AimTier.ShotsAndHits, AimComparisonMode.Comparable,
            "both count bullet weapon_fire during a live round"),

        new("enemy_hits", "aim_rating.enemy_hits", "shotsHitFoe", 1.0,
            AimTier.ShotsAndHits, AimComparisonMode.Comparable,
            "both count damage instances, so a penetrating bullet is two on both sides"),

        new("accuracy_pct", "aim_rating.accuracy_pct", "accuracy", 100.0,
            AimTier.ShotsAndHits, AimComparisonMode.Comparable,
            "enemy_hits over bullet_shots; the identity holds on every Leetify row we hold"),

        new("hs_accuracy_pct", "aim_rating.hs_accuracy_pct", "accuracyHead", 100.0,
            AimTier.ShotsAndHits, AimComparisonMode.Directional,
            "their denominator fits no published column and sits just below shotsHitFoe, "
            + "which reads as distinct bullets against our damage instances"),

        new("hs_damage_pct", "aim_rating.hs_damage_pct", null, 1.0,
            AimTier.ShotsAndHits, AimComparisonMode.NoReference,
            "head damage over ALL enemy damage, utility included; Leetify publishes no such column"),

        // ── Tier 2: the shot enrichments ──────────────────────────────────────────────────────
        new("cs_attempts", "aim_rating.cs_attempts", "counterStrafingShotsAll", 1.0,
            AimTier.ShotEnrichment, AimComparisonMode.Comparable,
            "THE calibration target: the admission gate's lookback window is fitted against this"),

        new("cs_clean", "aim_rating.cs_clean", "counterStrafingShotsGood", 1.0,
            AimTier.ShotEnrichment, AimComparisonMode.Comparable,
            "admitted shots taken below the movement-inaccuracy threshold"),

        new("moving_shots", "aim_rating.moving_shots", "counterStrafingShotsBad", 1.0,
            AimTier.ShotEnrichment, AimComparisonMode.Comparable,
            "admitted shots the engine charged a movement penalty for"),

        new("counter_strafe_pct", "aim_rating.counter_strafe_pct", "counterStrafingShotsGoodRatio", 100.0,
            AimTier.ShotEnrichment, AimComparisonMode.Comparable,
            "clean over attempted; inherits whatever the denominator fit leaves on the table"),

        new("counter_strafe_all_pct", "aim_rating.counter_strafe_all_pct", null, 1.0,
            AimTier.ShotEnrichment, AimComparisonMode.NoReference,
            "clean over ALL bullets; Leetify publishes the numerator and the total but not this ratio"),

        new("spray_residual_shots", "aim_rating.spray_residual_shots", null, 1.0,
            AimTier.ShotEnrichment, AimComparisonMode.NoReference,
            "spray-control population; recoilShots is declared by Leetify and null in every row"),

        new("spray_pitch_error", "aim_rating.spray_pitch_error", null, 1.0,
            AimTier.ShotEnrichment, AimComparisonMode.NoReference,
            "mean absolute pitch residual in degrees; oracle is SprayControlOracle, not Leetify"),

        new("spray_yaw_error", "aim_rating.spray_yaw_error", null, 1.0,
            AimTier.ShotEnrichment, AimComparisonMode.NoReference,
            "mean absolute yaw residual in degrees; oracle is SprayControlOracle, not Leetify"),

        // ── Tier 3: the enemy_spotted contact gate ────────────────────────────────────────────
        new("contact_shots", "aim_rating.contact_shots", "shotsFiredEnemySpotted", 1.0,
            AimTier.Visibility, AimComparisonMode.Comparable,
            "ours latches at first contact and holds for the round, so it over-counts a rotator"),

        new("contact_hits", "aim_rating.contact_hits", "shotsHitEnemySpotted", 1.0,
            AimTier.Visibility, AimComparisonMode.Comparable,
            "same latch on the hit side"),

        new("spotted_accuracy_pct", "aim_rating.spotted_accuracy_pct", "accuracyEnemySpotted", 100.0,
            AimTier.Visibility, AimComparisonMode.Comparable,
            "the ratio of the two above; Leetify's own two columns reproduce their ratio exactly"),

        new("spray_accuracy_pct", "aim_rating.spray_accuracy_pct", "sprayAccuracy", 100.0,
            AimTier.Visibility, AimComparisonMode.Comparable,
            "ours takes its hit side from bullet_damage and its shot side from weapon_fire"),

        new("preaim_deg", "aim_rating.preaim_deg", "preaim", 1.0,
            AimTier.Visibility, AimComparisonMode.Comparable,
            "mean degrees off the chest at FIRST contact; both are degrees, lower is better"),

        new("first_contacts", "aim_rating.first_contacts", null, 1.0,
            AimTier.Visibility, AimComparisonMode.NoReference,
            "the preaim denominator; zero here is what marks a preaim of 0.0 as an empty population"),

        // ── Reported, not computed ────────────────────────────────────────────────────────────
        new("time_to_damage_s", "", "reactionTime", 1.0,
            AimTier.Visibility, AimComparisonMode.NotImplemented,
            "the time-to-X ladder is deferred: event.tick and the synthesized spot sit on "
            + "different clocks, which biases every interval by the demo start tick")
    ];

    /// <summary>Every row that has a Leetify counterpart worth comparing by value.</summary>
    /// <returns>The comparable rows.</returns>
    public static IEnumerable<AimStatDefinition> Comparable() =>
        Stats.Where(s => s.Mode == AimComparisonMode.Comparable && s.LeetifyField is not null);

    /// <summary>Every row the engine actually produces, which is every row with a rule id.</summary>
    /// <returns>The produced rows.</returns>
    public static IEnumerable<AimStatDefinition> Produced() =>
        Stats.Where(s => s.RuleId.Length > 0);
}
